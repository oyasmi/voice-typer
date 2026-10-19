import AppKit
import ApplicationServices
import Foundation

/// 焦点窗口 / 插入点的屏幕几何（R8-E2、R8-E3）。
///
/// 辅助功能 API 的坐标原点在**主屏左上角**、y 向下；Cocoa 的坐标原点在主屏左下角、y 向上。
/// 纯计算部分（坐标换算、选屏、定位）与 AX 读取分开，前者可脱离系统单测。
enum FocusGeometry {
    // MARK: - 纯计算

    /// AX 坐标 → Cocoa 坐标：`y = 主屏高度 - axY - 高度`。
    static func cocoaRect(fromAX rect: CGRect, primaryScreenHeight: CGFloat) -> CGRect {
        CGRect(x: rect.minX, y: primaryScreenHeight - rect.minY - rect.height, width: rect.width, height: rect.height)
    }

    /// 矩形所在的屏：优先取中心点所在屏，其次取相交面积最大的屏；都不相交返回 nil。
    static func screenIndex(for rect: CGRect, screenFrames: [CGRect]) -> Int? {
        let center = CGPoint(x: rect.midX, y: rect.midY)
        if let index = screenFrames.firstIndex(where: { $0.contains(center) }) { return index }
        var best: (index: Int, area: CGFloat)?
        for (index, frame) in screenFrames.enumerated() {
            let overlap = frame.intersection(rect)
            guard !overlap.isNull else { continue }
            let area = overlap.width * overlap.height
            if area > (best?.area ?? 0) { best = (index, area) }
        }
        return best?.index
    }

    /// 插入点矩形是否可信：不少应用（Electron、自绘控件）会返回全零或离谱的坐标，
    /// 这种值宁可当作「取不到」回落到鼠标位置，也不能把浮窗放到屏幕外的某处。
    static func isPlausibleCaret(_ rect: CGRect, screenFrames: [CGRect]) -> Bool {
        guard rect.origin.x.isFinite, rect.origin.y.isFinite, rect.width.isFinite, rect.height.isFinite,
              rect.height > 0, rect.height < 400 else { return false }
        let center = CGPoint(x: rect.midX, y: rect.midY)
        return screenFrames.contains { $0.contains(center) }
    }

    /// 浮窗放在插入点下方 `gap`；下方放不下（低于可见区下沿加边距）翻到上方。
    /// 返回未做左右夹逼的左下角原点，夹逼由调用方统一处理。
    static func originNearCaret(caret: CGRect, size: CGSize, visible: CGRect, gap: CGFloat, margin: CGFloat) -> CGPoint {
        let x = caret.midX - size.width / 2
        let below = caret.minY - gap - size.height
        if below >= visible.minY + margin {
            return CGPoint(x: x, y: below)
        }
        return CGPoint(x: x, y: caret.maxY + gap)
    }

    // MARK: - AX 读取（主线程，超时见 `AXMessaging.probeTimeout`）

    /// 前台应用焦点窗口的位置与尺寸（Cocoa 坐标）。取不到返回 nil。
    @MainActor
    static func focusedWindowFrame() -> CGRect? {
        guard AXIsProcessTrusted(),
              let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier,
              let primaryHeight = NSScreen.screens.first?.frame.height else { return nil }
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, AXMessaging.probeTimeout)

        var windowObject: CFTypeRef?
        guard AXUIElementCopyAttributeValue(app, kAXFocusedWindowAttribute as CFString, &windowObject) == .success,
              let windowObject, CFGetTypeID(windowObject) == AXUIElementGetTypeID() else { return nil }
        let window = windowObject as! AXUIElement

        var positionObject: CFTypeRef?
        var sizeObject: CFTypeRef?
        guard AXUIElementCopyAttributeValue(window, kAXPositionAttribute as CFString, &positionObject) == .success,
              AXUIElementCopyAttributeValue(window, kAXSizeAttribute as CFString, &sizeObject) == .success,
              let positionObject, let sizeObject,
              CFGetTypeID(positionObject) == AXValueGetTypeID(), CFGetTypeID(sizeObject) == AXValueGetTypeID()
        else { return nil }
        var position = CGPoint.zero
        var size = CGSize.zero
        guard AXValueGetValue(positionObject as! AXValue, .cgPoint, &position),
              AXValueGetValue(sizeObject as! AXValue, .cgSize, &size),
              size.width > 0, size.height > 0 else { return nil }
        return cocoaRect(
            fromAX: CGRect(origin: position, size: size), primaryScreenHeight: primaryHeight
        )
    }

    /// 焦点元素的插入点矩形（Cocoa 坐标）：`kAXSelectedTextRange` → `kAXBoundsForRange`，
    /// 长度 0 取不到时依次试选区后一个字符、前一个字符（取其后沿）。取不到或不可信返回 nil。
    @MainActor
    static func caretRect() -> CGRect? {
        guard AXIsProcessTrusted(), let primaryHeight = NSScreen.screens.first?.frame.height,
              let element = AXMessaging.focusedElement(timeout: AXMessaging.probeTimeout) else { return nil }

        var rangeObject: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, &rangeObject) == .success,
              let rangeObject, CFGetTypeID(rangeObject) == AXValueGetTypeID() else { return nil }
        var selection = CFRange()
        guard AXValueGetValue(rangeObject as! AXValue, .cfRange, &selection), selection.location >= 0 else { return nil }

        let frames = NSScreen.screens.map(\.frame)
        func bounds(location: Int, length: Int) -> CGRect? {
            var range = CFRange(location: location, length: length)
            guard let rangeValue = AXValueCreate(.cfRange, &range) else { return nil }
            var boundsObject: CFTypeRef?
            guard AXUIElementCopyParameterizedAttributeValue(
                element, kAXBoundsForRangeParameterizedAttribute as CFString, rangeValue, &boundsObject
            ) == .success, let boundsObject, CFGetTypeID(boundsObject) == AXValueGetTypeID() else { return nil }
            var rect = CGRect.zero
            guard AXValueGetValue(boundsObject as! AXValue, .cgRect, &rect) else { return nil }
            return cocoaRect(fromAX: rect, primaryScreenHeight: primaryHeight)
        }

        var candidates: [CGRect] = []
        if let rect = bounds(location: selection.location, length: 0) { candidates.append(rect) }
        if let rect = bounds(location: selection.location, length: 1) { candidates.append(rect) }
        if selection.location > 0, let previous = bounds(location: selection.location - 1, length: 1) {
            // 前一个字符的后沿就是插入点。
            candidates.append(CGRect(x: previous.maxX, y: previous.minY, width: 0, height: previous.height))
        }
        return candidates.first { isPlausibleCaret($0, screenFrames: frames) }
    }
}
