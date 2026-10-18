namespace ClawDesk;

public sealed record LeaseProbe(bool Available, bool Reliable, string Reason);

/// <summary>Mutex ownership is thread-affine: this thread alone acquires, holds and releases it.</summary>
public sealed class OwnerLease : IDisposable
{
    private readonly ManualResetEventSlim _release = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private Exception? _failure;
    private volatile bool _held;
    private int _disposed;
    public bool IsHeld => _held && Volatile.Read(ref _disposed) == 0;
    public string MutexName { get; }

    private OwnerLease(GatewayIdentity identity)
    {
        var canonical = GatewayIdentity.Create(identity.AccountSid, identity.StateDirectory, identity.ConfigPath,
            identity.Profile, identity.Port, identity.TaskName);
        MutexName = @"Global\JFlash.GatewayOwner." + canonical.IdentityKey;
        _thread = new Thread(Hold) { IsBackground = true, Name = "JFlash owner lease" };
        _thread.Start();
        _ready.Wait();
    }

    public static OwnerLease? TryAcquire(GatewayIdentity identity)
    {
        var lease = new OwnerLease(identity);
        if (lease._failure is not null)
        {
            var failure = lease._failure;
            lease.Dispose();
            throw new InvalidOperationException("无法核验系统属主锁；必须保持观察模式。", failure);
        }
        if (lease.IsHeld) return lease;
        lease.Dispose();
        return null;
    }

    public static LeaseProbe Probe(GatewayIdentity identity)
    {
        try
        {
            using var lease = TryAcquire(identity);
            return lease is null ? new(false, true, "已有属主持有系统锁。") : new(true, true, "系统属主锁空闲。");
        }
        catch (Exception ex) { return new(false, false, "属主锁查询未知（" + ex.GetType().Name + "）。"); }
    }

    private void Hold()
    {
        bool acquired = false;
        try
        {
            using var mutex = new Mutex(false, MutexName);
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            _held = acquired;
            _ready.Set();
            if (!acquired) return;
            try { _release.Wait(); }
            finally { _held = false; mutex.ReleaseMutex(); }
        }
        catch (Exception ex) { _failure = ex; _held = false; _ready.Set(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _release.Set();
        if (Thread.CurrentThread != _thread) _thread.Join();
        _release.Dispose();
        _ready.Dispose();
    }
}

public sealed record GuardDecision(bool ShouldStartShell, string Reason);

public static class ShellGuardPolicy
{
    /// <summary>A read-only decision, not an executor. Starting shells must still acquire the real lease.</summary>
    public static GuardDecision Decide(ControlStateReadResult stateRead, LeaseProbe lease)
    {
        if (!stateRead.Success || stateRead.State is null) return new(false, "控制意图未知，守卫不得初始化或启动。");
        var state = stateRead.State;
        if (state.Mode != "owner" || !state.OwnerEnabled || state.Desired != "running")
            return new(false, "观察、维护或停止意图；守卫保持静默。");
        if (!lease.Reliable) return new(false, "属主锁查询未知；保持暂停。");
        if (!lease.Available) return new(false, "现有属主仍持锁；心跳文件过期不授权抢占。");
        return new(true, "允许只启动壳；壳需先取得属主锁并对账 pending，守卫不得操作网关。");
    }
}
