using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ClawDesk;

public sealed record P1StopEvidence
{
    public int TargetPid { get; init; }
    public string? TargetCreatedAt { get; init; }
    public string? TargetRunId { get; init; }
    public bool ProcessIdentityVerified { get; init; }
    public bool ProcessExited { get; init; }
    public bool ProcessQuerySucceeded { get; init; }
    public bool PortQuerySucceeded { get; init; }
    public bool PortReleased { get; init; }
    public bool DesiredPersisted { get; init; }
    public long DesiredRevision { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public bool SameTarget { get; init; }
    public int? ExitCode { get; init; }
    public string? ExitSignal { get; init; }
}

public sealed record P1BridgeResult
{
    public bool IdentityVerified { get; init; }
    public bool Live { get; init; }
    public bool Ready { get; init; }
    public bool Started { get; init; }
    public bool RpcOk { get; init; }
    public bool CanControl { get; init; }
    public bool Busy { get; init; }
    public string Desired { get; init; } = "unknown";
    public int? Pid { get; init; }
    public bool PidReleased { get; init; }
    public bool PortReleased { get; init; }
    public string ControlRoot { get; init; } = "";
    public int StartupGraceSeconds { get; init; }
    public string Phase { get; init; } = "unknown";
    public double? ProbeMilliseconds { get; init; }
    public string? DashboardUrl { get; init; }
    public string? RunId { get; init; }
    public string? PidStartTime { get; init; }
    public bool AutoRecover { get; init; }
    public int BudgetUsed { get; init; }
    public int PendingAttempts { get; init; }
    public int HealthIntervalSeconds { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public DateTimeOffset? FailureConfirmedAt { get; init; }
    public string? PausedReason { get; init; }
    public JsonElement? Timings { get; init; }
    public long Revision { get; init; }
    public long DesiredRevision { get; init; }
    public bool StopConfirmed { get; init; }
    public P1StopEvidence? StopEvidence { get; init; }

    public bool HasConfirmedStopEvidence()
    {
        var e = StopEvidence;
        return IdentityVerified && CanControl && StopConfirmed && Desired == "stopped"
            && Revision >= DesiredRevision && DesiredRevision > 0
            && PidReleased && PortReleased && !Live && !Started && !Ready && !RpcOk
            && e is not null && e.TargetPid > 0 && Pid == e.TargetPid
            && Guid.TryParse(RunId, out _) && RunId == e.TargetRunId
            && DateTimeOffset.TryParse(PidStartTime, out var actualCreated)
            && DateTimeOffset.TryParse(e.TargetCreatedAt, out var targetCreated) && actualCreated == targetCreated
            && e.ProcessIdentityVerified && e.ProcessExited && e.ProcessQuerySucceeded
            && e.PortQuerySucceeded && e.PortReleased && e.DesiredPersisted && e.SameTarget
            && e.DesiredRevision == DesiredRevision && e.ExitCode == 0 && e.ExitSignal is null
            && e.ObservedAt is { } observed && observed >= targetCreated
            && DateTimeOffset.UtcNow - observed is { TotalSeconds: >= -5 and <= 30 };
    }
}

public sealed record P1BridgeReply
{
    public int V { get; init; }
    public string Id { get; init; } = "";
    public string Action { get; init; } = "";
    public P1Identity? Identity { get; init; }
    public bool Ok { get; init; }
    public P1BridgeResult? Result { get; init; }
    public JsonElement? Error { get; init; }
}

public interface IP1BridgeClient
{
    Task<P1BridgeReply> CallAsync(string action, CancellationToken cancellationToken = default);
}

/// <summary>A same-account local pipe to one fixed P1 host. It cannot invoke a CLI or any gateway endpoint.</summary>
public sealed class P1BridgeClient : IP1BridgeClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false
    };
    private static readonly string[] Actions = { "status", "start", "stop", "restart", "rpc", "pair", "recover-on", "recover-off" };

    public async Task<P1BridgeReply> CallAsync(string action, CancellationToken cancellationToken = default)
    {
        if (Array.IndexOf(Actions, action) < 0) throw new InvalidOperationException("P1 action is not allowlisted.");
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("P1 account identity unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // P1 configuration supports 180..600 seconds. Include stop/verification overhead;
        // a caller timing out must never cause a second start to be sent.
        timeout.CancelAfter(TimeSpan.FromSeconds(action is "start" or "restart" ? 1020 : action == "stop" ? 420 : 45));
        using var pipe = new NamedPipeClientStream(".", P1Identity.PipeName(sid), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5000, timeout.Token);
        VerifyServer(pipe.SafePipeHandle, sid);
        string id = Guid.NewGuid().ToString("N");
        byte[] request = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v = 1, id, identity = P1Identity.Expected, action }, Json) + "\n");
        await pipe.WriteAsync(request, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        using var response = new MemoryStream();
        var buffer = new byte[4096];
        bool finished = false;
        while (!finished)
        {
            int count = await pipe.ReadAsync(buffer, timeout.Token);
            if (count == 0) throw new InvalidDataException("P1 bridge closed before a complete response.");
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            int take = newline < 0 ? count : newline;
            if (response.Length + take > 65536) throw new InvalidDataException("P1 bridge response exceeded its limit.");
            response.Write(buffer, 0, take);
            finished = newline >= 0;
        }
        var reply = JsonSerializer.Deserialize<P1BridgeReply>(response.ToArray(), Json);
        if (reply is null || reply.V != 1 || reply.Id != id || reply.Action != action || reply.Identity?.IsExpected != true)
            throw new InvalidDataException("P1 bridge response identity did not match the request.");
        if (reply.Result is { } result && !P1Identity.IsExpectedPath(result.ControlRoot, "control-v1"))
            throw new InvalidDataException("P1 bridge control root was outside the fixed identity.");
        if (action == "stop" && reply.Ok && reply.Result?.HasConfirmedStopEvidence() != true)
            return reply with { Ok = false, Error = JsonSerializer.SerializeToElement(new { code = "stop-evidence-incomplete" }) };
        return reply;
    }

    private static void VerifyServer(SafePipeHandle pipe, string expectedSid)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint serverPid)) throw new InvalidOperationException("P1 pipe server identity unavailable.");
        using var process = Process.GetProcessById(checked((int)serverPid));
        string? serverPath = process.MainModule?.FileName;
        if (serverPath is null || !P1Identity.IsExpectedPath(serverPath, @"host\P1BridgeHost.exe"))
            throw new InvalidOperationException("P1 pipe server executable was not the fixed host.");
        using var handle = OpenProcess(0x1000, false, serverPid);
        if (handle.IsInvalid || !OpenProcessToken(handle, 0x0008, out var token)) throw new InvalidOperationException("P1 pipe server account unavailable.");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (!string.Equals(identity.User?.Value, expectedSid, StringComparison.Ordinal)) throw new InvalidOperationException("P1 pipe server belongs to a different Windows account.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
}
