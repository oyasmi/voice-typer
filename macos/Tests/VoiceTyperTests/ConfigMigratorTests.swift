import XCTest
@testable import VoiceTyper

final class ConfigMigratorTests: XCTestCase {
    func testMigratesHotkeyAndOpacityFromInjectedPath() throws {
        let legacyURL = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: false)
        let legacyYAML = """
        hotkey:
          modifiers:
            - "ctrl"
          key: "f2"
        ui:
          opacity: 0.42
        """
        try legacyYAML.write(to: legacyURL, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: legacyURL) }

        let migrated = ConfigMigrator.migrateFromLegacyClientConfig(
            fileManager: .default,
            legacyURLOverride: legacyURL
        )

        let unwrapped = try XCTUnwrap(migrated)
        XCTAssertEqual(unwrapped.hotkey.modifiers, ["ctrl"])
        XCTAssertEqual(unwrapped.hotkey.key, "f2")
        XCTAssertEqual(unwrapped.ui.opacity, 0.42, accuracy: 1e-9)
    }
}
