using System;
using System.Threading;
using System.Windows.Forms;
using System.IO;

namespace JFWorkbench
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            string[] args = Environment.GetCommandLineArgs();
            // P0 delivery has no installed guard. Reject this unattended entry before any profile,
            // settings, UI, network or state initialization; future guard needs an explicit contract.
            if (Array.Exists(args, a => a.Equals("--guard", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = 2;
                return;
            }
            bool demo = Array.Exists(args, a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase) || a.Equals("--preview", StringComparison.OrdinalIgnoreCase) || a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
            bool uiReview = Array.Exists(args, a => a.Equals("--ui-review", StringComparison.OrdinalIgnoreCase));
            // Visual measurements are offline only. Never initialize the production observer
            // or P1 lifecycle merely because a screenshot argument was supplied.
            if (uiReview && (!demo || Array.Exists(args, a => a.Equals("--p1", StringComparison.OrdinalIgnoreCase)) ||
                Array.Exists(args, a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase))))
            { Environment.ExitCode = 2; return; }
            bool smoke = Array.Exists(args, a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
            bool p1 = !demo && Array.Exists(args, a => a.Equals("--p1", StringComparison.OrdinalIgnoreCase));
            bool p1Smoke = Array.Exists(args, a => a.Equals("--p1-smoke", StringComparison.OrdinalIgnoreCase) || a.Equals("--p1-stop-smoke", StringComparison.OrdinalIgnoreCase));
            if (p1Smoke && !p1) { Environment.ExitCode = 2; return; }
            bool lifecycleUi = Array.Exists(args, a => a.Equals("--p1-lifecycle-test-safeUI", StringComparison.OrdinalIgnoreCase));
            if (lifecycleUi && (!p1 || p1Smoke)) { Environment.ExitCode = 2; return; }
            bool quiet = lifecycleUi || uiReview || smoke || p1Smoke || Array.Exists(args, a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase) || a.Equals("--guard", StringComparison.OrdinalIgnoreCase));
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) =>
            {
                LogCrash("UI", e.Exception, demo, p1);
                if (lifecycleUi || uiReview || smoke || p1Smoke) { Environment.ExitCode = 1; Application.Exit(); }
                else if (!quiet) MessageBox.Show("操作未完成，异常类型与调用栈已记录到工作台数据目录。", "JFlash 工作台",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception error) LogCrash("Process", error, demo, p1);
            };
            bool isNew;
            using (var mutex = new Mutex(true, demo ? "JFWorkbench_Demo_v03" : p1 ? "JFWorkbench_P1_v1" : "JFWorkbench_SingleInstance_v1", out isNew))
            {
                if (!isNew)
                {
                    if (uiReview || p1Smoke) Environment.ExitCode = 3;
                    if (!quiet) MessageBox.Show("JFlash 工作台已经在运行（看右下角托盘图标）。", "JF Workbench",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                ApplicationConfiguration.Initialize();
                var form = new MainForm();
                if (uiReview) form.PrepareUiReview();
                Application.Run(form);
            }
        }

        static void LogCrash(string source, Exception error, bool demo, bool p1)
        {
            try
            {
                string directory = Path.Combine(AppContext.BaseDirectory, demo ? "demo-data" : p1 ? "p1-data" : "data");
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "crash.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1048576) File.WriteAllText(file, "");
                // Exception messages may contain URLs or credentials; retain only type, HResult and stack.
                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source} {error.GetType().FullName} HResult={error.HResult}\n{error.StackTrace}\n\n");
            }
            catch { }
        }
    }
}
