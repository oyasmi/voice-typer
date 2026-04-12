using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using VoiceTyper.Core;
using VoiceTyper.Services;
using VoiceTyper.Support;

namespace VoiceTyper.UI;

/// <summary>
/// 设置窗口。3 个 Tab: 服务端连接、热键配置、词库管理。
/// 顶部状态栏显示麦克风和服务端连接状态。
/// 对应 macOS Swift 版的 SetupWindowController。
/// </summary>
internal sealed class SetupForm : Form
{
    /// <summary>可选主键列表 (显示名, 配置值)</summary>
    private static readonly (string Display, string Value)[] KeyOptions =
    {
        ("F1", "f1"), ("F2", "f2"), ("F3", "f3"), ("F4", "f4"),
        ("F5", "f5"), ("F6", "f6"), ("F7", "f7"), ("F8", "f8"),
        ("F9", "f9"), ("F10", "f10"), ("F11", "f11"), ("F12", "f12"),
        ("Space", "space"), ("Tab", "tab"), ("Enter", "enter"),
        ("A", "a"), ("B", "b"), ("C", "c"), ("D", "d"), ("E", "e"),
        ("F", "f"), ("G", "g"), ("H", "h"), ("I", "i"), ("J", "j"),
        ("K", "k"), ("L", "l"), ("M", "m"), ("N", "n"), ("O", "o"),
        ("P", "p"), ("Q", "q"), ("R", "r"), ("S", "s"), ("T", "t"),
        ("U", "u"), ("V", "v"), ("W", "w"), ("X", "x"), ("Y", "y"),
        ("Z", "z"),
    };

    private readonly AppConfig _config;

    // Tab 1: 服务端
    private TextBox _txtHost = null!;
    private NumericUpDown _nudPort = null!;
    private TextBox _txtApiKey = null!;
    private NumericUpDown _nudTimeout = null!;
    private CheckBox _chkLlmRecorrect = null!;
    private Label _lblServerStatus = null!;
    private Button _btnTestConnection = null!;

    // Tab 2: 热键
    private CheckBox _chkCtrl = null!, _chkAlt = null!, _chkShift = null!, _chkWin = null!;
    private ComboBox _cboKey = null!;
    private Label _lblHotkeyPreview = null!;

    // Tab 3: 词库
    private TextBox _txtHotwords = null!;

    /// <summary>获取用户编辑后的配置</summary>
    public AppConfig GetConfig() => _config;

    /// <summary>获取词库编辑器的文本内容</summary>
    public string GetHotwordsText() => _txtHotwords.Text;

    public SetupForm(AppConfig config, bool serverConnected, int micCount)
    {
        _config = CloneConfig(config);
        BuildUI(serverConnected, micCount);
    }

    // ===================================================================
    //  UI 构建
    // ===================================================================

    private void BuildUI(bool serverConnected, int micCount)
    {
        Text = $"{Constants.AppName} 设置";
        Size = new Size(500, 500);
        MinimumSize = new Size(460, 440);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9);

        // 尝试加载图标
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon.ico");
        if (File.Exists(iconPath))
            try { Icon = new System.Drawing.Icon(iconPath); } catch { }

        // ── 顶部状态面板 ──
        var statusPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 50,
            Padding = new Padding(16, 8, 16, 8),
            BackColor = Color.FromArgb(245, 245, 245),
        };

        statusPanel.Controls.Add(new Label
        {
            Text = micCount > 0
                ? $"🎤 麦克风: 已检测到 {micCount} 个设备"
                : "🎤 麦克风: ⚠ 未检测到音频输入设备",
            ForeColor = micCount > 0 ? Color.FromArgb(40, 120, 40) : Color.FromArgb(180, 60, 20),
            AutoSize = true,
            Location = new Point(16, 6),
        });

        statusPanel.Controls.Add(new Label
        {
            Text = serverConnected
                ? $"🌐 服务端: 已连接 ({_config.Server.Host}:{_config.Server.Port})"
                : "🌐 服务端: 未连接",
            ForeColor = serverConnected ? Color.FromArgb(40, 120, 40) : Color.FromArgb(180, 60, 20),
            AutoSize = true,
            Location = new Point(16, 27),
        });

        // ── Tab 控件 ──
        var tabControl = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(12, 6),
        };
        tabControl.TabPages.Add(BuildServerTab());
        tabControl.TabPages.Add(BuildHotkeyTab());
        tabControl.TabPages.Add(BuildHotwordsTab());

        // ── 底部按钮面板 ──
        var bottomPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 9, 12, 9),
        };

        var btnCancel = new Button
        {
            Text = "取消",
            Size = new Size(80, 32),
            DialogResult = DialogResult.Cancel,
        };

        var btnSave = new Button
        {
            Text = "保存并关闭",
            Size = new Size(110, 32),
        };
        btnSave.Click += OnSave;

        // RightToLeft: Cancel 在最右, Save 在其左
        bottomPanel.Controls.Add(btnCancel);
        bottomPanel.Controls.Add(btnSave);

        AcceptButton = btnSave;
        CancelButton = btnCancel;

        // ── 组装 (顺序: Bottom → Top → Fill) ──
        Controls.Add(tabControl);
        Controls.Add(statusPanel);
        Controls.Add(bottomPanel);
    }

    // ===================================================================
    //  Tab 1: 服务端连接
    // ===================================================================

    private TabPage BuildServerTab()
    {
        var page = new TabPage("服务端连接") { Padding = new Padding(16, 12, 16, 8) };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;

        // 服务器地址
        table.Controls.Add(MakeLabel("服务器地址:"), 0, row);
        _txtHost = new TextBox
        {
            Text = _config.Server.Host,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        table.Controls.Add(_txtHost, 1, row++);

        // 端口
        table.Controls.Add(MakeLabel("端口:"), 0, row);
        _nudPort = new NumericUpDown
        {
            Minimum = 1, Maximum = 65535,
            Value = _config.Server.Port,
            Width = 100,
        };
        table.Controls.Add(_nudPort, 1, row++);

        // API 密钥
        table.Controls.Add(MakeLabel("API 密钥:"), 0, row);
        _txtApiKey = new TextBox
        {
            Text = _config.Server.ApiKey ?? "",
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        table.Controls.Add(_txtApiKey, 1, row++);

        // 超时
        table.Controls.Add(MakeLabel("超时 (秒):"), 0, row);
        _nudTimeout = new NumericUpDown
        {
            Minimum = 1, Maximum = 300,
            Value = (decimal)_config.Server.Timeout,
            Width = 100,
        };
        table.Controls.Add(_nudTimeout, 1, row++);

        // LLM 纠错
        _chkLlmRecorrect = new CheckBox
        {
            Text = "启用 LLM 智能纠错（需要服务端支持）",
            Checked = _config.Server.LlmRecorrect,
            AutoSize = true,
        };
        table.Controls.Add(_chkLlmRecorrect, 0, row);
        table.SetColumnSpan(_chkLlmRecorrect, 2);
        row++;

        // 分隔
        table.Controls.Add(new Label { Height = 12 }, 0, row++);

        // 连接测试
        var testPanel = new FlowLayoutPanel { AutoSize = true };
        _btnTestConnection = new Button { Text = "测试连接", Width = 90 };
        _btnTestConnection.Click += OnTestConnection;
        _lblServerStatus = new Label
        {
            Text = "点击测试确认连接状态",
            ForeColor = Color.Gray,
            AutoSize = true,
            Padding = new Padding(6, 6, 0, 0),
        };
        testPanel.Controls.AddRange(new Control[] { _btnTestConnection, _lblServerStatus });
        table.Controls.Add(testPanel, 0, row);
        table.SetColumnSpan(testPanel, 2);

        page.Controls.Add(table);
        return page;
    }

    // ===================================================================
    //  Tab 2: 热键设置
    // ===================================================================

    private TabPage BuildHotkeyTab()
    {
        var page = new TabPage("热键设置") { Padding = new Padding(16, 12, 16, 8) };

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };

        // ── 修饰键 ──
        var modGroup = new GroupBox { Text = "修饰键", Width = 420, Height = 65, Padding = new Padding(8) };
        var modFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(4) };

        var mods = _config.Hotkey.Modifiers.Select(m => m.ToLowerInvariant()).ToList();
        _chkCtrl = new CheckBox { Text = "Ctrl", Checked = mods.Any(m => m is "ctrl" or "control"), AutoSize = true, Padding = new Padding(4, 0, 12, 0) };
        _chkAlt = new CheckBox { Text = "Alt", Checked = mods.Contains("alt"), AutoSize = true, Padding = new Padding(4, 0, 12, 0) };
        _chkShift = new CheckBox { Text = "Shift", Checked = mods.Contains("shift"), AutoSize = true, Padding = new Padding(4, 0, 12, 0) };
        _chkWin = new CheckBox { Text = "Win", Checked = mods.Any(m => m is "win" or "win_l" or "win_r" or "cmd"), AutoSize = true, Padding = new Padding(4, 0, 12, 0) };

        foreach (var cb in new[] { _chkCtrl, _chkAlt, _chkShift, _chkWin })
            cb.CheckedChanged += (_, _) => RefreshHotkeyPreview();

        modFlow.Controls.AddRange(new Control[] { _chkCtrl, _chkAlt, _chkShift, _chkWin });
        modGroup.Controls.Add(modFlow);
        layout.Controls.Add(modGroup);

        // ── 主键 ──
        layout.Controls.Add(new Label { Height = 10 });
        var keyPanel = new FlowLayoutPanel { AutoSize = true };
        keyPanel.Controls.Add(MakeLabel("主键:"));

        _cboKey = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        int selectedIndex = 0;
        for (int i = 0; i < KeyOptions.Length; i++)
        {
            _cboKey.Items.Add(KeyOptions[i].Display);
            if (KeyOptions[i].Value == _config.Hotkey.Key.ToLowerInvariant())
                selectedIndex = i;
        }
        _cboKey.SelectedIndex = selectedIndex;
        _cboKey.SelectedIndexChanged += (_, _) => RefreshHotkeyPreview();
        keyPanel.Controls.Add(_cboKey);
        layout.Controls.Add(keyPanel);

        // ── 热键预览 ──
        layout.Controls.Add(new Label { Height = 12 });
        _lblHotkeyPreview = new Label
        {
            Font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 100, 180),
            AutoSize = true,
        };
        RefreshHotkeyPreview();
        layout.Controls.Add(_lblHotkeyPreview);

        // ── 说明 ──
        layout.Controls.Add(new Label { Height = 16 });
        layout.Controls.Add(new Label
        {
            Text = "提示: 按住热键说话，松开后自动识别并输入文本。\n录音不足 0.3 秒将被忽略。",
            ForeColor = Color.Gray,
            AutoSize = true,
        });

        page.Controls.Add(layout);
        return page;
    }

    // ===================================================================
    //  Tab 3: 词库管理
    // ===================================================================

    private TabPage BuildHotwordsTab()
    {
        var page = new TabPage("词库管理") { Padding = new Padding(16, 12, 16, 8) };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 文件路径提示
        layout.Controls.Add(new Label
        {
            Text = $"词库文件: {ConfigStore.DefaultHotwordsPath}",
            ForeColor = Color.Gray,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 6),
        }, 0, 0);

        // 编辑器
        _txtHotwords = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10),
            AcceptsReturn = true,
            WordWrap = false,
        };

        try
        {
            if (File.Exists(ConfigStore.DefaultHotwordsPath))
                _txtHotwords.Text = File.ReadAllText(ConfigStore.DefaultHotwordsPath);
        }
        catch (Exception ex)
        {
            _txtHotwords.Text = $"# 加载失败: {ex.Message}";
        }

        layout.Controls.Add(_txtHotwords, 0, 1);

        // 底部操作栏
        var actionPanel = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        var btnOpen = new Button { Text = "在编辑器中打开", Width = 120 };
        btnOpen.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(ConfigStore.DefaultHotwordsPath) { UseShellExecute = true }); }
            catch { }
        };
        actionPanel.Controls.Add(btnOpen);
        actionPanel.Controls.Add(new Label
        {
            Text = "每行一个词，# 开头为注释",
            ForeColor = Color.Gray,
            AutoSize = true,
            Padding = new Padding(8, 6, 0, 0),
        });
        layout.Controls.Add(actionPanel, 0, 2);

        page.Controls.Add(layout);
        return page;
    }

    // ===================================================================
    //  事件处理
    // ===================================================================

    /// <summary>测试服务端连接</summary>
    private async void OnTestConnection(object? sender, EventArgs e)
    {
        _btnTestConnection.Enabled = false;
        _lblServerStatus.Text = "正在连接...";
        _lblServerStatus.ForeColor = Color.Gray;

        try
        {
            var testConfig = new ServerConfig
            {
                Host = _txtHost.Text.Trim(),
                Port = (int)_nudPort.Value,
                ApiKey = _txtApiKey.Text.Trim(),
            };

            using var client = new ASRClient();
            var ok = await client.HealthCheckAsync(testConfig);

            _lblServerStatus.Text = ok ? "✓ 连接成功，服务就绪" : "✗ 连接失败，请检查地址和端口";
            _lblServerStatus.ForeColor = ok
                ? Color.FromArgb(40, 120, 40)
                : Color.FromArgb(200, 60, 20);
        }
        catch (Exception ex)
        {
            _lblServerStatus.Text = $"✗ {ex.Message}";
            _lblServerStatus.ForeColor = Color.FromArgb(200, 60, 20);
        }
        finally
        {
            _btnTestConnection.Enabled = true;
        }
    }

    /// <summary>保存配置</summary>
    private void OnSave(object? sender, EventArgs e)
    {
        // 验证
        if (string.IsNullOrWhiteSpace(_txtHost.Text))
        {
            MessageBox.Show("服务器地址不能为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 收集服务端配置
        _config.Server.Host = _txtHost.Text.Trim();
        _config.Server.Port = (int)_nudPort.Value;
        _config.Server.ApiKey = _txtApiKey.Text.Trim();
        _config.Server.Timeout = (double)_nudTimeout.Value;
        _config.Server.LlmRecorrect = _chkLlmRecorrect.Checked;

        // 收集热键配置
        var modifiers = new List<string>();
        if (_chkCtrl.Checked) modifiers.Add("ctrl");
        if (_chkAlt.Checked) modifiers.Add("alt");
        if (_chkShift.Checked) modifiers.Add("shift");
        if (_chkWin.Checked) modifiers.Add("win_l");
        _config.Hotkey.Modifiers = modifiers;

        if (_cboKey.SelectedIndex >= 0 && _cboKey.SelectedIndex < KeyOptions.Length)
            _config.Hotkey.Key = KeyOptions[_cboKey.SelectedIndex].Value;

        DialogResult = DialogResult.OK;
    }

    /// <summary>刷新热键预览文本</summary>
    private void RefreshHotkeyPreview()
    {
        var parts = new List<string>();
        if (_chkCtrl.Checked) parts.Add("Ctrl");
        if (_chkAlt.Checked) parts.Add("Alt");
        if (_chkShift.Checked) parts.Add("Shift");
        if (_chkWin.Checked) parts.Add("Win");

        var keyName = (_cboKey.SelectedIndex >= 0 && _cboKey.SelectedIndex < KeyOptions.Length)
            ? KeyOptions[_cboKey.SelectedIndex].Display
            : "?";
        parts.Add(keyName);

        _lblHotkeyPreview.Text = $"当前热键: {string.Join(" + ", parts)}";
    }

    // ===================================================================
    //  Helper
    // ===================================================================

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Padding = new Padding(0, 5, 8, 0),
    };

    private static AppConfig CloneConfig(AppConfig src) => new()
    {
        Server = new ServerConfig
        {
            Host = src.Server.Host,
            Port = src.Server.Port,
            Timeout = src.Server.Timeout,
            ApiKey = src.Server.ApiKey,
            LlmRecorrect = src.Server.LlmRecorrect,
        },
        Hotkey = new HotkeyConfig
        {
            Modifiers = new List<string>(src.Hotkey.Modifiers),
            Key = src.Hotkey.Key,
        },
        HotwordFiles = new List<string>(src.HotwordFiles),
        Ui = new UiConfig
        {
            Opacity = src.Ui.Opacity,
            Width = src.Ui.Width,
            Height = src.Ui.Height,
        },
    };
}
