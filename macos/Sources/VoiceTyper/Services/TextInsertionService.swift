import AppKit
import ApplicationServices
import Carbon.HIToolbox
import CoreGraphics
import Foundation

private struct PasteboardItemSnapshot {
    let dataByType: [String: Data]
}

private struct PasteboardSnapshot {
    let changeCount: Int
    let items: [PasteboardItemSnapshot]
}

enum TextInsertionResult {
    case inserted
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

    private let pasteboard = NSPasteboard.general
    private var pendingRestoreTask: Task<Void, Never>?

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
        if let expectedFrontmostPID,
           NSWorkspace.shared.frontmostApplication?.processIdentifier != expectedFrontmostPID {
            return .focusChanged
        }
        if insertUsingAccessibility(text: text) {
            return .inserted
        }
        return insertUsingPasteboard(text: text) ? .inserted : .failed
    }

    /// 插入失败时的兜底：把识别结果写入剪贴板，避免长听写内容彻底丢失。
    /// 取消待恢复任务，防止把这次的文本又还原掉。
    func copyToClipboard(text: String) {
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
            return true
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

        return true
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
            return pending.snapshot
        }
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
        guard let source = CGEventSource(stateID: .combinedSessionState),
              let keyDown = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(kVK_ANSI_V), keyDown: true),
              let keyUp = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(kVK_ANSI_V), keyDown: false) else {
            return false
        }

        keyDown.flags = .maskCommand
        keyUp.flags = .maskCommand
        keyDown.post(tap: .cghidEventTap)
        keyUp.post(tap: .cghidEventTap)
        return true
    }
}
