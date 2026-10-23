using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

/// <summary>
/// 「从最近结果添加」（VOC-9 §8.2）：选一条最近结果，在全文里选中被认错的片段，填上正确写法。
/// 与 macOS <c>VocabularyFromHistorySheet</c> 同一流程。识别文本只在本窗口内显示，不进日志。
/// </summary>
internal sealed class VocabularyFromHistoryForm : Form
{
    private readonly ListBox _entries = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _content = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = SystemColors.Window,
    };
    private readonly Label _heardLabel = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 6) };
    private readonly TextBox _termField = new() { Dock = DockStyle.Fill };
    private readonly Button _addButton = new() { AutoSize = true, DialogResult = DialogResult.OK };

    /// <summary>用户选中的、被认错的片段（「听起来像」）。</summary>
    public string Heard { get; private set; } = "";
    /// <summary>正确写法。</summary>
    public string Term => _termField.Text.Trim();

    public VocabularyFromHistoryForm(IReadOnlyList<RecognitionHistory.Entry> newestFirst)
    {
        Text = L10n.T("从最近结果添加词汇");
        Font = UiFonts.Get(9.5f);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 500);
        MinimumSize = new Size(420, 380);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var hint = new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 10),
            Text = newestFirst.Count == 0
                ? L10n.T("还没有最近结果。听写一次后再来；最近结果只保存在内存里，退出应用即清空。")
                : L10n.T("选一条最近结果，在全文里选中被认错的部分，再填上正确写法。"),
        };
        layout.Controls.Add(hint, 0, 0);
        layout.SetColumnSpan(hint, 2);

        _entries.AccessibleName = L10n.T("识别记录列表");
        _entries.FormattingEnabled = true;
        _entries.Format += (_, e) =>
        {
            if (e.ListItem is RecognitionHistory.Entry entry)
            {
                var preview = entry.Text.ReplaceLineEndings(" ").Replace('\t', ' ');
                if (preview.Length > 120) preview = preview[..120] + "…";
                e.Value = $"{entry.Time:HH:mm:ss}  {preview}";
            }
        };
        _entries.Items.AddRange(newestFirst.Cast<object>().ToArray());
        _entries.SelectedIndexChanged += (_, _) =>
        {
            _content.Text = (_entries.SelectedItem as RecognitionHistory.Entry)?.Text ?? "";
            SetHeard("");
        };
        layout.Controls.Add(_entries, 0, 1);
        layout.SetColumnSpan(_entries, 2);

        // TextBox 没有选区变化事件：鼠标拖选、Shift+方向键选择结束后读一次选中的文字。
        _content.AccessibleName = L10n.T("所选记录全文");
        _content.MouseUp += (_, _) => SetHeard(_content.SelectedText);
        _content.KeyUp += (_, _) => SetHeard(_content.SelectedText);
        layout.Controls.Add(_content, 0, 2);
        layout.SetColumnSpan(_content, 2);

        layout.Controls.Add(FieldLabel(L10n.T("听起来像")), 0, 3);
        layout.Controls.Add(_heardLabel, 1, 3);
        layout.Controls.Add(FieldLabel(L10n.T("正确写法")), 0, 4);
        _termField.AccessibleName = L10n.T("正确写法");
        _termField.TextChanged += (_, _) => RefreshAddButton();
        layout.Controls.Add(_termField, 1, 4);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
        };
        var cancel = new Button { Text = L10n.T("取消"), AutoSize = true, DialogResult = DialogResult.Cancel };
        _addButton.Text = L10n.T("添加");
        buttons.Controls.AddRange(new Control[] { cancel, _addButton });
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = _addButton;
        CancelButton = cancel;

        var hasEntries = newestFirst.Count > 0;
        _entries.Enabled = _content.Enabled = _termField.Enabled = hasEntries;
        if (hasEntries) _entries.SelectedIndex = 0;
        SetHeard("");
    }

    private void SetHeard(string selected)
    {
        var heard = selected.Trim();
        if (heard == Heard && _heardLabel.Text.Length > 0) return;
        Heard = heard;
        _heardLabel.Text = heard.Length == 0 ? L10n.T("（先在上面选中片段）") : heard;
        _heardLabel.ForeColor = heard.Length == 0 ? SystemColors.GrayText : SystemColors.ControlText;
        // 预填选中的片段，用户把它改成正确写法即可。
        _termField.Text = heard;
        RefreshAddButton();
    }

    private void RefreshAddButton() => _addButton.Enabled = Heard.Length > 0 && Term.Length > 0;

    private static Label FieldLabel(string text) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 6),
    };
}
