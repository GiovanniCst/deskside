using System;
using System.Threading;
using System.Windows.Forms;

namespace Deskside
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // One instance only: two processes talking over the same DDC/CI bus
            // would trip over each other.
            bool isNew;
            using (Mutex mutex = new Mutex(true, AppInfo.Name + ".SingleInstance", out isNew))
            {
                if (!isNew) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // A tray app has no window to show a crash in, so an unhandled
                // exception would otherwise vanish without a trace.
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Report(e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Report(e.ExceptionObject as Exception);
                };

                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
        }

        static void Report(Exception ex)
        {
            if (ex == null) return;
            MessageBox.Show(ex.ToString(), AppInfo.Name + " - unexpected error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
