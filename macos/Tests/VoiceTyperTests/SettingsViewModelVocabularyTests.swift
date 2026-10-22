import XCTest
@testable import VoiceTyper

/// VOC-9：「词汇」页的草稿、落盘与状态列。与热键测试一样不调用 `load(config:launchAtLogin:)`（它会读 Keychain）。
@MainActor
final class SettingsViewModelVocabularyTests: XCTestCase {
    private final class SaveRecorder {
        var saved: [AppConfig] = []
        var error: Error?
    }

    private struct SaveRejected: Error {}

    private func makeViewModel() -> (SettingsViewModel, SaveRecorder) {
        let vm = SettingsViewModel()
        let recorder = SaveRecorder()
        vm.onSaveConfig = { config in
            if let error = recorder.error { throw error }
            recorder.saved.append(config)
        }
        return (vm, recorder)
    }

    private func settle() async {
        try? await Task.sleep(nanoseconds: 120_000_000)
    }

    func testRowSplitsAliasesOnHalfAndFullWidthSeparators() {
        let row = VocabularyRow(term: "Claude", aliasesText: "克劳德，可劳德, kld、 ")
        XCTAssertEqual(row.entry, VocabularyEntry(term: "Claude", aliases: ["克劳德", "可劳德", "kld"]))
    }

    func testCommitSavesValidatedDraftOnlyWhenChanged() async {
        let (vm, recorder) = makeViewModel()
        vm.vocabularyRows = [VocabularyRow(term: " Claude ", aliasesText: "克劳德"), VocabularyRow()]
        vm.commitVocabulary()
        await settle()
        XCTAssertEqual(recorder.saved.count, 1)
        XCTAssertEqual(recorder.saved.last?.vocabulary.entries, [VocabularyEntry(term: "Claude", aliases: ["克劳德"])],
                       "空白新行不落盘，首尾空白去掉")

        vm.commitVocabulary()
        await settle()
        XCTAssertEqual(recorder.saved.count, 1, "没有变化不重复保存（每次保存都会重建控制器）")

        vm.vocabularyBias = .strong
        vm.commitVocabulary()
        await settle()
        XCTAssertEqual(recorder.saved.last?.vocabulary.bias, .strong)
    }

    /// 自己刚保存的结果回灌时，空白新行与行身份保留；外部改动才重建。
    func testSyncKeepsDraftRowsWhenConfigMatches() {
        let (vm, _) = makeViewModel()
        let blank = VocabularyRow()
        let claude = VocabularyRow(term: "Claude", aliasesText: "克劳德")
        vm.vocabularyRows = [claude, blank]
        vm.syncVocabularyRows(with: VocabularyConfig(entries: [VocabularyEntry(term: "Claude", aliases: ["克劳德"])]))
        XCTAssertEqual(vm.vocabularyRows.map(\.id), [claude.id, blank.id])

        vm.syncVocabularyRows(with: VocabularyConfig(bias: .light, entries: [VocabularyEntry(term: "张三丰")]))
        XCTAssertEqual(vm.vocabularyRows.map(\.term), ["张三丰"])
        XCTAssertEqual(vm.vocabularyBias, .light)
    }

    func testSaveFailureRevertsToEffectiveVocabulary() async {
        let (vm, recorder) = makeViewModel()
        recorder.error = SaveRejected()
        vm.vocabularyRows = [VocabularyRow(term: "Claude")]
        vm.vocabularyBias = .strong
        vm.commitVocabulary()
        await settle()
        XCTAssertTrue(vm.vocabularyRows.isEmpty)
        XCTAssertEqual(vm.vocabularyBias, .standard)
        XCTAssertEqual(vm.vocabularyMessageKind, .error)
    }

    func testAddFromHistoryCreatesAliasOrMergesIntoExistingTerm() async {
        let (vm, recorder) = makeViewModel()
        vm.addVocabularyFromHistory(term: "Claude", heard: "克劳德")
        await settle()
        XCTAssertEqual(recorder.saved.last?.vocabulary.entries, [VocabularyEntry(term: "Claude", aliases: ["克劳德"])])

        vm.addVocabularyFromHistory(term: "claude", heard: "可劳德")
        await settle()
        XCTAssertEqual(recorder.saved.last?.vocabulary.entries, [VocabularyEntry(term: "Claude", aliases: ["克劳德", "可劳德"])],
                       "同名 term（不区分大小写）并入已有词条")

        vm.addVocabularyFromHistory(term: "VoiceTyper", heard: "voicetyper")
        await settle()
        XCTAssertEqual(recorder.saved.last?.vocabulary.entries.last, VocabularyEntry(term: "VoiceTyper"),
                       "只差大小写的片段不作为 alias（替换层本来就不区分大小写）")
    }

    func testStatusColumn() {
        let (vm, _) = makeViewModel()
        let row = VocabularyRow(term: "李")
        XCTAssertEqual(vm.status(for: row), .unknown(L("模型加载后检查")))

        vm.vocabularyReport = HotwordCompileReport(
            entries: [.init(term: "李", status: .tooShort), .init(term: "张三丰", status: .biased)],
            sequenceCount: 1, compileNanos: 0
        )
        guard case .warning = vm.status(for: row) else { return XCTFail("单字应显示警告") }
        XCTAssertEqual(vm.status(for: VocabularyRow(term: "张三丰")), .active)
        XCTAssertEqual(vm.status(for: VocabularyRow(term: "还没编译")), .unknown(L("模型加载后检查")))
    }

    func testTrialOutputUsesDraft() {
        let (vm, _) = makeViewModel()
        vm.vocabularyRows = [VocabularyRow(term: "Claude", aliasesText: "克劳德"), VocabularyRow(term: "VoiceTyper")]
        vm.vocabularyTrialInput = "用克劳德写一个voice typer的插件"
        XCTAssertEqual(vm.vocabularyTrialOutput, "用 Claude 写一个 VoiceTyper 的插件")
    }
}
