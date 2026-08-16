import XCTest
import Yams
@testable import VoiceTyper

final class ConfigStoreTests: XCTestCase {
    private var tempDir: URL!
    private var store: ConfigStore!

    override func setUpWithError() throws {
        tempDir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        store = ConfigStore(baseDirectoryOverride: tempDir)
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
        config.llm.enabled = true
        config.llm.baseURL = "https://api.example.com/v1"
        config.llm.model = "gpt-4o"
        config.llm.temperature = 0.3
        config.llm.maxTokens = 1200
        config.llm.timeout = 8
        config.hotkey = HotkeyConfig(modifiers: ["ctrl"], key: "f2")
        config.ui.opacity = 0.7

        try store.save(config: config)
        let loaded = try store.loadOrCreate()

        XCTAssertEqual(loaded.asr.language, .zh)
        XCTAssertEqual(loaded.asr.threads, 4)
        XCTAssertEqual(loaded.asr.modelDir, "/tmp/custom-model")
        XCTAssertEqual(loaded.asr.idleUnloadMinutes, 30)
        XCTAssertEqual(loaded.llm.enabled, true)
        XCTAssertEqual(loaded.llm.baseURL, "https://api.example.com/v1")
        XCTAssertEqual(loaded.llm.model, "gpt-4o")
        XCTAssertEqual(loaded.llm.temperature, 0.3, accuracy: 1e-9)
        XCTAssertEqual(loaded.llm.maxTokens, 1200)
        XCTAssertEqual(loaded.llm.timeout, 8, accuracy: 1e-9)
        XCTAssertEqual(loaded.hotkey.modifiers, ["ctrl"])
        XCTAssertEqual(loaded.hotkey.key, "f2")
        XCTAssertEqual(loaded.ui.opacity, 0.7, accuracy: 1e-9)
    }

    func testCreatesDefaultFileOnFirstLoad() throws {
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.configURL.path))
        let config = try store.loadOrCreate()
        XCTAssertTrue(FileManager.default.fileExists(atPath: store.configURL.path))
        XCTAssertEqual(config.hotkey.key, "fn") // 默认值
        XCTAssertEqual(config.asr.idleUnloadMinutes, 10) // 默认值
    }

    func testMissingFieldsFallBackToDefaults() throws {
        // 只写 hotkey，其余字段全部缺失，应回落到各自默认值而不是解码失败。
        let partialYAML = "hotkey:\n  modifiers: []\n  key: \"f2\"\n"
        let config = try YAMLDecoder().decode(AppConfig.self, from: partialYAML)

        XCTAssertEqual(config.hotkey.key, "f2")
        XCTAssertEqual(config.asr.language, .auto)
        XCTAssertEqual(config.asr.idleUnloadMinutes, 10)
        XCTAssertEqual(config.llm.enabled, false)
        XCTAssertEqual(config.ui.opacity, 0.85, accuracy: 1e-9)
    }
}
