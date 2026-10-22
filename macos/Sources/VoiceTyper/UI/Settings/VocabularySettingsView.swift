import AppKit
import SwiftUI

/// 「词汇」页（VOC-9）：自定义词汇表、识别偏向强度、替换试一试、从最近结果添加。
/// 编辑后即时生效：回车、离开输入框、增删行、改强度时落盘（`SettingsViewModel.commitVocabulary`）。
struct VocabularySettingsView: View {
    @Bindable var vm: SettingsViewModel
    @State private var selection = Set<VocabularyRow.ID>()
    @State private var showingHistorySheet = false
    @FocusState private var focusedField: Field?

    private enum Field: Hashable {
        case term(VocabularyRow.ID)
        case aliases(VocabularyRow.ID)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("列出容易认错的人名、产品名和术语，识别时会优先往这些写法靠。全部在本机完成。"))
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            HStack(spacing: 8) {
                Text(L("识别偏向"))
                Picker("", selection: Binding(
                    get: { vm.vocabularyBias },
                    set: { vm.vocabularyBias = $0; vm.commitVocabulary() }
                )) {
                    ForEach(VocabularyBias.allCases, id: \.self) { bias in
                        Text(bias.displayName).tag(bias)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 240)
                Image(systemName: "info.circle")
                    .foregroundStyle(.secondary)
                    .help(Self.biasHelp)
                Spacer()
            }

            table

            HStack(spacing: 6) {
                Button {
                    if let id = vm.addVocabularyRow() {
                        selection = [id]
                        focusedField = .term(id)
                    }
                } label: {
                    Image(systemName: "plus")
                }
                .disabled(!vm.canAddVocabularyRow)
                .help(L("添加词汇"))
                Button {
                    vm.removeVocabularyRows(selection)
                    selection.removeAll()
                } label: {
                    Image(systemName: "minus")
                }
                .disabled(selection.isEmpty)
                .help(L("删除选中的词汇"))
                Text(LF("%d / %d 条", vm.vocabularyRows.count, VocabularyConfig.maxEntries))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Button(L("从最近结果添加…")) { showingHistorySheet = true }
            }

            trialBox

            if !vm.vocabularyMessage.isEmpty {
                Text(vm.vocabularyMessage)
                    .font(.callout)
                    .foregroundStyle(vm.vocabularyMessageKind.color)
            }
            if vm.isDictating {
                Text(L("正在听写，结束后可以继续编辑词汇。"))
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 16)
        .disabled(vm.isDictating)
        .onChange(of: focusedField) { previous, _ in
            // 离开一行的输入框即提交，不必每敲一个字就重建一次控制器。
            if previous != nil { vm.commitVocabulary() }
        }
        .sheet(isPresented: $showingHistorySheet) {
            VocabularyFromHistorySheet(
                entries: vm.recentResults(),
                onAdd: { term, heard in
                    vm.addVocabularyFromHistory(term: term, heard: heard)
                    showingHistorySheet = false
                },
                onCancel: { showingHistorySheet = false }
            )
        }
    }

    private static var biasHelp: String {
        L("越强越容易认出表里的词，也越可能把读音相近的普通词认成它。发音明显不同的词不会被改。觉得被误改时调低一档。")
    }

    private var table: some View {
        Table(vm.vocabularyRows, selection: $selection) {
            TableColumn(L("写法")) { row in
                TextField("", text: binding(row.id, \.term), prompt: Text(L("正确写法")))
                    .focused($focusedField, equals: .term(row.id))
                    .onSubmit { vm.commitVocabulary() }
            }
            .width(min: 140, ideal: 180)
            TableColumn(L("听起来像（可选，逗号分隔）")) { row in
                TextField("", text: binding(row.id, \.aliasesText), prompt: Text(L("例如：克劳德")))
                    .focused($focusedField, equals: .aliases(row.id))
                    .onSubmit { vm.commitVocabulary() }
            }
            TableColumn(L("状态")) { row in
                statusView(vm.status(for: row))
            }
            .width(56)
        }
        .frame(minHeight: 200)
    }

    /// 按行 id 取字段的绑定；行被删掉后的迟到写入直接忽略。
    private func binding(_ id: VocabularyRow.ID, _ keyPath: WritableKeyPath<VocabularyRow, String>) -> Binding<String> {
        Binding(
            get: { vm.vocabularyRows.first(where: { $0.id == id })?[keyPath: keyPath] ?? "" },
            set: { newValue in
                guard let index = vm.vocabularyRows.firstIndex(where: { $0.id == id }) else { return }
                vm.vocabularyRows[index][keyPath: keyPath] = newValue
            }
        )
    }

    @ViewBuilder
    private func statusView(_ status: VocabularyRowStatus) -> some View {
        switch status {
        case .active:
            Image(systemName: "checkmark.circle.fill")
                .foregroundStyle(.green)
                .help(L("已生效"))
        case .warning(let reason):
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(.orange)
                .help(reason)
        case .unknown(let reason):
            Text("—")
                .foregroundStyle(.secondary)
                .help(reason)
        }
    }

    private var trialBox: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(L("试一试替换"))
                TextField("", text: $vm.vocabularyTrialInput, prompt: Text(L("输入一句识别结果，例如：用克劳德写一个voice typer的插件")))
                    .textFieldStyle(.roundedBorder)
            }
            if !vm.vocabularyTrialInput.isEmpty {
                Text("→ " + vm.vocabularyTrialOutput)
                    .font(.callout)
                    .textSelection(.enabled)
            }
            Text(L("这里只演示替换；识别偏向的效果请直接听写体验。"))
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

/// 「从最近结果添加」：选一条最近结果，在其中选中被认错的片段，填上正确写法（VOC-9 §8.2）。
private struct VocabularyFromHistorySheet: View {
    let entries: [DictationHistory.Entry]
    let onAdd: (_ term: String, _ heard: String) -> Void
    let onCancel: () -> Void

    @State private var selectedID: DictationHistory.Entry.ID?
    @State private var heard = ""
    @State private var term = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("从最近结果添加词汇"))
                .font(.headline)
            if entries.isEmpty {
                Text(L("还没有最近结果。听写一次后再来；最近结果只保存在内存里，退出应用即清空。"))
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 120, alignment: .center)
            } else {
                List(entries, selection: $selectedID) { entry in
                    Text(DictationHistory.menuTitle(for: entry))
                        .lineLimit(1)
                        .tag(entry.id)
                }
                .frame(height: 140)

                if let entry = entries.first(where: { $0.id == selectedID }) {
                    Text(L("在下面的文字里选中被认错的部分："))
                        .font(.callout)
                    SelectableTextView(text: entry.text, selection: $heard)
                        .frame(height: 72)
                        .overlay(RoundedRectangle(cornerRadius: 4).stroke(Color.secondary.opacity(0.3)))
                }

                Grid(alignment: .leading, horizontalSpacing: 8, verticalSpacing: 6) {
                    GridRow {
                        Text(L("听起来像"))
                        Text(heard.isEmpty ? L("（先在上面选中片段）") : heard)
                            .foregroundStyle(heard.isEmpty ? .secondary : .primary)
                    }
                    GridRow {
                        Text(L("正确写法"))
                        TextField("", text: $term)
                            .textFieldStyle(.roundedBorder)
                    }
                }
            }

            HStack {
                Spacer()
                Button(L("取消"), action: onCancel)
                    .keyboardShortcut(.cancelAction)
                Button(L("添加")) { onAdd(term, heard) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(term.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || heard.isEmpty)
            }
        }
        .padding(20)
        .frame(width: 480)
        .onChange(of: selectedID) { _, _ in
            heard = ""
            term = ""
        }
        .onChange(of: heard) { _, newValue in
            // 预填选中的片段，用户把它改成正确写法即可。
            term = newValue
        }
    }
}

/// 只读、可选中的文本视图，把当前选中的子串回写给 `selection`。
/// SwiftUI 的 `Text.textSelection` 在 macOS 14 上拿不到选区，所以包一层 NSTextView。
private struct SelectableTextView: NSViewRepresentable {
    let text: String
    @Binding var selection: String

    func makeCoordinator() -> Coordinator { Coordinator(selection: $selection) }

    func makeNSView(context: Context) -> NSScrollView {
        let scrollView = NSTextView.scrollableTextView()
        guard let textView = scrollView.documentView as? NSTextView else { return scrollView }
        textView.isEditable = false
        textView.isSelectable = true
        textView.isRichText = false
        textView.font = NSFont.systemFont(ofSize: NSFont.systemFontSize)
        textView.textContainerInset = NSSize(width: 4, height: 4)
        textView.delegate = context.coordinator
        textView.string = text
        return scrollView
    }

    func updateNSView(_ scrollView: NSScrollView, context: Context) {
        guard let textView = scrollView.documentView as? NSTextView, textView.string != text else { return }
        textView.string = text
        textView.setSelectedRange(NSRange(location: 0, length: 0))
    }

    @MainActor
    final class Coordinator: NSObject, NSTextViewDelegate {
        private var selection: Binding<String>

        init(selection: Binding<String>) {
            self.selection = selection
        }

        func textViewDidChangeSelection(_ notification: Notification) {
            guard let textView = notification.object as? NSTextView else { return }
            let range = textView.selectedRange()
            let selected = (textView.string as NSString).substring(with: range)
            selection.wrappedValue = selected.trimmingCharacters(in: .whitespacesAndNewlines)
        }
    }
}
