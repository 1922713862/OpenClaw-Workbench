using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawDesk;

/// <summary>Production controller for JF-1: delegates lifecycle to jfctl.cjs (single authority).</summary>
public sealed class JfctlGatewayController : IGatewayController
{
    private const int Port = 18789;
    private readonly string _appDataDir;
    private readonly string _shellDir;
    private readonly string _node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
    private readonly string _jfctl;
    private readonly string _realCli = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "openclaw.cmd");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly Queue<string> _events = new();
    private readonly object _sync = new();
    private GatewaySnapshot _snapshot = new();
    private bool _disposed;
    private bool _busy;

    public JfctlGatewayController(string appDataDir)
    {
        _appDataDir = appDataDir;
        _shellDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "jf-shell");
        _jfctl = Path.Combine(_shellDir, "bin", "jfctl.cjs");
        Directory.CreateDirectory(_appDataDir);
    }

    public GatewaySnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    public bool LastOperationSucceeded { get; private set; } = true;
    public event Action<GatewaySnapshot>? Updated;

    public async Task InitializeAsync() { await RefreshAsync(true); }

    public async Task RefreshAsync(bool includeRpc = false)
    {
        if (_disposed) return;
        GatewaySnapshot snap = new()
        {
            Endpoint = $"http://127.0.0.1:{Port}",
            Mode = "owner",
            ControlRoot = Path.Combine(_shellDir, "control"),
            UiScale = 1,
            HealthIntervalSeconds = 30,
            RpcIntervalSeconds = 120
        };
        var sw = Stopwatch.StartNew();
        bool live = await ProbeAsync("/healthz");
        bool ready = await ProbeAsync("/readyz");
        bool startup = await ProbeAsync("/startupz");
        sw.Stop();

        JsonElement st = default;
        bool hasState = false;
        try
        {
            var raw = await RunNodeAsync("status --json", 20000);
            using var doc = JsonDocument.Parse(raw);
            st = doc.RootElement.Clone();
            hasState = true;
        }
        catch { }

        string desired = hasState && st.TryGetProperty("desired", out var d) ? (d.GetString() ?? "unknown") : "unknown";
        string phase = hasState && st.TryGetProperty("phase", out var ph) ? (ph.GetString() ?? "unknown") : "unknown";
        bool paused = hasState && st.TryGetProperty("pausedReason", out var pr) && pr.ValueKind == JsonValueKind.String;
        int attempts = hasState && st.TryGetProperty("attempts", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetInt32() : 0;
        int procs = hasState && st.TryGetProperty("gatewayProcesses", out var gp) && gp.ValueKind == JsonValueKind.Array ? gp.GetArrayLength() : 0;
        bool portOpen = hasState && st.TryGetProperty("portListening", out var pl) && pl.ValueKind == JsonValueKind.True;

        string status;
        string title;
        string detail;
        if (ready) { status = "ready"; title = "网关已就绪"; detail = "Control UI 可用，可以开始工作。"; }
        else if (procs > 0 || portOpen) { status = "starting"; title = "网关正在启动"; detail = "冷启动通常 4–7 分钟；已检测到进程，请勿重复启动。"; }
        else if (desired == "stopped") { status = "stopped"; title = "网关已停止"; detail = "按你的停止意图保持停止；点“启动网关”才会恢复。"; }
        else { status = "offline"; title = "暂时连接不到网关"; detail = "没有进程、没有端口；点“启动网关”或等待守卫恢复。"; }
        if (paused) { status = "degraded"; title = "自动恢复已暂停"; detail = "连续 3 次恢复失败，已停止自动尝试，请查看日志。"; }

        snap.Status = status;
        snap.Title = title;
        snap.Detail = detail;
        snap.Live = live ? "在线" : "离线";
        snap.Ready = ready ? "就绪" : live ? "在线未就绪" : "未就绪";
        snap.Rpc = ready ? "健康探针通过" : "未通过";
        snap.Latency = $"{sw.ElapsedMilliseconds} ms";
        snap.LastChecked = DateTime.Now.ToString("HH:mm:ss");
        snap.IsReady = ready;
        snap.Busy = _busy || (status == "starting");
        snap.BusyHint = _busy ? "正在执行操作…" : status == "starting" ? "启动等待中；禁止重复启动。" : "";
        snap.AutoRecovery = true;
        snap.RecoveryDescription = paused ? $"自动恢复已暂停（已尝试 {attempts}/3 次）。" : "自动恢复开启：只启动、不杀进程，预算 1/5/15 分钟。";
        snap.SettingsSummary = $"端口 {Port} · profile default · 控制面 jfctl.cjs";
        snap.CanControl = true;
        snap.StopConfirmed = status == "stopped";
        snap.StartupPhase = phase;
        snap.TrayState = status == "ready" ? "green" : status is "starting" or "degraded" ? "yellow" : status == "stopped" ? "gray" : "red";
        snap.Events = TakeEvents();

        lock (_sync) _snapshot = snap;
        Updated?.Invoke(snap);
    }

    public async Task ExecuteAsync(string action)
    {
        if (_disposed) return;
        _busy = true;
        AddEvent($"执行 {action} …");
        try
        {
            string arg = action switch
            {
                "start" => "start",
                "stop" => "stop",
                "restart" => "restart",
                _ => action
            };
            var (code, output) = await RunNodeRawAsync(arg, 150000);
            LastOperationSucceeded = code == 0;
            AddEvent(code == 0 ? $"{action} 成功" : $"{action} 失败：{Trim(output)}");
        }
        finally
        {
            _busy = false;
            await RefreshAsync(false);
        }
    }

    public void SetAutoRecovery(bool enabled)
    {
        _ = RunNodeRawAsync(enabled ? "recover-on" : "recover-off", 20000);
        AddEvent(enabled ? "自动恢复已开启" : "自动恢复已关闭");
    }

    public Task NotifyResumeAsync() => RunNodeRawAsync("ensure", 60000);

    public async Task<string?> GetDashboardUrlAsync()
    {
        var (code, output) = await RunCmdAsync(_realCli + " dashboard --json --no-open", 60000);
        if (code != 0) return null;
        int a = output.IndexOf('{'), b = output.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try
        {
            using var doc = JsonDocument.Parse(output.Substring(a, b - a + 1));
            return doc.RootElement.TryGetProperty("browserUrl", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    public Task<string> ReadLogsAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("刷新于 " + DateTime.Now.ToString("HH:mm:ss") + "（每 5 秒自动刷新）");
        sb.AppendLine();
        var gatewayLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "openclaw", "openclaw-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
        sb.AppendLine("=== 网关日志尾部 " + gatewayLog + " ===");
        try
        {
            if (File.Exists(gatewayLog))
            {
                var gl = File.ReadAllLines(gatewayLog);
                foreach (var line in gl.Skip(Math.Max(0, gl.Length - 40)))
                {
                    string text = line;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        text = (doc.RootElement.TryGetProperty("time", out var t) ? t.GetString() : "") + "  " +
                               (doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : line);
                    }
                    catch { }
                    sb.AppendLine(text);
                }
            }
            else sb.AppendLine("(当日日志不存在)");
        }
        catch (Exception ex) { sb.AppendLine("(读取失败: " + ex.Message + ")"); }
        sb.AppendLine();
        foreach (var f in new[] { Path.Combine(_shellDir, "logs", "supervisor.jsonl"), Path.Combine(_shellDir, "logs", "soak.log") })
        {
            sb.AppendLine("=== " + f + " ===");
            try
            {
                if (File.Exists(f))
                {
                    var lines = File.ReadAllLines(f);
                    foreach (var line in lines.Skip(Math.Max(0, lines.Length - 120))) sb.AppendLine(line);
                }
                else sb.AppendLine("(不存在)");
            }
            catch (Exception ex) { sb.AppendLine("(读取失败: " + ex.Message + ")"); }
            sb.AppendLine();
        }
        return Task.FromResult(sb.ToString());
    }

    public Task<string> ExportDiagnosticsAsync(string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var f in new[] { "control-state.json" })
        {
            var src = Path.Combine(_shellDir, "control", f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(targetDir, f), true);
        }
        var log = Path.Combine(_shellDir, "logs", "supervisor.jsonl");
        if (File.Exists(log)) File.Copy(log, Path.Combine(targetDir, "supervisor.jsonl"), true);
        return Task.FromResult(targetDir);
    }

    public void Dispose() { _disposed = true; _http.Dispose(); }

    private async Task<bool> ProbeAsync(string path)
    {
        try
        {
            using var r = await _http.GetAsync($"http://127.0.0.1:{Port}{path}");
            return (int)r.StatusCode == 200;
        }
        catch { return false; }
    }

    private Task<string> RunNodeAsync(string args, int timeoutMs) => RunNodeRawAsync(args, timeoutMs).ContinueWith(t => t.Result.output);

    private Task<(int code, string output)> RunNodeRawAsync(string args, int timeoutMs)
        => RunDirectAsync(_node, $"\"{_jfctl}\" {args}", timeoutMs);

    private Task<(int code, string output)> RunDirectAsync(string exe, string args, int timeoutMs)
    {
        return Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = _shellDir
                };
                using var p = Process.Start(psi)!;
                string outp = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "TIMEOUT"); }
                return (p.ExitCode, outp + err);
            }
            catch (Exception ex) { return (-2, ex.Message); }
        });
    }

    private Task<(int code, string output)> RunCmdAsync(string line, int timeoutMs)
    {
        return Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/d /c " + line)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = _shellDir
                };
                using var p = Process.Start(psi)!;
                string outp = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "TIMEOUT"); }
                return (p.ExitCode, outp + err);
            }
            catch (Exception ex) { return (-2, ex.Message); }
        });
    }

    private static string Trim(string s) => s.Length <= 400 ? s.Trim() : s[..400].Trim();

    private void AddEvent(string text)
    {
        lock (_sync)
        {
            _events.Enqueue($"{DateTime.Now:HH:mm:ss}  {text}");
            while (_events.Count > 50) _events.Dequeue();
        }
    }

    private IReadOnlyList<string> TakeEvents()
    {
        lock (_sync) return _events.ToArray();
    }
}
