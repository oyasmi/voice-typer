import AppKit
import ApplicationServices
import Carbon
import CoreGraphics
import Foundation

private struct PasteboardItemSnapshot {
    let dataByType: [String: Data]
}

private struct PasteboardSnapshot {
    let changeCount: Int
    let items: [PasteboardItemSnapshot]
}

/// 文本实际走的插入路径，仅用于耗时度量。
enum InsertionPath: String {
    case accessibility = "ax"
    case paste
}

/// AX 写入成功后的回读判定（R8-A3a）。本批只记录、不据此回退粘贴。
enum InsertionVerification: String, Equatable {
    /// 值变了且包含插入的文本。
    case ok
    /// 值完全没变——AX 报告成功，但控件可能没有真正接受（网页里由前端框架接管的输入框）。
    case unchanged
    /// 写入前后的值读不到（或变了但不含插入文本）。
    case unknown

    nonisolated static func classify(before: String?, after: String?, inserted: String) -> InsertionVerification {
        guard let before, let after else { return .unknown }
        if after == before { return .unchanged }
        return after.contains(inserted) ? .ok : .unknown
    }
}

enum TextInsertionResult: Equatable {
    case inserted(InsertionPath)
    /// 录音开始到插入之间前台应用已切换：为避免写入用户未预期的窗口（最坏情况是
    /// 密码框），不再插入，只把结果复制到剪贴板（F-10）。
    case focusChanged
    case failed
}

@MainActor
final class TextInsertionService {
    /// AX 全量 value 替换是对整个文本字段的字符串重建，对超长文档成本很高，
    /// 且如果连 kAXSelectedTextAttribute 都不可设置，多半意味着该控件的 AX 支持
    /// 本身就不完整——这种规模直接放弃 AX，走粘贴更可靠。
    private static let maxAXValueLengthForFullReplace = 100_000

    /// 社区约定（Paste / Maccy / CleanClipboard 等剪贴板历史工具遵循）：带有这两个类型的
    /// 剪贴板内容会被直接跳过，不计入历史。听写内容本应"用完即走"，若不标记，走粘贴兜底路径
    /// 的每一段识别文本都会被这类工具在恢复前的 1 秒窗口内永久留档，与"音频仅在设备端处理"
    /// 的隐私承诺相悖（R3-15）。
    private static let concealedType = NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType")
    private static let transientType = NSPasteboard.PasteboardType("org.nspasteboard.TransientType")

    /// AX 写入后到回读之间的等待：异步更新的控件需要一点时间才会反映到辅助功能层。
    private static let verifyDelay: TimeInterval = 0.05
    /// 沿 `kAXParentAttribute` 向上查找 `AXWebArea` 的最大层数。
    private static let maxWebAreaAncestorDepth = 20

    private let pasteboard = NSPasteboard.general
    private var pendingRestoreTask: Task<Void, Never>?

    /// 最近一次 `insert` 的 AX 回读判定；未走 AX 为 nil。内容只在判定函数内比较，不写日志。
    private(set) var lastInsertVerification: InsertionVerification?

    /// 识别阶段提前备份的剪贴板（R8-A5）。粘贴兜底时若 `changeCount` 未变就直接使用，
    /// 不再在「松键 → 出字」的关键路径上同步读取剪贴板全部类型。
    private var preparedBackup: PasteboardSnapshot?

    /// 上一次粘贴兜底尚未完成的恢复：记录当时备份的「用户原始剪贴板」快照，以及我们
    /// 写进剪贴板的临时文本和写入后的 changeCount。在临时恢复窗口内再次走粘贴兜底时，
    /// 若剪贴板仍是这次的临时文本（用户没有复制新内容），下一次必须**继承**这份原始
    /// 快照，而不是把当前这段听写临时文本当成「用户原始内容」快照下来——否则连续两次
    /// 兜底会把用户最初的剪贴板永久覆盖掉。
    private struct PendingRestore {
        let snapshot: PasteboardSnapshot
        let writtenText: String
        let writtenChangeCount: Int
    }
    private var pendingRestore: PendingRestore?

    /// 纯函数：连续粘贴兜底时，判断本次是否应继承上一次 pending 的原始快照。
    /// true = 剪贴板仍是上一段听写写入的临时文本且用户未复制新内容，应继承；
    /// false = 剪贴板已变化（用户复制了新内容，或已被别处改写），应重新快照当前内容。
    nonisolated static func shouldInheritPendingSnapshot(
        currentChangeCount: Int,
        currentString: String?,
        pendingWrittenChangeCount: Int,
        pendingWrittenText: String
    ) -> Bool {
        currentChangeCount == pendingWrittenChangeCount && currentString == pendingWrittenText
    }

    /// - Parameter expectedFrontmostPID: 录音开始时记录的前台应用 pid；插入前若与
    ///   当前前台应用不一致，说明用户已切换焦点，直接放弃插入、只复制到剪贴板。
    func insert(text: String, expectedFrontmostPID: pid_t?) -> TextInsertionResult {
        lastInsertVerification = nil
        if let expectedFrontmostPID,
           NSWorkspace.shared.frontmostApplication?.processIdentifier != expectedFrontmostPID {
            return .focusChanged
        }
        if insertUsingAccessibility(text: text) {
            return .inserted(.accessibility)
        }
        return insertUsingPasteboard(text: text) ? .inserted(.paste) : .failed
    }

    /// 进入识别阶段时提前备份用户剪贴板（主线程，与 asrQueue 上的推理并行）。
    /// 仍有待恢复的粘贴（`pendingRestore`）时不备份：此刻剪贴板里是我们写的临时文本，
    /// 插入时由继承逻辑取回用户的原始快照。返回备份耗时（纳秒）。
    @discardableResult
    func prepareForInsert() -> UInt64? {
        guard pendingRestore == nil else { return nil }
        let started = DispatchTime.now().uptimeNanoseconds
        preparedBackup = snapshotPasteboard()
        return DispatchTime.now().uptimeNanoseconds &- started
    }

    /// 本次听写不再插入（取消、失败、空结果…）时丢弃提前备份。
    func discardPreparedBackup() {
        preparedBackup = nil
    }

    /// 插入失败时的兜底：把识别结果写入剪贴板，避免长听写内容彻底丢失。
    /// 取消待恢复任务，防止把这次的文本又还原掉。
    func copyToClipboard(text: String) {
        preparedBackup = nil
        pendingRestoreTask?.cancel()
        pendingRestoreTask = nil
        pendingRestore = nil
        pasteboard.clearContents()
        _ = writeConcealedString(text)
    }

    /// 写入一份带 concealed/transient 标记的纯文本。`writeObjects` 会先清空当前内容再写入，
    /// 调用方需自行决定是否需要保留原有 pasteboard 内容（此处不清空，交给调用方处理）。
    @discardableResult
    private func writeConcealedString(_ text: String) -> Bool {
        let item = NSPasteboardItem()
        item.setString(text, forType: .string)
        item.setData(Data(), forType: Self.concealedType)
        item.setData(Data(), forType: Self.transientType)
        return pasteboard.writeObjects([item])
    }

    private func insertUsingAccessibility(text: String) -> Bool {
        let systemWide = AXUIElementCreateSystemWide()
        var focusedObject: CFTypeRef?
        let focusedResult = AXUIElementCopyAttributeValue(systemWide, kAXFocusedUIElementAttribute as CFString, &focusedObject)
        guard focusedResult == .success, let focusedObject else {
            return false
        }

        let element = focusedObject as! AXUIElement

        // 写入前先读一次值，用于写入后的回读判定。读不到或超长就不判定（unknown）。
        let valueBefore = Self.readValue(of: element)

        // 优先走标准的"替换选区"接口：天然保留富文本与 undo 语义，不是整字段重建，
        // 也不需要手动计算插入后的新光标位置（F-10）。
        var isSelectedTextSettable = DarwinBoolean(false)
        let selectedTextSettableResult = AXUIElementIsAttributeSettable(
            element,
            kAXSelectedTextAttribute as CFString,
            &isSelectedTextSettable
        )
        if selectedTextSettableResult == .success, isSelectedTextSettable.boolValue,
           AXUIElementSetAttributeValue(element, kAXSelectedTextAttribute as CFString, text as CFTypeRef) == .success {
            recordVerification(element: element, before: valueBefore, inserted: text)
            return true
        }

        // 网页控件（前端框架接管的输入框）整值替换可能只改到辅助功能层，下一次按键文字就消失：
        // 不做整值替换，直接走粘贴（R8-A3a）。`kAXSelectedText` 路径不受影响。
        if Self.hasWebAreaAncestor(element) {
            return false
        }

        var isValueSettable = DarwinBoolean(false)
        let settableResult = AXUIElementIsAttributeSettable(
            element,
            kAXValueAttribute as CFString,
            &isValueSettable
        )
        guard settableResult == .success, isValueSettable.boolValue else {
            return false
        }

        var valueObject: CFTypeRef?
        let valueResult = AXUIElementCopyAttributeValue(element, kAXValueAttribute as CFString, &valueObject)
        guard valueResult == .success, let currentValue = valueObject as? String else {
            return false
        }
        guard currentValue.count <= Self.maxAXValueLengthForFullReplace else {
            return false
        }

        let currentNSString = currentValue as NSString
        let selectedRange = selectedTextRange(for: element) ?? CFRange(location: currentNSString.length, length: 0)
        guard let nsRange = Self.validInsertionRange(selectedRange, in: currentNSString.length) else {
            // AX 控件返回的 CFRange 不可信：kCFNotFound、负值、越界都可能出现，直接喂给
            // NSString.replacingCharacters 会抛 NSRangeException（Swift 无法捕获，直接崩溃）。
            // 放弃 AX 全量替换、回退到粘贴，而不是猜一个位置写进用户没预期的地方（R2-05a）。
            AppLog.app.warning("AX 选区越界，放弃 AX 插入，回退到粘贴")
            return false
        }
        let updatedValue = currentNSString.replacingCharacters(in: nsRange, with: text)
        let setResult = AXUIElementSetAttributeValue(element, kAXValueAttribute as CFString, updatedValue as CFTypeRef)

        guard setResult == .success else {
            return false
        }

        let insertedLength = (text as NSString).length
        var newRange = CFRange(location: nsRange.location + insertedLength, length: 0)
        if let axRange = AXValueCreate(.cfRange, &newRange) {
            _ = AXUIElementSetAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, axRange)
        }

        recordVerification(element: element, before: valueBefore, inserted: text)
        return true
    }

    /// 写入成功后等一小会儿再读一次值，得到回读判定（R8-A3a）。日志里只有判定结果，没有任何文本。
    private func recordVerification(element: AXUIElement, before: String?, inserted: String) {
        Thread.sleep(forTimeInterval: Self.verifyDelay)
        let after = before == nil ? nil : Self.readValue(of: element)
        lastInsertVerification = .classify(before: before, after: after, inserted: inserted)
    }

    /// 读控件的整个值；读不到、不是字符串或超过上限（100k 字符）返回 nil。
    private static func readValue(of element: AXUIElement) -> String? {
        var object: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXValueAttribute as CFString, &object) == .success,
              let value = object as? String, value.count <= maxAXValueLengthForFullReplace else {
            return nil
        }
        return value
    }

    /// 焦点元素自身或其祖先（最多 20 层）是否为 `AXWebArea`，即处于网页内容里。
    private static func hasWebAreaAncestor(_ element: AXUIElement) -> Bool {
        var current: AXUIElement? = element
        for _ in 0..<maxWebAreaAncestorDepth {
            guard let node = current else { return false }
            var roleObject: CFTypeRef?
            if AXUIElementCopyAttributeValue(node, kAXRoleAttribute as CFString, &roleObject) == .success,
               (roleObject as? String) == "AXWebArea" {
                return true
            }
            var parentObject: CFTypeRef?
            guard AXUIElementCopyAttributeValue(node, kAXParentAttribute as CFString, &parentObject) == .success,
                  let parentObject, CFGetTypeID(parentObject) == AXUIElementGetTypeID() else {
                return false
            }
            current = (parentObject as! AXUIElement)
        }
        return false
    }

    /// AX 控件返回的 `CFRange` 不可信：`kCFNotFound`、负值、越界都可能出现，直接喂给
    /// `NSString.replacingCharacters` 会抛 `NSRangeException`（Swift 无法捕获，直接崩溃）。
    /// 返回 nil 表示范围非法，调用方应放弃 AX 全量替换、回退到粘贴（R2-05a）。
    nonisolated static func validInsertionRange(_ range: CFRange, in length: Int) -> NSRange? {
        guard range.location >= 0, range.length >= 0,
              range.location <= length,
              length - range.location >= range.length else { return nil }
        return NSRange(location: range.location, length: range.length)
    }

    private func selectedTextRange(for element: AXUIElement) -> CFRange? {
        var rangeObject: CFTypeRef?
        let result = AXUIElementCopyAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, &rangeObject)
        guard result == .success, let rangeObject, CFGetTypeID(rangeObject) == AXValueGetTypeID() else {
            return nil
        }

        let value = rangeObject as! AXValue
        guard AXValueGetType(value) == .cfRange else {
            return nil
        }

        var range = CFRange()
        return AXValueGetValue(value, .cfRange, &range) ? range : nil
    }

    private func insertUsingPasteboard(text: String) -> Bool {
        // 取消上一次的剪贴板恢复任务，避免与本次操作冲突
        pendingRestoreTask?.cancel()
        pendingRestoreTask = nil

        let backup = backupSnapshotInheritingPendingIfNeeded()
        pasteboard.clearContents()
        guard writeConcealedString(text) else {
            // 剪贴板已被 clearContents() 清空，必须先恢复用户原内容，
            // 否则失败之余还会把用户原剪贴板永久丢失（F-10）。
            restorePasteboardContentsUnconditionally(backup)
            pendingRestore = nil
            return false
        }

        let changeCount = pasteboard.changeCount
        guard simulatePaste() else {
            restorePasteboardContentsUnconditionally(backup)
            pendingRestore = nil
            return false
        }

        pendingRestore = PendingRestore(snapshot: backup, writtenText: text, writtenChangeCount: changeCount)
        pendingRestoreTask = Task { @MainActor [weak self] in
            // changeCount 守卫已保证不会覆盖用户在此期间的新复制内容，
            // 延长只是给慢应用更多时间读取剪贴板（原 500ms 对部分应用偏短）。
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            guard !Task.isCancelled else { return }
            self?.restorePasteboardIfNeeded(
                snapshot: backup,
                expectedText: text,
                expectedChangeCount: changeCount
            )
            self?.pendingRestore = nil
        }

        return true
    }

    /// 取备份快照：若仍处于上一次粘贴兜底的临时恢复窗口内、且剪贴板未被用户改动，
    /// 继承上一次备份的「用户原始快照」；否则重新快照当前剪贴板内容。
    private func backupSnapshotInheritingPendingIfNeeded() -> PasteboardSnapshot {
        if let pending = pendingRestore,
           Self.shouldInheritPendingSnapshot(
               currentChangeCount: pasteboard.changeCount,
               currentString: pasteboard.string(forType: .string),
               pendingWrittenChangeCount: pending.writtenChangeCount,
               pendingWrittenText: pending.writtenText
           ) {
            preparedBackup = nil
            return pending.snapshot
        }
        // 识别阶段提前备份的快照：剪贴板没变过就直接用（R8-A5）。
        if let prepared = preparedBackup, prepared.changeCount == pasteboard.changeCount {
            preparedBackup = nil
            return prepared
        }
        preparedBackup = nil
        return snapshotPasteboard()
    }

    private func restorePasteboardContentsUnconditionally(_ snapshot: PasteboardSnapshot) {
        pasteboard.clearContents()
        let restoredItems = snapshot.items.map { item -> NSPasteboardItem in
            let pasteboardItem = NSPasteboardItem()
            for (rawType, data) in item.dataByType {
                pasteboardItem.setData(data, forType: NSPasteboard.PasteboardType(rawValue: rawType))
            }
            return pasteboardItem
        }
        if !restoredItems.isEmpty {
            pasteboard.writeObjects(restoredItems)
        }
    }

    private func snapshotPasteboard() -> PasteboardSnapshot {
        let items = (pasteboard.pasteboardItems ?? []).map { item in
            var dataByType: [String: Data] = [:]
            for type in item.types {
                if let data = item.data(forType: type) {
                    dataByType[type.rawValue] = data
                }
            }
            return PasteboardItemSnapshot(dataByType: dataByType)
        }
        return PasteboardSnapshot(changeCount: pasteboard.changeCount, items: items)
    }

    private func restorePasteboardIfNeeded(snapshot: PasteboardSnapshot, expectedText: String, expectedChangeCount: Int) {
        guard pasteboard.changeCount == expectedChangeCount else {
            return
        }
        guard pasteboard.string(forType: .string) == expectedText else {
            return
        }
        restorePasteboardContentsUnconditionally(snapshot)
    }

    private func simulatePaste() -> Bool {
        // 键码是物理键位，目标应用按当前布局把它翻译成字符：Dvorak 下 ANSI_V 键位是 k，
        // 固定发它会变成 ⌘K。每次现查（用户可能随时切换布局，开销可忽略）。
        let keyCode = PasteKeyResolver.currentPasteKeyCode()
        guard let source = CGEventSource(stateID: .combinedSessionState),
              let keyDown = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: true),
              let keyUp = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: false) else {
            return false
        }

        keyDown.flags = .maskCommand
        keyUp.flags = .maskCommand
        keyDown.post(tap: .cghidEventTap)
        keyUp.post(tap: .cghidEventTap)
        return true
    }
}

/// 找出"在当前键盘布局下产生字符 v"的键码，让模拟粘贴在 Dvorak、Colemak 等布局下仍是 ⌘V。
enum PasteKeyResolver {
    private static let qwertyV = CGKeyCode(kVK_ANSI_V)

    /// - Parameters:
    ///   - layoutID / layoutName: 当前键盘布局的 ID 与本地化名称。
    ///   - translate: 该键码在无修饰键时产生的字符。
    nonisolated static func pasteKeyCode(
        layoutID: String?, layoutName: String?, translate: (CGKeyCode) -> String?
    ) -> CGKeyCode {
        // "按住 ⌘ 时切回 QWERTY"的布局（如 Dvorak - QWERTY ⌘）：⌘V 的键位仍是 QWERTY 的 V。
        if layoutID?.contains("QWERTYCMD") == true || layoutName?.hasSuffix("⌘") == true {
            return qwertyV
        }
        for code in CGKeyCode(0)...CGKeyCode(127) where translate(code)?.lowercased() == "v" {
            return code
        }
        return qwertyV
    }

    /// 读取当前键盘布局（中文输入法下返回其底层键盘布局，通常是 ABC）。TIS 调用须在主线程。
    @MainActor
    static func currentPasteKeyCode() -> CGKeyCode {
        guard let source = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue() else {
            AppLog.app.warning("无法读取当前键盘布局，模拟粘贴回落到 QWERTY 的 V 键位")
            return qwertyV
        }
        func property(_ key: CFString) -> AnyObject? {
            guard let raw = TISGetInputSourceProperty(source, key) else { return nil }
            return Unmanaged<AnyObject>.fromOpaque(raw).takeUnretainedValue()
        }
        let layoutID = property(kTISPropertyInputSourceID) as? String
        let layoutName = property(kTISPropertyLocalizedName) as? String
        guard let layoutData = property(kTISPropertyUnicodeKeyLayoutData) else {
            AppLog.app.warning("当前键盘布局没有字符映射数据，模拟粘贴回落到 QWERTY 的 V 键位")
            return qwertyV
        }
        let data = layoutData as! CFData
        let layout = unsafeBitCast(CFDataGetBytePtr(data), to: UnsafePointer<UCKeyboardLayout>.self)
        let keyboardType = UInt32(LMGetKbdType())

        return pasteKeyCode(layoutID: layoutID, layoutName: layoutName) { code in
            var deadKeyState: UInt32 = 0
            var chars = [UniChar](repeating: 0, count: 4)
            var length = 0
            let status = UCKeyTranslate(
                layout, UInt16(code), UInt16(kUCKeyActionDisplay), 0, keyboardType,
                OptionBits(kUCKeyTranslateNoDeadKeysBit), &deadKeyState, chars.count, &length, &chars
            )
            guard status == noErr, length > 0 else { return nil }
            return String(utf16CodeUnits: chars, count: length)
        }
    }
}
