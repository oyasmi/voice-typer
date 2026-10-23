using System.Drawing;
using System.Windows.Forms;
using VoiceTyper.Asr.Hotword;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

/// <summary>
/// 「词汇」页（VOC-9）：识别偏向强度、词汇表、替换试一试、从最近结果添加。与 macOS
/// <c>VocabularySettingsView</c> 同一功能，但沿用本窗口的草稿 + 统一「保存并应用」：表格改动先进草稿，
/// 保存时与其他页一起走 <c>AppCoordinator.SaveRecognition</c> 的同一事务（听写中保存会被拒绝）。
/// </summary>
internal sealed partial class SetupForm
{
    /// <summary>「从最近结果添加」读取最近的识别文本，新到旧（只在内存）。</summary>
    public Func<IReadOnlyList<RecognitionHistory.Entry>>? OnLoadRecentResults;

    private readonly ComboBox _vocabularyBiasCombo = new();
    private readonly DataGridView _vocabularyGrid = new();
    private readonly Button _vocabularyAddButton = new();
    private readonly Button _vocabularyRemoveButton = new();
    private readonly Button _vocabularyFromHistoryButton = new();
    private readonly Label _vocabularyCountLabel = new();
    private readonly Label _vocabularyMessage = new();
    private readonly TextBox _vocabularyTrialInput = new();
    private readonly Label _vocabularyTrialOutput = new();
    /// <summary>引擎的编译报告；模型从未加载过为 null，状态列显示「—」。</summary>
    private HotwordCompileReport? _vocabularyReport;
    /// <summary>表格的编辑框是复用的同一个 TextBox，只挂一次 TextChanged。</summary>
    private Control? _vocabularyEditingControl;

    private const int VocabularyTermColumn = 0;
    private const int VocabularyAliasesColumn = 1;
    private const int VocabularyStatusColumn = 2;

    private void BuildVocabularySettings()
    {
        var page = Page(SetupTab.Vocabulary, L10n.T("词汇"),
            L10n.T("列出容易认错的人名、产品名和术语，识别时会优先往这些写法靠。全部在本机完成。"));

        var bias = Card(page, L10n.T("识别偏向"));
        ConfigureCombo(_vocabularyBiasCombo, Enum.GetValues<VocabularyBias>(), value => ((VocabularyBias)value).DisplayName());
        AddField(bias, L10n.T("强度"),
            L10n.T("越强越容易认出表里的词，也越可能把读音相近的普通词认成它。发音明显不同的词不会被改。觉得被误改时调低一档。"),
            _vocabularyBiasCombo);

        var table = Card(page, L10n.T("词汇表"));
        Add(table, TextLabel(
            L10n.T("「听起来像」可选，写常被识别成的样子，多个用逗号分隔；英文的大小写、空格和连字符变体无需另写。"),
            9, false, Muted));
        ConfigureVocabularyGrid();
        Add(table, _vocabularyGrid);
        StyleButton(_vocabularyAddButton, L10n.T("添加词汇"));
        _vocabularyAddButton.Click += (_, _) => AddVocabularyRow();
        StyleButton(_vocabularyRemoveButton, L10n.T("删除选中的词汇"));
        _vocabularyRemoveButton.Click += (_, _) => RemoveSelectedVocabularyRows();
        StyleButton(_vocabularyFromHistoryButton, L10n.T("从最近结果添加…"));
        _vocabularyFromHistoryButton.Click += (_, _) => AddVocabularyFromHistory();
        Add(table, ButtonRow(_vocabularyAddButton, _vocabularyRemoveButton, _vocabularyFromHistoryButton));
        StyleLabel(_vocabularyCountLabel, Muted);
        Add(table, _vocabularyCountLabel);
        StyleLabel(_vocabularyMessage, Muted);
        _vocabularyMessage.Visible = false;
        Add(table, _vocabularyMessage);

        var trial = Card(page, L10n.T("试一试替换"));
        _vocabularyTrialInput.PlaceholderText = L10n.T("输入一句识别结果，例如：用克劳德写一个voice typer的插件");
        _vocabularyTrialInput.AccessibleName = L10n.T("试一试替换");
        _vocabularyTrialInput.Dock = DockStyle.Top;
        _vocabularyTrialInput.TextChanged += (_, _) => RefreshVocabularyTrial();
        Add(trial, _vocabularyTrialInput);
        StyleLabel(_vocabularyTrialOutput, Color.FromArgb(34, 43, 58));
        Add(trial, _vocabularyTrialOutput);
        Add(trial, TextLabel(L10n.T("这里只演示替换，用的是表格里尚未保存的内容；识别偏向的效果请保存后直接听写体验。"), 9, false, Muted));
    }

    private void ConfigureVocabularyGrid()
    {
        var grid = _vocabularyGrid;
        grid.Dock = DockStyle.Top;
        grid.Height = 260;
        grid.AllowUserToAddRows = false; // 用「添加词汇」按钮，避免末尾「新行」半提交的语义
        grid.AllowUserToDeleteRows = true; // 选中整行按 Delete 删除
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.ShowCellToolTips = true;
        grid.AccessibleName = L10n.T("词汇表");
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = L10n.T("写法"), FillWeight = 38, MaxInputLength = VocabularyConfig.MaxSurfaceLength,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = L10n.T("听起来像（可选，逗号分隔）"), FillWeight = 52,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = L10n.T("状态"), FillWeight = 10, MinimumWidth = 56, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });

        // 编辑中的文字也要进脏检查（否则「保存并应用」要等离开单元格才可用）：编辑框每变一次就刷新，
        // 草稿读取时用编辑框里的文字代替尚未提交的单元格值（见 ReadVocabularyRows）。
        grid.EditingControlShowing += (_, e) =>
        {
            if (ReferenceEquals(_vocabularyEditingControl, e.Control)) return;
            _vocabularyEditingControl = e.Control;
            e.Control.TextChanged += (_, _) => { if (grid.IsCurrentCellInEditMode) OnVocabularyEdited(); };
        };
        grid.CellValueChanged += (_, e) =>
        {
            if (e.ColumnIndex != VocabularyStatusColumn) OnVocabularyEdited();
        };
        grid.RowsRemoved += (_, _) => OnVocabularyEdited();
        grid.SelectionChanged += (_, _) => _vocabularyRemoveButton.Enabled = grid.SelectedRows.Count > 0;
    }

    /// <summary>载入已保存的词汇（<see cref="LoadEditableContent"/> 调用，_loading 期间不触发脏检查）。</summary>
    private void LoadVocabulary(VocabularyConfig vocabulary)
    {
        _vocabularyBiasCombo.SelectedItem = vocabulary.BiasValue;
        // 正在编辑时清空表格会让编辑框指向已删除的行；先放弃编辑（草稿未改时才会走到这里）。
        _vocabularyGrid.CancelEdit();
        _vocabularyGrid.EndEdit();
        _vocabularyGrid.Rows.Clear();
        foreach (var row in VocabularyRows.FromConfig(vocabulary))
        {
            _vocabularyGrid.Rows.Add(row.Term, row.AliasesText, "");
        }
        _vocabularyGrid.ClearSelection();
        SetMessage(_vocabularyMessage, "", Muted);
        RefreshVocabularyDecorations();
    }

    /// <summary>编译报告到达（模型加载完成、保存后重新编译）：只刷新状态列。</summary>
    public void UpdateVocabularyReport(HotwordCompileReport? report)
    {
        _vocabularyReport = report;
        RefreshVocabularyStatuses();
    }

    /// <summary>表格当前内容，含编辑框里尚未提交的文字。</summary>
    private List<VocabularyRow> ReadVocabularyRows()
    {
        var rows = new List<VocabularyRow>(_vocabularyGrid.Rows.Count);
        // 编辑框还没挂上（开始编辑的过程中）时读单元格原值，不能当成空串，否则会误判为「有改动」。
        var editingControl = _vocabularyGrid.IsCurrentCellInEditMode ? _vocabularyGrid.EditingControl : null;
        var editing = editingControl is null ? null : _vocabularyGrid.CurrentCell;
        foreach (DataGridViewRow gridRow in _vocabularyGrid.Rows)
        {
            string Cell(int column) =>
                editing is not null && editing.RowIndex == gridRow.Index && editing.ColumnIndex == column
                    ? editingControl!.Text
                    : gridRow.Cells[column].Value as string ?? "";
            rows.Add(new VocabularyRow(Cell(VocabularyTermColumn), Cell(VocabularyAliasesColumn)));
        }
        return rows;
    }

    private VocabularyConfig BuildVocabularyDraft() => VocabularyRows.BuildConfig(
        (VocabularyBias)(_vocabularyBiasCombo.SelectedItem ?? VocabularyBias.Standard), ReadVocabularyRows());

    private void OnVocabularyEdited()
    {
        if (_loading) return;
        RefreshVocabularyDecorations();
        RefreshDirtyState();
    }

    private void RefreshVocabularyDecorations()
    {
        var count = _vocabularyGrid.Rows.Count;
        _vocabularyCountLabel.Text = L10n.F("{0} / {1} 条", count, VocabularyConfig.MaxEntries);
        _vocabularyAddButton.Enabled = count < VocabularyConfig.MaxEntries;
        _vocabularyRemoveButton.Enabled = _vocabularyGrid.SelectedRows.Count > 0;
        RefreshVocabularyStatuses();
        RefreshVocabularyTrial();
    }

    private void RefreshVocabularyStatuses()
    {
        var rows = ReadVocabularyRows();
        var savedBias = _loadedConfig.Vocabulary.BiasValue;
        for (var index = 0; index < rows.Count; index++)
        {
            var status = VocabularyRows.Status(rows[index].Term, _vocabularyReport, savedBias);
            var cell = _vocabularyGrid.Rows[index].Cells[VocabularyStatusColumn];
            if (cell.Value as string != status.Symbol) cell.Value = status.Symbol;
            cell.ToolTipText = status.Reason;
            cell.Style.ForeColor = status.Status switch
            {
                VocabularyRowStatus.Kind.Active => Color.SeaGreen,
                VocabularyRowStatus.Kind.Warning => Color.DarkOrange,
                _ => Muted,
            };
        }
    }

    private void RefreshVocabularyTrial()
    {
        var input = _vocabularyTrialInput.Text;
        _vocabularyTrialOutput.Text = input.Length == 0
            ? ""
            : "→ " + OutputFormatter.FormatForOutput(input, new VocabularyReplacer(BuildVocabularyDraft()));
        _vocabularyTrialOutput.Visible = input.Length > 0;
    }

    private void AddVocabularyRow()
    {
        if (_vocabularyGrid.Rows.Count >= VocabularyConfig.MaxEntries)
        {
            SetMessage(_vocabularyMessage, L10n.F("最多 {0} 条词汇。", VocabularyConfig.MaxEntries), Color.Firebrick);
            return;
        }
        _vocabularyGrid.EndEdit();
        var index = _vocabularyGrid.Rows.Add("", "", "");
        FocusVocabularyCell(index, VocabularyTermColumn, beginEdit: true);
        OnVocabularyEdited();
    }

    private void RemoveSelectedVocabularyRows()
    {
        _vocabularyGrid.EndEdit();
        foreach (var row in _vocabularyGrid.SelectedRows.Cast<DataGridViewRow>().ToList())
        {
            _vocabularyGrid.Rows.Remove(row); // RowsRemoved 会刷新脏检查
        }
    }

    private void AddVocabularyFromHistory()
    {
        _vocabularyGrid.EndEdit();
        var entries = OnLoadRecentResults?.Invoke() ?? Array.Empty<RecognitionHistory.Entry>();
        using var dialog = new VocabularyFromHistoryForm(entries);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var rows = ReadVocabularyRows();
        if (VocabularyRows.AddFromHistory(rows, dialog.Term, dialog.Heard) is not { } index)
        {
            SetMessage(_vocabularyMessage, L10n.F("最多 {0} 条词汇。", VocabularyConfig.MaxEntries), Color.Firebrick);
            return;
        }
        if (index == _vocabularyGrid.Rows.Count) _vocabularyGrid.Rows.Add("", "", "");
        var gridRow = _vocabularyGrid.Rows[index];
        gridRow.Cells[VocabularyTermColumn].Value = rows[index].Term;
        gridRow.Cells[VocabularyAliasesColumn].Value = rows[index].AliasesText;
        FocusVocabularyCell(index, VocabularyTermColumn, beginEdit: false);
        SetMessage(_vocabularyMessage, L10n.T("已加入词汇表，点「保存并应用」生效。"), Color.SeaGreen);
        OnVocabularyEdited();
    }

    private void FocusVocabularyCell(int row, int column, bool beginEdit)
    {
        _vocabularyGrid.ClearSelection();
        _vocabularyGrid.CurrentCell = _vocabularyGrid.Rows[row].Cells[column];
        _vocabularyGrid.Rows[row].Selected = true;
        if (!beginEdit) return;
        _vocabularyGrid.Focus();
        _vocabularyGrid.BeginEdit(selectAll: true);
    }
}
