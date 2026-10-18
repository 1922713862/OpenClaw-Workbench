using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClawDesk;

namespace JFWorkbench;

public partial class MainForm
{
    // P1-only messages expose the existing real button without global keyboard/mouse input.
    // Neither message can start/stop a Gateway, change intent, or change recovery settings.
    protected override void WndProc(ref Message message)
    {
        if (p1 && message.WParam.ToInt64() == 0x504131)
        {
            if (message.Msg == 0x8071) { ShowP1LifecycleControls(); message.Result = new IntPtr(1); return; }
            if (message.Msg == 0x8072) { ShowInTaskbar = false; Hide(); message.Result = new IntPtr(1); return; }
        }
        base.WndProc(ref message);
    }

    private void ShowP1LifecycleControls()
    {
        if (!p1 || IsDisposed) return;
        showRequested = true;
        StartPosition = FormStartPosition.Manual;
        WindowState = FormWindowState.Normal;
        Location = new Point(-16000, -16000);
        ShowInTaskbar = false;
        SetFocusMode(false);
        SetRailExpanded(true);
        SelectPage("status");
        Show();
        startButton.CreateControl();
        WriteP1GuiSession();
    }

    private void WriteP1GuiSession()
    {
        if (!p1 || controller is not P1GatewayController p1Controller || !IsHandleCreated) return;
        try
        {
            string logs = Path.Combine(P1Identity.Expected.Root, "logs");
            if (!P1Identity.IsExpectedPath(logs, "logs")) return;
            Directory.CreateDirectory(logs);
            using var process = Process.GetCurrentProcess();
            var r = p1Controller.LastVerifiedResult;
            var record = new
            {
                guiPid = process.Id, guiStartTime = process.StartTime.ToUniversalTime(),
                gatewayPid = r?.Pid, gatewayStartTime = r?.PidStartTime, runId = r?.RunId,
                ready = r is not null && r.Ready && snapshot.IsReady,
                authenticatedRpc = r?.RpcOk == true, budgetUsed = r?.BudgetUsed,
                autoRecover = r?.AutoRecover, observedAt = DateTimeOffset.UtcNow,
                mainHwnd = Handle.ToInt64(), startButtonHwnd = startButton.Handle.ToInt64(),
                startButtonEnabled = startButton.Enabled, lifecycleTestUi = p1LifecycleTestSafeUi
            };
            string target = Path.Combine(logs, $"Codex-p1-gui-session-{process.Id}.json");
            string temporary = target + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(record), new UTF8Encoding(false));
            File.Move(temporary, target, true);
        }
        catch { /* Evidence failure cannot change a lifecycle decision. */ }
    }

    private void RecordP1UiRequest(string action, bool accepted)
    {
        if (!p1) return;
        try
        {
            string logs = Path.Combine(P1Identity.Expected.Root, "logs");
            if (!P1Identity.IsExpectedPath(logs, "logs")) return;
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, $"Codex-p1-gui-clicks-{Environment.ProcessId}.jsonl"),
                JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, operation = action, accepted,
                    guiPid = Environment.ProcessId }) + "\n", new UTF8Encoding(false));
        }
        catch { }
    }
}
