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

    init(bundle: ModelBundle, language: ASRLanguage, threads: Int) throws {
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
        frontend = FbankFrontend(opts: bundle.fbankOptions)
        cmvn = try CMVNStats.parse(contentsOf: bundle.cmvnURL)
        tokens = try ModelLocator.loadTokens(from: bundle.tokensURL)
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

        guard let logitsValue = outputs["ctc_logits"], let lensValue = outputs["encoder_out_lens"] else {
            return ""
        }

        let lensData = try lensValue.tensorData() as Data
        let validFrames = lensData.withUnsafeBytes { $0.load(as: Int32.self) }
        guard validFrames > 0 else { return "" }

        let shapeInfo = try logitsValue.tensorTypeAndShapeInfo()
        guard let vocabSize = shapeInfo.shape.last?.intValue, vocabSize > 0 else { return "" }

        let logitsData = try logitsValue.tensorData() as Data
        let numFrames = Int(validFrames)
        let neededFloats = numFrames * vocabSize
        let logits: [Float] = logitsData.withUnsafeBytes { raw in
            Array(raw.bindMemory(to: Float.self).prefix(neededFloats))
        }
        guard logits.count == neededFloats else { return "" }

        return CTCDecoder.decode(logits: logits, numFrames: numFrames, vocabSize: vocabSize, tokens: tokens)
    }
}
