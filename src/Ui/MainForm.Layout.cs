using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace JFWorkbench;

public partial class MainForm
{
    // Named surfaces also let the offline review sample the actual visible rectangles.
    private readonly Panel shellRoot = new() { Dock = DockStyle.Fill, Name = "shellRoot" };
    private readonly Panel sidebar = new() { Dock = DockStyle.Left, Width = 64, Name = "sidebar" };
    private readonly Panel header = new() { Dock = DockStyle.Top, Height = 56, Name = "header" };
    private readonly StatusStrip statusStrip = new() { Name = "statusStrip" };
    private readonly Panel webHost = new() { Dock = DockStyle.Fill, Name = "webHost" };
    private readonly Panel drawer = new() { Dock = DockStyle.Right, Width = 360, Name = "drawer", Visible = false };
    private readonly Panel contentArea = new() { Dock = DockStyle.Fill, Name = "contentArea" };
    private readonly Panel workspace = new() { Dock = DockStyle.Fill };
    private readonly Panel railNavigation = new();
    private readonly Panel railControls = new() { Dock = DockStyle.Bottom, Height = 224 };
    private readonly Label brandName = MakeLabel("JFlash", 18.5F);
    private readonly Label drawerTitle = MakeLabel("网关总览", 18.5F, true);
    private readonly ShellButton expandRailButton = new() { IconKey = "expand", ShowCaption = false, DrawOutline = false };
    private readonly ShellButton brandMark = new() { IconKey = "bolt", ShowCaption = false, DrawOutline = false };
    private readonly ShellButton drawerCloseButton = new() { IconKey = "close", ShowCaption = false, DrawOutline = false };
    private readonly Panel railStatusDot = new();
    private readonly ToolTip uiTooltip = new() { AutoPopDelay = 9000, InitialDelay = 450, ReshowDelay = 120, ShowAlways = true };
    private bool railExpanded;
    private bool focusMode;
    private string drawerPage = "workbench";
    private bool drawerOverlaysWebView;
    private bool arrangingChrome;
    private int? uiReviewDpiOverride = null;

    private int LayoutPixel(int value) => (int)Math.Round(value * ((uiReviewDpiOverride ?? (IsHandleCreated ? DeviceDpi : 96)) / 96F));

    private void BuildLayout()
    {
        KeyPreview = true;
        shellRoot.BackColor = Canvas;
        sidebar.BackColor = Color.FromArgb(247, 248, 244);
        header.BackColor = Canvas;
        workspace.BackColor = Canvas;
        contentArea.BackColor = Color.White;
        webHost.BackColor = Color.White;
        drawer.BackColor = Canvas;
        shellRoot.Controls.Add(workspace);
        shellRoot.Controls.Add(sidebar);
        workspace.Controls.Add(contentArea);
        workspace.Controls.Add(header);
        contentArea.Controls.Add(workbenchPanel);
        contentArea.Controls.Add(drawer);
        Controls.Add(shellRoot);

        statusStrip.AutoSize = false;
        statusStrip.Height = 24;
        statusStrip.Dock = DockStyle.Bottom;
        statusStrip.BackColor = Canvas;
        statusStrip.SizingGrip = false;
        statusStrip.Padding = new Padding(16, 0, 16, 0);
        statusStrip.Font = UiFont(11);
        statusStrip.Items.Add(stripState);
        statusStrip.Items.Add(stripEndpoint);
        statusStrip.ShowItemToolTips = true;
        Controls.Add(statusStrip);
        // Reserve the bottom strip before filling the workspace.
        Controls.SetChildIndex(shellRoot, 0);

        expandRailButton.Click += (_, _) => SetRailExpanded(!railExpanded);
        expandRailButton.AccessibleName = "展开或折叠导航";
        brandMark.ForeColor = Accent;
        brandMark.Enabled = false;
        sidebar.Controls.Add(expandRailButton);
        sidebar.Controls.Add(brandMark);
        sidebar.Controls.Add(brandName);
        sidebar.Controls.Add(railNavigation);
        sidebar.Controls.Add(railControls);
        foreach (var button in new[] { workbenchNav, statusNav, logsNav })
        {
            railNavigation.Controls.Add(button);
            uiTooltip.SetToolTip(button, button.Text);
        }
        workbenchNav.Click += (_, _) => SelectPage("workbench");
        statusNav.Click += (_, _) => SelectPage(drawer.Visible && drawerPage == "status" ? "workbench" : "status");
        logsNav.Click += async (_, _) => { bool hide = drawer.Visible && drawerPage == "logs"; SelectPage(hide ? "workbench" : "logs"); if (!hide) await RefreshLogsAsync(); };
        ConfigureRailControl(startButton, "start");
        ConfigureRailControl(stopButton, "stop");
        ConfigureRailControl(restartButton, "restart");
        startButton.Click += async (_, _) => await RunOperationAsync("start");
        stopButton.Click += async (_, _) => await RunOperationAsync("stop");
        restartButton.Click += async (_, _) => await RunOperationAsync("restart");
        railState.AutoSize = false;
        railState.AutoEllipsis = true;
        railState.TextAlign = ContentAlignment.MiddleLeft;
        railControls.Controls.Add(railState);
        railControls.Controls.Add(railStatusDot);
        railStatusDot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(snapshot.IsReady ? Accent : Muted);
            int size = LayoutPixel(6);
            e.Graphics.FillEllipse(brush, (railStatusDot.Width - size) / 2, (railStatusDot.Height - size) / 2, size, size);
        };
        railState.TextChanged += (_, _) => { uiTooltip.SetToolTip(railStatusDot, railState.Text); uiTooltip.SetToolTip(railState, railState.Text); railStatusDot.Invalidate(); };

        BuildHeader();
        BuildDrawer();
        BuildStatusPage();
        BuildWorkbenchPage();
        BuildLogsPage();
        contentArea.SizeChanged += (_, _) => ArrangeWorkspace();
        sidebar.SizeChanged += (_, _) => ArrangeRail();
        header.SizeChanged += (_, _) => ArrangeHeader();
        DpiChanged += (_, _) => { ApplyChromeMetrics(); ArrangeWorkspace(); };
        HandleCreated += (_, _) => ApplyChromeMetrics();
        Shown += (_, _) =>
        {
            if (Location.X < -10000 || WindowState != FormWindowState.Normal) return;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int width = Math.Min(Width, area.Width - LayoutPixel(16));
            int height = Math.Min(Height, area.Height - LayoutPixel(16));
            if (width == Width && height == Height) return;
            Size = new Size(Math.Max(MinimumSize.Width, width), Math.Max(MinimumSize.Height, height));
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        };
        Disposed += (_, _) => uiTooltip.Dispose();
        SetRailExpanded(false);
        SelectPage("workbench");
    }

    private void ConfigureRailControl(Button button, string icon)
    {
        button.AutoSize = false;
        button.MinimumSize = Size.Empty;
        button.Margin = Padding.Empty;
        button.Padding = Padding.Empty;
        if (button is ShellButton shellButton) { shellButton.IconKey = icon; shellButton.ShowCaption = false; shellButton.DrawOutline = false; }
        uiTooltip.SetToolTip(button, button.Text);
        railControls.Controls.Add(button);
    }

    private readonly Panel headerActions = new();
    private void BuildHeader()
    {
        headerTitle.AutoSize = false;
        headerTitle.Text = "工作台";
        headerTitle.TextAlign = ContentAlignment.MiddleLeft;
        headerSubtitle.AutoSize = false;
        headerSubtitle.AutoEllipsis = true;
        headerSubtitle.TextAlign = ContentAlignment.MiddleLeft;
        headerSubtitle.Text = demo ? "离线演示" : p1 ? "P1 · 18790" : "生产 · 单一属主";
        workbenchHint.AutoSize = false;
        workbenchHint.AutoEllipsis = true;
        workbenchHint.TextAlign = ContentAlignment.MiddleLeft;
        workbenchHint.TextChanged += (_, _) => uiTooltip.SetToolTip(workbenchHint, workbenchHint.Text);
        header.Controls.AddRange(new Control[] { headerTitle, headerSubtitle, workbenchHint, headerActions });
        var refresh = HeaderButton("refresh", "刷新网关状态", async (_, _) => await RefreshStatusAsync(true));
        var reconnect = HeaderButton("reconnect", "重新连接工作台", async (_, _) => await ReconnectWorkbenchAsync());
        reconnect.Enabled = !demo;
        var browser = HeaderButton("browser", "在浏览器打开", (_, _) => OpenInBrowser());
        browser.Enabled = !demo && !p1;
        var focus = HeaderButton("focus", "专注模式 · F11 / Ctrl+Shift+F", (_, _) => SetFocusMode(!focusMode));
        headerActions.Controls.AddRange(new Control[] { refresh, reconnect, browser, focus });
    }

    private ShellButton HeaderButton(string icon, string tooltip, EventHandler handler)
    {
        var button = new ShellButton { IconKey = icon, ShowCaption = false, DrawOutline = false, BackColor = Canvas, ForeColor = Muted, AccessibleName = tooltip, TabStop = true };
        button.Click += handler;
        uiTooltip.SetToolTip(button, tooltip);
        return button;
    }

    private void BuildDrawer()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 56 };
        drawerTitle.AutoSize = false;
        drawerTitle.TextAlign = ContentAlignment.MiddleLeft;
        top.Controls.Add(drawerTitle);
        top.Controls.Add(drawerCloseButton);
        drawerCloseButton.AccessibleName = "关闭信息抽屉";
        drawerCloseButton.BackColor = Canvas;
        drawerCloseButton.Click += (_, _) => SelectPage("workbench");
        uiTooltip.SetToolTip(drawerCloseButton, "关闭抽屉");
        top.SizeChanged += (_, _) =>
        {
            top.Height = LayoutPixel(56);
            drawerTitle.SetBounds(LayoutPixel(16), 0, Math.Max(20, top.Width - LayoutPixel(72)), top.Height);
            drawerCloseButton.SetBounds(top.Width - LayoutPixel(48), LayoutPixel(8), LayoutPixel(32), LayoutPixel(40));
        };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 16, 16), BackColor = Canvas };
        body.Controls.Add(statusPanel);
        body.Controls.Add(logsPanel);
        drawer.Controls.Add(body);
        drawer.Controls.Add(top);
        drawer.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(230, 232, 228)); e.Graphics.DrawLine(pen, 0, 0, 0, drawer.Height); };
    }

    private void BuildStatusPage()
    {
        statusPanel.BackColor = Canvas;
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Canvas, Margin = Padding.Empty };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(stack, ContentCard(overallTitle, overallDetail, endpointLabel), 0);
        AddRow(stack, HealthCard("存活探测", liveValue, checkLabel), 0);
        AddRow(stack, HealthCard("服务就绪", readyValue, MakeLabel("通道降级只提示，不触发重启", 11, false, Muted)), 0);
        AddRow(stack, HealthCard("RPC 检测", rpcValue, latencyLabel), 0);
        AddRow(stack, ContentCard(MakeLabel("启动与恢复", 12), startupLabel), 0);
        recoveryCheck.Enabled = demo || p1;
        recoveryCheck.CheckedChanged += (_, _) => { if (!applyingSnapshot && (demo || p1)) controller.SetAutoRecovery(recoveryCheck.Checked); };
        AddRow(stack, ContentCard(recoveryCheck, recoveryDescription), 0);
        eventsText.MinimumSize = new Size(0, 144);
        eventsText.Height = 144;
        eventsText.WordWrap = true;
        eventsText.ScrollBars = ScrollBars.Vertical;
        AddRow(stack, ContentCard(MakeLabel("最近事件", 12), eventsText), 0);
        AddRow(stack, ContentCard(MakeLabel("当前连接", 12), settingsSummary), 0);
        statusPanel.Controls.Add(stack);
        statusPanel.Resize += (_, _) => stack.Width = Math.Max(120, statusPanel.ClientSize.Width - (statusPanel.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
    }

    private void BuildWorkbenchPage()
    {
        workbenchPanel.Controls.Add(webHost);
        if (demo || p1LifecycleTestSafeUi)
        {
            var notice = new Panel { BackColor = Color.White, Width = 520, Height = 240 };
            var title = MakeLabel(p1LifecycleTestSafeUi ? "P1 网关控制验收" : "你的 OpenClaw 工作空间", 18.5F, true);
            title.AutoSize = false;
            title.TextAlign = ContentAlignment.MiddleCenter;
            title.SetBounds(0, 40, 520, 48);
            var body = MakeLabel(p1LifecycleTestSafeUi ? "此窗口的按钮会实际控制隔离端口18790。" : "现有 Control UI 会在这里完整展示。", 12, false, Muted);
            body.AutoSize = false;
            body.TextAlign = ContentAlignment.MiddleCenter;
            body.SetBounds(0, 96, 520, 32);
            var caption = MakeLabel(p1LifecycleTestSafeUi ? "验收时不打开浏览器，连接真实P1控制桥。" : "当前为离线演示，不连接真实网关。", 11, false, Muted);
            caption.AutoSize = false;
            caption.TextAlign = ContentAlignment.MiddleCenter;
            caption.SetBounds(0, 136, 520, 32);
            var inspect = MakeButton("查看演示状态");
            inspect.AutoSize = false;
            inspect.SetBounds(172, 184, 176, 40);
            inspect.Click += (_, _) => SelectPage("status");
            notice.Controls.AddRange(new Control[] { title, body, caption, inspect });
            var demoSurface = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Name = "offlineWorkbenchViewport" };
            demoSurface.Controls.Add(notice);
            webHost.Controls.Add(demoSurface);
            demoSurface.SizeChanged += (_, _) => notice.Location = new Point((demoSurface.ClientSize.Width - notice.Width) / 2, (demoSurface.ClientSize.Height - notice.Height) / 2);
            workbenchHint.Text = "演示状态仅在本地变化。";
        }
        else
        {
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
            // Browser keyboard routing is UI-only; it does not alter navigation or authentication.
            web.KeyDown += (_, key) =>
            {
                bool toggle = key.KeyCode == Keys.F11 || key.KeyCode == Keys.F && key.Control && key.Shift;
                bool leave = key.KeyCode == Keys.Escape && focusMode;
                if (!toggle && !leave) return;
                key.Handled = true;
                key.SuppressKeyPress = true;
                BeginInvoke(new Action(() => SetFocusMode(leave ? false : !focusMode)));
            };
            webHost.Controls.Add(web);
        }
    }

    private void BuildLogsPage()
    {
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 8), WrapContents = true };
        var refresh = MakeButton("刷新");
        refresh.Click += async (_, _) => await RefreshLogsAsync();
        var export = MakeButton("导出");
        export.Click += async (_, _) => await ExportDiagnosticsAsync();
        var openData = MakeButton("数据目录");
        openData.Click += (_, _) => OpenDataDirectory();
        toolbar.Controls.AddRange(new Control[] { refresh, export, openData });
        var card = Card(200);
        card.AutoSize = false;
        card.Dock = DockStyle.Fill;
        logsText.Dock = DockStyle.Fill;
        logsText.Text = demo ? "离线演示：尚无控制事件。" : p1 ? "点击刷新读取 P1 独立控制记录。" : "点击刷新读取控制记录。";
        card.Controls.Add(logsText);
        var foot = MakeLabel("诊断不包含聊天内容、配置原文和配对链接。", 11, false, Muted);
        foot.AutoSize = false;
        foot.Height = 72;
        foot.Dock = DockStyle.Bottom;
        foot.Padding = new Padding(0, 8, 0, 0);
        logsPanel.Controls.Add(card);
        logsPanel.Controls.Add(foot);
        logsPanel.Controls.Add(toolbar);
    }

    private void SelectPage(string page)
    {
        drawerPage = page is "status" or "logs" ? page : "workbench";
        workbenchPanel.Visible = true;
        statusPanel.Visible = drawerPage == "status";
        logsPanel.Visible = drawerPage == "logs";
        drawer.Visible = !focusMode && drawerPage != "workbench";
        drawerTitle.Text = drawerPage == "logs" ? "运行日志" : "网关总览";
        foreach (var (button, selected) in new[] { (workbenchNav, drawerPage == "workbench"), (statusNav, drawerPage == "status"), (logsNav, drawerPage == "logs") })
        {
            if (button is ShellButton shellButton) shellButton.Selected = selected;
            button.ForeColor = selected ? Accent : Muted;
            button.Invalidate();
        }
        if (drawerPage == "status") statusPanel.AutoScrollPosition = Point.Empty;
        ArrangeWorkspace();
    }

    internal void SetRailExpanded(bool expanded)
    {
        railExpanded = expanded;
        sidebar.Width = LayoutPixel(expanded ? 220 : 64);
        expandRailButton.IconKey = expanded ? "collapse" : "expand";
        uiTooltip.SetToolTip(expandRailButton, expanded ? "折叠导航" : "展开导航");
        brandName.Visible = expanded;
        railState.Visible = expanded;
        foreach (var button in new[] { workbenchNav, statusNav, logsNav, startButton, stopButton, restartButton })
            if (button is ShellButton shellButton) { shellButton.ShowCaption = expanded; shellButton.Invalidate(); }
        ArrangeRail();
        ArrangeWorkspace();
    }

    internal void SetFocusMode(bool focused)
    {
        focusMode = focused;
        SuspendLayout();
        sidebar.Visible = !focused;
        header.Visible = !focused;
        statusStrip.Visible = !focused;
        drawer.Visible = !focused && drawerPage != "workbench";
        workbenchPanel.Visible = true;
        ResumeLayout(true);
        ArrangeWorkspace();
        if (focused) web?.Focus();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.F11 || keyData == (Keys.Control | Keys.Shift | Keys.F)) { SetFocusMode(!focusMode); return true; }
        if (focusMode && keyData == Keys.Escape) { SetFocusMode(false); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private void ApplyChromeMetrics()
    {
        sidebar.Width = LayoutPixel(railExpanded ? 220 : 64);
        header.Height = LayoutPixel(56);
        statusStrip.Height = LayoutPixel(24);
        railControls.Height = LayoutPixel(224);
        ArrangeRail();
        ArrangeHeader();
    }

    private void ArrangeRail()
    {
        int width = sidebar.ClientSize.Width;
        expandRailButton.SetBounds(LayoutPixel(8), LayoutPixel(8), LayoutPixel(48), LayoutPixel(40));
        brandMark.SetBounds(LayoutPixel(8), LayoutPixel(64), LayoutPixel(48), LayoutPixel(40));
        brandName.SetBounds(LayoutPixel(64), LayoutPixel(64), Math.Max(0, width - LayoutPixel(80)), LayoutPixel(40));
        railNavigation.SetBounds(0, LayoutPixel(128), width, LayoutPixel(176));
        var navButtons = new[] { workbenchNav, statusNav, logsNav };
        for (int i = 0; i < navButtons.Length; i++) navButtons[i].SetBounds(LayoutPixel(8), LayoutPixel(i * 56), width - LayoutPixel(16), LayoutPixel(48));
        railStatusDot.SetBounds(LayoutPixel(8), 0, LayoutPixel(48), LayoutPixel(40));
        railState.SetBounds(LayoutPixel(56), 0, Math.Max(0, width - LayoutPixel(72)), LayoutPixel(40));
        var actions = new[] { startButton, stopButton, restartButton };
        for (int i = 0; i < actions.Length; i++) actions[i].SetBounds(LayoutPixel(8), LayoutPixel(48 + i * 56), width - LayoutPixel(16), LayoutPixel(48));
    }

    private void ArrangeHeader()
    {
        headerTitle.SetBounds(LayoutPixel(16), 0, LayoutPixel(104), header.Height);
        headerSubtitle.SetBounds(LayoutPixel(136), 0, LayoutPixel(144), header.Height);
        headerActions.SetBounds(Math.Max(LayoutPixel(320), header.Width - LayoutPixel(208)), LayoutPixel(8), LayoutPixel(192), LayoutPixel(40));
        for (int i = 0; i < headerActions.Controls.Count; i++) headerActions.Controls[i].SetBounds(LayoutPixel(i * 48), 0, LayoutPixel(40), LayoutPixel(40));
        workbenchHint.SetBounds(LayoutPixel(296), 0, Math.Max(0, headerActions.Left - LayoutPixel(312)), header.Height);
    }

    private void ArrangeWorkspace()
    {
        if (arrangingChrome || IsDisposed) return;
        arrangingChrome = true;
        try
        {
            drawerOverlaysWebView = contentArea.ClientSize.Width < LayoutPixel(1320);
            if (drawerOverlaysWebView)
            {
                drawer.Dock = DockStyle.None;
                drawer.SetBounds(Math.Max(0, contentArea.ClientSize.Width - LayoutPixel(360)), 0, Math.Min(LayoutPixel(360), contentArea.ClientSize.Width), contentArea.ClientSize.Height);
                drawer.BringToFront();
            }
            else { drawer.Dock = DockStyle.Right; drawer.Width = LayoutPixel(360); drawer.SendToBack(); }
            contentArea.PerformLayout();
        }
        finally { arrangingChrome = false; }
    }

    private static Label MakeLabel(string text, float size, bool bold = false, Color? color = null) => new()
    {
        Text = text, Font = UiFont(size, bold ? FontStyle.Bold : FontStyle.Regular),
        ForeColor = color ?? Ink, BackColor = Color.Transparent, AutoSize = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 8)
    };

    private static TextBox MakeTextBox() => new()
    {
        Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, TabStop = false,
        BackColor = Color.White, ForeColor = Color.FromArgb(93, 105, 96),
        Font = new Font("Consolas", 12F), ScrollBars = ScrollBars.Both, WordWrap = false,
        Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty
    };

    private static Button MakeButton(string text, bool primary = false) => new ShellButton
    {
        Text = text, AutoSize = false, Size = new Size(88, 40), MinimumSize = new Size(80, 40),
        Margin = new Padding(0, 0, 8, 8), Padding = new Padding(8),
        BackColor = Color.White, ForeColor = Ink, Font = UiFont(12), FilledAccent = primary,
        AccessibleName = text
    };

    private static Button MakeNav(string text, string glyph) => new ShellButton
    {
        Text = text, IconKey = text == "网关状态" ? "status" : text == "运行日志" ? "logs" : "workbench",
        Size = new Size(48, 48), MinimumSize = Size.Empty, ShowCaption = false, DrawOutline = false,
        BackColor = Color.FromArgb(247, 248, 244), ForeColor = Muted, Font = UiFont(12), AccessibleName = text
    };

    private static Font UiFont(float points, FontStyle style = FontStyle.Regular) => new("Microsoft YaHei UI", Math.Max(11, points), style, GraphicsUnit.Point);

    private static Size ScaleSize(Size size, float factor) => new((int)Math.Round(size.Width * factor), (int)Math.Round(size.Height * factor));

    private void ApplyUiScale()
    {
        // uiScale changes typography only; it never changes the promised rail, header, or window geometry.
        void Apply(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                Font old = control.Font;
                control.Font = new Font(old.FontFamily, Math.Max(11, old.SizeInPoints * (float)uiScale), old.Style, GraphicsUnit.Point);
                Apply(control);
            }
        }
        if (Math.Abs(uiScale - 1) > .001) Apply(this);
        Font = UiFont(12 * (float)uiScale);
        ApplyChromeMetrics();
    }

    private static Panel Card(int height) => new CardPanel
    {
        Height = height, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
        BackColor = Color.White, Padding = new Padding(16), Margin = new Padding(0, 0, 0, 8)
    };

    private static Panel ContentCard(params Control[] content)
    {
        var card = Card(0);
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (Control control in content) AddRow(table, control, 0);
        card.Controls.Add(table);
        bool measuring = false;
        void FitContent()
        {
            if (measuring || card.IsDisposed) return;
            int width = card.ClientSize.Width - card.Padding.Horizontal;
            if (width < 40) return;
            measuring = true;
            try
            {
                int height = card.Padding.Vertical;
                foreach (Control control in content)
                {
                    int textWidth = Math.Max(20, width - control.Margin.Horizontal);
                    if (control is Label) { control.MaximumSize = new Size(textWidth, 0); control.MinimumSize = Size.Empty; }
                    int preferredHeight = control.GetPreferredSize(new Size(textWidth, int.MaxValue)).Height;
                    height += preferredHeight + control.Margin.Vertical;
                }
                card.MinimumSize = new Size(0, height);
            }
            finally { measuring = false; }
        }
        card.SizeChanged += (_, _) => FitContent();
        foreach (Control control in content) { control.TextChanged += (_, _) => FitContent(); control.FontChanged += (_, _) => FitContent(); }
        return card;
    }

    private static Panel HealthCard(string caption, Label value, Label note) => ContentCard(MakeLabel(caption, 11, false, Muted), value, note);

    private static void AddRow(TableLayoutPanel table, Control control, int height)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        table.Controls.Add(control, 0, row);
    }

    private sealed class CardPanel : Panel
    {
        public CardPanel() { DoubleBuffered = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Canvas);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = ShellButton.Rounded(new RectangleF(.5F, .5F, Width - 1F, Height - 1F), 12 * (ShellButton.ReviewDpiOverride ?? DeviceDpi) / 96F);
            using var fill = new SolidBrush(BackColor);
            e.Graphics.FillPath(fill, path);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = ShellButton.Rounded(new RectangleF(.5F, .5F, Width - 1F, Height - 1F), 12 * (ShellButton.ReviewDpiOverride ?? DeviceDpi) / 96F);
            using var pen = new Pen(Color.FromArgb(230, 232, 228));
            e.Graphics.DrawPath(pen, path);
        }
    }
}
