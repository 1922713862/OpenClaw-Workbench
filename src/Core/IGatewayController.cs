using System;
using System.Threading.Tasks;

namespace ClawDesk;

/// <summary>UI contract; each implementation owns its explicit control boundary.</summary>
public interface IGatewayController : IDisposable
{
    GatewaySnapshot Snapshot { get; }
    bool LastOperationSucceeded { get; }
    event Action<GatewaySnapshot>? Updated;
    Task InitializeAsync();
    Task RefreshAsync(bool includeRpc = false);
    Task ExecuteAsync(string action);
    void SetAutoRecovery(bool enabled);
    Task NotifyResumeAsync();
    Task<string?> GetDashboardUrlAsync();
    Task<string> ReadLogsAsync();
    Task<string> ExportDiagnosticsAsync(string targetDir);
}
