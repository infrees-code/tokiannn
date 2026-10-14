using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MouseBattery
{
    /// <summary>
    /// 托盘常驻上下文：定时轮询电量、动态托盘图标、低电量系统通知
    /// </summary>
    public class MainContext : ApplicationContext
    {
        readonly Settings _settings = new Settings();
        NotifyIcon _tray;
        ToolStripMenuItem _miLevel;
        HostForm _host;          // 隐藏窗口，用于跨线程封送 + 接收 TaskbarCreated
        SettingsForm _form;      // 主窗口（按需创建）
        System.Windows.Forms.Timer _timer;
        System.Windows.Forms.Timer _bootTimer;    // 首次轮询延后计时器
        BatteryReading _last;
        List<BatteryReading> _lastAll;
        DateTime _lastGoodAt = DateTime.MinValue;
        bool _lowNotified;
        bool _firstBalloonShown;
        IntPtr _lastIconHandle = IntPtr.Zero;

        public MainContext()
        {
            CrashLog.Write("MainContext 初始化开始");
            _settings.Load();

            // 隐藏宿主窗口（用于 Invoke/BeginInvoke + 接收 TaskbarCreated 消息）
            _host = new HostForm();
            _host.FormBorderStyle = FormBorderStyle.None;
            _host.ShowInTaskbar = false;
            _host.WindowState = FormWindowState.Minimized;
            _host.Opacity = 0;
            _host.TaskbarCreated += delegate { RestoreTrayIcon(); };
            _host.Show();

            _tray = new NotifyIcon();
            _tray.Text = "鼠标电量：读取中…";
            _tray.Visible = true;
            SetTrayIcon(0, false);
            _tray.DoubleClick += delegate { ShowWindow(); };
            BuildMenu();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = Math.Max(10, _settings.IntervalSec) * 1000;
            _timer.Tick += delegate { Poll(); };
            _timer.Start();

            CrashLog.Write("托盘就绪，准备首次轮询");
            // 延后 1.5 秒再首次轮询：等托盘完全注册后再弹通知，避免通知被系统丢弃
            _bootTimer = new System.Windows.Forms.Timer();
            _bootTimer.Interval = 1500;
            _bootTimer.Tick += delegate
            {
                _bootTimer.Stop();
                Poll();
            };
            _bootTimer.Start();
        }

        // ---------- 轮询 ----------

        public void Poll()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<BatteryReading> list;
                try
                {
                    list = HidBatteryReader.ReadAll(_settings.EnableVendorProbe);
                }
                catch (Exception ex)
                {
                    CrashLog.Crash("后台 HID 读取异常", ex);
                    list = new List<BatteryReading>();
                }
                try
                {
                    _host.BeginInvoke((MethodInvoker)delegate { OnPolled(list); });
                }
                catch (Exception ex)
                {
                    CrashLog.Crash("封送轮询结果异常", ex);
                }
            });
        }

        void OnPolled(List<BatteryReading> list)
        {
            try
            {
                OnPolledCore(list);
            }
            catch (Exception ex)
            {
                CrashLog.Crash("轮询回调异常", ex);
            }
        }

        void OnPolledCore(List<BatteryReading> list)
        {
            CrashLog.Write(string.Format("轮询完成：{0} 个电池设备", list == null ? 0 : list.Count));

            // 兜底：若托盘图标因任何原因掉线，借每次轮询重新挂上
            if (!_tray.Visible) _tray.Visible = true;

            _lastAll = list;
            BatteryReading best = HidBatteryReader.PickBest(list);
            bool stale = false;

            // 兜底：迈从私有通道偶尔会被占用/设备短暂休眠导致本次读取失败，
            // 此时沿用最近 10 分钟内的成功读数，避免托盘在"电量"与"未检测到设备"之间乱跳。
            if (best == null && _last != null && _last.Percent >= 0
                && (DateTime.Now - _lastGoodAt).TotalMinutes < 10)
            {
                best = _last;
                stale = true;
            }

            if (best != null)
            {
                _last = best;
                _lastGoodAt = DateTime.Now;

                string chg = best.Charging ? "（充电中）" : "";
                string tail = stale ? "（上次读数）" : "";
                SetTrayIcon(best.Percent, best.Percent <= _settings.Threshold && !best.Charging);
                _tray.Text = string.Format("{0}：{1}%{2}{3}", ShortName(best), best.Percent, chg, tail);
                _miLevel.Text = string.Format("电量：{0}%{1}{2}（{3}）", best.Percent, chg, tail, ShortName(best));

                if (best.Percent <= _settings.Threshold && !best.Charging)
                {
                    if (!_lowNotified)
                    {
                        _lowNotified = true;
                        _tray.ShowBalloonTip(10000, "鼠标电量不足",
                            string.Format("「{0}」当前电量仅 {1}%，已低于 {2}% 提醒阈值，请及时充电。",
                                ShortName(best), best.Percent, _settings.Threshold),
                            ToolTipIcon.Warning);
                    }
                }
                else if (best.Percent >= _settings.Threshold + 5)
                {
                    _lowNotified = false; // 回充后重新布防
                }
            }
            else
            {
                SetTrayIcon(0, false);
                _tray.Text = "鼠标电量：未检测到设备";
                _miLevel.Text = "电量：未检测到设备";
            }

            // 首次必弹启动通知：确认程序确实在运行（Win11 新托盘图标默认折叠隐藏）
            if (!_firstBalloonShown)
            {
                _firstBalloonShown = true;
                string tip = best != null
                    ? string.Format("正在监控「{0}」，当前电量 {1}%。可在托盘折叠区查看电量图标。",
                        ShortName(best), best.Percent)
                    : "程序已在后台运行，但没读到电量。请确认接收器已插好、鼠标已开机并与接收器配对；"
                      + "若仍读不到，可点“导出检测报告”发回以适配。";
                try
                {
                    _tray.ShowBalloonTip(8000, "鼠标电量监控已启动", tip, ToolTipIcon.Info);
                }
                catch (Exception ex) { CrashLog.Crash("启动通知异常", ex); }
            }

            if (_form != null && !_form.IsDisposed)
            {
                _form.UpdateStatus(_last, _lastAll);
            }
        }

        static string ShortName(BatteryReading r)
        {
            string n = r.DeviceName ?? "";
            if (n.Length > 28) n = n.Substring(0, 28) + "…";
            return string.IsNullOrEmpty(n) ? "鼠标" : n;
        }

        // ---------- 托盘图标 ----------

        /// <summary>
        /// 渲染新图标 → 先赋给托盘 → 再销毁旧句柄。
        /// 顺序不能反：否则托盘会在窗口期内持有已销毁的 GDI 句柄，可能导致进程崩溃。
        /// </summary>
        void SetTrayIcon(int percent, bool low)
        {
            IntPtr h = IntPtr.Zero;
            Icon newIcon = null;
            try
            {
                newIcon = RenderIcon(percent, low, out h);
            }
            catch (Exception ex)
            {
                CrashLog.Crash("托盘图标渲染异常", ex);
            }
            if (newIcon == null)
            {
                h = IntPtr.Zero;
                newIcon = SystemIcons.Application;
            }

            _tray.Icon = newIcon;

            if (_lastIconHandle != IntPtr.Zero)
            {
                try { Native.DestroyIcon(_lastIconHandle); } catch { }
            }
            _lastIconHandle = h;
        }

        /// <summary>
        /// 资源管理器（explorer.exe）重启后系统会清空所有托盘图标，
        /// 必须监听 "TaskbarCreated" 消息重新注册，否则进程还在但图标消失。
        /// </summary>
        void RestoreTrayIcon()
        {
            try
            {
                CrashLog.Write("收到 TaskbarCreated，重新注册托盘图标");
                _tray.Visible = false;
                _tray.Visible = true;
                if (_last != null)
                {
                    SetTrayIcon(_last.Percent, _last.Percent <= _settings.Threshold && !_last.Charging);
                }
            }
            catch (Exception ex)
            {
                CrashLog.Crash("重新注册托盘图标异常", ex);
            }
        }

        Icon RenderIcon(int percent, bool low, out IntPtr hIcon)
        {
            hIcon = IntPtr.Zero;
            using (Bitmap b = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    Color outline = low
                        ? Color.FromArgb(0xE0, 0x40, 0x40)
                        : Color.FromArgb(0x55, 0x5D, 0x66);
                    Color fill = ColorFor(percent);

                    using (Pen p = new Pen(outline))
                    {
                        g.DrawRectangle(p, 1, 4, 11, 8);
                    }
                    using (SolidBrush tb = new SolidBrush(outline))
                    {
                        g.FillRectangle(tb, 12, 7, 2, 2);
                    }

                    int w = (int)Math.Round(percent / 100.0 * 9);
                    if (w < 1 && percent > 5) w = 1;
                    if (w > 0)
                    {
                        using (SolidBrush fb = new SolidBrush(fill))
                        {
                            g.FillRectangle(fb, 2, 5, Math.Min(9, w), 6);
                        }
                    }
                }
                hIcon = b.GetHicon();
                return Icon.FromHandle(hIcon);
            }
        }

        static Color ColorFor(int percent)
        {
            if (percent <= 20) return Color.FromArgb(0xE5, 0x39, 0x35);
            if (percent <= 50) return Color.FromArgb(0xFB, 0x8C, 0x00);
            return Color.FromArgb(0x2E, 0xA0, 0x43);
        }

        // ---------- 托盘菜单 ----------

        void BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            _miLevel = new ToolStripMenuItem("电量：读取中…");
            _miLevel.Enabled = false;

            ToolStripMenuItem miOpen = new ToolStripMenuItem("打开主窗口");
            miOpen.Font = new Font(menu.Font, FontStyle.Bold);
            miOpen.Click += delegate { ShowWindow(); };

            ToolStripMenuItem miRefresh = new ToolStripMenuItem("立即刷新");
            miRefresh.Click += delegate { Poll(); };

            ToolStripMenuItem miExport = new ToolStripMenuItem("导出检测报告（适配诊断）");
            miExport.Click += delegate { ExportDiagnostics(); };

            ToolStripMenuItem miProbe = new ToolStripMenuItem("其他品牌接收器探测");
            miProbe.Checked = _settings.EnableVendorProbe;
            miProbe.ToolTipText = "迈从 A7 Pro（2.4G）走专属协议，始终开启；此开关只影响其他品牌接收器的通用查询";
            miProbe.Click += delegate
            {
                _settings.EnableVendorProbe = !_settings.EnableVendorProbe;
                _settings.Save();
                miProbe.Checked = _settings.EnableVendorProbe;
                Poll();
            };

            ToolStripMenuItem miAuto = new ToolStripMenuItem("开机自启动");
            miAuto.Checked = Settings.IsAutoStart();
            miAuto.Click += delegate
            {
                bool enable = !Settings.IsAutoStart();
                Settings.SetAutoStart(enable);
                miAuto.Checked = Settings.IsAutoStart();
            };

            ToolStripMenuItem miThreshold = new ToolStripMenuItem("低电量提醒阈值");
            int[] opts = new int[] { 10, 15, 20, 25, 30 };
            foreach (int v in opts)
            {
                int vv = v;
                ToolStripMenuItem mi = new ToolStripMenuItem(v + "%");
                mi.Checked = (_settings.Threshold == v);
                mi.Click += delegate
                {
                    _settings.Threshold = vv;
                    _settings.Save();
                    foreach (ToolStripItem it in miThreshold.DropDownItems)
                    {
                        ToolStripMenuItem t = it as ToolStripMenuItem;
                        if (t != null) t.Checked = (t == mi);
                    }
                    Poll();
                };
                miThreshold.DropDownItems.Add(mi);
            }

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate
            {
                _tray.Visible = false;
                _tray.Dispose();
                ExitThread();
            };

            menu.Items.Add(_miLevel);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miOpen);
            menu.Items.Add(miRefresh);
            menu.Items.Add(miExport);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miAuto);
            menu.Items.Add(miProbe);
            menu.Items.Add(miThreshold);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);
            _tray.ContextMenuStrip = menu;
        }

        // ---------- 主窗口 ----------

        public void ShowWindow()
        {
            if (_form == null || _form.IsDisposed)
            {
                _form = new SettingsForm(this, _settings);
            }
            _form.UpdateStatus(_last, _lastAll);
            _form.Show();
            _form.Activate();
        }

        public void ApplyInterval()
        {
            _timer.Stop();
            _timer.Interval = Math.Max(10, _settings.IntervalSec) * 1000;
            _timer.Start();
        }

        /// <summary>导出全量 HID 设备诊断报告到桌面，用于适配未识别的 2.4G 接收器</summary>
        public void ExportDiagnostics()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                try { text = HidBatteryReader.BuildDiagnosticsReport(); }
                catch (Exception ex) { text = "生成报告失败: " + ex.Message; }

                string file = null;
                try
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    file = Path.Combine(desktop,
                        "A7Pro电池诊断报告_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                    File.WriteAllText(file, text, Encoding.UTF8);
                }
                catch { file = null; }

                string msg = file != null
                    ? "已保存到桌面: " + Path.GetFileName(file)
                    : "保存失败（可能没有桌面写入权限）";
                string tip = msg;
                try
                {
                    _host.BeginInvoke((MethodInvoker)delegate
                    {
                        _tray.ShowBalloonTip(8000, "诊断报告导出", tip, ToolTipIcon.Info);
                    });
                }
                catch { }
            });
        }

        protected override void ExitThreadCore()
        {
            if (_form != null && !_form.IsDisposed) _form.Dispose();
            base.ExitThreadCore();
        }
    }

    /// <summary>
    /// 隐藏宿主窗口。唯一职责：转发 "TaskbarCreated" 广播消息。
    /// 资源管理器（explorer.exe）重启 / 崩溃恢复后，系统会清空所有托盘图标，
    /// 不监听这条消息的话就会出现"进程还在、图标没了"。
    /// </summary>
    internal class HostForm : Form
    {
        static readonly uint WM_TASKBARCREATED = Native.RegisterWindowMessage("TaskbarCreated");

        public event Action TaskbarCreated;

        protected override void WndProc(ref Message m)
        {
            if (WM_TASKBARCREATED != 0 && (uint)m.Msg == WM_TASKBARCREATED)
            {
                Action h = TaskbarCreated;
                if (h != null) h();
            }
            base.WndProc(ref m);
        }
    }
}
