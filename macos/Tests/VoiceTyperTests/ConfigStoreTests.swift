import XCTest
import Yams
@testable import VoiceTyper

final class ConfigStoreTests: XCTestCase {
    private var tempDir: URL!
    private var store: ConfigStore!

    override func setUpWithError() throws {
        tempDir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        // 指向一个必然不存在的路径，避免在装过旧客户端的开发机上读到真实的
        // ~/.config/voice_typer/config.yaml 导致断言随机失败（F-01b）。
        let nonexistentLegacyURL = tempDir.appendingPathComponent("legacy-not-present.yaml")
        store = ConfigStore(baseDirectoryOverride: tempDir, legacyConfigURLOverride: nonexistentLegacyURL)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: tempDir)
    }

    func testSaveThenLoadRoundTripsAllFields() throws {
        var config = AppConfig()
        config.asr.language = .zh
        config.asr.threads = 4
        config.asr.modelDir = "/tmp/custom-model"
        config.asr.idleUnloadMinutes = 30
        config.asr.preloadOnLaunch = true
        config.llm.enabled = true
        config.llm.baseURL = "https://api.example.com/v1"
        config.llm.model = "gpt-4o"
        config.llm.temperature = 0.3
        config.llm.maxTokens = 1200
        config.llm.timeout = 8
        config.hotkey = HotkeyConfig(modifiers: ["ctrl"], key: "f2", mode: .toggle)
        config.ui.opacity = 0.7
        config.ui.hudPosition = .nearCursor
        config.audio.inputDevice = "system"

        try store.save(config: config)
        let loaded = try store.loadOrCreate()

        XCTAssertEqual(loaded.asr.language, .zh)
        XCTAssertEqual(loaded.asr.threads, 4)
        XCTAssertEqual(loaded.asr.modelDir, "/tmp/custom-model")
        XCTAssertEqual(loaded.asr.idleUnloadMinutes, 30)
        XCTAssertEqual(loaded.asr.preloadOnLaunch, true)
        XCTAssertEqual(loaded.llm.enabled, true)
        XCTAssertEqual(loaded.llm.baseURL, "https://api.example.com/v1")
        XCTAssertEqual(loaded.llm.model, "gpt-4o")
        XCTAssertEqual(loaded.llm.temperature, 0.3, accuracy: 1e-9)
        XCTAssertEqual(loaded.llm.maxTokens, 1200)
        XCTAssertEqual(loaded.llm.timeout, 8, accuracy: 1e-9)
        XCTAssertEqual(loaded.hotkey.modifiers, ["ctrl"])
        XCTAssertEqual(loaded.hotkey.key, "f2")
        XCTAssertEqual(loaded.hotkey.mode, .toggle)
        XCTAssertEqual(loaded.ui.opacity, 0.7, accuracy: 1e-9)
        XCTAssertEqual(loaded.ui.hudPosition, .nearCursor)
        XCTAssertEqual(loaded.audio.inputDevice, "system")
    }

    func testAudioInputDeviceDefaultsToAutoWhenSectionOrValueIsMissing() throws {
        XCTAssertEqual(AppConfig().audio.inputDevice, "auto")

        let noAudioSection = try YAMLDecoder().decode(AppConfig.self, from: "hotkey:\n  key: fn\n")
        XCTAssertEqual(noAudioSection.audio.inputDevice, "auto")

        let empty = try YAMLDecoder().decode(AppConfig.self, from: "audio:\n  input_device: \"\"\n")
        XCTAssertEqual(empty.audio.inputDevice, "auto", "空字符串按缺失处理")

        let uid = try YAMLDecoder().decode(AppConfig.self, from: "audio:\n  input_device: \"BuiltInMicrophoneDevice\"\n")
        XCTAssertEqual(uid.audio.inputDevice, "BuiltInMicrophoneDevice")
    }

    func testCreatesDefaultFileOnFirstLoad() throws {
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.configURL.path))
        let config = try store.loadOrCreate()
        XCTAssertTrue(FileManager.default.fileExists(atPath: store.configURL.path))
        XCTAssertEqual(config.hotkey.key, "fn") // 默认值
        XCTAssertEqual(config.hotkey.mode, .hold) // 默认值
        XCTAssertEqual(config.asr.idleUnloadMinutes, 0) // 默认值：从不卸载
    }

    func testMissingFieldsFallBackToDefaults() throws {
        // 只写 hotkey，其余字段全部缺失，应回落到各自默认值而不是解码失败。
        let partialYAML = "hotkey:\n  modifiers: []\n  key: \"f2\"\n"
        let config = try YAMLDecoder().decode(AppConfig.self, from: partialYAML)

        XCTAssertEqual(config.hotkey.key, "f2")
        // 老配置文件里没有 mode 字段：必须回落 hold，而不是解码失败。
        XCTAssertEqual(config.hotkey.mode, .hold)
        XCTAssertEqual(config.asr.language, .auto)
        XCTAssertEqual(config.asr.idleUnloadMinutes, 0)
        XCTAssertEqual(config.llm.enabled, false)
        XCTAssertEqual(config.ui.opacity, 0.85, accuracy: 1e-9)
        // 老配置文件里没有这两个新字段：必须回落默认值而不是解码失败。
        XCTAssertEqual(config.ui.hudPosition, .bottomCenter)
        XCTAssertEqual(config.asr.preloadOnLaunch, true)
    }

    func testUnknownEnumValuesFallBackToDefaults() throws {
        let yaml = """
        asr:
          preload_on_launch: true
        hotkey:
          key: "fn"
          mode: "flip-flop"
        ui:
          hud_position: "somewhere-else"
        """
        let config = try YAMLDecoder().decode(AppConfig.self, from: yaml)
        XCTAssertEqual(config.hotkey.mode, .hold, "无法识别的触发方式应回落，而不是让整份配置解码失败")
        XCTAssertEqual(config.ui.hudPosition, .bottomCenter)
        XCTAssertEqual(config.asr.preloadOnLaunch, true)
    }

    /// 读不出来的配置文件：另存一份备份、回落默认配置，而不是让应用停在错误态（与 Windows W5-04 一致）。
    /// 原文件保持不动，直到下一次保存。
    func testUnparsableConfigFallsBackToDefaultsAndKeepsBackup() throws {
        try store.ensureDefaultFiles()
        let broken = "asr: [\nthis is not yaml: : :\n"
        try broken.write(to: store.configURL, atomically: true, encoding: .utf8)

        let config = try store.loadOrCreate()

        XCTAssertTrue(store.lastLoadFellBackToDefaults)
        XCTAssertEqual(config.hotkey.key, "fn", "回落默认配置")
        let backupURL = store.configDirectoryURL.appendingPathComponent(ConfigStore.brokenBackupFileName)
        XCTAssertEqual(try String(contentsOf: backupURL, encoding: .utf8), broken, "备份与坏文件内容一致")
        XCTAssertEqual(try String(contentsOf: store.configURL, encoding: .utf8), broken, "保存之前不动原文件")

        // 用户保存一次之后，再读就正常了。
        try store.save(config: config)
        _ = try store.loadOrCreate()
        XCTAssertFalse(store.lastLoadFellBackToDefaults)
    }

    /// VOC-1：词汇表写回 YAML 后能原样读出，含需要转义的字符。
    func testVocabularyRoundTrips() throws {
        var config = AppConfig()
        config.vocabulary = VocabularyConfig(bias: .strong, entries: [
            VocabularyEntry(term: "Claude", aliases: ["克劳德", "可劳德"]),
            VocabularyEntry(term: "张三丰"),
            VocabularyEntry(term: "say \"hi\" \\ ok", aliases: ["x: y", "- z", "#tag"]),
        ])
        try store.save(config: config)
        let loaded = try store.loadOrCreate()
        XCTAssertEqual(loaded.vocabulary, config.vocabulary)

        var empty = AppConfig()
        empty.vocabulary.bias = .off
        try store.save(config: empty)
        XCTAssertEqual(try store.loadOrCreate().vocabulary, VocabularyConfig(bias: .off, entries: []))
    }
}
