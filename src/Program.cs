using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MouseBattery
{
    /// <summary>
    /// 程序入口：单实例保护 + 全局异常捕获 + 启动托盘上下文
    /// </summary>
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            CrashLog.Write("程序启动");

            // UI 线程未处理异常：记录并弹窗提示（不再无声闪退）
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                CrashLog.Crash("UI线程异常", e.Exception);
                Fatal("程序遇到错误：" + e.Exception.Message);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // 后台线程未处理异常：记录（进程将终止，但日志会留下线索）
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                CrashLog.Crash("未处理异常(AppDomain)", e.ExceptionObject as Exception);
            };

            // 进程退出时留痕：若日志里只有"程序启动"没有这一行，说明进程是被强杀/原生崩溃掉的
            AppDomain.CurrentDomain.ProcessExit += delegate { CrashLog.Write("进程退出(ProcessExit)"); };

            bool createdNew;
            using (Mutex m = new Mutex(true, "MCHOSE_A7Pro_BatteryMonitor_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    CrashLog.Write("已有实例在运行，本次退出");
                    return;
                }
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                try
                {
                    Application.Run(new MainContext());
                }
                catch (Exception ex)
                {
                    CrashLog.Crash("启动/主循环异常", ex);
                    Fatal("程序启动失败：" + ex.Message);
                }
            }
            CrashLog.Write("程序正常退出");
        }

        static void Fatal(string msg)
        {
            try
            {
                MessageBox.Show(msg + "\r\n\r\n详细错误已写入:\r\n" + CrashLog.FilePath,
                    "鼠标电量监控", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }

    /// <summary>崩溃与运行日志（%LOCALAPPDATA%\A7ProBatteryMonitor\error.log）</summary>
    internal static class CrashLog
    {
        static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "A7ProBatteryMonitor");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(Dir, "error.log"); }
        }

        public static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        public static void Crash(string tag, Exception ex)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ").AppendLine(tag);
            if (ex != null)
            {
                sb.Append("  类型: ").AppendLine(ex.GetType().FullName);
                sb.Append("  消息: ").AppendLine(ex.Message);
                sb.AppendLine("  堆栈:");
                sb.AppendLine(ex.StackTrace);
            }
            else
            {
                sb.AppendLine("  (非 CLR 异常)");
            }
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, sb.ToString() + "\r\n", Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [System.Runtime.InteropServices.PreserveSig]
        public static extern bool SetProcessDPIAware();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string lpString);
    }
}
