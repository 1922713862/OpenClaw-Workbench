using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawDesk;

/// <summary>Explicit, fail-closed settings. Observer and demo never execute a gateway CLI.</summary>
public sealed record GatewaySettings
{
    public int SchemaVersion { get; init; } = 1;
    public string Mode { get; init; } = "observer";
    public int Port { get; init; } = 18789;
    public string? CliExePath { get; init; }
    public string? CliEntryPath { get; init; }
    public string? StateDir { get; init; }
    public string? ConfigPath { get; init; }
    public string Profile { get; init; } = "default";
    public string TaskName { get; init; } = "OpenClaw Gateway";
    public string? ControlRoot { get; init; }
    public string? SandboxRoot { get; init; }
    public string? FixtureManifestPath { get; init; }
    public string? TaskPolicyXmlPath { get; init; }
    public int StartupGraceMinutes { get; init; } = 12;
    public int HealthIntervalSeconds { get; init; } = 30;
    public int RpcIntervalSeconds { get; init; } = 120;
    public int[] BackoffMinutes { get; init; } = new[] { 1, 5, 15 };
    public int MaxRestarts { get; init; } = 3;
    public int StableResetMinutes { get; init; } = 10;
    public double UiScale { get; init; } = 1;
    public string CloseMode { get; init; } = "tray";
    public bool AutoRecover { get; init; }

    [JsonIgnore] public bool IsDemo => Mode == "demo";
    [JsonIgnore] public bool IsValid => ValidationErrors.Count == 0;
    [JsonIgnore] public bool CanExecuteIsolated => Mode == "isolated" && IsValid;
    [JsonIgnore] public IReadOnlyList<string> ValidationErrors { get; private init; } = Array.Empty<string>();
    [JsonIgnore] public string WorkspaceRoot { get; private init; } = "";
    [JsonIgnore] private string? LoadError { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static GatewaySettings Demo() => new GatewaySettings { Mode = "demo", Port = 18790, TaskName = "JFWorkbench Demo" }.Validate();

    public static GatewaySettings Load(string appDataDir, string? workspaceRoot = null)
    {
        try
        {
            var path = Path.Combine(appDataDir, "settings.json");
            if (!File.Exists(path)) return new GatewaySettings().Validate(workspaceRoot);
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException();
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1)) throw new InvalidDataException();
            return (JsonSerializer.Deserialize<GatewaySettings>(json, JsonOptions)
                ?? throw new InvalidDataException()).Validate(workspaceRoot);
        }
        catch
        {
            return new GatewaySettings
            {
                WorkspaceRoot = FindWorkspaceRoot(workspaceRoot),
                LoadError = "settings.json 无效；已退回只读观察，未执行 CLI。",
                ValidationErrors = new[] { "settings.json 无效；已退回只读观察，未执行 CLI。" }
            };
        }
    }

    public GatewaySettings Validate(string? workspaceRoot = null)
    {
        var errors = new List<string>();
        if (LoadError is not null) errors.Add(LoadError);
        var workspace = FindWorkspaceRoot(workspaceRoot);
        if (SchemaVersion != 1) errors.Add("不支持的设置版本。");
        if (Mode is not ("observer" or "demo" or "isolated")) errors.Add("mode 仅支持 observer、demo、isolated。");
        if (Port is < 1 or > 65535) errors.Add("端口无效。");
        if (StartupGraceMinutes is < 1 or > 60) errors.Add("启动保护需为 1–60 分钟。");
        if (HealthIntervalSeconds is < 1 or > 3600) errors.Add("健康检查间隔需为 1–3600 秒。");
        if (RpcIntervalSeconds < HealthIntervalSeconds || RpcIntervalSeconds > 86400) errors.Add("RPC 间隔须不短于健康检查间隔，且不超过一天。");
        if (BackoffMinutes is null || !BackoffMinutes.SequenceEqual(new[] { 1, 5, 15 })) errors.Add("当前验证版本的退避必须为 [1,5,15] 分钟。");
        if (MaxRestarts is < 0 or > 3) errors.Add("恢复上限需为 0–3 次。");
        if (StableResetMinutes < 10 || StableResetMinutes > 1440) errors.Add("稳定清零窗口需为 10–1440 分钟。");
        if (!double.IsFinite(UiScale) || UiScale is < .9 or > 1.5) errors.Add("uiScale 需为 0.9–1.5。");
        if (CloseMode is not ("tray" or "stop")) errors.Add("closeMode 仅支持 tray 或 stop。");
        if (string.IsNullOrWhiteSpace(Profile) || Profile.Length > 64 || Profile.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')) errors.Add("profile 格式无效。");
        if (Mode == "isolated") ValidateIsolation(workspace, errors);
        return this with { WorkspaceRoot = workspace, ValidationErrors = errors.ToArray() };
    }

    public bool VerifyFixture(out string reason)
    {
        var checkedSettings = Validate(WorkspaceRoot);
        if (!checkedSettings.CanExecuteIsolated) { reason = string.Join(" ", checkedSettings.ValidationErrors); return false; }
        try
        {
            if (!File.Exists(CliExePath) || !File.Exists(CliEntryPath) || !File.Exists(FixtureManifestPath)) throw new InvalidDataException();
            if (new FileInfo(CliEntryPath!).Length > 262144) throw new InvalidDataException();
            if (new FileInfo(FixtureManifestPath!).Length > 65536) throw new InvalidDataException();
            using var manifest = JsonDocument.Parse(File.ReadAllText(FixtureManifestPath!));
            var root = manifest.RootElement;
            var signature = root.GetProperty("signature").GetString();
            var entry = root.GetProperty("entryPath").GetString();
            var hash = root.GetProperty("sha256").GetString();
            if (signature != "JFWorkbench.IsolatedFixture.v1" || string.IsNullOrWhiteSpace(entry)
                || !string.Equals(Path.GetFullPath(entry), Path.GetFullPath(CliEntryPath!), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(CliEntryPath!))), hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            // This is an adapter allowlist, never permission to call an installed production CLI.
            if (!File.ReadAllText(CliEntryPath!).Contains("JFWorkbench.IsolatedFixture.v1", StringComparison.Ordinal)) throw new InvalidDataException();
            reason = "已核验隔离夹具签名与哈希。";
            return true;
        }
        catch { reason = "隔离夹具缺失、签名或 SHA-256 不匹配；控制已暂停。"; return false; }
    }

    private void ValidateIsolation(string workspace, List<string> errors)
    {
        if (Port == 18789) errors.Add("隔离模式禁止生产端口 18789。");
        if (string.IsNullOrWhiteSpace(TaskName) || string.Equals(TaskName.Replace('/', '\\').Trim().TrimStart('\\'), "OpenClaw Gateway", StringComparison.OrdinalIgnoreCase)) errors.Add("隔离任务名必须与生产任务不同。");
        if (Profile == "default") errors.Add("隔离 profile 必须显式指定且不同于 default。");
        if (!IsWithin(SandboxRoot, workspace) || IsSamePath(SandboxRoot, workspace)) errors.Add("sandboxRoot 必须是当前工作库内的独立子目录。");
        foreach (var (label, path) in new[] { ("sandboxRoot", SandboxRoot), ("stateDir", StateDir), ("configPath", ConfigPath), ("controlRoot", ControlRoot), ("cliEntryPath", CliEntryPath), ("fixtureManifestPath", FixtureManifestPath), ("taskPolicyXmlPath", TaskPolicyXmlPath) })
        {
            if (!IsWithin(path, SandboxRoot) || IsProductionPath(path) || HasReparsePoint(path)) errors.Add($"{label} 必须位于隔离目录内，且不能指向生产目录或链接。");
        }
        if (IsSamePath(StateDir, ControlRoot)) errors.Add("stateDir 与 controlRoot 必须分离。");
        if (!Path.IsPathFullyQualified(CliExePath ?? "") || !string.Equals(Path.GetFileName(CliExePath), "node.exe", StringComparison.OrdinalIgnoreCase) || HasReparsePoint(CliExePath)) errors.Add("隔离执行器必须显式指定无链接的 node.exe。");
        else
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!IsWithin(CliExePath, SandboxRoot) && !IsWithin(CliExePath, programFiles)) errors.Add("node.exe 必须位于隔离目录或 Program Files。");
        }
    }

    private static string FindWorkspaceRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return Path.GetFullPath(explicitRoot);
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "JFWorkbench")) && Directory.Exists(Path.Combine(current.FullName, "ClawDesk"))) return current.FullName;
                current = current.Parent;
            }
        }
        return Path.GetFullPath(Environment.CurrentDirectory);
    }

    private static bool IsProductionPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var production = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw");
        string? extraRoot = Environment.GetEnvironmentVariable("JF_EXTRA_ROOT");
        return IsWithin(path, production) || (!string.IsNullOrWhiteSpace(extraRoot) && IsWithin(path, extraRoot));
    }

    private static bool IsSamePath(string? left, string? right)
    {
        try { return string.Equals(Path.GetFullPath(left ?? "").TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right ?? "").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool IsWithin(string? candidate, string? root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(candidate) || !Path.IsPathFullyQualified(root)) return false;
        try
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalized = Path.GetFullPath(candidate);
            return normalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) || IsSamePath(normalized, root);
        }
        catch { return false; }
    }

    private static bool HasReparsePoint(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return true;
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                current = Path.GetDirectoryName(current);
            }
            return false;
        }
        catch { return true; }
    }
}
