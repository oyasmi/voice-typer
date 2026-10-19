using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

/// <summary>最近识别文本的浏览与复制。所有操作在 UI 线程执行，剪贴板写入交给协调器。</summary>
internal sealed class RecognitionHistoryForm : Form
{
    private readonly RecognitionHistory _history;
    private readonly Func<string, string?> _copy;
    private readonly ListBox _entries = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _content = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, WordWrap = true,
    };
    private readonly Button _copySelected = new() { AutoSize = true };
    private readonly Button _copyAll = new() { AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill };

    public RecognitionHistoryForm(RecognitionHistory history, Func<string, string?> copy)
    {
        _history = history;
        _copy = copy;
        Text = L10n.T("近期识别历史");
        Font = UiFonts.Get(9.5f);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, 540);
        MinimumSize = new Size(400, 340);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = L10n.F("最近 {0} 条，新到旧排列；仅保存在内存中，退出即清除。", RecognitionHistory.Capacity),
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 12),
        }, 0, 0);
        _entries.AccessibleName = L10n.T("识别记录列表");
        _entries.FormattingEnabled = true;
        _entries.Format += (_, e) =>
        {
            if (e.ListItem is RecognitionHistory.Entry entry)
            {
                var preview = entry.Text.ReplaceLineEndings(" ").Replace('\t', ' ');
                if (preview.Length > 120) preview = preview[..120] + "…";
                e.Value = $"{entry.Time:MM-dd HH:mm:ss}  {preview}";
            }
        };
        _entries.SelectedIndexChanged += (_, _) => ShowSelection();
        layout.Controls.Add(_entries, 0, 1);
        _content.AccessibleName = L10n.T("所选记录全文");
        layout.Controls.Add(_content, 0, 2);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        _copySelected.Text = L10n.T("复制所选条目");
        _copySelected.Click += (_, _) =>
        {
            if (_entries.SelectedItem is RecognitionHistory.Entry entry) Copy(entry.Text);
        };
        _copyAll.Text = L10n.T("全部复制（旧到新）");
        _copyAll.Click += (_, _) => Copy(_history.AllText());
        var close = new Button { Text = L10n.T("关闭"), AutoSize = true, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        CancelButton = close;
        buttons.Controls.AddRange(new Control[] { _copySelected, _copyAll, close });
        layout.Controls.Add(buttons, 0, 3);
        layout.Controls.Add(_status, 0, 4);
        Controls.Add(layout);
        RefreshEntries();
    }

    /// <summary>窗口打开期间的新结果实时可见；正在查看旧记录时尽量保留选择。</summary>
    public void RefreshEntries()
    {
        var previous = _entries.SelectedItem;
        var followNewest = _entries.SelectedIndex <= 0;
        _entries.BeginUpdate();
        try
        {
            _entries.Items.Clear();
            _entries.Items.AddRange(_history.NewestFirst().Cast<object>().ToArray());
            var index = !followNewest && previous is not null ? _entries.Items.IndexOf(previous) : 0;
            if (_entries.Items.Count > 0) _entries.SelectedIndex = Math.Max(0, index);
        }
        finally { _entries.EndUpdate(); }
        _copyAll.Enabled = _entries.Items.Count > 0;
        ShowSelection();
    }

    private void ShowSelection()
    {
        _content.Text = (_entries.SelectedItem as RecognitionHistory.Entry)?.Text ?? "";
        _copySelected.Enabled = _entries.SelectedItem is RecognitionHistory.Entry;
        _status.Text = _entries.Items.Count == 0 ? L10n.T("暂无识别记录") : "";
        _status.ForeColor = SystemColors.ControlText;
    }

    private void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var error = _copy(text);
        _status.Text = error ?? L10n.T("已复制");
        _status.ForeColor = error is null ? Color.SeaGreen : Color.Firebrick;
    }
}
