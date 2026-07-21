using System;
using System.Threading;
using System.Windows.Forms;

namespace CodexQuotaTray
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool autoStart = HasArgument(args, "--autostart");
            bool forcePopup = !autoStart || HasArgument(args, "--popup");
            bool createdNew;
            using (Mutex mutex = new Mutex(true, @"Local\CodexQuotaTray.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    if (!autoStart)
                    {
                        MessageBox.Show(
                            "Codex 额度工具已经在任务栏通知区域运行。",
                            "Codex 额度",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }

                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs eventArgs)
                {
                    MessageBox.Show(
                        "程序遇到问题，但不会读取或更改你的 Codex 登录信息。\r\n\r\n" + eventArgs.Exception.Message,
                        "Codex 额度",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                };

                using (TrayApplicationContext context = new TrayApplicationContext(forcePopup))
                {
                    Application.Run(context);
                }

                try { mutex.ReleaseMutex(); }
                catch (ApplicationException) { }
            }
        }

        private static bool HasArgument(string[] args, string expected)
        {
            foreach (string argument in args)
            {
                if (String.Equals(argument, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
