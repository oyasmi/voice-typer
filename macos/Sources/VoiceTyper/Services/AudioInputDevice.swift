import CoreAudio
import Foundation
import IOKit

/// 输入设备的传输类型（由 CoreAudio 的 `kAudioDeviceTransportType*` 归并而来）。
/// rawValue 同时用于耗时日志，只含稳定枚举，不含设备名。
enum AudioTransport: String {
    case builtIn = "builtin"
    case bluetooth
    case usb
    case other
    case unknown
}

struct AudioDeviceInfo: Equatable {
    let id: AudioDeviceID
    let uid: String
    let name: String
    let transport: AudioTransport
    /// 设备存活、输入未静音、输入音量不为 0。
    let isUsableInput: Bool
}

/// 录音时选哪个输入设备，对应配置项 `audio.input_device`。
enum AudioInputPolicy: Hashable {
    /// 默认：跟随系统，仅在「蓝牙耳机通话模式」场景改用内置麦克风。
    case automatic
    /// 严格跟随系统默认输入。
    case systemDefault
    /// 指定设备 UID（预留给手选设备；UID 不存在时回落系统默认）。
    case device(uid: String)

    init(configValue: String) {
        switch configValue.trimmingCharacters(in: .whitespacesAndNewlines) {
        case "", "auto": self = .automatic
        case "system": self = .systemDefault
        case let uid: self = .device(uid: uid)
        }
    }

    var configValue: String {
        switch self {
        case .automatic: return "auto"
        case .systemDefault: return "system"
        case .device(let uid): return uid
        }
    }
}

/// 一次录音实际使用的输入设备，供 HUD 与耗时日志展示。
struct ActiveInputDevice: Equatable {
    let name: String
    let transport: AudioTransport
    /// 是否由「自动」策略从系统默认输入切换而来。
    let switchedByAuto: Bool
}

enum AudioInputDevice {
    // MARK: - 选择策略（纯函数，见 AudioInputDeviceTests）

    /// 决定本次录音使用哪个输入设备；返回 nil 表示"不设置，交给引擎默认"。
    ///
    /// `.automatic` 只有一种情况偏离系统默认：输出与默认输入都是蓝牙（耳机麦克风走通话
    /// 模式，音质差且会拖垮耳机里正在播放的音乐），且有可用的内置麦克风、机器未合盖。
    /// 输出不是蓝牙时，蓝牙输入是用户有意的选择，一律尊重。
    static func resolveInput(
        policy: AudioInputPolicy,
        devices: [AudioDeviceInfo],
        defaultInputID: AudioDeviceID?,
        defaultOutputTransport: AudioTransport,
        lidClosed: Bool
    ) -> (deviceID: AudioDeviceID?, switchedByAuto: Bool) {
        switch policy {
        case .systemDefault:
            return (defaultInputID, false)
        case .device(let uid):
            if let device = devices.first(where: { $0.uid == uid }) {
                return (device.id, false)
            }
            return (defaultInputID, false)
        case .automatic:
            guard defaultOutputTransport == .bluetooth,
                  let defaultInputID,
                  devices.first(where: { $0.id == defaultInputID })?.transport == .bluetooth,
                  !lidClosed,
                  let builtIn = devices.first(where: { $0.transport == .builtIn && $0.isUsableInput })
            else {
                return (defaultInputID, false)
            }
            return (builtIn.id, true)
        }
    }

    /// 读取当前系统状态并按策略解析。
    static func resolveCurrent(policy: AudioInputPolicy) -> (deviceID: AudioDeviceID?, switchedByAuto: Bool) {
        // 只有 `.automatic` 需要输出与合盖信息；其余策略不做多余的 CoreAudio 查询。
        let outputTransport: AudioTransport
        let lidClosed: Bool
        if case .automatic = policy {
            outputTransport = defaultOutputDeviceID().map(transport(of:)) ?? .unknown
            lidClosed = isLidClosed()
        } else {
            outputTransport = .unknown
            lidClosed = false
        }
        return resolveInput(
            policy: policy,
            devices: allInputDevices(),
            defaultInputID: defaultInputDeviceID(),
            defaultOutputTransport: outputTransport,
            lidClosed: lidClosed
        )
    }

    /// 由设备 ID 生成展示信息；取不到名称时返回 nil（HUD 只显示"录音中"）。
    static func activeDevice(id: AudioDeviceID, switchedByAuto: Bool) -> ActiveInputDevice {
        let rawName = stringProperty(id, kAudioObjectPropertyName) ?? ""
        let name = rawName.trimmingCharacters(in: .whitespacesAndNewlines)
        return ActiveInputDevice(name: name, transport: transport(of: id), switchedByAuto: switchedByAuto)
    }

    // MARK: - CoreAudio 读取（薄封装：任何 OSStatus 失败都返回空 / nil）

    static func defaultInputDeviceID() -> AudioDeviceID? {
        defaultDeviceID(kAudioHardwarePropertyDefaultInputDevice)
    }

    static func defaultOutputDeviceID() -> AudioDeviceID? {
        defaultDeviceID(kAudioHardwarePropertyDefaultOutputDevice)
    }

    static func allInputDevices() -> [AudioDeviceInfo] {
        var address = propertyAddress(kAudioHardwarePropertyDevices)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr,
              size > 0 else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &ids) == noErr else {
            return []
        }
        return ids.compactMap { id in
            guard hasInputStreams(id), let uid = stringProperty(id, kAudioDevicePropertyDeviceUID) else { return nil }
            return AudioDeviceInfo(
                id: id,
                uid: uid,
                name: stringProperty(id, kAudioObjectPropertyName) ?? "",
                transport: transport(of: id),
                isUsableInput: isUsableInput(id)
            )
        }
    }

    /// 合盖状态（IOKit `AppleClamshellState`）；读不到按未合盖处理。
    static func isLidClosed() -> Bool {
        let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOPMrootDomain"))
        guard service != IO_OBJECT_NULL else { return false }
        defer { IOObjectRelease(service) }
        let value = IORegistryEntryCreateCFProperty(service, "AppleClamshellState" as CFString, kCFAllocatorDefault, 0)
        return (value?.takeRetainedValue() as? Bool) ?? false
    }

    static func transport(of id: AudioDeviceID) -> AudioTransport {
        var address = propertyAddress(kAudioDevicePropertyTransportType)
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr else { return .unknown }
        switch value {
        case kAudioDeviceTransportTypeBuiltIn: return .builtIn
        case kAudioDeviceTransportTypeBluetooth, kAudioDeviceTransportTypeBluetoothLE: return .bluetooth
        case kAudioDeviceTransportTypeUSB: return .usb
        default: return .other
        }
    }

    // MARK: - 私有

    private static func propertyAddress(
        _ selector: AudioObjectPropertySelector,
        scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal
    ) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    }

    private static func defaultDeviceID(_ selector: AudioObjectPropertySelector) -> AudioDeviceID? {
        var address = propertyAddress(selector)
        var id = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &id) == noErr,
              id != AudioDeviceID(kAudioObjectUnknown) else { return nil }
        return id
    }

    private static func stringProperty(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = propertyAddress(selector)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        let status = withUnsafeMutablePointer(to: &value) {
            AudioObjectGetPropertyData(id, &address, 0, nil, &size, $0)
        }
        guard status == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }

    private static func hasInputStreams(_ id: AudioDeviceID) -> Bool {
        var address = propertyAddress(kAudioDevicePropertyStreams, scope: kAudioObjectPropertyScopeInput)
        var size: UInt32 = 0
        return AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size) == noErr && size > 0
    }

    private static func uint32Property(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector, scope: AudioObjectPropertyScope) -> UInt32? {
        var address = propertyAddress(selector, scope: scope)
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr else { return nil }
        return value
    }

    /// 存活、输入 scope 未静音、输入音量读不到或大于 0。读不到的属性一律按"没有问题"处理——
    /// 多数设备并不暴露静音/音量属性，不能因此把它们判为不可用。
    private static func isUsableInput(_ id: AudioDeviceID) -> Bool {
        if let alive = uint32Property(id, kAudioDevicePropertyDeviceIsAlive, scope: kAudioObjectPropertyScopeGlobal), alive == 0 {
            return false
        }
        if let muted = uint32Property(id, kAudioDevicePropertyMute, scope: kAudioObjectPropertyScopeInput), muted != 0 {
            return false
        }
        var address = propertyAddress(kAudioDevicePropertyVolumeScalar, scope: kAudioObjectPropertyScopeInput)
        var volume: Float32 = 1
        var size = UInt32(MemoryLayout<Float32>.size)
        if AudioObjectGetPropertyData(id, &address, 0, nil, &size, &volume) == noErr, volume <= 0 {
            return false
        }
        return true
    }
}
