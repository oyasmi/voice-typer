import Foundation
import OnnxRuntimeBindings

/// SenseVoice-Small ONNX 推理封装：fbank → LFR/CMVN → ORT session → CTC 贪心解码。
/// 对外只有一个方法 `recognize`，非线程安全——只应在 `ASRService.asrQueue` 上使用
/// （与服务端单 worker executor 的约束一致：ONNX session 与前端状态都有可变缓冲）。
///
/// 模型 I/O 契约（已实测确认，见 macos/DESIGN.md §4.1）：
/// ```
/// IN   speech           float32  [1, feats_length, 560]
/// IN   speech_lengths   int32    [1]
/// IN   language         int32    [1]   auto=0 zh=3 en=4 yue=7 ja=11 ko=12
/// IN   textnorm         int32    [1]   withitn=14  woitn=15
/// OUT  ctc_logits       float32  [1, logits_length, 25055]
/// OUT  encoder_out_lens int32    [1]
/// ```
/// 识别引擎的最小接口，供 `RecognitionBuffer` 依赖，便于用假实现测试滑窗逻辑
/// （见 RecognitionBufferTests）而不必加载真实 ONNX 模型。
protocol SenseVoiceRecognizing: AnyObject, Sendable {
    func recognize(_ samples: [Float]) throws -> String
    func setLanguage(_ language: ASRLanguage)
}

extension SenseVoiceRecognizing {
    /// 默认空实现：并非所有假实现（测试用）都关心语言切换。
    func setLanguage(_ language: ASRLanguage) {}
}

/// `@unchecked Sendable`：实例只应在 `ASRService.asrQueue`（单一串行队列）上访问，
/// 由调用方保证串行化，编译器无法静态验证但语义上是安全的——与
/// `AudioCaptureService` 对可变缓冲的处理方式一致。
final class SenseVoiceEngine: SenseVoiceRecognizing, @unchecked Sendable {
    /// withitn：SenseVoice 自带 ITN（"六十四兆"→"64兆"），本项目不暴露 woitn 选项。
    private static let textNormWithITN: Int32 = 14

    private let env: ORTEnv
    private let session: ORTSession
    private let frontend: FbankFrontend
    private let cmvn: CMVNStats
    private let tokens: [String]
    private let lfrM: Int
    private let lfrN: Int
    private var languageID: Int32

    /// 侧载模型（`asr.model_dir`）的 config.yaml / am.mvn 相互独立，解析失败时各自静默
    /// 回落默认值，二者之间此前没有任何一致性校验：`lfr_m × n_mels` 与 am.mvn 实际维度
    /// 不匹配会在 `applyCMVN` 里数组越界崩溃，`lfr_n = 0` 会在 `applyLFR` 里除零 trap（F-08）。
    enum ConfigurationError: LocalizedError {
        case inconsistentDimensions(String)

        var errorDescription: String? {
            switch self {
            case .inconsistentDimensions(let detail):
                return "模型参数不自洽，请检查侧载模型的 config.yaml 与 am.mvn: \(detail)"
            }
        }
    }

    /// 模型契约的输入/输出名称（见类文档顶部的 I/O 表）。加载期一次性校验，
    /// 避免侧载模型 I/O 不齐时直到每次推理才失败（R2-09）。
    private static let expectedInputNames: Set<String> = ["speech", "speech_lengths", "language", "textnorm"]
    private static let expectedOutputNames: Set<String> = ["ctc_logits", "encoder_out_lens"]

    init(bundle: ModelBundle, language: ASRLanguage, threads: Int) throws {
        let fbankOptions = bundle.fbankOptions
        guard bundle.lfrM >= 1, bundle.lfrN >= 1,
              fbankOptions.numMelBins > 0,
              fbankOptions.frameLengthMs > 0, fbankOptions.frameShiftMs > 0
        else {
            throw ConfigurationError.inconsistentDimensions(
                "lfr_m=\(bundle.lfrM) lfr_n=\(bundle.lfrN) n_mels=\(fbankOptions.numMelBins) "
                    + "frame_length_ms=\(fbankOptions.frameLengthMs) frame_shift_ms=\(fbankOptions.frameShiftMs)"
            )
        }
        // 采集链路固定 16kHz（AGENTS.md）；别的采样率的模型在这套前端下本来就不可用，
        // 用等值判断比"大于 0"更强，同时挡掉 fs<=0 导致 FbankFrontend 里的非正
        // frameLength 触发的数组分配 trap。
        guard fbankOptions.sampleRate == AppConstants.targetSampleRate else {
            throw ConfigurationError.inconsistentDimensions(
                "fs=\(fbankOptions.sampleRate)，但采集链路固定 \(AppConstants.targetSampleRate)"
            )
        }

        env = try ORTEnv(loggingLevel: .warning)

        let options = try ORTSessionOptions()
        let resolvedThreads = threads > 0 ? threads : min(4, ProcessInfo.processInfo.activeProcessorCount)
        try options.setIntraOpNumThreads(Int32(resolvedThreads))
        try options.setGraphOptimizationLevel(.all)
        // 实测：这一行把常驻内存从 ~800MB 降到 ~510MB，30s 音频推理耗时几乎不变
        // （262ms vs 260ms）。详见 macos/DESIGN.md §4.3。
        try options.addConfigEntry(withKey: "session.disable_prepacking", value: "1")
        try options.setLogSeverityLevel(.warning)

        session = try ORTSession(env: env, modelPath: bundle.onnxFileURL.path, sessionOptions: options)

        let actualInputNames = Set(try session.inputNames())
        let actualOutputNames = Set(try session.outputNames())
        guard Self.expectedInputNames.isSubset(of: actualInputNames),
              Self.expectedOutputNames.isSubset(of: actualOutputNames)
        else {
            throw ConfigurationError.inconsistentDimensions(
                "模型 I/O 与 SenseVoice-Small 契约不符：期望输入 \(Self.expectedInputNames.sorted()) "
                    + "输出 \(Self.expectedOutputNames.sorted())，实际输入 \(actualInputNames.sorted()) "
                    + "输出 \(actualOutputNames.sorted())"
            )
        }

        frontend = FbankFrontend(opts: bundle.fbankOptions)
        let parsedCMVN = try CMVNStats.parse(contentsOf: bundle.cmvnURL)
        let parsedTokens = try ModelLocator.loadTokens(from: bundle.tokensURL)

        let expectedDim = bundle.lfrM * fbankOptions.numMelBins
        guard parsedCMVN.means.count == expectedDim, parsedCMVN.vars.count == expectedDim else {
            throw ConfigurationError.inconsistentDimensions(
                "lfr_m(\(bundle.lfrM)) × n_mels(\(fbankOptions.numMelBins)) = \(expectedDim)，"
                    + "但 am.mvn 维度为 means=\(parsedCMVN.means.count) vars=\(parsedCMVN.vars.count)"
            )
        }
        guard parsedTokens.count > 1 else {
            throw ConfigurationError.inconsistentDimensions("tokens.json 词表为空或只有 1 个 token")
        }

        cmvn = parsedCMVN
        tokens = parsedTokens
        lfrM = bundle.lfrM
        lfrN = bundle.lfrN
        languageID = language.tokenID
    }

    func setLanguage(_ language: ASRLanguage) {
        languageID = language.tokenID
    }

    /// - Parameter samples: 16kHz / mono / float32，范围 [-1, 1]。
    /// - Returns: 识别文本；样本不足一帧或识别为纯静音/纯标点时返回空字符串。
    func recognize(_ samples: [Float]) throws -> String {
        guard samples.count >= fbankMinimumSamples else { return "" }

        let feats = frontend.compute(samples)
        guard !feats.isEmpty else { return "" }
        let lfr = applyLFR(feats, m: lfrM, n: lfrN)
        let normalized = applyCMVN(lfr, stats: cmvn)
        guard !normalized.isEmpty else { return "" }

        let featsLen = normalized.count
        let dim = normalized[0].count
        var flatFeats = [Float]()
        flatFeats.reserveCapacity(featsLen * dim)
        for row in normalized { flatFeats.append(contentsOf: row) }

        let speechData = NSMutableData(bytes: &flatFeats, length: flatFeats.count * MemoryLayout<Float>.size)
        let speechValue = try ORTValue(
            tensorData: speechData,
            elementType: .float,
            shape: [1, NSNumber(value: featsLen), NSNumber(value: dim)]
        )

        var lengthScalar: Int32 = Int32(featsLen)
        let lengthData = NSMutableData(bytes: &lengthScalar, length: MemoryLayout<Int32>.size)
        let lengthValue = try ORTValue(tensorData: lengthData, elementType: .int32, shape: [1])

        var languageScalar: Int32 = languageID
        let languageData = NSMutableData(bytes: &languageScalar, length: MemoryLayout<Int32>.size)
        let languageValue = try ORTValue(tensorData: languageData, elementType: .int32, shape: [1])

        var textnormScalar: Int32 = Self.textNormWithITN
        let textnormData = NSMutableData(bytes: &textnormScalar, length: MemoryLayout<Int32>.size)
        let textnormValue = try ORTValue(tensorData: textnormData, elementType: .int32, shape: [1])

        let outputs = try session.run(
            withInputs: [
                "speech": speechValue,
                "speech_lengths": lengthValue,
                "language": languageValue,
                "textnorm": textnormValue,
            ],
            outputNames: ["ctc_logits", "encoder_out_lens"],
            runOptions: nil
        )

        // 输出缺失是模型契约错误，不是合法静音——用 throw 让两者可区分（R2-09）。
        guard let logitsValue = outputs["ctc_logits"], let lensValue = outputs["encoder_out_lens"] else {
            throw ConfigurationError.inconsistentDimensions("推理输出缺失 ctc_logits/encoder_out_lens")
        }

        let lensData = try lensValue.tensorData() as Data
        let validFrames = lensData.withUnsafeBytes { $0.load(as: Int32.self) }
        guard validFrames > 0 else { return "" }

        let shapeInfo = try logitsValue.tensorTypeAndShapeInfo()
        guard let vocabSize = shapeInfo.shape.last?.intValue, vocabSize > 0 else { return "" }
        // 只有拿到推理输出的实际 shape 才能知道 vocabSize；此前不匹配时 CTCDecoder 会
        // 静默跳过越界 id，表现为"识别成功但缺字"，比报错更难排查（R2-09）。
        guard vocabSize == tokens.count else {
            throw ConfigurationError.inconsistentDimensions(
                "模型输出 vocab 维度(\(vocabSize)) 与词表大小(\(tokens.count)) 不一致"
            )
        }

        let logitsData = try logitsValue.tensorData() as Data
        let numFrames = Int(validFrames)
        let neededFloats = numFrames * vocabSize
        // 直接在 ORT 输出的底层内存上解码，不构造中间 [Float] 副本：300 秒音频场景下
        // 可省去约 500MB 的峰值内存（logits 与 Swift 副本同时驻留）（F-07a）。
        return logitsData.withUnsafeBytes { raw -> String in
            guard raw.count >= neededFloats * MemoryLayout<Float>.size,
                  let base = raw.bindMemory(to: Float.self).baseAddress else { return "" }
            return CTCDecoder.decode(logits: base, numFrames: numFrames, vocabSize: vocabSize, tokens: tokens)
        }
    }
}
