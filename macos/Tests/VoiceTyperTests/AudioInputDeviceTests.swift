import CoreAudio
import XCTest
@testable import VoiceTyper

final class AudioInputDeviceTests: XCTestCase {
    private let builtIn = AudioDeviceInfo(id: 1, uid: "builtin-mic", name: "MacBook 麦克风", transport: .builtIn, isUsableInput: true)
    private let airPods = AudioDeviceInfo(id: 2, uid: "airpods", name: "AirPods", transport: .bluetooth, isUsableInput: true)
    private let usbMic = AudioDeviceInfo(id: 3, uid: "usb-mic", name: "USB Mic", transport: .usb, isUsableInput: true)

    private func resolve(
        _ policy: AudioInputPolicy = .automatic,
        devices: [AudioDeviceInfo]? = nil,
        defaultInput: AudioDeviceID? = 2,
        output: AudioTransport = .bluetooth,
        lidClosed: Bool = false
    ) -> (deviceID: AudioDeviceID?, switchedByAuto: Bool) {
        AudioInputDevice.resolveInput(
            policy: policy,
            devices: devices ?? [builtIn, airPods, usbMic],
            defaultInputID: defaultInput,
            defaultOutputTransport: output,
            lidClosed: lidClosed
        )
    }

    func testAutoSwitchesToBuiltInWhenBluetoothOutputAndInput() {
        let result = resolve()
        XCTAssertEqual(result.deviceID, 1)
        XCTAssertTrue(result.switchedByAuto)
    }

    func testAutoKeepsDefaultWhenLidClosed() {
        let result = resolve(lidClosed: true)
        XCTAssertEqual(result.deviceID, 2)
        XCTAssertFalse(result.switchedByAuto)
    }

    func testAutoKeepsDefaultWhenAlreadyBuiltIn() {
        let result = resolve(defaultInput: 1)
        XCTAssertEqual(result.deviceID, 1)
        XCTAssertFalse(result.switchedByAuto)
    }

    /// 输出不是蓝牙时，蓝牙麦克风是用户有意的选择（比如外放 + 耳机麦），一律尊重。
    func testAutoRespectsBluetoothInputWhenOutputIsNotBluetooth() {
        for output in [AudioTransport.builtIn, .usb, .other, .unknown] {
            let result = resolve(output: output)
            XCTAssertEqual(result.deviceID, 2, "输出为 \(output) 时不应改用内置麦克风")
            XCTAssertFalse(result.switchedByAuto)
        }
    }

    func testAutoKeepsDefaultWhenNoBuiltInInput() {
        let result = resolve(devices: [airPods, usbMic])
        XCTAssertEqual(result.deviceID, 2)
        XCTAssertFalse(result.switchedByAuto)
    }

    func testAutoKeepsDefaultWhenBuiltInIsUnusable() {
        let muted = AudioDeviceInfo(id: 1, uid: "builtin-mic", name: "MacBook 麦克风", transport: .builtIn, isUsableInput: false)
        let result = resolve(devices: [muted, airPods])
        XCTAssertEqual(result.deviceID, 2)
        XCTAssertFalse(result.switchedByAuto)
    }

    func testSystemPolicyAlwaysUsesDefaultInput() {
        let result = resolve(.systemDefault)
        XCTAssertEqual(result.deviceID, 2)
        XCTAssertFalse(result.switchedByAuto)
    }

    func testSpecificDeviceIsUsedWhenPresentAndFallsBackWhenMissing() {
        XCTAssertEqual(resolve(.device(uid: "usb-mic")).deviceID, 3)
        XCTAssertFalse(resolve(.device(uid: "usb-mic")).switchedByAuto)
        XCTAssertEqual(resolve(.device(uid: "unplugged")).deviceID, 2)
    }

    func testNoDefaultInputAndSystemPolicyYieldsNil() {
        XCTAssertNil(resolve(.systemDefault, devices: [], defaultInput: nil).deviceID)
    }

    func testPolicyConfigValueMapping() {
        XCTAssertEqual(AudioInputPolicy(configValue: "auto"), .automatic)
        XCTAssertEqual(AudioInputPolicy(configValue: ""), .automatic)
        XCTAssertEqual(AudioInputPolicy(configValue: "  "), .automatic)
        XCTAssertEqual(AudioInputPolicy(configValue: "system"), .systemDefault)
        XCTAssertEqual(AudioInputPolicy(configValue: "BuiltInMicrophoneDevice"), .device(uid: "BuiltInMicrophoneDevice"))

        for policy in [AudioInputPolicy.automatic, .systemDefault, .device(uid: "some-uid")] {
            XCTAssertEqual(AudioInputPolicy(configValue: policy.configValue), policy)
        }
    }
}
