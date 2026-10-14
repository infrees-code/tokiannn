using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MouseBattery
{
    /// <summary>
    /// 配置持久化（ini 风格）+ 开机自启动（HKCU Run 注册表）
    /// </summary>
    public class Settings
    {
        public int Threshold = 20;      // 低电量提醒阈值（%）
        public int IntervalSec = 60;    // 轮询间隔（秒）
        // 其他品牌接收器的通用厂商探测（17 字节查询命令）。
        // 迈从 A7 Pro（VID_5253）走专属私有协议，不受此开关影响、始终启用。
        public bool EnableVendorProbe = false;

        static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "A7ProBatteryMonitor");
            }
        }

        static string FilePath
        {
            get { return Path.Combine(Dir, "settings.txt"); }
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                string[] lines = File.ReadAllLines(FilePath);
                foreach (string ln in lines)
                {
                    string line = ln.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    int n;
                    if (k == "threshold" && int.TryParse(v, out n)) Threshold = Clamp(n, 5, 60);
                    if (k == "interval" && int.TryParse(v, out n)) IntervalSec = Clamp(n, 10, 3600);
                    if (k == "vendorprobe") EnableVendorProbe = (v == "1" || v == "true");
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath,
                    "threshold=" + Threshold + "\r\ninterval=" + IntervalSec +
                    "\r\nvendorprobe=" + (EnableVendorProbe ? "1" : "0") + "\r\n");
            }
            catch { }
        }

        static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // ---------- 开机自启动 ----------

        const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const string RunValueName = "MCHOSE_A7Pro_BatteryMonitor";

        public static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return k != null && k.GetValue(RunValueName) != null;
                }
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (enable)
                    {
                        k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        k.DeleteValue(RunValueName, false);
                    }
                }
            }
            catch { }
        }
    }
}
