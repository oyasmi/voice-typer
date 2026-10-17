import Foundation

/// 最近的听写结果，只保存在内存里（R8-A2）：退出即清，不落盘、不进日志。
///
/// 存在的意义：Esc 取消、焦点变化、插入失败、识别超时，用户说过的话不能就此找不回来。
@MainActor
final class DictationHistory {
    enum Kind: Equatable {
        case inserted
        case copiedFocusChanged
        case copiedInsertFailed
        case cancelled
        case partialAfterTimeout
        case partialAfterFailure
    }

    struct Entry: Identifiable, Equatable {
        let id: UUID
        let text: String
        let createdAt: Date
        let kind: Kind
    }

    static let capacity = 10

    /// 菜单里条目标题保留的字符数。
    private static let menuTitleLimit = 30

    /// 新的在前。
    private(set) var entries: [Entry] = []
    var onChange: (() -> Void)?

    /// 空白文本忽略；与最新一条文本相同时只更新 kind 与时间，避免重复条目。
    func record(_ text: String, kind: Kind, now: Date = Date()) {
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        if let latest = entries.first, latest.text == text {
            entries[0] = Entry(id: latest.id, text: text, createdAt: now, kind: kind)
        } else {
            entries.insert(Entry(id: UUID(), text: text, createdAt: now, kind: kind), at: 0)
            if entries.count > Self.capacity {
                entries.removeLast(entries.count - Self.capacity)
            }
        }
        onChange?()
    }

    func clear() {
        guard !entries.isEmpty else { return }
        entries.removeAll()
        onChange?()
    }

    // MARK: - 菜单展示

    /// 非正常输入的条目带后缀，一眼分清「已经输入过」还是「需要手动粘贴」。
    static func suffix(for kind: Kind) -> String? {
        switch kind {
        case .inserted: return nil
        case .copiedFocusChanged, .copiedInsertFailed: return L("（未输入）")
        case .cancelled: return L("（已取消）")
        case .partialAfterTimeout, .partialAfterFailure: return L("（部分）")
        }
    }

    /// 菜单标题：换行替换为空格，前 30 个字符，超出加 `…`，再接后缀。
    /// 刻意不设 toolTip：完整文本不应该因为悬停而被展示在菜单之外。
    static func menuTitle(for entry: Entry) -> String {
        let flattened = entry.text
            .components(separatedBy: .newlines)
            .joined(separator: " ")
            .trimmingCharacters(in: .whitespaces)
        let characters = Array(flattened)
        let body = characters.count > menuTitleLimit
            ? String(characters.prefix(menuTitleLimit)) + "…"
            : flattened
        guard let suffix = suffix(for: entry.kind) else { return body }
        // 中文后缀紧贴正文；英文后缀前留一个空格。
        return body + (L10n.current == .en ? " " : "") + suffix
    }
}
