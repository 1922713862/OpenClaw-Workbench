namespace ClawDesk;

public sealed record GatewayInstance(int Pid, DateTimeOffset StartedAt)
{
    public string Key => $"{Pid}:{StartedAt.UtcTicks}";
}

public sealed record RecoverySample
{
    public string SampleId { get; init; } = "";
    public DateTimeOffset ObservedAt { get; init; }
    public string ProcessState { get; init; } = "unknown"; // down / present / unknown
    public GatewayInstance? Instance { get; init; }
    public bool Ready { get; init; }
    public string Rpc { get; init; } = "unknown"; // ok / timeout / not-sampled / auth / config / unknown
}

public sealed record RecoveryEvidence
{
    public bool OwnerLeaseHeld { get; init; }
    public bool IdentityVerified { get; init; }
    public bool ExecutorVerified { get; init; }
    public bool ConflictsClear { get; init; }
    public bool NoInFlight { get; init; }
    public bool NoForeignListener { get; init; }
    public RecoverySample Sample { get; init; } = new();
}

public sealed record RecoveryDecision(bool Allowed, string? Operation, string Kind, string Reason,
    DateTimeOffset? EligibleAt = null, long DesiredRevision = 0);

/// <summary>Pure planning only. Persist Reserve with CAS BEFORE invoking an executor, then reread intent.</summary>
public static class RecoveryPolicy
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    public static RecoveryDecision Evaluate(ControlState state, RecoveryEvidence evidence, DateTimeOffset now)
    {
        RecoveryDecision Deny(string why, DateTimeOffset? when = null) =>
            new(false, null, "recovery", why, when, state.DesiredRevision);
        if (state.Desired != "running" || state.Mode != "owner" || !state.OwnerEnabled)
            return Deny("停止、观察或维护意图禁止自动动作。");
        if (!evidence.OwnerLeaseHeld || !evidence.IdentityVerified || !evidence.ExecutorVerified ||
            !evidence.ConflictsClear || !evidence.NoInFlight || !evidence.NoForeignListener)
            return Deny("属主、身份、被动执行器或冲突证据不完整。");
        var recovery = state.Recovery;
        if (recovery.Attempts.Any(a => a.State == "pending")) return Deny("存在 pending；先对账，绝不重放。");
        if (!string.IsNullOrWhiteSpace(recovery.PausedReason)) return Deny("恢复已暂停：" + recovery.PausedReason);
        var sample = evidence.Sample;
        if (string.IsNullOrWhiteSpace(sample.SampleId) || sample.ObservedAt > now ||
            now - sample.ObservedAt > TimeSpan.FromSeconds(60)) return Deny("当前进程证据缺失或过期。");
        string operation;
        if (sample.ProcessState == "down") operation = "start";
        else if (sample.ProcessState == "present" && sample.Instance is not null)
        {
            if (sample.Rpc == "ok" || (sample.Ready && sample.Rpc != "timeout"))
                return Deny("当前实例仍可响应；readyz 降级不授权重启。");
            if (recovery.ReadyInstanceKey != sample.Instance.Key)
                return Deny("该 PID 与创建时间未曾就绪；保持启动等待或诊断。");
            if (sample.Rpc is not ("timeout" or "not-sampled")) return Deny("认证、配置或未知故障只诊断。");
            if (recovery.FreshRpcTimeouts < 3 || recovery.FailureInstanceKey != sample.Instance.Key)
                return Deny("尚不足三个连续独立 RPC 超时样本。");
            if (recovery.LastRpcTimeoutAt is null || now - recovery.LastRpcTimeoutAt > TimeSpan.FromMinutes(3))
                return Deny("RPC 超时证据已过期，需新的独立样本。");
            operation = "restart";
        }
        else return Deny("进程状态未知；不当作退出。");

        // This allowance is durable and can be consumed exactly once, including across shell restarts.
        if (operation == "start" && !recovery.InitialStartUsed)
            return new(true, operation, "initial", "显式接管后的唯一首次启动。", now, state.DesiredRevision);
        if (!state.AutoRecover) return Deny("自动恢复未启用。");
        if (recovery.BudgetUsed >= 3) return Deny("三次自动恢复预算已用尽，只报警。");
        DateTimeOffset? anchor = recovery.FailureConfirmedAt;
        DateTimeOffset? eligible = recovery.NextRetryAt ?? (anchor is null ? null : anchor + Backoff[recovery.BudgetUsed]);
        if (eligible is null) return Deny("需先记录可重试故障确认时间。");
        if (now < eligible) return Deny("恢复退避中。", eligible);
        return new(true, operation, "recovery", "证据及退避满足；先原子预占再执行。", eligible, state.DesiredRevision);
    }

    public static ControlState ConfirmFailure(ControlState state, DateTimeOffset now)
    {
        if (state.Recovery.FailureConfirmedAt is not null) return state;
        return state with { Recovery = state.Recovery with { FailureConfirmedAt = now,
            NextRetryAt = state.Recovery.BudgetUsed < 3 ? now + Backoff[state.Recovery.BudgetUsed] : null } };
    }

    public static ControlState Reserve(ControlState state, RecoveryDecision decision, DateTimeOffset now)
    {
        if (!decision.Allowed || decision.Operation is not ("start" or "restart") ||
            decision.Kind is not ("initial" or "recovery" or "manual") || decision.DesiredRevision != state.DesiredRevision ||
            state.Desired != "running" || state.Mode != "owner" || !state.OwnerEnabled ||
            state.Recovery.Attempts.Any(a => a.State == "pending"))
            throw new InvalidOperationException("恢复授权已失效或存在未对账动作。");
        var recovery = state.Recovery;
        if (decision.Kind == "initial" && recovery.InitialStartUsed) throw new InvalidOperationException("首次启动额度已使用。");
        if (decision.Kind == "recovery" && (!state.AutoRecover || recovery.BudgetUsed >= 3 ||
            decision.EligibleAt is null || now < decision.EligibleAt || !string.IsNullOrWhiteSpace(recovery.PausedReason)))
            throw new InvalidOperationException("恢复预算或退避条件未满足。");
        long seq = recovery.Attempts.Length == 0 ? 1 : checked(recovery.Attempts[^1].Seq + 1);
        var attempt = new RecoveryAttempt { Seq = seq, Kind = decision.Kind, Operation = decision.Operation,
            IdentityKey = state.IdentityKey, DesiredRev = state.DesiredRevision, StartedAt = now };
        return state with { Recovery = recovery with { Attempts = [.. recovery.Attempts, attempt],
            BudgetUsed = recovery.BudgetUsed + (decision.Kind == "recovery" ? 1 : 0),
            InitialStartUsed = true,
            NextRetryAt = null, FailureConfirmedAt = null, StableSince = null, LastObservedAt = null,
            FreshRpcTimeouts = 0, FailureInstanceKey = null, LastRpcTimeoutAt = null } };
    }

    /// <summary>"unknown" preserves pending and pauses: an unproven outcome cannot create retry credit.</summary>
    public static ControlState CompleteAttempt(ControlState state, long seq, string outcome, DateTimeOffset now)
    {
        var recovery = state.Recovery;
        var attempt = recovery.Attempts.SingleOrDefault(a => a.Seq == seq && a.State == "pending")
            ?? throw new InvalidOperationException("找不到待对账的恢复尝试。");
        if (outcome == "unknown") return state with { Recovery = recovery with { PausedReason = "pending 结果未知，需只读对账。" } };
        if (outcome is not ("ready" or "failed" or "superseded")) throw new ArgumentException("未知结算结果。");
        var completed = attempt with { State = "completed", Outcome = outcome, CompletedAt = now };
        return state with { Recovery = recovery with {
            Attempts = recovery.Attempts.Select(a => a.Seq == seq ? completed : a).ToArray(),
            FailureConfirmedAt = outcome == "failed" ? now : null,
            NextRetryAt = outcome == "failed" && recovery.BudgetUsed < 3 ? now + Backoff[recovery.BudgetUsed] : null,
            PausedReason = outcome == "failed" && recovery.BudgetUsed >= 3 ? "三次自动恢复预算已用尽。" : null,
            StableSince = null, LastObservedAt = null } };
    }

    /// <summary>Call with one genuinely new probe, not the cached UI snapshot. Session/gap breaks stability.</summary>
    public static ControlState Observe(ControlState state, RecoverySample sample, bool sameObserverSession,
        TimeSpan? maxObservationGap = null)
    {
        var recovery = state.Recovery;
        if (string.IsNullOrWhiteSpace(sample.SampleId) || recovery.RecentSampleIds.Contains(sample.SampleId) ||
            (recovery.LastObservedAt is not null && sample.ObservedAt <= recovery.LastObservedAt)) return state;
        var gap = maxObservationGap ?? TimeSpan.FromSeconds(90);
        bool continuous = sameObserverSession && recovery.LastObservedAt is not null &&
            sample.ObservedAt - recovery.LastObservedAt <= gap;
        string? instance = sample.ProcessState == "present" ? sample.Instance?.Key : null;
        bool instanceChanged = instance != recovery.ReadyInstanceKey;
        bool ready = sample.Ready && (sample.Rpc is "ok" or "not-sampled") && instance is not null;
        DateTimeOffset? stableSince = ready ? (continuous && !instanceChanged ? recovery.StableSince ?? sample.ObservedAt : sample.ObservedAt) : null;
        int failures = recovery.FreshRpcTimeouts;
        string? failureInstance = recovery.FailureInstanceKey;
        DateTimeOffset? lastTimeout = recovery.LastRpcTimeoutAt;
        if (!sameObserverSession || instance != failureInstance || (lastTimeout is not null &&
            sample.ObservedAt - lastTimeout > TimeSpan.FromMinutes(3)))
        { failures = 0; failureInstance = instance; lastTimeout = null; }
        if (sample.Rpc == "timeout" && instance is not null && instance == recovery.ReadyInstanceKey)
        { failures = Math.Min(3, failures + 1); lastTimeout = sample.ObservedAt; }
        else if (sample.Rpc != "not-sampled" || instance is null)
        { failures = 0; failureInstance = null; lastTimeout = null; }
        var updated = recovery with { LastSampleId = sample.SampleId, LastObservedAt = sample.ObservedAt,
            RecentSampleIds = recovery.RecentSampleIds.TakeLast(127).Append(sample.SampleId).ToArray(),
            ReadyInstanceKey = ready ? instance : recovery.ReadyInstanceKey,
            StableSince = stableSince, FreshRpcTimeouts = failures, FailureInstanceKey = failureInstance,
            LastRpcTimeoutAt = lastTimeout };
        if (stableSince is not null && sample.ObservedAt - stableSince >= TimeSpan.FromMinutes(10) &&
            !recovery.Attempts.Any(a => a.State == "pending"))
            updated = updated with { BudgetUsed = 0, NextRetryAt = null, FailureConfirmedAt = null,
                PausedReason = null, FreshRpcTimeouts = 0, FailureInstanceKey = null };
        return state with { Recovery = updated };
    }
}
