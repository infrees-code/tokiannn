using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace MouseBattery
{
    /// <summary>
    /// 主窗口：简洁中文界面，显示电量、设置阈值/刷新间隔/自启动
    /// </summary>
    public class SettingsForm : Form
    {
        readonly MainContext _ctx;
        readonly Settings _settings;

        Label _lblBig;
        BatteryBar _bar;
        Label _lblDevice;
        Label _lblTime;
        ComboBox _cmbThreshold;
        ComboBox _cmbInterval;
        CheckBox _chkAuto;
        Button _btnRefresh;
        Button _btnHide;
        Button _btnExport;
        Label _lblDevices;

        static readonly int[] ThresholdOptions = new int[] { 10, 15, 20, 25, 30 };
        static readonly int[] IntervalOptions = new int[] { 30, 60, 120, 300, 600 };
        static readonly string[] IntervalNames = new string[] { "30 秒", "1 分钟", "2 分钟", "5 分钟", "10 分钟" };
        bool _uiReady;

        public SettingsForm(MainContext ctx, Settings settings)
        {
            _ctx = ctx;
            _settings = settings;

            Text = "鼠标电量监控 · MCHOSE A7 Pro";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(430, 382);
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);

            BuildUi();
            LoadSettingsToUi();
            _uiReady = true;
        }

        void BuildUi()
        {
            // 大号电量数字
            _lblBig = new Label();
            _lblBig.Font = new Font("Segoe UI", 40F, FontStyle.Bold);
            _lblBig.ForeColor = ColorFor(100);
            _lblBig.Location = new Point(20, 12);
            _lblBig.Size = new Size(150, 84);
            _lblBig.TextAlign = ContentAlignment.MiddleCenter;
            _lblBig.Text = "--%";
            Controls.Add(_lblBig);

            // 电量条
            _bar = new BatteryBar();
            _bar.Location = new Point(190, 40);
            _bar.Size = new Size(210, 28);
            Controls.Add(_bar);

            // 设备名
            _lblDevice = new Label();
            _lblDevice.ForeColor = Color.FromArgb(0x66, 0x70, 0x7A);
            _lblDevice.Location = new Point(190, 74);
            _lblDevice.Size = new Size(210, 22);
            _lblDevice.Text = "未检测到设备";
            Controls.Add(_lblDevice);

            // 最近刷新时间
            _lblTime = new Label();
            _lblTime.ForeColor = Color.FromArgb(0x99, 0xA1, 0xA8);
            _lblTime.Location = new Point(28, 98);
            _lblTime.Size = new Size(372, 20);
            _lblTime.Text = "";
            Controls.Add(_lblTime);

            // 分隔线
            PictureBox line1 = new PictureBox();
            line1.BackColor = Color.FromArgb(0xEC, 0xEF, 0xF1);
            line1.Location = new Point(24, 128);
            line1.Size = new Size(382, 1);
            Controls.Add(line1);

            // 阈值
            Label labThreshold = new Label();
            labThreshold.Text = "低电量提醒阈值：";
            labThreshold.Location = new Point(28, 146);
            labThreshold.Size = new Size(130, 22);
            Controls.Add(labThreshold);

            _cmbThreshold = new ComboBox();
            _cmbThreshold.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbThreshold.Location = new Point(164, 142);
            _cmbThreshold.Size = new Size(90, 26);
            foreach (int v in ThresholdOptions) _cmbThreshold.Items.Add(v + " %");
            _cmbThreshold.SelectedIndexChanged += delegate { SaveUi(); };
            Controls.Add(_cmbThreshold);

            // 轮询间隔
            Label labInterval = new Label();
            labInterval.Text = "刷新间隔：";
            labInterval.Location = new Point(28, 182);
            labInterval.Size = new Size(130, 22);
            Controls.Add(labInterval);

            _cmbInterval = new ComboBox();
            _cmbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbInterval.Location = new Point(164, 178);
            _cmbInterval.Size = new Size(90, 26);
            foreach (string s in IntervalNames) _cmbInterval.Items.Add(s);
            _cmbInterval.SelectedIndexChanged += delegate { SaveUi(); };
            Controls.Add(_cmbInterval);

            // 自启动
            _chkAuto = new CheckBox();
            _chkAuto.Text = "开机自启动（随 Windows 登录自动运行）";
            _chkAuto.Location = new Point(28, 216);
            _chkAuto.Size = new Size(340, 24);
            _chkAuto.CheckedChanged += delegate { SaveUi(); };
            Controls.Add(_chkAuto);

            // 按钮
            _btnRefresh = new Button();
            _btnRefresh.Text = "立即刷新";
            _btnRefresh.FlatStyle = FlatStyle.Flat;
            _btnRefresh.BackColor = Color.FromArgb(0x21, 0x96, 0xF3);
            _btnRefresh.ForeColor = Color.White;
            _btnRefresh.FlatAppearance.BorderColor = Color.FromArgb(0x21, 0x96, 0xF3);
            _btnRefresh.Location = new Point(28, 258);
            _btnRefresh.Size = new Size(110, 34);
            _btnRefresh.Click += delegate { _ctx.Poll(); };
            Controls.Add(_btnRefresh);

            _btnHide = new Button();
            _btnHide.Text = "隐藏到托盘";
            _btnHide.FlatStyle = FlatStyle.Flat;
            _btnHide.FlatAppearance.BorderColor = Color.FromArgb(0xB0, 0xB8, 0xC0);
            _btnHide.Location = new Point(154, 258);
            _btnHide.Size = new Size(110, 34);
            _btnHide.Click += delegate { Hide(); };
            Controls.Add(_btnHide);

            _btnExport = new Button();
            _btnExport.Text = "导出诊断";
            _btnExport.FlatStyle = FlatStyle.Flat;
            _btnExport.FlatAppearance.BorderColor = Color.FromArgb(0xB0, 0xB8, 0xC0);
            _btnExport.ForeColor = Color.FromArgb(0x66, 0x70, 0x7A);
            _btnExport.Location = new Point(280, 258);
            _btnExport.Size = new Size(110, 34);
            _btnExport.Click += delegate { _ctx.ExportDiagnostics(); };
            Controls.Add(_btnExport);

            // 诊断信息
            PictureBox line2 = new PictureBox();
            line2.BackColor = Color.FromArgb(0xEC, 0xEF, 0xF1);
            line2.Location = new Point(24, 306);
            line2.Size = new Size(382, 1);
            Controls.Add(line2);

            _lblDevices = new Label();
            _lblDevices.ForeColor = Color.FromArgb(0x99, 0xA1, 0xA8);
            _lblDevices.Font = new Font("Microsoft YaHei UI", 8F);
            _lblDevices.Location = new Point(24, 312);
            _lblDevices.Size = new Size(382, 62);
            _lblDevices.Text = "等待检测…";
            Controls.Add(_lblDevices);
        }

        void LoadSettingsToUi()
        {
            int ti = 2; // 默认 20%
            for (int i = 0; i < ThresholdOptions.Length; i++)
                if (ThresholdOptions[i] == _settings.Threshold) ti = i;
            _cmbThreshold.SelectedIndex = ti;

            int ii = 1; // 默认 60 秒
            for (int i = 0; i < IntervalOptions.Length; i++)
                if (IntervalOptions[i] == _settings.IntervalSec) ii = i;
            _cmbInterval.SelectedIndex = ii;

            _chkAuto.Checked = Settings.IsAutoStart();
        }

        void SaveUi()
        {
            if (!_uiReady) return;
            _settings.Threshold = ThresholdOptions[_cmbThreshold.SelectedIndex];
            _settings.IntervalSec = IntervalOptions[_cmbInterval.SelectedIndex];
            _settings.Save();
            Settings.SetAutoStart(_chkAuto.Checked);
            _ctx.ApplyInterval();
        }

        /// <summary>由 MainContext 调用，刷新界面显示</summary>
        public void UpdateStatus(BatteryReading best, List<BatteryReading> allDevices)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke((MethodInvoker)delegate { UpdateStatus(best, allDevices); });
                    return;
                }
            }
            catch { return; }

            if (best != null)
            {
                _lblBig.Text = best.Percent + "%";
                _lblBig.ForeColor = ColorFor(best.Percent);
                _bar.Percent = best.Percent;
                string extra = "";
                if (best.Charging) extra += " · 充电中";
                if (!string.IsNullOrEmpty(best.Source)) extra += "（来源: " + best.Source + "）";
                _lblDevice.Text = best.DeviceName + extra;
            }
            else
            {
                _lblBig.Text = "--%";
                _lblBig.ForeColor = Color.FromArgb(0x99, 0xA1, 0xA8);
                _bar.Percent = -1;
                _lblDevice.Text = "未检测到可上报电量的设备";
            }
            _lblTime.Text = "最近刷新：" + DateTime.Now.ToString("HH:mm:ss");

            if (allDevices != null)
            {
                if (allDevices.Count == 0)
                {
                    _lblDevices.Text = "诊断：未发现带电池上报能力的 HID 设备。" +
                        "请确认鼠标已开机；若使用 2.4G 接收器无读数，可切换到蓝牙模式再试。";
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("诊断：发现 ").Append(allDevices.Count).Append(" 个电池上报设备");
                    foreach (BatteryReading d in allDevices)
                    {
                        sb.Append("\r\n  · ").Append(d.DeviceName);
                        if (d.Percent >= 0) sb.Append("（").Append(d.Percent).Append("%）");
                    }
                    if (allDevices.Count > 2) sb.Append("\r\n  （设备较多时建议切换到蓝牙模式以便识别）");
                    _lblDevices.Text = sb.ToString();
                }
            }
        }

        static Color ColorFor(int percent)
        {
            if (percent <= 20) return Color.FromArgb(0xE5, 0x39, 0x35);
            if (percent <= 50) return Color.FromArgb(0xFB, 0x8C, 0x00);
            return Color.FromArgb(0x2E, 0xA0, 0x43);
        }

        // 关闭窗口 = 隐藏到托盘，真正退出走托盘菜单
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }

    /// <summary>自绘电量条控件</summary>
    public class BatteryBar : Control
    {
        int _percent = -1;

        public int Percent
        {
            get { return _percent; }
            set { _percent = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Color.White);

            Rectangle body = new Rectangle(0, 2, Width - 12, Height - 6);

            using (GraphicsPath gp = RoundRect(body, 7))
            using (Pen p = new Pen(Color.FromArgb(0xB0, 0xB8, 0xC0), 2))
            {
                g.DrawPath(p, gp);
            }

            if (_percent > 0)
            {
                int w = (int)((body.Width - 6) * Math.Min(100, _percent) / 100.0);
                if (w > 0)
                {
                    Rectangle f = new Rectangle(body.X + 3, body.Y + 3, w, body.Height - 6);
                    using (GraphicsPath fp = RoundRect(f, 4))
                    using (SolidBrush b = new SolidBrush(
                        _percent <= 20 ? Color.FromArgb(0xE5, 0x39, 0x35) :
                        _percent <= 50 ? Color.FromArgb(0xFB, 0x8C, 0x00) :
                                         Color.FromArgb(0x2E, 0xA0, 0x43)))
                    {
                        g.FillPath(b, fp);
                    }
                }
            }

            // 电池正极头
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(0xB0, 0xB8, 0xC0)))
            {
                g.FillRectangle(tb, body.Right + 2, Height / 2 - 4, 5, 8);
            }
        }

        static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
