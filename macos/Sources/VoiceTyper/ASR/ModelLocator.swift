import Foundation
import Yams

/// 一份可用的 SenseVoice 模型目录：onnx 权重 + 词表 + CMVN 统计 + 前端参数。
struct ModelBundle {
    let modelDir: URL
    let onnxFileURL: URL
    let tokensURL: URL
    let cmvnURL: URL
    let quantized: Bool
    /// 从 config.yaml 的 frontend_conf 解析，解析失败则用 FbankOptions 的默认值。
    let fbankOptions: FbankOptions
    let lfrM: Int
    let lfrN: Int
}

/// 按优先级定位模型目录，第一个通过校验的立即返回：
/// 1. 用户在设置里显式指定的 `asr.model_dir`（换 fp32 版本或手动放置）
/// 2. App 首启下载的落点 `~/Library/Application Support/VoiceTyper/models/sensevoice-small/`
/// 3. Python 服务端已下载过的 ModelScope 缓存
///    `~/.cache/modelscope/hub/models/iic/SenseVoiceSmall-onnx/`（跑过 client-server/server/ 的机器零下载）
///
/// 都没有命中时返回 nil，调用方据此进入 `.modelMissing` 状态触发首启下载引导。
enum ModelLocator {
    static let downloadDestination: URL = {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
            .appendingPathComponent(AppConstants.appSupportDirectoryName, isDirectory: true)
            .appendingPathComponent("models/sensevoice-small", isDirectory: true)
    }()

    private static let modelScopeCache: URL = {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".cache/modelscope/hub/models/iic/SenseVoiceSmall-onnx", isDirectory: true)
    }()

    static func locate(explicitDir: String) -> ModelBundle? {
        let trimmed = explicitDir.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty, let bundle = validate(URL(fileURLWithPath: trimmed)) {
            return bundle
        }
        if let bundle = validate(downloadDestination) {
            return bundle
        }
        if let bundle = validate(modelScopeCache) {
            return bundle
        }
        return nil
    }

    private static func validate(_ dir: URL) -> ModelBundle? {
        let fm = FileManager.default
        let quantURL = dir.appendingPathComponent("model_quant.onnx")
        let fullURL = dir.appendingPathComponent("model.onnx")
        let onnxURL: URL
        let quantized: Bool
        if fm.fileExists(atPath: quantURL.path) {
            onnxURL = quantURL
            quantized = true
        } else if fm.fileExists(atPath: fullURL.path) {
            onnxURL = fullURL
            quantized = false
        } else {
            return nil
        }

        let cmvnURL = dir.appendingPathComponent("am.mvn")
        var tokensURL = dir.appendingPathComponent("tokens.json")
        if !fm.fileExists(atPath: tokensURL.path) {
            tokensURL = dir.appendingPathComponent("tokens.txt")
        }
        guard fm.fileExists(atPath: cmvnURL.path), fm.fileExists(atPath: tokensURL.path) else {
            return nil
        }

        let (fbankOptions, lfrM, lfrN) = parseFrontendConf(dir.appendingPathComponent("config.yaml"))
        return ModelBundle(
            modelDir: dir,
            onnxFileURL: onnxURL,
            tokensURL: tokensURL,
            cmvnURL: cmvnURL,
            quantized: quantized,
            fbankOptions: fbankOptions,
            lfrM: lfrM,
            lfrN: lfrN
        )
    }

    /// 读 config.yaml 的 frontend_conf；缺失或解析失败时静默回落到验证过的默认值
    /// （fs=16000, n_mels=80, lfr_m=7, lfr_n=6, hamming window）。
    private static func parseFrontendConf(_ configURL: URL) -> (FbankOptions, Int, Int) {
        var opts = FbankOptions()
        var lfrM = 7
        var lfrN = 6

        guard let content = try? String(contentsOf: configURL, encoding: .utf8),
              let yaml = try? Yams.load(yaml: content) as? [String: Any],
              let frontendConf = yaml["frontend_conf"] as? [String: Any] else {
            return (opts, lfrM, lfrN)
        }

        if let fs = frontendConf["fs"] as? Int { opts.sampleRate = Double(fs) }
        if let frameLength = frontendConf["frame_length"] as? Int { opts.frameLengthMs = Double(frameLength) }
        if let frameShift = frontendConf["frame_shift"] as? Int { opts.frameShiftMs = Double(frameShift) }
        if let nMels = frontendConf["n_mels"] as? Int { opts.numMelBins = nMels }
        if let m = frontendConf["lfr_m"] as? Int { lfrM = m }
        if let n = frontendConf["lfr_n"] as? Int { lfrN = n }
        return (opts, lfrM, lfrN)
    }

    static func loadTokens(from url: URL) throws -> [String] {
        if url.pathExtension == "json" {
            let data = try Data(contentsOf: url)
            guard let tokens = try JSONSerialization.jsonObject(with: data) as? [String] else {
                throw NSError(
                    domain: AppConstants.bundleIdentifier, code: 1102,
                    userInfo: [NSLocalizedDescriptionKey: "tokens.json 格式不是字符串数组: \(url.path)"]
                )
            }
            return tokens
        }
        // tokens.txt：每行 "piece\tscore"，piece 本身可能含空格，只从右侧切掉分数列。
        let text = try String(contentsOf: url, encoding: .utf8)
        return text.split(separator: "\n", omittingEmptySubsequences: true).map { line in
            if let tabRange = line.range(of: "\t", options: .backwards) {
                return String(line[line.startIndex..<tabRange.lowerBound])
            }
            return String(line)
        }
    }
}
