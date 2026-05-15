using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

internal enum SetupTab { Connection = 0, Hotkey = 1, Hotwords = 2 }

/// <summary>
/// 设置窗口：连接 / 热键 / 用户热词 三个 Tab。
/// 所有方法必须在 UI 线程调用。
/// </summary>
internal sealed class SetupForm : Form
{
    public Func<ServerConfig, Task<bool>>? OnTestServerConnection;
    public Func<AppConfig, Task>? OnSaveConfig;
    public Func<string, Task>? OnSaveHotwords;
    public Action? OnRetryServerCheck;

    private AppConfig _loadedConfig = new();
    private string _loadedHotwordsText = "";
    private int _additionalHotwordFileCount;

    // ─ 顶部状态横幅 ────────────────────────────────────────────
    private readonly Panel _bannerPanel = new();
    private readonly Label _bannerLabel = new();
    private readonly Button _bannerRetry = new();
    private readonly Button _bannerOpenMicSettings = new();
    private bool _showMicWarning;

    // ─ Tab 1：连接 ────────────────────────────────────────────
    private readonly TextBox _hostField = new();
    private readonly NumericUpDown _portField = new();
    private readonly TextBox _apiKeyField = new();
    private readonly CheckBox _streamingCheck = new();
    private readonly CheckBox _llmCheck = new();
    private readonly Button _testButton = new();
    private readonly Button _saveConnectionButton = new();
    private readonly Label _connectionMessage = new();

    // ─ Tab 2：热键 ────────────────────────────────────────────
    private readonly CheckBox _modCtrl = new();
    private readonly CheckBox _modAlt = new();
    private readonly CheckBox _modShift = new();
    private readonly CheckBox _modWin = new();
    private readonly TextBox _hotkeyKey = new();
    private readonly Label _hotkeyPreview = new();
    private readonly Button _saveHotkeyButton = new();
    private readonly Label _hotkeyMessage = new();

    // ─ Tab 3：热词 ────────────────────────────────────────────
    private readonly TextBox _hotwordsArea = new();
    private readonly Label _hotwordsInfo = new();
    private readonly Label _hotwordsCount = new();
    private readonly Button _reloadHotwordsButton = new();
    private readonly Button _saveHotwordsButton = new();
    private readonly Label _hotwordsMessage = new();

    private readonly TabControl _tabs = new();
    private readonly Label _versionLabel = new();

    public SetupForm()
    {
        Text = "VoiceTyper 设置";
        Size = new Size(720, 600);
        MinimumSize = new Size(640, 520);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = SystemColors.Control;

        BuildBanner();
        BuildTabs();
        BuildFooter();

        Controls.Add(_tabs);
        Controls.Add(_bannerPanel);
        Controls.Add(_versionLabel);
    }

    public void LoadEditableContent(AppConfig config, string managedHotwordsText, int additionalHotwordFileCount)
    {
        _loadedConfig = config.Clone();
        _loadedHotwordsText = managedHotwordsText;
        _additionalHotwordFileCount = additionalHotwordFileCount;

        _hostField.Text = config.Server.Host;
        _portField.Value = Math.Clamp(config.Server.Port, 1, 65535);
        _apiKeyField.Text = config.Server.ApiKey;
        _streamingCheck.Checked = config.Server.Streaming;
        _llmCheck.Checked = config.Server.LlmRecorrect;

        var mods = config.Hotkey.Modifiers.Select(m => m.ToLowerInvariant()).ToHashSet();
        _modCtrl.Checked = mods.Contains("ctrl") || mods.Contains("control");
        _modAlt.Checked = mods.Contains("alt") || mods.Contains("option");
        _modShift.Checked = mods.Contains("shift");
        _modWin.Checked = mods.Any(m => m is "win" or "win_l" or "win_r" or "super" or "command" or "cmd");
        _hotkeyKey.Text = config.Hotkey.Key;
        UpdateHotkeyPreview();

        _hotwordsArea.Text = managedHotwordsText;
        UpdateHotwordsMeta();

        _connectionMessage.Text = "";
        _hotkeyMessage.Text = "";
        _hotwordsMessage.Text = "";
    }

    public void UpdateServerStatus(bool ready, string display, bool micPermissionDenied)
    {
        _showMicWarning = micPermissionDenied;
        if (micPermissionDenied)
        {
            _bannerPanel.BackColor = Color.FromArgb(255, 245, 220);
            _bannerLabel.Text = "麦克风权限可能被禁用：请在 Windows 设置 → 隐私和安全 → 麦克风中允许桌面应用访问。";
            _bannerLabel.ForeColor = Color.FromArgb(120, 70, 0);
            _bannerOpenMicSettings.Visible = true;
            _bannerRetry.Visible = false;
        }
        else if (ready)
        {
            _bannerPanel.BackColor = Color.FromArgb(225, 245, 230);
            _bannerLabel.Text = $"服务已连接：{display}";
            _bannerLabel.ForeColor = Color.FromArgb(30, 110, 50);
            _bannerOpenMicSettings.Visible = false;
            _bannerRetry.Visible = true;
        }
        else
        {
            _bannerPanel.BackColor = Color.FromArgb(252, 230, 230);
            _bannerLabel.Text = $"服务未连接：{display}";
            _bannerLabel.ForeColor = Color.FromArgb(150, 40, 40);
            _bannerOpenMicSettings.Visible = false;
            _bannerRetry.Visible = true;
        }
    }

    public void SelectTab(SetupTab tab)
    {
        if ((int)tab >= 0 && (int)tab < _tabs.TabPages.Count)
        {
            _tabs.SelectedIndex = (int)tab;
        }
    }

    public void Present()
    {
        if (!Visible)
        {
            Show();
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    // ─── UI 构建 ────────────────────────────────────────────────

    private void BuildBanner()
    {
        _bannerPanel.Dock = DockStyle.Top;
        _bannerPanel.Height = 46;
        _bannerPanel.Padding = new Padding(14, 6, 14, 6);
        _bannerPanel.BackColor = Color.FromArgb(245, 245, 245);

        _bannerLabel.AutoSize = false;
        _bannerLabel.Dock = DockStyle.Fill;
        _bannerLabel.TextAlign = ContentAlignment.MiddleLeft;
        _bannerLabel.Font = new Font(Font, FontStyle.Regular);

        _bannerRetry.Text = "重新检测";
        _bannerRetry.AutoSize = true;
        _bannerRetry.Dock = DockStyle.Right;
        _bannerRetry.Padding = new Padding(8, 0, 8, 0);
        _bannerRetry.FlatStyle = FlatStyle.Standard;
        _bannerRetry.Click += (_, _) => OnRetryServerCheck?.Invoke();

        _bannerOpenMicSettings.Text = "打开麦克风设置";
        _bannerOpenMicSettings.AutoSize = true;
        _bannerOpenMicSettings.Dock = DockStyle.Right;
        _bannerOpenMicSettings.Padding = new Padding(8, 0, 8, 0);
        _bannerOpenMicSettings.Visible = false;
        _bannerOpenMicSettings.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:privacy-microphone",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                AppLog.Warn("ui", $"打开麦克风设置失败: {ex.Message}");
            }
        };

        _bannerPanel.Controls.Add(_bannerLabel);
        _bannerPanel.Controls.Add(_bannerRetry);
        _bannerPanel.Controls.Add(_bannerOpenMicSettings);
    }

    private void BuildTabs()
    {
        _tabs.Dock = DockStyle.Fill;
        _tabs.Padding = new Point(14, 6);

        var connectionPage = new TabPage("连接") { BackColor = SystemColors.Control };
        var hotkeyPage = new TabPage("热键") { BackColor = SystemColors.Control };
        var hotwordsPage = new TabPage("用户热词") { BackColor = SystemColors.Control };

        BuildConnectionPage(connectionPage);
        BuildHotkeyPage(hotkeyPage);
        BuildHotwordsPage(hotwordsPage);

        _tabs.TabPages.Add(connectionPage);
        _tabs.TabPages.Add(hotkeyPage);
        _tabs.TabPages.Add(hotwordsPage);
    }

    private void BuildFooter()
    {
        _versionLabel.Text = $"版本 {AppConstants.Version}";
        _versionLabel.Dock = DockStyle.Bottom;
        _versionLabel.Height = 24;
        _versionLabel.Padding = new Padding(16, 4, 16, 4);
        _versionLabel.TextAlign = ContentAlignment.MiddleLeft;
        _versionLabel.ForeColor = Color.Gray;
        _versionLabel.Font = new Font(Font.FontFamily, 8f);
    }

    private void BuildConnectionPage(TabPage page)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 20, 20, 16),
            ColumnCount = 2,
            AutoSize = false,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(MakeFieldLabel("服务地址："), 0, 0);
        _hostField.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _hostField.Width = 360;
        layout.Controls.Add(_hostField, 1, 0);

        layout.Controls.Add(MakeFieldLabel("端口："), 0, 1);
        _portField.Minimum = 1;
        _portField.Maximum = 65535;
        _portField.Width = 120;
        _portField.Value = 6008;
        layout.Controls.Add(_portField, 1, 1);

        layout.Controls.Add(MakeFieldLabel("API Key："), 0, 2);
        _apiKeyField.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _apiKeyField.UseSystemPasswordChar = true;
        _apiKeyField.Width = 360;
        layout.Controls.Add(_apiKeyField, 1, 2);

        // 流式开关
        _streamingCheck.Text = "流式识别（推荐，低延迟）";
        _streamingCheck.AutoSize = true;
        layout.Controls.Add(new Panel { Height = 8, Width = 1 }, 0, 3);
        var streamingNote = new Label
        {
            Text = "流式模式通过 WebSocket 实时回传识别结果；非流式模式支持热词，兼容旧服务端。",
            ForeColor = Color.Gray,
            AutoSize = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Height = 36,
        };
        layout.Controls.Add(_streamingCheck, 1, 4);
        layout.Controls.Add(streamingNote, 1, 5);

        _llmCheck.Text = "启用 LLM 纠错（需服务端支持）";
        _llmCheck.AutoSize = true;
        layout.Controls.Add(_llmCheck, 1, 6);

        _connectionMessage.AutoSize = false;
        _connectionMessage.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _connectionMessage.Height = 22;
        _connectionMessage.ForeColor = Color.Gray;
        layout.Controls.Add(_connectionMessage, 1, 7);

        // 按钮行
        _testButton.Text = "测试连接";
        _testButton.AutoSize = true;
        _testButton.Padding = new Padding(10, 4, 10, 4);
        _testButton.Click += async (_, _) => await HandleTestConnection();

        _saveConnectionButton.Text = "保存并应用";
        _saveConnectionButton.AutoSize = true;
        _saveConnectionButton.Padding = new Padding(10, 4, 10, 4);
        _saveConnectionButton.Click += async (_, _) => await HandleSaveConnection();

        var buttonRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            AutoSize = true,
            Height = 36,
            Padding = new Padding(0, 6, 0, 0),
        };
        buttonRow.Controls.Add(_saveConnectionButton);
        buttonRow.Controls.Add(_testButton);
        layout.Controls.Add(buttonRow, 1, 8);

        page.Controls.Add(layout);
    }

    private void BuildHotkeyPage(TabPage page)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 20, 20, 16),
            ColumnCount = 2,
            AutoSize = false,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(MakeFieldLabel("修饰键："), 0, 0);
        var modsRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Height = 32,
            WrapContents = false,
        };
        _modCtrl.Text = "Ctrl";
        _modAlt.Text = "Alt";
        _modShift.Text = "Shift";
        _modWin.Text = "Win";
        foreach (var c in new[] { _modCtrl, _modAlt, _modShift, _modWin })
        {
            c.AutoSize = true;
            c.Margin = new Padding(0, 4, 16, 4);
            c.CheckedChanged += (_, _) => UpdateHotkeyPreview();
            modsRow.Controls.Add(c);
        }
        layout.Controls.Add(modsRow, 1, 0);

        layout.Controls.Add(MakeFieldLabel("主键："), 0, 1);
        _hotkeyKey.Width = 160;
        _hotkeyKey.Text = "f2";
        _hotkeyKey.TextChanged += (_, _) => UpdateHotkeyPreview();
        layout.Controls.Add(_hotkeyKey, 1, 1);

        layout.Controls.Add(MakeFieldLabel("预览："), 0, 2);
        _hotkeyPreview.AutoSize = true;
        _hotkeyPreview.Font = new Font("Consolas", 11f, FontStyle.Bold);
        _hotkeyPreview.ForeColor = SystemColors.HighlightText.IsEmpty ? Color.RoyalBlue : Color.RoyalBlue;
        _hotkeyPreview.Text = "Ctrl+F2";
        layout.Controls.Add(_hotkeyPreview, 1, 2);

        var hotkeyHint = new Label
        {
            Text = "支持的主键示例：a-z、0-9、space、tab、enter、esc、f1-f12、insert、delete、home/end、pageup/pagedown、↑↓←→",
            ForeColor = Color.Gray,
            AutoSize = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Height = 36,
        };
        layout.Controls.Add(hotkeyHint, 1, 3);

        _hotkeyMessage.AutoSize = false;
        _hotkeyMessage.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _hotkeyMessage.Height = 22;
        _hotkeyMessage.ForeColor = Color.Gray;
        layout.Controls.Add(_hotkeyMessage, 1, 4);

        _saveHotkeyButton.Text = "保存并应用";
        _saveHotkeyButton.AutoSize = true;
        _saveHotkeyButton.Padding = new Padding(10, 4, 10, 4);
        _saveHotkeyButton.Click += async (_, _) => await HandleSaveHotkey();

        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            AutoSize = true,
            Height = 36,
            Padding = new Padding(0, 6, 0, 0),
        };
        btnRow.Controls.Add(_saveHotkeyButton);
        layout.Controls.Add(btnRow, 1, 5);

        page.Controls.Add(layout);
    }

    private void BuildHotwordsPage(TabPage page)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 20, 20, 16),
            ColumnCount = 1,
            RowCount = 5,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

        _hotwordsInfo.AutoSize = false;
        _hotwordsInfo.Dock = DockStyle.Fill;
        _hotwordsInfo.ForeColor = Color.Gray;
        layout.Controls.Add(_hotwordsInfo, 0, 0);

        _hotwordsArea.Multiline = true;
        _hotwordsArea.AcceptsReturn = true;
        _hotwordsArea.AcceptsTab = false;
        _hotwordsArea.ScrollBars = ScrollBars.Vertical;
        _hotwordsArea.WordWrap = false;
        _hotwordsArea.Dock = DockStyle.Fill;
        _hotwordsArea.Font = new Font("Consolas", 10f);
        _hotwordsArea.TextChanged += (_, _) => UpdateHotwordsMeta();
        layout.Controls.Add(_hotwordsArea, 0, 1);

        _hotwordsCount.AutoSize = false;
        _hotwordsCount.Dock = DockStyle.Fill;
        _hotwordsCount.ForeColor = Color.Gray;
        _hotwordsCount.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_hotwordsCount, 0, 2);

        _reloadHotwordsButton.Text = "重新加载";
        _reloadHotwordsButton.AutoSize = true;
        _reloadHotwordsButton.Padding = new Padding(10, 4, 10, 4);
        _reloadHotwordsButton.Click += (_, _) =>
        {
            _hotwordsArea.Text = _loadedHotwordsText;
            SetHotwordsMessage("已重新加载磁盘内容", Color.Gray);
            UpdateHotwordsMeta();
        };

        _saveHotwordsButton.Text = "保存并应用";
        _saveHotwordsButton.AutoSize = true;
        _saveHotwordsButton.Padding = new Padding(10, 4, 10, 4);
        _saveHotwordsButton.Click += async (_, _) => await HandleSaveHotwords();

        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = false,
        };
        btnRow.Controls.Add(_saveHotwordsButton);
        btnRow.Controls.Add(_reloadHotwordsButton);
        layout.Controls.Add(btnRow, 0, 3);

        _hotwordsMessage.AutoSize = false;
        _hotwordsMessage.Dock = DockStyle.Fill;
        _hotwordsMessage.ForeColor = Color.Gray;
        layout.Controls.Add(_hotwordsMessage, 0, 4);

        page.Controls.Add(layout);
    }

    private static Label MakeFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = 110,
        TextAlign = ContentAlignment.MiddleRight,
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
        Padding = new Padding(0, 4, 8, 0),
    };

    // ─── 行为 ────────────────────────────────────────────────

    private void UpdateHotkeyPreview()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (_modCtrl.Checked) parts.Add("Ctrl");
        if (_modAlt.Checked) parts.Add("Alt");
        if (_modShift.Checked) parts.Add("Shift");
        if (_modWin.Checked) parts.Add("Win");
        var key = _hotkeyKey.Text.Trim();
        if (!string.IsNullOrEmpty(key)) parts.Add(key.ToUpperInvariant());
        _hotkeyPreview.Text = parts.Count == 0 ? "—" : string.Join("+", parts);
    }

    private void UpdateHotwordsMeta()
    {
        var count = 0;
        foreach (var raw in _hotwordsArea.Text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            count++;
        }
        _hotwordsCount.Text = $"词条数：{count}";

        _hotwordsInfo.Text = _additionalHotwordFileCount > 0
            ? $"这里编辑的是主热词文件。当前还有 {_additionalHotwordFileCount} 个附加词库会继续加载，但不在此处编辑。"
            : "这里编辑的是主热词文件，保存后立即写回并重新加载。";
    }

    private ServerConfig? TryBuildServerConfig()
    {
        var host = _hostField.Text.Trim();
        if (string.IsNullOrEmpty(host))
        {
            SetConnectionMessage("服务地址不能为空", Color.Firebrick);
            return null;
        }

        var server = new ServerConfig
        {
            Host = host,
            Port = (int)_portField.Value,
            Timeout = _loadedConfig.Server.Timeout > 0 ? _loadedConfig.Server.Timeout : 60.0,
            ApiKey = _apiKeyField.Text,
            LlmRecorrect = _llmCheck.Checked,
            Streaming = _streamingCheck.Checked,
        };
        return server;
    }

    private HotkeyConfig? TryBuildHotkeyConfig()
    {
        var key = _hotkeyKey.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
        {
            SetHotkeyMessage("主键不能为空", Color.Firebrick);
            return null;
        }

        var mods = new System.Collections.Generic.List<string>();
        if (_modCtrl.Checked) mods.Add("ctrl");
        if (_modAlt.Checked) mods.Add("alt");
        if (_modShift.Checked) mods.Add("shift");
        if (_modWin.Checked) mods.Add("win");

        return new HotkeyConfig { Modifiers = mods, Key = key };
    }

    private async Task HandleTestConnection()
    {
        var server = TryBuildServerConfig();
        if (server is null) return;

        SetConnectionButtonsEnabled(false);
        SetConnectionMessage("正在测试连接...", Color.Gray);

        try
        {
            var ok = (OnTestServerConnection is null)
                ? false
                : await OnTestServerConnection(server).ConfigureAwait(true);
            SetConnectionMessage(
                ok ? "连接成功，服务已就绪。" : "连接失败，请检查服务地址、端口与流式配置。",
                ok ? Color.SeaGreen : Color.Firebrick
            );
        }
        finally
        {
            SetConnectionButtonsEnabled(true);
        }
    }

    private async Task HandleSaveConnection()
    {
        var server = TryBuildServerConfig();
        if (server is null) return;
        if (OnSaveConfig is null) return;

        var draft = _loadedConfig.Clone();
        draft.Server = server;

        SetConnectionButtonsEnabled(false);
        SetConnectionMessage("保存中...", Color.Gray);
        try
        {
            await OnSaveConfig(draft).ConfigureAwait(true);
            SetConnectionMessage("设置已保存并生效。", Color.SeaGreen);
        }
        catch (Exception ex)
        {
            SetConnectionMessage($"保存失败：{ex.Message}", Color.Firebrick);
        }
        finally
        {
            SetConnectionButtonsEnabled(true);
        }
    }

    private async Task HandleSaveHotkey()
    {
        var hotkey = TryBuildHotkeyConfig();
        if (hotkey is null) return;
        if (OnSaveConfig is null) return;

        var draft = _loadedConfig.Clone();
        draft.Hotkey = hotkey;

        _saveHotkeyButton.Enabled = false;
        SetHotkeyMessage("保存中...", Color.Gray);
        try
        {
            await OnSaveConfig(draft).ConfigureAwait(true);
            SetHotkeyMessage("热键已保存并生效。", Color.SeaGreen);
        }
        catch (Exception ex)
        {
            SetHotkeyMessage($"保存失败：{ex.Message}", Color.Firebrick);
        }
        finally
        {
            _saveHotkeyButton.Enabled = true;
        }
    }

    private async Task HandleSaveHotwords()
    {
        if (OnSaveHotwords is null) return;

        _saveHotwordsButton.Enabled = false;
        _reloadHotwordsButton.Enabled = false;
        SetHotwordsMessage("保存中...", Color.Gray);
        try
        {
            await OnSaveHotwords(_hotwordsArea.Text).ConfigureAwait(true);
            SetHotwordsMessage("热词已保存并重新加载。", Color.SeaGreen);
        }
        catch (Exception ex)
        {
            SetHotwordsMessage($"保存失败：{ex.Message}", Color.Firebrick);
        }
        finally
        {
            _saveHotwordsButton.Enabled = true;
            _reloadHotwordsButton.Enabled = true;
        }
    }

    private void SetConnectionMessage(string text, Color color)
    {
        _connectionMessage.Text = text;
        _connectionMessage.ForeColor = color;
    }

    private void SetHotkeyMessage(string text, Color color)
    {
        _hotkeyMessage.Text = text;
        _hotkeyMessage.ForeColor = color;
    }

    private void SetHotwordsMessage(string text, Color color)
    {
        _hotwordsMessage.Text = text;
        _hotwordsMessage.ForeColor = color;
    }

    private void SetConnectionButtonsEnabled(bool enabled)
    {
        _testButton.Enabled = enabled;
        _saveConnectionButton.Enabled = enabled;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 关闭按钮 → 隐藏到托盘
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}
