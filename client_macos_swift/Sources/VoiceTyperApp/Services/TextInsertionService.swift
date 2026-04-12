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

@MainActor
final class TextInsertionService {
    private let pasteboard = NSPasteboard.general
    private var pendingRestoreTask: Task<Void, Never>?

    func insert(text: String) -> Bool {
        if insertUsingAccessibility(text: text) {
            return true
        }
        return insertUsingPasteboard(text: text)
    }

    private func insertUsingAccessibility(text: String) -> Bool {
        let systemWide = AXUIElementCreateSystemWide()
        var focusedObject: CFTypeRef?
        let focusedResult = AXUIElementCopyAttributeValue(systemWide, kAXFocusedUIElementAttribute as CFString, &focusedObject)
        guard focusedResult == .success, let focusedObject else {
            return false
        }

        let element = focusedObject as! AXUIElement

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

        let currentNSString = currentValue as NSString
        let selectedRange = selectedTextRange(for: element) ?? CFRange(location: currentNSString.length, length: 0)
        let nsRange = NSRange(location: selectedRange.location, length: selectedRange.length)
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

        let backup = snapshotPasteboard()
        pasteboard.clearContents()
        guard pasteboard.setString(text, forType: .string) else {
            return false
        }

        let changeCount = pasteboard.changeCount
        guard simulatePaste() else {
            return false
        }

        pendingRestoreTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 500_000_000)
            guard !Task.isCancelled else { return }
            self?.restorePasteboardIfNeeded(
                snapshot: backup,
                expectedText: text,
                expectedChangeCount: changeCount
            )
        }

        return true
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

        pasteboard.clearContents()
        let restoredItems = snapshot.items.map { snapshot in
            let item = NSPasteboardItem()
            for (rawType, data) in snapshot.dataByType {
                item.setData(data, forType: NSPasteboard.PasteboardType(rawValue: rawType))
            }
            return item
        }
        if !restoredItems.isEmpty {
            pasteboard.writeObjects(restoredItems)
        }
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
