using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ClawDesk;

/// <summary>The real disposable P1 identity is a separate allowlist from fixture and observer settings.</summary>
public sealed record P1Identity(string Root, int Port, string Profile, string TaskName)
{
    public static P1Identity Expected { get; } = new(
        Environment.GetEnvironmentVariable("JF_P1_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "jf-shell-p1"),
        18790, "jf-shell-p1", "JFlash P1 Gateway");

    [JsonIgnore] public bool IsExpected => Port == Expected.Port
        && string.Equals(Profile, Expected.Profile, StringComparison.Ordinal)
        && string.Equals(TaskName, Expected.TaskName, StringComparison.Ordinal)
        && SamePath(Root, Expected.Root) && IsExpectedPath(Root, "");

    public static string PipeName(string accountSid)
    {
        if (string.IsNullOrWhiteSpace(accountSid) || !accountSid.StartsWith("S-1-", StringComparison.Ordinal))
            throw new InvalidOperationException("P1 pipe requires an explicit Windows account SID.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountSid))).ToLowerInvariant();
        return "JFWorkbench.P1.v1." + hash[..24];
    }

    public static bool IsExpectedPath(string candidate, string relative)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) return false;
            string fixedPath = Path.GetFullPath(Path.Combine(Expected.Root, relative));
            if (!SamePath(candidate, fixedPath)) return false;
            var current = Path.GetFullPath(candidate);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                current = Path.GetDirectoryName(current);
            }
            return true;
        }
        catch { return false; }
    }

    private static bool SamePath(string left, string right)
    {
        try { return Path.IsPathFullyQualified(left) && string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
