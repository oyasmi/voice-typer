import Foundation
import OnnxRuntimeBindings

/// SenseVoice-Small ONNX 推理封装：fbank → LFR/CMVN → ORT session → CTC 解码（无词汇时贪心，有词汇时带热词偏置的束搜索）。
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
    /// 只在 asrQueue 上调用。编译在调用线程同步完成，返回报告供设置页显示（VOC-2）。
    func setVocabulary(_ vocabulary: VocabularyConfig) -> HotwordCompileReport?
    /// 最近一次 `recognize` 的解码统计，只在 asrQueue 上读。
    var lastDecodeStats: DecodeStats { get }
}

extension SenseVoiceRecognizing {
    /// 默认空实现：并非所有假实现（测试用）都关心语言切换。
    func setLanguage(_ language: ASRLanguage) {}
    func setVocabulary(_ vocabulary: VocabularyConfig) -> HotwordCompileReport? { nil }
    var lastDecodeStats: DecodeStats { .greedy }
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

    /// 词表前缀树与 token 类别：第一次 `setVocabulary` 时懒构建，之后复用（与具体词汇无关）。
    private var tokenTrie: TokenTrie?
    private var tokenClasses: CTCPrefixBeamSearch.TokenClasses?
    /// 当前词汇的上下文图；nil = 走贪心解码，金标准与既有输出逐字节不变（VOC-D4）。
    private var contextGraph: ContextGraph?
    private var beamParameters: CTCPrefixBeamSearch.Parameters?
    private(set) var lastDecodeStats = DecodeStats.greedy

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

    func setVocabulary(_ vocabulary: VocabularyConfig) -> HotwordCompileReport? {
        guard !vocabulary.entries.isEmpty else {
            contextGraph = nil
            beamParameters = nil
            return HotwordCompileReport(entries: [], sequenceCount: 0, compileNanos: 0)
        }
        let trie: TokenTrie
        if let tokenTrie {
            trie = tokenTrie
        } else {
            trie = TokenTrie(tokens: tokens)
            tokenTrie = trie
            tokenClasses = CTCPrefixBeamSearch.TokenClasses(tokens: tokens)
        }
        // 强度为「关」时也编译：报告照样告诉用户哪些词条能参与偏置，只是不建图。
        let compiled = HotwordCompiler.compile(vocabulary, trie: trie)
        let report = compiled.report
        if let parameters = CTCPrefixBeamSearch.Parameters.preset(vocabulary.bias), !compiled.sequences.isEmpty {
            contextGraph = ContextGraph(
                sequences: compiled.sequences,
                perTokenBias: parameters.perTokenBias,
                maxScoredDepth: parameters.maxScoredDepth
            )
            beamParameters = parameters
        } else {
            contextGraph = nil
            beamParameters = nil
        }
        AppLog.asr.info(
            "词汇已编译：entries=\(report.entries.count, privacy: .public) sequences=\(report.sequenceCount, privacy: .public) biased=\(report.count(of: .biased), privacy: .public) too_short=\(report.count(of: .tooShort), privacy: .public) unsupported=\(report.count(of: .unsupportedCharacters), privacy: .public) truncated=\(report.count(of: .truncated), privacy: .public) bias=\(vocabulary.bias.rawValue, privacy: .public) 耗时=\(report.compileNanos / 1_000_000, privacy: .public)ms"
        )
        return report
    }

    /// - Parameter samples: 16kHz / mono / float32，范围 [-1, 1]。
    /// - Returns: 识别文本；样本不足一帧或识别为纯静音/纯标点时返回空字符串。
    func recognize(_ samples: [Float]) throws -> String {
        // 提前返回（样本不足、静音）时不能留着上一次的统计。
        lastDecodeStats = DecodeStats(mode: contextGraph == nil ? .greedy : .beam, hotwordHits: 0, decodeNanos: 0)
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
            return decode(logits: base, numFrames: numFrames, vocabSize: vocabSize)
        }
    }

    /// 无词汇走贪心（一行不改）；有词汇走带偏置的束搜索。两条路径都把拼好的原始串交给 `TextPostprocessor`。
    private func decode(logits: UnsafePointer<Float>, numFrames: Int, vocabSize: Int) -> String {
        let startedAt = DispatchTime.now().uptimeNanoseconds
        guard let contextGraph, let beamParameters, let tokenClasses else {
            let text = CTCDecoder.decode(logits: logits, numFrames: numFrames, vocabSize: vocabSize, tokens: tokens)
            lastDecodeStats = DecodeStats(
                mode: .greedy, hotwordHits: 0, decodeNanos: DispatchTime.now().uptimeNanoseconds &- startedAt
            )
            return text
        }
        do {
            let result = try CTCPrefixBeamSearch.decode(
                logits: logits, numFrames: numFrames, vocabSize: vocabSize,
                graph: contextGraph, classes: tokenClasses, parameters: beamParameters
            )
            var raw = ""
            for id in result.tokens where id >= 0 && id < tokens.count {
                raw += tokens[id]
            }
            lastDecodeStats = DecodeStats(
                mode: .beam, hotwordHits: result.hotwordHits,
                decodeNanos: DispatchTime.now().uptimeNanoseconds &- startedAt
            )
            return TextPostprocessor.process(raw)
        } catch {
            // 束搜索的内部不一致不应让本次听写失败：记录后回退贪心结果（§5.8）。
            AppLog.asr.error("热词束搜索失败，回退贪心解码: \(String(describing: error), privacy: .public)")
            let text = CTCDecoder.decode(logits: logits, numFrames: numFrames, vocabSize: vocabSize, tokens: tokens)
            lastDecodeStats = DecodeStats(
                mode: .greedy, hotwordHits: 0, decodeNanos: DispatchTime.now().uptimeNanoseconds &- startedAt
            )
            return text
        }
    }
}
