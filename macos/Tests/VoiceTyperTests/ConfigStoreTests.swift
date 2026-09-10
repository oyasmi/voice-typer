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
}
