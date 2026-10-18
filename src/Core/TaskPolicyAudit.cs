using System.Xml;
using System.Xml.Linq;

namespace ClawDesk;

public sealed record TaskExpectedIdentity(string AccountSid, string Command, string Arguments, string WorkingDirectory);
public sealed record TaskPolicyReport(bool Safe, IReadOnlyList<string> Reasons);

/// <summary>Pure offline audit of supplied XML; this class never queries or modifies Task Scheduler.</summary>
public static class TaskPolicyAudit
{
    public static TaskPolicyReport Parse(string xml, TaskExpectedIdentity? expected = null)
    {
        var errors = new List<string>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var document = XDocument.Load(reader);
            var root = document.Root;
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            if (root?.Name != ns + "Task") return new(false, ["不是受支持的计划任务 XML。"]);
            XElement? Single(XElement parent, string name)
            {
                var items = parent.Elements(ns + name).ToArray();
                if (items.Length > 1) errors.Add("重复的 " + name + " 字段。");
                return items.SingleOrDefault();
            }
            bool Boolean(XElement parent, string name, bool? defaultValue)
            {
                var element = Single(parent, name);
                if (element is null)
                {
                    if (defaultValue is null) errors.Add(name + " 缺失，不能证明安全。");
                    return defaultValue ?? false;
                }
                if (element.Value is "true" or "1") return true;
                if (element.Value is "false" or "0") return false;
                errors.Add(name + " 不是合法布尔值。");
                return false;
            }
            var triggers = Single(root!, "Triggers");
            if (triggers is not null && triggers.Elements().Any()) errors.Add("存在主动触发器。");
            var settings = Single(root!, "Settings");
            if (settings is null) errors.Add("缺少 Settings，无法核验被动策略。");
            else
            {
                if (!Boolean(settings, "Enabled", null)) errors.Add("执行器必须 Enabled。");
                if (!Boolean(settings, "AllowStartOnDemand", null)) errors.Add("必须允许按需运行。");
                if (Boolean(settings, "StartWhenAvailable", false)) errors.Add("StartWhenAvailable 必须关闭。");
                if (Boolean(settings, "RunOnlyIfIdle", false)) errors.Add("空闲启动条件必须关闭。");
                if (Single(settings, "MultipleInstancesPolicy")?.Value != "IgnoreNew") errors.Add("多实例策略必须 IgnoreNew。");
                var restart = Single(settings, "RestartOnFailure");
                if (restart is not null && (!int.TryParse(Single(restart, "Count")?.Value, out int count) || count != 0))
                    errors.Add("任务级 RestartOnFailure 未明确关闭。");
                var idle = Single(settings, "IdleSettings");
                if (idle is not null && Boolean(idle, "RestartOnIdle", false)) errors.Add("RestartOnIdle 必须关闭。");
                if (settings.Descendants().Any(e => e.Name.LocalName is "Delay" or "MaintenanceSettings"))
                    errors.Add("存在延迟或维护启动策略，需人工审计。");
                string[] known = ["Enabled", "AllowStartOnDemand", "StartWhenAvailable", "RunOnlyIfIdle",
                    "MultipleInstancesPolicy", "RestartOnFailure", "IdleSettings", "DisallowStartIfOnBatteries",
                    "StopIfGoingOnBatteries", "AllowHardTerminate", "RunOnlyIfNetworkAvailable", "NetworkSettings",
                    "WakeToRun", "ExecutionTimeLimit", "Priority", "Hidden", "DeleteExpiredTaskAfter",
                    "UseUnifiedSchedulingEngine", "DisallowStartOnRemoteAppSession", "Volatile"];
                if (settings.Elements().Any(e => e.Name.Namespace != ns || !known.Contains(e.Name.LocalName)))
                    errors.Add("存在未知 Settings 策略，按未知暂停。");
            }
            var principals = Single(root!, "Principals")?.Elements(ns + "Principal").ToArray() ?? [];
            var actions = Single(root!, "Actions");
            var execs = actions?.Elements(ns + "Exec").ToArray() ?? [];
            if (principals.Length != 1 || execs.Length != 1 || actions!.Elements().Count() != 1)
                errors.Add("必须恰好一个 Principal 与一个 Exec。");
            if (expected is null) errors.Add("未提供执行器身份白名单，不能批准自动控制。");
            else if (principals.Length == 1 && execs.Length == 1)
            {
                var principal = principals[0];
                if (!string.Equals(Single(principal, "UserId")?.Value, expected.AccountSid, StringComparison.OrdinalIgnoreCase))
                    errors.Add("运行账户 SID 不匹配。");
                if (expected.AccountSid.Equals("S-1-5-18", StringComparison.OrdinalIgnoreCase) ||
                    Single(principal, "LogonType")?.Value != "InteractiveToken") errors.Add("必须由指定交互账户运行，禁止 SYSTEM。");
                string? context = actions?.Attribute("Context")?.Value;
                if (context is not null && context != principal.Attribute("id")?.Value) errors.Add("Action Context 与 Principal 不匹配。");
                var exec = execs[0];
                string command = Single(exec, "Command")?.Value ?? "";
                string directory = Single(exec, "WorkingDirectory")?.Value ?? "";
                if (!Path.IsPathFullyQualified(command) || !Path.IsPathFullyQualified(directory) ||
                    GatewayIdentity.CanonicalPath(command) != GatewayIdentity.CanonicalPath(expected.Command) ||
                    GatewayIdentity.CanonicalPath(directory) != GatewayIdentity.CanonicalPath(expected.WorkingDirectory))
                    errors.Add("执行器或工作目录绝对路径不匹配。");
                if ((Single(exec, "Arguments")?.Value ?? "") != expected.Arguments) errors.Add("执行器参数与身份白名单不匹配。");
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException or IOException or NotSupportedException)
        { errors.Add("任务 XML 无法完整解析，按未知暂停（" + ex.GetType().Name + "）。"); }
        return new(errors.Count == 0, errors.ToArray());
    }
}
