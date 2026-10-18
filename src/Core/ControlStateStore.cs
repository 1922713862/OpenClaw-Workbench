using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawDesk;

/// <summary>The complete, release-independent identity used for state, locks and budgets.</summary>
public sealed record GatewayIdentity(string AccountSid, string StateDirectory, string ConfigPath,
    string Profile, int Port, string TaskName)
{
    public static GatewayIdentity Create(string accountSid, string stateDir, string configPath,
        string profile, int port, string taskName)
    {
        if (string.IsNullOrWhiteSpace(accountSid) || string.IsNullOrWhiteSpace(profile) ||
            string.IsNullOrWhiteSpace(taskName) || port is < 1 or > 65535)
            throw new ArgumentException("Gateway identity is incomplete.");
        string canonicalTask = "\\" + string.Join("\\", taskName.Trim().Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        if (canonicalTask == "\\") throw new ArgumentException("Task identity is empty.");
        return new(accountSid.Trim().ToUpperInvariant(), CanonicalPath(stateDir), CanonicalPath(configPath),
            profile.Trim(), port, canonicalTask);
    }

    public string IdentityKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new[] { AccountSid, StateDirectory, ConfigPath, Profile,
            Port.ToString(System.Globalization.CultureInfo.InvariantCulture), TaskName })))).ToLowerInvariant();

    internal static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An absolute path is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
    }
}

public sealed record OwnerMetadata
{
    public int OwnerPid { get; init; }
    public DateTimeOffset? OwnerStartTime { get; init; }
    public DateTimeOffset? AcquiredAt { get; init; }
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    // Diagnostic only: expiry NEVER authorizes breaking an actually-held OS lease.
    public DateTimeOffset? LeaseUntil { get; init; }
}

public sealed record RecoveryAttempt
{
    [JsonRequired]
    public long Seq { get; init; }
    [JsonRequired]
    public string State { get; init; } = "pending";
    [JsonRequired]
    public string Kind { get; init; } = "recovery";
    [JsonRequired]
    public string IdentityKey { get; init; } = "";
    [JsonRequired]
    public long DesiredRev { get; init; }
    [JsonRequired]
    public string Operation { get; init; } = "start";
    [JsonRequired]
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Outcome { get; init; }
}

public sealed record RecoveryState
{
    [JsonRequired]
    public RecoveryAttempt[] Attempts { get; init; } = [];
    [JsonRequired]
    public int BudgetUsed { get; init; }
    [JsonRequired]
    public bool InitialStartUsed { get; init; }
    [JsonRequired]
    public DateTimeOffset? NextRetryAt { get; init; }
    [JsonRequired]
    public DateTimeOffset? FailureConfirmedAt { get; init; }
    [JsonRequired]
    public string? PausedReason { get; init; }
    public string? ReadyInstanceKey { get; init; }
    public string? LastSampleId { get; init; }
    public string[] RecentSampleIds { get; init; } = [];
    public DateTimeOffset? LastObservedAt { get; init; }
    public DateTimeOffset? LastRpcTimeoutAt { get; init; }
    public DateTimeOffset? StableSince { get; init; }
    public int FreshRpcTimeouts { get; init; }
    public string? FailureInstanceKey { get; init; }
}

/// <summary>One atomic snapshot; separate JSON files cannot implement a single commit.</summary>
public sealed record ControlState
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    [JsonRequired]
    public string IdentityKey { get; init; } = "";
    [JsonRequired]
    public GatewayIdentity? Identity { get; init; }
    [JsonRequired]
    public long Revision { get; init; } = 1;
    [JsonRequired]
    public long DesiredRevision { get; init; } = 1;
    [JsonRequired]
    public string Desired { get; init; } = "stopped";
    [JsonRequired]
    public string Mode { get; init; } = "observer";
    [JsonRequired]
    public bool OwnerEnabled { get; init; }
    [JsonRequired]
    public bool AutoRecover { get; init; }
    public string? PriorDesired { get; init; }
    [JsonRequired]
    public string DesiredScope { get; init; } = "persistent";
    public string? LogonEpoch { get; init; }
    [JsonRequired]
    public string Reason { get; init; } = "explicit-initialization";
    [JsonRequired]
    public DateTimeOffset ChangedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonRequired]
    public OwnerMetadata Owner { get; init; } = new();
    [JsonRequired]
    public RecoveryState Recovery { get; init; } = new();

    public static ControlState CreateInitial(GatewayIdentity identity, string desired = "stopped") =>
        new() { Identity = identity, IdentityKey = identity.IdentityKey, Desired = desired };
}

public sealed record ControlStateReadResult(bool Success, ControlState? State, string Error);
public sealed record ControlStateWriteResult(bool Success, ControlState? State, string Error);

/// <summary>Only mutates the explicitly supplied control root; never initializes from a read.</summary>
public sealed class ControlStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly GatewayIdentity _identity;
    private readonly string _transactionMutex;
    public string ControlRoot { get; }
    public string StatePath => Path.Combine(ControlRoot, "control-state.json");

    public ControlStateStore(string controlRoot, GatewayIdentity identity)
    {
        ControlRoot = Path.GetFullPath(controlRoot);
        _identity = GatewayIdentity.Create(identity.AccountSid, identity.StateDirectory, identity.ConfigPath,
            identity.Profile, identity.Port, identity.TaskName);
        string rootKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            GatewayIdentity.CanonicalPath(ControlRoot)))).ToLowerInvariant();
        _transactionMutex = @"Global\JFlash.ControlTransaction." + rootKey;
    }

    public ControlStateReadResult Read()
    {
        try
        {
            if (!File.Exists(StatePath)) return new(false, null, "共享控制状态缺失；需要显式初始化。");
            using var file = new FileStream(StatePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 8 * 1024 * 1024) return new(false, null, "共享控制状态过大，已暂停。");
            var state = JsonSerializer.Deserialize<ControlState>(file, JsonOptions);
            string? error = Validate(state);
            return error is null ? new(true, state, "") : new(false, null, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException)
        { return new(false, null, "共享控制状态不可读，已暂停（" + ex.GetType().Name + "）。"); }
    }

    public ControlStateWriteResult InitializeExplicit(ControlState initial) => Transaction(() =>
    {
        if (File.Exists(StatePath)) return new(false, null, "状态已存在；不会覆盖或隐式修复。");
        var state = initial with { Identity = _identity, IdentityKey = _identity.IdentityKey,
            Revision = 1, DesiredRevision = 1 };
        string? error = Validate(state);
        if (error is not null) return new(false, null, error);
        Directory.CreateDirectory(ControlRoot);
        return WriteAtomic(state);
    });

    public ControlStateWriteResult TryUpdate(long expectedRevision, Func<ControlState, ControlState> change) =>
        Transaction(() =>
        {
            var read = Read();
            if (!read.Success) return new(false, null, read.Error);
            var current = read.State!;
            if (current.Revision != expectedRevision) return new(false, current, "修订号已变化；旧动作已取消。");
            var proposed = change(current) ?? throw new ArgumentException("State mutation returned null.");
            long revision = checked(current.Revision + 1);
            bool newIntent = proposed.Desired != current.Desired || proposed.Mode != current.Mode ||
                proposed.OwnerEnabled != current.OwnerEnabled || proposed.AutoRecover != current.AutoRecover ||
                proposed.LogonEpoch != current.LogonEpoch || proposed.DesiredScope != current.DesiredScope ||
                proposed.DesiredRevision != current.DesiredRevision;
            var state = proposed with { Identity = _identity, IdentityKey = _identity.IdentityKey,
                Revision = revision, DesiredRevision = newIntent ? revision : current.DesiredRevision,
                ChangedAt = DateTimeOffset.UtcNow };
            string? error = Validate(state);
            return error is null ? WriteAtomic(state) : new(false, null, error);
        });

    private ControlStateWriteResult Transaction(Func<ControlStateWriteResult> action)
    {
        bool held = false;
        try
        {
            using var mutex = new Mutex(false, _transactionMutex);
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { held = true; } // Entire snapshot is revalidated below.
            if (!held) return new(false, null, "控制状态事务忙；未执行动作。");
            try { return action(); }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or OverflowException or System.Security.SecurityException or WaitHandleCannotBeOpenedException)
        { return new(false, null, "控制状态提交失败；不得执行网关动作（" + ex.GetType().Name + "）。"); }
    }

    private ControlStateWriteResult WriteAtomic(ControlState state)
    {
        string temporary = Path.Combine(ControlRoot, ".control-state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, state, JsonOptions); stream.Flush(flushToDisk: true); }
            if (File.Exists(StatePath)) File.Replace(temporary, StatePath, null);
            else File.Move(temporary, StatePath); // First creation only; destination overwrite is forbidden.
            return new(true, state, "");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string? Validate(ControlState? state)
    {
        if (state is null || state.SchemaVersion != 1) return "未知或损坏的控制状态版本，已暂停。";
        if (state.IdentityKey != _identity.IdentityKey || state.Identity is null ||
            state.Identity.IdentityKey != _identity.IdentityKey) return "控制状态身份不匹配，已暂停。";
        if (state.Revision < 1 || state.DesiredRevision < 1 || state.DesiredRevision > state.Revision ||
            state.Desired is not ("running" or "stopped") || state.Mode is not ("observer" or "owner" or "maintenance") ||
            state.DesiredScope is not ("session" or "persistent") ||
            state.PriorDesired is not (null or "running" or "stopped") || state.Owner is null || state.Recovery is null)
            return "控制状态字段无效，已暂停。";
        var recovery = state.Recovery;
        if (recovery.Attempts is null || recovery.RecentSampleIds is null || recovery.RecentSampleIds.Length > 128 ||
            recovery.BudgetUsed is < 0 or > 3 || recovery.FreshRpcTimeouts < 0 ||
            recovery.Attempts.Count(a => a is not null && a.State == "pending") > 1)
            return "恢复预算无效，已暂停。";
        long lastSeq = 0;
        foreach (var attempt in recovery.Attempts)
        {
            if (attempt is null || attempt.Seq <= lastSeq || attempt.IdentityKey != _identity.IdentityKey ||
                attempt.DesiredRev < 1 || attempt.DesiredRev > state.Revision ||
                attempt.State is not ("pending" or "completed") || attempt.Kind is not ("initial" or "recovery" or "manual") ||
                attempt.Operation is not ("start" or "restart") ||
                (attempt.State == "completed" && (attempt.CompletedAt is null || attempt.Outcome is not ("ready" or "failed" or "superseded"))))
                return "恢复尝试记录无效，已暂停。";
            lastSeq = attempt.Seq;
        }
        return null;
    }
}
