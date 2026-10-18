using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawDesk;

/// <summary>Explicit P1-only bridge; no production settings, direct HTTP, CLI or scheduled task access.</summary>
public sealed class P1GatewayController : IGatewayController
{
    private readonly string _appDataDir;
    private readonly IP1BridgeClient _bridge;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly object _sync = new();
    private readonly Queue<string> _events = new();
    private GatewaySnapshot _snapshot = Initial();
    private bool _initialized, _disposed, _commandRunning;
    private bool _stopFailureLatched;
    private long _stateGeneration, _lastRevision, _lastDesiredRevision;

    public P1GatewayController(string appDataDir, IP1BridgeClient? bridge = null)
    {
        _appDataDir = Path.GetFullPath(appDataDir);
        _bridge = bridge ?? new P1BridgeClient();
    }

    public GatewaySnapshot Snapshot { get { lock (_sync) return _snapshot with { }; } }
    public bool LastOperationSucceeded { get; private set; }
    public P1BridgeResult? LastVerifiedResult { get; private set; }
    public event Action<GatewaySnapshot>? Updated;

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        AddEvent("P1 独立入口：只控制 18790；打开工作台不会自动启动。");
        await RefreshAsync(true);
        _ = PollAsync();
    }

    public async Task RefreshAsync(bool includeRpc = false)
    {
        if (_disposed) return;
        bool entered = false;
        long generation = Interlocked.Read(ref _stateGeneration);
        try
        {
            entered = await _refresh.WaitAsync(0, _lifetime.Token);
            if (!entered || _commandRunning) return;
            string action = includeRpc ? "rpc" : "status";
            var reply = await _bridge.CallAsync(action, _lifetime.Token);
            if (generation != Interlocked.Read(ref _stateGeneration)) return;
            if (!TryValidate(reply, action, out var result)) { PublishUnavailable("P1 身份或状态核验未通过；保持暂停。"); return; }
            PublishResult(result!, generation);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { if (generation == Interlocked.Read(ref _stateGeneration)) PublishUnavailable("P1 控制桥暂不可用；未发起任何生产请求。"); }
        finally { if (entered) _refresh.Release(); }
    }

    public async Task ExecuteAsync(string action) => _ = await ExecuteWithResultAsync(action);

    public async Task<bool> ExecuteWithResultAsync(string action)
    {
        if (_disposed || action is not ("start" or "stop" or "restart" or "recover-on" or "recover-off"))
        { LastOperationSucceeded = false; return false; }
        if (!await _operation.WaitAsync(0, _lifetime.Token))
        { LastOperationSucceeded = false; return false; }
        bool requestSucceeded = false;
        LastOperationSucceeded = false;
        long generation = Interlocked.Increment(ref _stateGeneration);
        _commandRunning = true;
        if (action == "stop") _stopFailureLatched = true;
        else if (action is "start" or "restart") _stopFailureLatched = false;
        Publish(action == "stop"
            ? Snapshot with { Status = "stopping", Title = "P1 正在停止 · 等待三项确认", Live = "停止核验中",
                Detail = "等待 stopped 落盘、原 PID 与创建时间绑定的进程退出，以及 OS 监听释放。",
                Busy = true, BusyHint = "停止尚未确认，工作台保持打开。", StopConfirmed = false, TrayState = "yellow" }
            : Snapshot with { Busy = true, BusyHint = "正在处理 P1 隔离实例操作…", StopConfirmed = false });
        try
        {
            var reply = await _bridge.CallAsync(action, _lifetime.Token);
            if (!TryValidate(reply, action, out var result)) { PublishUnavailable("P1 操作未确认；保持面板打开，禁止假报成功。"); return false; }
            _commandRunning = false;
            // Decide from this request's evidence, never from a concurrently refreshed snapshot.
            bool succeeded = action == "stop" ? result!.HasConfirmedStopEvidence() : reply.Ok;
            if (action == "stop") _stopFailureLatched = !succeeded;
            requestSucceeded = PublishResult(result!, generation) && succeeded;
            LastOperationSucceeded = requestSucceeded;
            if (!LastOperationSucceeded && action == "stop")
                PublishUnavailable("P1 停止未确认：原进程退出、端口释放及 stopped 落盘证据不完整，工作台保持打开。");
            AddEvent(LastOperationSucceeded ? "P1 " + action + " 已确认。" : "P1 操作尚未确认，保留现场供对账。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { PublishUnavailable("P1 操作结果未知；请先对账，不自动重放。"); }
        finally
        {
            _commandRunning = false;
            _operation.Release();
            Publish(Snapshot with { Busy = false, BusyHint = "" });
        }
        return requestSucceeded;
    }

    public async Task<bool> VerifyStopBeforeExitAsync()
    {
        var expected = LastVerifiedResult;
        if (_disposed || !LastOperationSucceeded || expected?.HasConfirmedStopEvidence() != true) return false;
        long generation = Interlocked.Read(ref _stateGeneration);
        bool entered = false;
        try
        {
            // Wait for any poll already in progress, then issue a new request of our own.
            entered = await _refresh.WaitAsync(TimeSpan.FromSeconds(45), _lifetime.Token);
            if (!entered || _commandRunning || generation != Interlocked.Read(ref _stateGeneration)) return false;
            var reply = await _bridge.CallAsync("status", _lifetime.Token);
            if (!TryValidate(reply, "status", out var result) || result?.HasConfirmedStopEvidence() != true
                || result.DesiredRevision != expected.DesiredRevision || result.Pid != expected.Pid || result.RunId != expected.RunId
                || !DateTimeOffset.TryParse(result.PidStartTime, out var currentCreated)
                || !DateTimeOffset.TryParse(expected.PidStartTime, out var expectedCreated) || currentCreated != expectedCreated
                || generation != Interlocked.Read(ref _stateGeneration))
            { PublishUnavailable("退出前停止复验未通过，工作台保持打开。"); return false; }
            return PublishResult(result, generation) && Snapshot.StopConfirmed;
        }
        catch { PublishUnavailable("退出前停止复验失败，工作台保持打开。"); return false; }
        finally { if (entered) _refresh.Release(); }
    }

    public void SetAutoRecovery(bool enabled)
    {
        _ = ExecuteAsync(enabled ? "recover-on" : "recover-off");
    }

    public Task NotifyResumeAsync() => RefreshAsync(true);

    public async Task<string?> GetDashboardUrlAsync()
    {
        if (_disposed) return null;
        try
        {
            var reply = await _bridge.CallAsync("pair", _lifetime.Token);
            if (!TryValidate(reply, "pair", out var result) || !IsAllowedPairUrl(result?.DashboardUrl)) return null;
            // Transient URL is returned directly to the webview, never stored in snapshots or logs.
            return result!.DashboardUrl;
        }
        catch { AddEvent("P1 工作台配对链接未取得；未退回生产入口。"); return null; }
    }

    public static bool IsAllowedPairUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != "http" || url.Host != "127.0.0.1"
            || url.Port != 18790 || url.AbsolutePath != "/" || url.Query.Length != 0 || url.UserInfo.Length != 0) return false;
        try
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in url.Fragment.TrimStart('#').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0 || !fields.TryAdd(Uri.UnescapeDataString(part[..equals]), Uri.UnescapeDataString(part[(equals + 1)..]))) return false;
            }
            if (!fields.TryGetValue("bootstrapToken", out var token) || token.Length < 8 || token.Length > 8192
                || fields.Keys.Any(k => k is not ("bootstrapToken" or "bootstrapProfile" or "gatewayUrl"))) return false;
            if (fields.TryGetValue("bootstrapProfile", out var profile) && profile != "owner") return false;
            return !fields.TryGetValue("gatewayUrl", out var gateway)
                || Uri.TryCreate(gateway, UriKind.Absolute, out var socket) && socket.Scheme == "ws" && socket.Host == "127.0.0.1"
                    && socket.Port == 18790 && socket.AbsolutePath == "/" && socket.UserInfo.Length == 0 && socket.Query.Length == 0 && socket.Fragment.Length == 0;
        }
        catch { return false; }
    }

    public Task<string> ReadLogsAsync() => Task.FromResult("P1 工作台本地记录（不读取配置、凭据、聊天或生产日志）\n" + string.Join(Environment.NewLine, EventSnapshot().Reverse()));

    public async Task<string> ExportDiagnosticsAsync(string targetDir)
    {
        string path = Path.Combine(Path.GetFullPath(targetDir), "JFlash-P1-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
        var s = Snapshot;
        string safe = JsonSerializer.Serialize(new
        {
            mode = "p1", port = 18790, profile = P1Identity.Expected.Profile, status = s.Status,
            desired = s.StopConfirmed ? "stopped" : "unconfirmed", stopConfirmed = s.StopConfirmed,
            rpc = s.Rpc, autoRecover = s.AutoRecovery, budgetUsed = LastVerifiedResult?.BudgetUsed,
            nextRetryAt = LastVerifiedResult?.NextRetryAt, production = "not-accessed"
        });
        await File.WriteAllTextAsync(path, safe + "\n" + await ReadLogsAsync(), new UTF8Encoding(false));
        return path;
    }

    private static bool TryValidate(P1BridgeReply reply, string action, out P1BridgeResult? result)
    {
        result = reply.Result;
        return reply.V == 1 && reply.Action == action && reply.Identity?.IsExpected == true && reply.Ok && result is not null && result.IdentityVerified
            && result.DesiredRevision > 0 && result.Revision >= result.DesiredRevision
            && result.StartupGraceSeconds is >= 180 and <= 600 && result.HealthIntervalSeconds == 30
            && P1Identity.IsExpectedPath(result.ControlRoot, "control-v1")
            && result.Desired is "running" or "stopped";
    }

    private bool PublishResult(P1BridgeResult r, long generation)
    {
        lock (_sync)
        {
        if (generation != _stateGeneration || r.Revision < _lastRevision || r.DesiredRevision < _lastDesiredRevision) return false;
        _lastRevision = r.Revision; _lastDesiredRevision = r.DesiredRevision;
        LastVerifiedResult = r;
        bool stopped = !_stopFailureLatched && r.HasConfirmedStopEvidence();
        string status = stopped ? "stopped" : r.Live && r.Ready ? "ready" : r.Live ? "degraded" : r.Busy ? "starting" : "offline";
        string title = _stopFailureLatched ? "P1 停止未确认 · 请核对后重试" : status switch { "stopped" => "P1 已确认停止", "ready" => "P1 网关已就绪", "degraded" => "P1 在线 · 就绪待确认", "starting" => "P1 网关正在启动", _ => "P1 状态待确认" };
        Publish(Initial() with
        {
            Status = status, Title = title,
            Detail = _stopFailureLatched ? "上一停止请求未确认，界面保留；重新核对本次停止证据后才允许退出。" : stopped ? "stopped 已原子落盘，原 PID 与创建时间绑定的进程已退出，OS 确认无监听。" : $"P1 隔离实例；启动保护 {r.StartupGraceSeconds} 秒，生产阈值另行验证。",
            Live = stopped ? "已停止" : r.Live ? "在线" : "无有效响应", Ready = r.Ready ? "已就绪" : "未就绪",
            Rpc = r.RpcOk ? "认证 RPC 通过" : "未通过认证 RPC", IsReady = r.Live && (r.Ready || r.Started),
            Busy = r.Busy || _commandRunning, CanControl = r.CanControl, StopConfirmed = stopped,
            Latency = r.ProbeMilliseconds is >= 0 and < 100000 ? $"{r.ProbeMilliseconds:0} ms" : "—",
            StartupPhase = stopped ? "P1 已干净停止" : r.Started ? "P1 初始化完成" : $"P1 启动/诊断阶段（{r.StartupGraceSeconds} 秒保护）",
            AutoRecovery = r.AutoRecover,
            RecoveryDescription = $"P1 自动恢复{(r.AutoRecover ? "已开启" : "已关闭")} · 已用预算 {r.BudgetUsed}/3" +
                (r.NextRetryAt is { } retry ? $" · 下次不早于 {retry.ToLocalTime():HH:mm:ss}" : "") +
                (string.IsNullOrWhiteSpace(r.PausedReason) ? "" : " · 已暂停：" + r.PausedReason),
            SettingsSummary = $"p1 · 18790 · 独立 profile · 启动保护 {r.StartupGraceSeconds}s · 当前 SID 守卫",
            BusyHint = r.Busy || _commandRunning ? "P1 操作进行中；禁止重复启动。" : "",
            LastChecked = DateTime.Now.ToString("HH:mm:ss"), Events = EventSnapshot(),
            TrayState = status == "ready" ? "green" : status is "degraded" or "starting" ? "yellow" : stopped ? "gray" : "red"
        });
        return true;
        }
    }

    private void PublishUnavailable(string reason)
    {
        LastVerifiedResult = null;
        AddEvent(reason);
        Publish(Initial() with { Status = "paused", Title = _stopFailureLatched ? "P1 停止未确认" : "P1 暂停控制", Detail = reason, CanControl = false, StopConfirmed = false, LastChecked = DateTime.Now.ToString("HH:mm:ss"), Events = EventSnapshot() });
    }

    private static GatewaySnapshot Initial() => new()
    {
        Mode = "p1", Endpoint = "http://127.0.0.1:18790", Title = "正在核验 P1 隔离实例",
        Detail = "P1 只允许固定隔离身份；不会读取生产设置或运行生产任务。",
        ControlRoot = Path.Combine(P1Identity.Expected.Root, "control-v1"), AutoRecovery = false,
        RecoveryDescription = "P1 自动恢复状态待权威快照确认，不套用生产阈值。",
        SettingsSummary = "p1 · 端口 18790 · profile jf-shell-p1 · 独立 Control UI 配对 · 启动保护180s起",
        HealthIntervalSeconds = 30, RpcIntervalSeconds = 120, CanControl = false
    };

    private async Task PollAsync()
    {
        try
        {
            int cycles = 0;
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _lifetime.Token);
                await RefreshAsync(++cycles % 4 == 0);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void AddEvent(string text) { lock (_sync) { _events.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + text); while (_events.Count > 50) _events.Dequeue(); } }
    private string[] EventSnapshot() { lock (_sync) return _events.Reverse().ToArray(); }
    private void Publish(GatewaySnapshot next) { lock (_sync) _snapshot = next; if (!_disposed) Updated?.Invoke(next with { }); }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); }
}
