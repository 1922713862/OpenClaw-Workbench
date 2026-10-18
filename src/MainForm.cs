using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using ClawDesk;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace JFWorkbench;

public partial class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(45, 51, 49);
    private static readonly Color Muted = Color.FromArgb(94, 104, 99);
    private static readonly Color Accent = Color.FromArgb(33, 106, 85);
    private static readonly Color Canvas = Color.FromArgb(247, 248, 247);
    private readonly string shellDir;
    private readonly string settingsFile;
    private readonly bool demo;
    private readonly bool p1;
    private readonly bool observer;
    private readonly bool smokeTest;
    private readonly bool p1Smoke;
    private readonly bool p1StopSmoke;
    private readonly bool p1LifecycleTestSafeUi;
    private readonly bool startInTray;
    private readonly string appDataDir;
    private readonly string screenshotPath;
    private readonly double uiScale;
    private readonly IGatewayController controller;
    private readonly Panel workbenchPanel = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly Panel statusPanel = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Canvas };
    private readonly Panel logsPanel = new() { Dock = DockStyle.Fill, BackColor = Canvas };
    private readonly Label headerTitle = MakeLabel("工作台", 18.5F, true);
    private readonly Label headerSubtitle = MakeLabel("熟悉的 OpenClaw Control UI，连接状态随时可见。", 11, false, Muted);
    private readonly Label railState = MakeLabel("正在检测网关", 11);
    private readonly Label overallTitle = MakeLabel("正在检测本机网关", 18.5F, true);
    private readonly Label overallDetail = MakeLabel("读取安装配置并检查实际运行状态，不会自动启动。", 12, false, Muted);
    private readonly Label endpointLabel = MakeLabel("本机入口 · 检测中", 11, false, Muted);
    private readonly Label liveValue = MakeLabel("检测中", 16.5F, true);
    private readonly Label readyValue = MakeLabel("检测中", 16.5F, true);
    private readonly Label rpcValue = MakeLabel("尚未检查", 16.5F, true);
    private readonly Label checkLabel = MakeLabel("等待首次检查", 11, false, Muted);
    private readonly Label latencyLabel = MakeLabel("响应时间：—", 11, false, Muted);
    private readonly Label startupLabel = MakeLabel("正在读取网关状态…", 12, false, Muted);
    private readonly Label recoveryDescription = MakeLabel("自动恢复默认关闭。", 12, false, Muted);
    private readonly Label settingsSummary = MakeLabel("正在检测本机连接配置…", 12, false, Muted);
    private readonly Label workbenchHint = MakeLabel("网关初始化期间，可以切换到“网关状态”查看进度。", 11, false, Muted);
    private readonly TextBox eventsText = MakeTextBox();
    private readonly TextBox logsText = MakeTextBox();
    private readonly CheckBox recoveryCheck = new() { Text = "断线自动恢复", AutoSize = true, ForeColor = Ink, Font = UiFont(12) };
    private readonly Button startButton = MakeButton("启动网关", true);
    private readonly Button stopButton = MakeButton("停止网关");
    private readonly Button restartButton = MakeButton("重启网关");
    private readonly Button workbenchNav = MakeNav("工作台", "\uE8A5");
    private readonly Button statusNav = MakeNav("网关状态", "\uE9D9");
    private readonly Button logsNav = MakeNav("运行日志", "\uE8A5");
    private readonly ToolStripStatusLabel stripState = new("网关：检测中…");
    private readonly ToolStripStatusLabel stripEndpoint = new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly System.Windows.Forms.Timer rpcTimer = new() { Interval = 60000 };
    private WebView2? web;
    private NotifyIcon? tray;
    private ToolStripMenuItem? trayState;
    private ToolStripMenuItem? closeModeItem;
    private ToolStripMenuItem? trayStart;
    private ToolStripMenuItem? trayStop;
    private ToolStripMenuItem? trayRestart;
    private ToolStripMenuItem? trayStopExit;
    private Icon? trayStatusIcon;
    private string trayIconState = "";
    private GatewaySnapshot snapshot = new();
    private bool closeStopsGateway;
    private bool paired;
    private bool allowExit;
    private bool stoppingForExit;
    private bool operationBusy;
    private bool navigationBusy;
    private bool onBootPage = true;
    private bool hasEnteredGateway;
    private bool previouslyUsable;
    private bool applyingSnapshot;
    private bool refreshBusy;
    private int pairAttempts;
    private int navigationVersion;
    private bool initialVisibilityHandled;
    private bool showRequested;
    private bool initializationStarted;
    private bool disposedManaged;
    private readonly TaskCompletionSource<bool> p1PairEvidence = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool p1ConnectSeen, p1DeviceIdentity, p1HelloOk, p1DeviceTokenExists, p1StoredDeviceMatches, p1OperatorTokenStored;

    public MainForm()
    {
        var args = Environment.GetCommandLineArgs();
        smokeTest = args.Any(a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
        // Offline mode is decided BEFORE looking at a user profile or loading production settings.
        demo = smokeTest || args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase) || a.Equals("--preview", StringComparison.OrdinalIgnoreCase));
        p1 = !demo && args.Any(a => a.Equals("--p1", StringComparison.OrdinalIgnoreCase));
        p1Smoke = p1 && args.Any(a => a.Equals("--p1-smoke", StringComparison.OrdinalIgnoreCase));
        p1StopSmoke = p1 && args.Any(a => a.Equals("--p1-stop-smoke", StringComparison.OrdinalIgnoreCase));
        p1LifecycleTestSafeUi = p1 && args.Any(a => a.Equals("--p1-lifecycle-test-safeUI", StringComparison.OrdinalIgnoreCase));
        startInTray = !smokeTest && !p1Smoke && !p1StopSmoke && args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase) || a.Equals("--guard", StringComparison.OrdinalIgnoreCase));
        appDataDir = Path.Combine(AppContext.BaseDirectory, demo ? "demo-data" : p1 ? "p1-data" : "data");
        var settings = demo ? GatewaySettings.Demo() : p1 ? new GatewaySettings
        {
            Port = P1Identity.Expected.Port, Profile = P1Identity.Expected.Profile,
            TaskName = P1Identity.Expected.TaskName, AutoRecover = false
        }.Validate() : GatewaySettings.Load(appDataDir);
        observer = false;
        uiScale = double.IsFinite(settings.UiScale) ? Math.Clamp(settings.UiScale, 0.9, 1.5) : 1;
        string? scaleArgument = ArgumentValue(args, "--ui-scale");
        if ((demo || p1) && double.TryParse(scaleArgument, NumberStyles.Float, CultureInfo.InvariantCulture, out double requestedScale) && double.IsFinite(requestedScale))
            uiScale = Math.Clamp(requestedScale, 0.9, 1.5);
        screenshotPath = Path.GetFullPath(ArgumentValue(args, "--screenshot") ?? Path.Combine(AppContext.BaseDirectory, "JFWorkbench-preview.png"));
        shellDir = demo || p1 ? appDataDir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "jf-shell");
        settingsFile = Path.Combine(shellDir, "shell.json");
        if (!demo) LoadSettings();
        if (observer) closeStopsGateway = false;
        controller = p1 ? new P1GatewayController(appDataDir) : new JfctlGatewayController(appDataDir);
        controller.Updated += ControllerUpdated;

        Text = "JFlash 工作台 · OpenClaw · " + (demo ? "离线演示" : p1 ? "P1 隔离实例" : "生产 · 单一属主");
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1440, 900);
        MinimumSize = SizeFromClientSize(new Size(1100, 720));
        Font = UiFont(12);
        BackColor = Canvas;
        ForeColor = Ink;
        Icon = TrayIconFactory.Make(64, true);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        BuildLayout();
        ApplyUiScale();
        if (smokeTest || p1Smoke || p1StopSmoke || p1LifecycleTestSafeUi)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-16000, -16000);
            ShowInTaskbar = false;
        }
        if (!smokeTest && !p1Smoke && !p1StopSmoke) BuildTray();
        SelectPage("workbench");
        SetControlEnabled();
        rpcTimer.Tick += async (_, _) => await RefreshStatusAsync(true);
        Load += async (_, _) => await EnsureInitializedAsync();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        FormClosing += OnClosing;
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    protected override void SetVisibleCore(bool value)
    {
        if (value && startInTray && !showRequested && !initialVisibilityHandled)
        {
            initialVisibilityHandled = true;
            // Create a message-loop handle without ever showing a first frame or taskbar button.
            ShowInTaskbar = false;
            if (!IsHandleCreated) CreateHandle();
            BeginInvoke(new Action(async () => await EnsureInitializedAsync()));
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(value);
    }

    private async Task EnsureInitializedAsync()
    {
        if (initializationStarted || IsDisposed) return;
        initializationStarted = true;
        await InitializeAsync();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(async () => { try { if (!IsDisposed) await controller.NotifyResumeAsync(); } catch { } })); }
        catch (InvalidOperationException) { }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var gatewayInitialization = controller.InitializeAsync();
            if (!demo && !p1StopSmoke && !p1LifecycleTestSafeUi) await InitializeWebViewAsync();
            await gatewayInitialization;
            ApplySnapshot(controller.Snapshot);
            // Production: opening the workbench is the user's start gesture.
            if (!demo && !p1 && !snapshot.IsReady && (snapshot.Status == "stopped" || snapshot.Status == "offline"))
            {
                await RunOperationAsync("start");
                ApplySnapshot(controller.Snapshot);
            }
            if (!demo && !p1StopSmoke && !p1LifecycleTestSafeUi && snapshot.IsReady) await NavigateDashboardAsync(false, !hasEnteredGateway);
            rpcTimer.Interval = Math.Clamp(snapshot.RpcIntervalSeconds, 10, 86400) * 1000;
            rpcTimer.Start();
            if (p1LifecycleTestSafeUi) ShowP1LifecycleControls();
            if (p1StopSmoke) await StopAndExitAsync();
            else if (smokeTest) await FinishSmokeTestAsync(true);
            else if (p1Smoke) await FinishP1SmokeTestAsync(true);
        }
        catch
        {
            overallTitle.Text = "面板初始化未完成";
            overallDetail.Text = "本机状态仍可通过日志检查；本次没有自动启动或停止网关。";
            railState.Text = "初始化未完成";
            SelectPage("status");
            if (p1StopSmoke) await FinishP1StopSmokeTestAsync(false);
            else if (smokeTest) await FinishSmokeTestAsync(false);
            else if (p1Smoke) await FinishP1SmokeTestAsync(false);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        if (demo || web is null) return;
        try
        {
            Directory.CreateDirectory(shellDir);
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(shellDir, "webview-data"));
            if (p1Smoke)
            {
                var options = environment.CreateCoreWebView2ControllerOptions();
                options.ProfileName = "P1Smoke";
                options.IsInPrivateModeEnabled = true;
                await web.EnsureCoreWebView2Async(environment, options);
            }
            else await web.EnsureCoreWebView2Async(environment);
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            if (p1Smoke)
            {
                await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(P1EvidenceScript);
                web.CoreWebView2.WebMessageReceived += OnP1EvidenceMessage;
            }
            web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (e.Uri == "about:blank" || IsAllowedDashboardUrl(e.Uri)) return;
                e.Cancel = true;
                workbenchHint.Text = "工作台只载入当前配置的本机网关地址。";
            };
            web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (p1) { workbenchHint.Text = "P1 配对只在此独立工作台中使用；外部浏览器入口已关闭。"; return; }
                if (IsAllowedDashboardUrl(e.Uri)) OpenInBrowser();
                else if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && target.Scheme is "https" or "http") OpenUrl(target.AbsoluteUri);
            };
            web.CoreWebView2.NavigationCompleted += async (_, e) => await OnNavigationCompletedAsync(e.IsSuccess);
            web.CoreWebView2.ProcessFailed += (_, _) => { workbenchHint.Text = "工作台页面进程已断开；网关状态页仍可使用。点击重新连接可恢复页面。"; };
            web.NavigateToString(BootPage.Html);
            await UpdateBootPageAsync();
        }
        catch
        {
            workbenchHint.Text = "工作台浏览器初始化失败；请确认已安装 Microsoft Edge WebView2。网关控制与状态页仍可使用。";
            SelectPage("status");
        }
    }

    private void ControllerUpdated(GatewaySnapshot next)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => { if (!IsDisposed) ApplySnapshot(next); })); }
        catch (InvalidOperationException) { }
    }

    private void ApplySnapshot(GatewaySnapshot next)
    {
        snapshot = next;
        overallTitle.Text = next.Title;
        overallTitle.ForeColor = next.IsReady ? Accent : Ink;
        overallDetail.Text = next.Detail;
        endpointLabel.Text = demo ? "离线演示 · 未连接真实网关" : "本机入口   " + PlainEndpoint;
        liveValue.Text = next.Live;
        readyValue.Text = next.Ready;
        rpcValue.Text = next.Rpc;
        checkLabel.Text = "上次检查 " + next.LastChecked;
        latencyLabel.Text = (p1 ? "身份与健康核验 " : "健康响应 ") + next.Latency;
        startupLabel.Text = next.StartupPhase + Environment.NewLine + (string.IsNullOrWhiteSpace(next.BusyHint) ? next.Detail : next.BusyHint);
        settingsSummary.Text = next.SettingsSummary + Environment.NewLine + "共享控制根：" + next.ControlRoot;
        recoveryDescription.Text = next.RecoveryDescription;
        eventsText.Text = string.Join(Environment.NewLine, next.Events.Take(12));
        applyingSnapshot = true;
        recoveryCheck.Checked = next.AutoRecovery;
        applyingSnapshot = false;
        railState.Text = demo ? next.Status switch { "ready" => "演示已就绪", "starting" => "演示启动中", "stopped" => "演示已停止", _ => "演示状态待检查" } : next.Title;
        railState.ForeColor = next.IsReady ? Accent : Ink;
        stripState.Text = next.Title;
        stripEndpoint.Text = demo ? "离线演示 · 未连接真实网关" : (p1 ? "P1 隔离实例 · " : "生产 · ") + PlainEndpoint;
        if (trayState is not null) trayState.Text = next.Title;
        if (tray is not null) tray.Text = ("JFlash · " + next.Title)[..Math.Min(63, ("JFlash · " + next.Title).Length)];
        UpdateTrayIcon(next.TrayState);
        SetControlEnabled();
        WriteP1GuiSession();
        bool recovered = next.IsReady && !previouslyUsable;
        previouslyUsable = next.IsReady;
        if (!demo && next.IsReady && (!hasEnteredGateway || recovered) && web?.CoreWebView2 is not null)
            _ = NavigateDashboardAsync(false, !hasEnteredGateway);
        else if (onBootPage) _ = UpdateBootPageAsync();
        if (!next.IsReady && hasEnteredGateway)
            workbenchHint.Text = "暂时未收到网关健康响应。页面保留，左侧状态与控制仍然可用；恢复后会重新载入。";
        else if (!demo && next.IsReady)
            workbenchHint.Text = p1 ? "P1 可用 · 此工作台独立配对；不使用原浏览器身份。" : "网关可用 · 沿用已有配对；重新连接只刷新当前工作台。";
    }

    private string PlainEndpoint => p1 ? "http://127.0.0.1:18790/" : snapshot.Endpoint.TrimEnd('/') + "/";

    private bool IsAllowedDashboardUrl(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var url) || !Uri.TryCreate(PlainEndpoint, UriKind.Absolute, out var expected)) return false;
        return url.Scheme == expected.Scheme && url.Port == expected.Port && string.IsNullOrEmpty(url.UserInfo)
            && (url.Host == "127.0.0.1" || url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || url.Host is "[::1]" or "::1");
    }

    private async Task NavigateDashboardAsync(bool forcePair, bool initialEntry)
    {
        if (demo || navigationBusy || web?.CoreWebView2 is null || IsDisposed) return;
        navigationBusy = true;
        try
        {
            // A bootstrap URL is transient. Never store it in shell.json, a log, or an application field.
            string target = observer ? PlainEndpoint : (forcePair || (initialEntry && !paired)) ? await controller.GetDashboardUrlAsync() ?? PlainEndpoint : PlainEndpoint;
            if (!IsAllowedDashboardUrl(target)) target = PlainEndpoint;
            if (IsDisposed || web.CoreWebView2 is null) return;
            hasEnteredGateway = true;
            onBootPage = false;
            navigationVersion++;
            web.CoreWebView2.Navigate(target);
        }
        catch { workbenchHint.Text = "工作台页面暂时无法载入。可查看网关状态后再点击重新连接。"; }
        finally { navigationBusy = false; }
    }

    private async Task OnNavigationCompletedAsync(bool success)
    {
        if (demo || observer || onBootPage || !success || web?.CoreWebView2 is null || !IsAllowedDashboardUrl(web.Source?.AbsoluteUri ?? "")) return;
        int version = navigationVersion;
        try
        {
            await Task.Delay(2000);
            if (IsDisposed || version != navigationVersion || web?.CoreWebView2 is null) return;
            var authenticationFault = await HasKnownAuthenticationFaultAsync();
            if (authenticationFault)
            {
                paired = false;
                SaveSettings();
                if (pairAttempts < 2)
                {
                    pairAttempts++;
                    workbenchHint.Text = $"检测到令牌失效，正在重新配对（{pairAttempts}/2）。";
                    await NavigateDashboardAsync(true, false);
                }
                else workbenchHint.Text = "自动配对已尝试两次。请检查工作台认证提示，或手动重新连接。";
            }
            else if (!paired)
            {
                paired = true;
                SaveSettings();
            }
        }
        catch { /* A loading or disconnected page is not an authentication failure. */ }
    }

    private async Task<bool> HasKnownAuthenticationFaultAsync()
    {
        if (web?.CoreWebView2 is null) return false;
        const string script = "(()=>{let parts=[];function walk(root){if(!root)return;parts.push(root.innerText||Array.from(root.children||[]).map(e=>e.innerText||'').join(' '));for(const e of root.querySelectorAll('*'))if(e.shadowRoot)walk(e.shadowRoot);}walk(document.body);let t=parts.join(' ').slice(0,180000);return /AUTH_TOKEN_(?:MISSING|INVALID)|token[_ ]mismatch|token[_ ]missing|invalid (?:gateway )?token|粘贴令牌|需要令牌|Paste the token|Gateway secret/i.test(t);})()";
        var result = await web.CoreWebView2.ExecuteScriptAsync(script);
        return result == "true";
    }

    private async Task ReconnectWorkbenchAsync()
    {
        SelectPage("workbench");
        if (demo) return;
        if (!snapshot.IsReady)
        {
            workbenchHint.Text = p1 ? "P1 尚未可用，请到网关状态页查看隔离实例的启动进度。" : "网关尚未可用，可到网关状态页查看详情或启动网关。";
            return;
        }
        pairAttempts = 0;
        if (!observer) { paired = false; SaveSettings(); }
        await NavigateDashboardAsync(true, false);
    }

    private async Task UpdateBootPageAsync()
    {
        if (!onBootPage || web?.CoreWebView2 is null || demo) return;
        try
        {
            string state = JsonSerializer.Serialize(new { title = snapshot.Title, detail = snapshot.Detail, hint = snapshot.BusyHint });
            await web.CoreWebView2.ExecuteScriptAsync("window.updateGatewayState && window.updateGatewayState(" + state + ");");
        }
        catch { }
    }

    private async Task<bool> RunOperationAsync(string action)
    {
        bool accepted = snapshot.CanControl && !operationBusy;
        if (p1) RecordP1UiRequest(action, accepted);
        if (!accepted) return false;
        operationBusy = true;
        SetControlEnabled();
        try
        {
            if (action != "stop") SelectPage("status");
            bool requestSucceeded;
            if (controller is P1GatewayController p1Controller) requestSucceeded = await p1Controller.ExecuteWithResultAsync(action);
            else { await controller.ExecuteAsync(action); requestSucceeded = controller.LastOperationSucceeded; }
            ApplySnapshot(controller.Snapshot);
            bool succeeded = requestSucceeded && (!p1 || action != "stop" || controller.Snapshot.StopConfirmed);
            if (p1 && action == "stop" && !succeeded)
            {
                ShowPanel();
                SelectPage("status");
                workbenchHint.Text = "P1 停止未确认：请核对原进程、端口与 stopped 意图，工作台保持打开。";
            }
            return succeeded;
        }
        catch { workbenchHint.Text = "网关操作没有完成，请查看运行日志。"; return false; }
        finally { operationBusy = false; SetControlEnabled(); }
    }

    private void SetControlEnabled()
    {
        bool enabled = snapshot.CanControl && !operationBusy;
        startButton.Enabled = enabled && !snapshot.Busy;
        restartButton.Enabled = enabled && !snapshot.Busy;
        stopButton.Enabled = enabled;
        recoveryCheck.Enabled = snapshot.CanControl && !operationBusy && !snapshot.Busy;
        if (trayStart is not null) trayStart.Enabled = startButton.Enabled;
        if (trayRestart is not null) trayRestart.Enabled = restartButton.Enabled;
        if (trayStop is not null) trayStop.Enabled = stopButton.Enabled;
        if (trayStopExit is not null) trayStopExit.Enabled = stopButton.Enabled;
    }

    private async Task RefreshStatusAsync(bool detailed)
    {
        if (refreshBusy || IsDisposed) return;
        refreshBusy = true;
        try { await controller.RefreshAsync(detailed); }
        catch { }
        finally { refreshBusy = false; }
    }

    private async Task RefreshLogsAsync()
    {
        try { logsText.Text = (await controller.ReadLogsAsync()).Replace("ClawDesk", "JFlash 工作台"); }
        catch { logsText.Text = "暂时无法读取日志，请稍后刷新。"; }
    }

    private async Task ExportDiagnosticsAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择诊断文件的保存目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            string path = await controller.ExportDiagnosticsAsync(dialog.SelectedPath);
            await RefreshLogsAsync();
            MessageBox.Show(this, "诊断已保存：\n" + path, "JFlash 工作台", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch { MessageBox.Show(this, "诊断导出未完成，请确认目录可写。", "JFlash 工作台", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip { Font = UiFont((float)(11 * uiScale)) };
        menu.Items.Add("打开工作台", null, (_, _) => { SelectPage("workbench"); ShowPanel(); });
        menu.Items.Add("查看网关状态", null, (_, _) => { SelectPage("status"); ShowPanel(); });
        menu.Items.Add("在浏览器中打开", null, (_, _) => OpenInBrowser()).Enabled = !demo && !p1;
        menu.Items.Add("重新连接工作台", null, async (_, _) => { ShowPanel(); await ReconnectWorkbenchAsync(); }).Enabled = !demo;
        menu.Items.Add(new ToolStripSeparator());
        trayState = new ToolStripMenuItem("网关状态：检测中…") { Enabled = false };
        menu.Items.Add(trayState);
        trayStart = new ToolStripMenuItem(demo ? "启动演示网关" : "启动网关", null, async (_, _) => await RunOperationAsync("start"));
        trayStop = new ToolStripMenuItem(demo ? "停止演示网关" : "停止网关", null, async (_, _) => await RunOperationAsync("stop"));
        trayRestart = new ToolStripMenuItem(demo ? "重启演示网关" : "重启网关", null, async (_, _) => await RunOperationAsync("restart"));
        menu.Items.AddRange(new ToolStripItem[] { trayStart, trayStop, trayRestart });
        menu.Items.Add("打开数据目录", null, (_, _) => OpenDataDirectory());
        menu.Items.Add("导出诊断", null, async (_, _) => { ShowPanel(); await ExportDiagnosticsAsync(); });
        menu.Items.Add(new ToolStripSeparator());
        closeModeItem = new ToolStripMenuItem { Enabled = true };
        closeModeItem.Click += (_, _) => { closeStopsGateway = !closeStopsGateway; SaveSettings(); RefreshCloseLabel(); };
        menu.Items.Add(closeModeItem);
        trayStopExit = new ToolStripMenuItem(demo ? "停止演示并退出" : "停止网关并退出", null, async (_, _) => await StopAndExitAsync());
        menu.Items.Add(trayStopExit);
        menu.Items.Add(demo ? "退出演示" : p1 ? "退出工作台（P1 继续运行）" : "退出工作台（网关继续运行）", null, (_, _) => ExitApp());
        trayStatusIcon = TrayIconFactory.MakeStatus(32, "stopped");
        tray = new NotifyIcon { Icon = trayStatusIcon, Text = "JFlash 工作台", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowPanel();
        RefreshCloseLabel();
    }

    private void RefreshCloseLabel()
    {
        if (closeModeItem is not null) closeModeItem.Text = closeStopsGateway ? "关窗行为：停止网关并退出" : "关窗行为：收进托盘（网关继续）";
    }

    private void UpdateTrayIcon(string state)
    {
        if (tray is null || state == trayIconState) return;
        Icon replacement = TrayIconFactory.MakeStatus(32, state);
        Icon? previous = trayStatusIcon;
        tray.Icon = replacement;
        trayStatusIcon = replacement;
        trayIconState = state;
        previous?.Dispose();
    }

    private void OpenDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(appDataDir);
            Process.Start(new ProcessStartInfo { FileName = appDataDir, UseShellExecute = true });
        }
        catch { workbenchHint.Text = "数据目录暂时无法打开。"; }
    }

    private async Task StopAndExitAsync()
    {
        if (stoppingForExit) return;
        stoppingForExit = true;
        if (!p1StopSmoke) ShowPanel();
        SelectPage("status");
        try
        {
            bool accepted = await RunOperationAsync("stop");
            if (accepted && p1)
            {
                // A successful command is not permission to exit after a later conflicting start.
                accepted = controller is P1GatewayController p1Controller && await p1Controller.VerifyStopBeforeExitAsync();
                ApplySnapshot(controller.Snapshot);
                accepted = accepted && controller.Snapshot.StopConfirmed;
            }
            else if (accepted)
            {
                for (int i = 0; i < 4 && controller.Snapshot.Live == "在线"; i++)
                {
                    await Task.Delay(1000);
                    await controller.RefreshAsync();
                }
                accepted = controller.Snapshot.Live != "在线";
            }
            if (p1StopSmoke) await FinishP1StopSmokeTestAsync(accepted);
            else if (accepted) ExitApp();
            else MessageBox.Show(this, "尚未确认网关已停止，工作台保持打开。请查看日志后重试，或从托盘选择“退出（网关继续运行）”。", "停止未确认", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { stoppingForExit = false; }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowExit || smokeTest || p1Smoke || p1StopSmoke || e.CloseReason != CloseReason.UserClosing) return;
        e.Cancel = true;
        if (closeStopsGateway) _ = StopAndExitAsync();
        else
        {
            Hide();
            if (tray is not null)
            {
                tray.BalloonTipTitle = "JFlash 仍在后台运行";
                tray.BalloonTipText = demo ? "离线演示保留在后台，双击托盘图标可返回。" : p1 ? "P1 隔离实例控制面板保留在后台，双击托盘图标可返回。" : "工作台继续运行，双击托盘图标可返回。";
                tray.ShowBalloonTip(2500);
            }
        }
    }

    private void ShowPanel() { showRequested = true; ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate(); BringToFront(); }
    private async void OpenInBrowser()
    {
        if (demo) return;
        try
        {
            string target = observer ? PlainEndpoint : await controller.GetDashboardUrlAsync() ?? PlainEndpoint;
            OpenUrl(IsAllowedDashboardUrl(target) ? target : PlainEndpoint);
        }
        catch { workbenchHint.Text = "浏览器入口暂时无法打开，请稍后重试。"; }
    }
    private static void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
    private void ExitApp() { allowExit = true; if (tray is not null) tray.Visible = false; Close(); }

    private void LoadSettings()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsFile));
            if (document.RootElement.TryGetProperty("closeMode", out var close) && close.ValueKind == JsonValueKind.String) closeStopsGateway = close.GetString() == "stop";
            if (document.RootElement.TryGetProperty("paired", out var pair) && pair.ValueKind is JsonValueKind.True or JsonValueKind.False) paired = pair.GetBoolean();
        }
        catch { }
    }

    private void SaveSettings()
    {
        if (observer) return;
        try
        {
            // Preserve other settings added by an older or future shell version.
            var values = File.Exists(settingsFile) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsFile)) as System.Text.Json.Nodes.JsonObject : null;
            values ??= new System.Text.Json.Nodes.JsonObject();
            values["closeMode"] = closeStopsGateway ? "stop" : "tray";
            values["paired"] = paired;
            Directory.CreateDirectory(shellDir);
            File.WriteAllText(settingsFile, values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch { }
    }

    private async Task FinishSmokeTestAsync(bool success)
    {
        string? action = ArgumentValue(Environment.GetCommandLineArgs(), "--smoke-action");
        if (demo && success && action is "start" or "stop" or "restart") success = await RunOperationAsync(action);
        SelectPage("status");
        await Task.Delay(350);
        statusNav.Focus();
        statusPanel.AutoScrollPosition = Point.Empty;
        statusPanel.PerformLayout();
        try
        {
            using var bitmap = new Bitmap(Width, Height);
            DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
            bitmap.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(screenshotPath)!, "smoke-result" + (uiScale == 1 ? "" : "-" + uiScale.ToString("0.0", CultureInfo.InvariantCulture)) + ".txt"), $"initialized={success}\nmode=demo\nwebview=not-opened\nproduction-profile=not-read\nreal-gateway=not-contacted\ngateway-controls=demo-only\nui-scale={uiScale.ToString(CultureInfo.InvariantCulture)}\nfont-unit=point\ntray-startup={(startInTray ? "hidden" : "window")}\nstatus={snapshot.Status}\nlive={snapshot.Live}\nready={snapshot.Ready}\nrpc={snapshot.Rpc}\n", Encoding.UTF8);
        }
        catch { success = false; }
        Environment.ExitCode = success ? 0 : 1;
        ExitApp();
    }

    private void OnP1EvidenceMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!p1Smoke || !IsAllowedDashboardUrl(e.Source)) return;
        try
        {
            if (e.WebMessageAsJson.Length > 2048) return;
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var item = message.RootElement;
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "p1-pair-evidence") return;
            bool Flag(string name) => item.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.True;
            p1ConnectSeen |= Flag("connectSeen");
            p1DeviceIdentity |= Flag("deviceIdentity");
            p1HelloOk |= Flag("helloOk");
            p1DeviceTokenExists |= Flag("deviceTokenExists");
            p1StoredDeviceMatches |= Flag("storedDeviceMatches");
            p1OperatorTokenStored |= Flag("operatorTokenStored");
            if (p1ConnectSeen && p1DeviceIdentity && p1HelloOk && p1DeviceTokenExists && p1StoredDeviceMatches && p1OperatorTokenStored)
                p1PairEvidence.TrySetResult(true);
        }
        catch { }
    }

    private async Task FinishP1SmokeTestAsync(bool initialized)
    {
        bool success = false;
        string resultCode = initialized ? "pair-timeout" : "initialization-failed";
        if (initialized)
        {
            try { success = await p1PairEvidence.Task.WaitAsync(TimeSpan.FromSeconds(150)); resultCode = success ? "paired" : "pair-failed"; }
            catch (TimeoutException) { }
            catch { resultCode = "pair-evidence-failed"; }
        }
        bool controlUiCaptured = false;
        try
        {
            string directory = Path.GetDirectoryName(screenshotPath)!;
            Directory.CreateDirectory(directory);
            await controller.RefreshAsync(true);
            ApplySnapshot(controller.Snapshot);
            SelectPage(success ? "workbench" : "status");
            await Task.Delay(300);
            using var bitmap = new Bitmap(Width, Height);
            CaptureOwnWindow(bitmap);
            if (success && web?.CoreWebView2 is not null)
            {
                using var preview = new MemoryStream();
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
                preview.Position = 0;
                using var page = Image.FromStream(preview);
                var webPosition = web.PointToScreen(Point.Empty);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.DrawImage(page, new Rectangle(webPosition.X - Left, webPosition.Y - Top, web.ClientSize.Width, web.ClientSize.Height));
                await File.WriteAllBytesAsync(Path.Combine(directory, "p1-control-ui.png"), preview.ToArray());
                controlUiCaptured = true;
            }
            bitmap.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
            if (success && !controlUiCaptured) { success = false; resultCode = "webview-capture-failed"; }
            if (controlUiCaptured) await WriteP1UiMeasurementAsync(bitmap, directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "p1-smoke-result.json"), JsonSerializer.Serialize(new
            {
                generatedAt = DateTimeOffset.Now, initialized, mode = "p1", port = 18790, profile = "jf-shell-p1",
                inPrivate = true, productionProfileRead = false, productionGatewayContacted = false,
                connectSeen = p1ConnectSeen, signedDeviceIdentity = p1DeviceIdentity, helloOk = p1HelloOk,
                deviceTokenExists = p1DeviceTokenExists, storedDeviceMatches = p1StoredDeviceMatches,
                operatorTokenStored = p1OperatorTokenStored, credentialsExported = false,
                status = snapshot.Status, authenticatedRpc = snapshot.Rpc == "认证 RPC 通过",
                controlUiCaptured, success, resultCode, gatewayLeftRunning = true
            }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch { success = false; }
        Environment.ExitCode = success ? 0 : 1;
        ExitApp();
    }

    private async Task FinishP1StopSmokeTestAsync(bool acceptedByStopPath)
    {
        bool confirmed = acceptedByStopPath && controller.LastOperationSucceeded && controller.Snapshot.StopConfirmed;
        bool reportWritten = false;
        try
        {
            ApplySnapshot(controller.Snapshot);
            SelectPage("status");
            await Task.Delay(250);
            string directory = Path.GetDirectoryName(screenshotPath)!;
            Directory.CreateDirectory(directory);
            using var bitmap = new Bitmap(Width, Height);
            DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
            bitmap.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
            await File.WriteAllTextAsync(Path.Combine(directory, "p1-stop-smoke-result.json"), JsonSerializer.Serialize(new
            {
                generatedAt = DateTimeOffset.Now, mode = "p1", port = 18790, profile = "jf-shell-p1",
                desired = confirmed ? "stopped" : "unconfirmed", stopConfirmed = confirmed,
                pidReleased = confirmed ? true : (bool?)null, portReleased = confirmed ? true : (bool?)null,
                exitAllowedByStopPath = confirmed, success = confirmed, webviewOpened = false,
                productionProfileRead = false, productionGatewayContacted = false,
                resultCode = confirmed ? "clean-stop-confirmed-before-exit" : "stop-not-confirmed-test-exits-with-failure"
            }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            reportWritten = true;
        }
        catch { }
        // Test-run cleanup exits with failure when unconfirmed; normal StopAndExit still
        // keeps the user's panel open and never takes this test-only cleanup branch.
        Environment.ExitCode = confirmed && reportWritten ? 0 : 1;
        ExitApp();
    }

    // Injected only into the disposable P1 smoke WebView. No frame, URL, device ID,
    // private key, bootstrap token or device token crosses the WebView boundary.
    private const string P1EvidenceScript = """
        (() => {
          if (location.protocol !== 'http:' || location.hostname !== '127.0.0.1' || location.port !== '18790') return;
          const evidence = { type: 'p1-pair-evidence', connectSeen: false, deviceIdentity: false, helloOk: false,
            deviceTokenExists: false, storedDeviceMatches: false, operatorTokenStored: false };
          const emit = () => {
            try {
              const identity = JSON.parse(localStorage.getItem('openclaw-device-identity-v1') || 'null');
              if (identity && typeof identity.deviceId === 'string') {
                for (let i = 0; i < localStorage.length; i++) {
                  const key = localStorage.key(i);
                  if (!key || !key.startsWith('openclaw.device.auth.v1:')) continue;
                  const entry = JSON.parse(localStorage.getItem(key) || 'null');
                  if (entry && entry.deviceId === identity.deviceId) {
                    evidence.storedDeviceMatches = true;
                    evidence.operatorTokenStored = typeof entry.tokens?.operator?.token === 'string' && entry.tokens.operator.token.length > 0;
                  }
                }
              }
            } catch (_) {}
            window.chrome?.webview?.postMessage({ ...evidence });
          };
          const Native = window.WebSocket;
          const pending = new Set();
          window.WebSocket = class extends Native {
            constructor(...args) {
              super(...args);
              let isP1 = false;
              try { const u = new URL(String(args[0]), location.href); isP1 = u.protocol === 'ws:' && u.hostname === '127.0.0.1' && u.port === '18790'; } catch (_) {}
              if (isP1) this.addEventListener('message', event => {
                try {
                  const frame = JSON.parse(event.data);
                  if (frame.type === 'res' && pending.has(frame.id) && frame.ok === true && frame.payload?.type === 'hello-ok') {
                    evidence.helloOk = true;
                    evidence.deviceTokenExists = typeof frame.payload.auth?.deviceToken === 'string' && frame.payload.auth.deviceToken.length > 0;
                    emit(); setTimeout(emit, 250); setTimeout(emit, 1000);
                  }
                } catch (_) {}
              });
              this.__jfP1 = isP1;
            }
            send(data) {
              if (this.__jfP1) {
                try {
                  const frame = JSON.parse(data);
                  if (frame.type === 'req' && frame.method === 'connect') {
                    pending.add(frame.id); evidence.connectSeen = true;
                    evidence.deviceIdentity = typeof frame.params?.device?.id === 'string' && frame.params.device.id.length > 0
                      && typeof frame.params.device.signature === 'string' && frame.params.device.signature.length > 0;
                    emit();
                  }
                } catch (_) {}
              }
              return super.send(data);
            }
          };
        })();
        """;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposedManaged)
        {
            disposedManaged = true;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            rpcTimer.Stop();
            rpcTimer.Dispose();
            controller.Updated -= ControllerUpdated;
            controller.Dispose();
            if (tray is not null) { tray.Visible = false; tray.Dispose(); }
            trayStatusIcon?.Dispose();
            Icon?.Dispose();
        }
        base.Dispose(disposing);
    }

}
