using System;
using System.Collections.Generic;

namespace ClawDesk;

public sealed record GatewaySnapshot
{
    public string Status { get; set; } = "unknown";
    public string Title { get; set; } = "正在检查网关";
    public string Detail { get; set; } = "只检查本机状态，不会自动启动或重启。";
    public string Endpoint { get; set; } = "http://127.0.0.1:18789";
    public string Live { get; set; } = "待检查";
    public string Ready { get; set; } = "待检查";
    public string Rpc { get; set; } = "尚未检查";
    public string Latency { get; set; } = "—";
    public string LastChecked { get; set; } = "—";
    public string RecoveryDescription { get; set; } = "自动恢复已关闭。";
    public string BusyHint { get; set; } = "";
    public string SettingsSummary { get; set; } = "";
    public bool Busy { get; set; }
    public bool IsReady { get; set; }
    public bool AutoRecovery { get; set; }
    public bool CanControl { get; set; }
    public bool StopConfirmed { get; set; }
    public string Mode { get; set; } = "observer";
    public string ControlRoot { get; set; } = "未配置（观察模式）";
    public string StartupPhase { get; set; } = "等待健康证据";
    public string TrayState { get; set; } = "gray";
    public double UiScale { get; set; } = 1;
    public int HealthIntervalSeconds { get; set; } = 30;
    public int RpcIntervalSeconds { get; set; } = 120;
    public IReadOnlyList<string> Events { get; set; } = Array.Empty<string>();
}
