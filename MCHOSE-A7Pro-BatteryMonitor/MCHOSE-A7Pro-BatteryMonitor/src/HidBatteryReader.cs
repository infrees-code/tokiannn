using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MouseBattery
{
    /// <summary>
    /// 一次电量读取结果
    /// </summary>
    public class BatteryReading
    {
        public int Percent = -1;          // -1 表示读取失败
        public bool Charging;             // 是否充电中（仅部分协议可识别）
        public string DeviceName = "";
        public string DevicePath = "";
        public ushort Vid;
        public ushort Pid;
        public string Source = "";        // 电量来源：标准 HID 电池页 / 厂商私有协议
    }

    /// <summary>厂商协议探测结果</summary>
    internal class VendorResult
    {
        public int Percent = -1;
        public bool Charging;
        public bool Connected;        // 接收器是否判定鼠标在线
        public int ConnectMode = -1;  // 1 = 2.4G 无线，0 = 有线
        public int FwVersion = -1;
        public string Detail = "";    // 便于日志排查的完整解码结果
    }

    /// <summary>
    /// 通过 Win32 HID API 读取无线鼠标电量。
    /// 覆盖两种业界标准上报方式：
    ///   1) Generic Device Controls 用法页(0x06) / Battery Strength(0x20) —— 蓝牙与 2.4G 接收器的主流方式
    ///   2) Battery System 用法页(0x20) / Battery Strength(0x01)        —— 早期蓝牙 HID 设备
    /// 同时尝试 Input Report 与 Feature Report 两种通道。
    /// </summary>
    public static class HidBatteryReader
    {
        // ---------- 常量 ----------
        const int DIGCF_PRESENT = 0x2;
        const int DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000u;
        const uint GENERIC_WRITE = 0x40000000u;
        const uint FILE_SHARE_READ = 1;
        const uint FILE_SHARE_WRITE = 2;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_OVERLAPPED = 0x40000000u;
        const int ERROR_IO_PENDING = 997;
        const int WAIT_OBJECT_0 = 0;
        const int WAIT_TIMEOUT = 0x102;
        const int HIDP_STATUS_SUCCESS = 0x00110000;

        const int HidP_Input = 0;
        const int HidP_Feature = 2;

        // 迈从 A7 Pro（Realtek 方案）2.4G 接收器实测 VID/PID
        const ushort VID_MCHOSE = 0x5253;
        const ushort PID_A7PRO = 0x1021;

        const ushort PAGE_GDC = 0x06;    // Generic Device Controls
        const ushort USAGE_GDC_BATTERY = 0x20;   // Battery Strength
        const ushort PAGE_BATTERY = 0x20;        // Battery System（旧）
        const ushort USAGE_BAT_BATTERY = 0x01;   // Battery Strength

        static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        static readonly Guid HidGuid;

        static HidBatteryReader()
        {
            HidD_GetHidGuid(out HidGuid);
        }

        // ---------- 公共入口 ----------

        /// <summary>枚举当前所有可上报电量的 HID 设备并读取电量</summary>
        public static List<BatteryReading> ReadAll(bool enableVendorProbe)
        {
            List<BatteryReading> result = new List<BatteryReading>();
            Guid g = HidGuid;
            IntPtr hSet = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (hSet == IntPtr.Zero || hSet == INVALID_HANDLE_VALUE) return result;
            try
            {
                for (int i = 0; ; i++)
                {
                    SP_DEVICE_INTERFACE_DATA ifd = new SP_DEVICE_INTERFACE_DATA();
                    ifd.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(hSet, IntPtr.Zero, ref g, i, ref ifd))
                        break;

                    string path = GetInterfacePath(hSet, ref ifd);
                    if (string.IsNullOrEmpty(path)) continue;

                    BatteryReading r = ReadDevice(path, enableVendorProbe);
                    if (r != null) result.Add(r);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hSet);
            }
            return result;
        }

        /// <summary>从读取结果中挑选最可能是目标鼠标的一项（优先名称匹配 A7/MCHOSE/迈从）</summary>
        public static BatteryReading PickBest(List<BatteryReading> list)
        {
            if (list == null || list.Count == 0) return null;
            BatteryReading fallback = null;
            foreach (BatteryReading r in list)
            {
                if (r.Percent < 0) continue;
                string n = (r.DeviceName ?? "").ToLowerInvariant();
                if (n.Contains("a7") || n.Contains("mchose") || (r.DeviceName ?? "").Contains("迈从"))
                {
                    return r;
                }
                if (fallback == null) fallback = r;
            }
            return fallback;
        }

        // ---------- 内部实现 ----------

        static string GetInterfacePath(IntPtr hSet, ref SP_DEVICE_INTERFACE_DATA ifd)
        {
            int required = 0;
            SetupDiGetDeviceInterfaceDetail(hSet, ref ifd, IntPtr.Zero, 0, out required, IntPtr.Zero);
            if (required <= 0) return null;

            IntPtr buf = Marshal.AllocHGlobal(required);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize：64 位系统为 8，32 位为 6
                Marshal.WriteInt32(buf, 0, IntPtr.Size == 8 ? 8 : 6);
                int r2;
                if (!SetupDiGetDeviceInterfaceDetail(hSet, ref ifd, buf, required, out r2, IntPtr.Zero))
                    return null;
                return Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        static BatteryReading ReadDevice(string path, bool enableVendorProbe)
        {
            IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE)
            {
                h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            }
            if (h == INVALID_HANDLE_VALUE) return null;

            try
            {
                IntPtr ppd = IntPtr.Zero;
                try
                {
                    if (!HidD_GetPreparsedData(h, out ppd) || ppd == IntPtr.Zero) return null;

                    HIDP_CAPS caps;
                    if (HidP_GetCaps(ppd, out caps) != HIDP_STATUS_SUCCESS) return null;

                    BatteryReading r = new BatteryReading();
                    r.DevicePath = path;

                    HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
                    attr.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                    if (HidD_GetAttributes(h, ref attr))
                    {
                        r.Vid = attr.VendorID;
                        r.Pid = attr.ProductID;
                    }

                    StringBuilder sb = new StringBuilder(128);
                    if (HidD_GetProductString(h, sb, sb.Capacity * 2) && sb.Length > 0)
                    {
                        r.DeviceName = sb.ToString();
                    }
                    if (string.IsNullOrEmpty(r.DeviceName))
                    {
                        r.DeviceName = string.Format("HID 设备 (VID_{0:X4} PID_{1:X4})", r.Vid, r.Pid);
                    }

                    // 通道一：标准 HID 电池用法页（蓝牙 + 支持 GDC 电池上报的 2.4G 接收器）
                    int pct = ReadPercent(h, ppd, caps, HidP_Input);
                    if (pct < 0) pct = ReadPercent(h, ppd, caps, HidP_Feature);
                    if (pct >= 0)
                    {
                        r.Percent = pct;
                        r.Source = "标准 HID 电池页";
                        return r;
                    }

                    // 通道二（实测适配）：迈从 A7 Pro 2.4G 接收器 VID_5253
                    // 该接收器完全不上报标准 HID 电池页，必须走厂商特征报告 0x11 的私有协议。
                    // 协议与 MCHOSE HUB 官方软件逐字节一致（详见 ReadA7Battery 注释）。
                    if (r.Vid == VID_MCHOSE && IsA7Channel(caps))
                    {
                        VendorResult a7;
                        if (ReadA7Battery(path, caps, out a7) >= 0)
                        {
                            r.Percent = a7.Percent;
                            r.Charging = a7.Charging;
                            r.Pid = (ushort)(r.Pid == 0 ? PID_A7PRO : r.Pid);
                            r.Source = "迈从 A7 私有协议（特征报告 0x11）";
                            if (string.IsNullOrEmpty(r.DeviceName)) r.DeviceName = "迈从 A7 Pro 鼠标";
                            CrashLog.Write("A7 私有协议读取成功：" + a7.Detail);
                            return r;
                        }
                        CrashLog.Write(string.Format(
                            "A7 私有协议无应答（usagePage=0x{0:X4} usage=0x{1:X4} featLen={2}）",
                            caps.UsagePage, caps.Usage, caps.FeatureReportByteLength));
                        return null;
                    }

                    // 通道三：其他厂商的通用 17 字节查询协议
                    // （仅对其他品牌接收器生效；迈从设备已在上面的专属通道处理）
                    if (enableVendorProbe && r.Vid != VID_MCHOSE && IsVendorTlc(caps))
                    {
                        VendorResult vr = ProbeVendorBattery(h, path, caps);
                        if (vr.Percent >= 0)
                        {
                            r.Percent = vr.Percent;
                            r.Charging = vr.Charging;
                            r.Source = "厂商私有协议探测";
                            return r;
                        }
                    }
                    return null;
                }
                finally
                {
                    if (ppd != IntPtr.Zero) HidD_FreePreparsedData(ppd);
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>是否含有（页 0x06/用法 0x20 或 页 0x20/用法 0x01）的电池值</summary>
        static bool HasBatteryCaps(IntPtr ppd, HIDP_CAPS caps)
        {
            HIDP_VALUE_CAPS[] capsIn = GetValueCaps(ppd, caps, HidP_Input);
            HIDP_VALUE_CAPS[] capsFe = GetValueCaps(ppd, caps, HidP_Feature);
            if (capsIn != null) foreach (HIDP_VALUE_CAPS c in capsIn) if (IsBatteryCap(c)) return true;
            if (capsFe != null) foreach (HIDP_VALUE_CAPS c in capsFe) if (IsBatteryCap(c)) return true;
            return false;
        }

        static bool IsBatteryCap(HIDP_VALUE_CAPS c)
        {
            if (c.IsRange != 0) return false;
            if (c.UsagePage == PAGE_GDC && c.Usage == USAGE_GDC_BATTERY) return true;
            if (c.UsagePage == PAGE_BATTERY && c.Usage == USAGE_BAT_BATTERY) return true;
            return false;
        }

        static bool _sizeLogged;

        /// <summary>首次调用时把结构体实际大小写进日志，便于远程确认布局正确（应为 72）</summary>
        static void LogStructSizeOnce(int managedSize)
        {
            if (_sizeLogged) return;
            _sizeLogged = true;
            CrashLog.Write("HIDP_VALUE_CAPS 托管大小=" + managedSize + "，原生应为 " + HIDP_VALUE_CAPS_SIZE
                + (managedSize == HIDP_VALUE_CAPS_SIZE ? "（一致）" : "（不一致！读取结果不可信）"));
        }

        static HIDP_VALUE_CAPS[] GetValueCaps(IntPtr ppd, HIDP_CAPS caps, int reportType)
        {
            int n = reportType == HidP_Input ? caps.NumberInputValueCaps : caps.NumberFeatureValueCaps;
            if (n <= 0) return null;
            // 必须用常量 72 作为步长：hid.dll 按原生结构体大小写，托管结构体哪怕算错也不会越界
            int elemSize = HIDP_VALUE_CAPS_SIZE;
            int managedSize = Marshal.SizeOf(typeof(HIDP_VALUE_CAPS));
            if (managedSize > elemSize) elemSize = managedSize;
            LogStructSizeOnce(managedSize);
            IntPtr pBuf = Marshal.AllocHGlobal(n * elemSize);
            try
            {
                short len = (short)n;
                int st = HidP_GetValueCaps(reportType, pBuf, ref len, ppd);
                if (st != HIDP_STATUS_SUCCESS) return null;
                int count = Math.Min(n, (int)len);
                HIDP_VALUE_CAPS[] arr = new HIDP_VALUE_CAPS[count];
                for (int i = 0; i < count; i++)
                {
                    arr[i] = (HIDP_VALUE_CAPS)Marshal.PtrToStructure(
                        new IntPtr(pBuf.ToInt64() + i * elemSize), typeof(HIDP_VALUE_CAPS));
                }
                return arr;
            }
            finally
            {
                Marshal.FreeHGlobal(pBuf);
            }
        }

        static int ReadPercent(IntPtr h, IntPtr ppd, HIDP_CAPS caps, int reportType)
        {
            HIDP_VALUE_CAPS[] arr = GetValueCaps(ppd, caps, reportType);
            if (arr == null) return -1;
            foreach (HIDP_VALUE_CAPS c in arr)
            {
                if (!IsBatteryCap(c)) continue;
                int pct = ReadCapValue(h, ppd, caps, reportType, c);
                if (pct >= 0) return pct;
            }
            return -1;
        }

        static int ReadCapValue(IntPtr h, IntPtr ppd, HIDP_CAPS caps, int reportType, HIDP_VALUE_CAPS cap)
        {
            int len = reportType == HidP_Input ? caps.InputReportByteLength : caps.FeatureReportByteLength;
            if (len <= 0) return -1;
            byte[] rep = new byte[len];
            rep[0] = cap.ReportID;
            bool ok = reportType == HidP_Input
                ? HidD_GetInputReport(h, rep, rep.Length)
                : HidD_GetFeature(h, rep, rep.Length);
            if (!ok) return -1;

            uint v;
            int st = HidP_GetUsageValue(reportType, cap.UsagePage, cap.LinkCollection, cap.Usage,
                out v, ppd, rep, rep.Length);
            if (st != HIDP_STATUS_SUCCESS) return -1;
            return ScalePercent((int)v, cap.LogicalMin, cap.LogicalMax);
        }

        static int ScalePercent(int v, int logicalMin, int logicalMax)
        {
            if (logicalMax <= logicalMin) return -1;
            double pct;
            if (logicalMin >= 0 && logicalMax == 100)
            {
                pct = v;
            }
            else
            {
                pct = (v - logicalMin) * 100.0 / (logicalMax - logicalMin);
            }
            pct = Math.Round(pct);
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            return (int)pct;
        }

        // ===================================================================================
        //  迈从 A7 Pro / A7 系列 2.4G 私有协议（与 MCHOSE HUB 官方软件完全一致）
        // ===================================================================================
        //
        // 协议来源（权威定义，非猜测）：MCHOSE HUB 前端 bundle，取自 Chromium 缓存
        //   %APPDATA%\MCHOSEHUB\Cache\Cache_Data\f_000011
        //   NM["0x11 0x06"] = { order: `11 06 ${"00 ".repeat(19).trim()}`, parser: vXe }
        //   vXe = little-endian: uint16 vid, uint16 pid, uint32 fwVersion,
        //                        bit3 connectMode, bit1 connectStatus, bit4 inReserved,
        //                        uint8 batteryLevel, uint8 chargeStatus
        //   gP() = sendFeatureReport(RID, bytes ^ 0xFF)
        //          -> receiveFeatureReport(RID) -> slice(2) -> 每字节 ^0xFF -> parser.parse()
        //
        // 即：命令与应答整帧都按 XOR 0xFF 混淆；应答头 2 字节是 [特征报告ID] + [命令回显]，
        //     从第 3 字节起才是 vXe 结构体。首字节 0x11 同时是该特征报告的 Report ID。
        //
        // 本机实测回读（A7 Pro 2.4G，与 HUB 自己的 localStorage deviceInfoMap 逐字段一致）：
        //   HID 应答 : 53 52 10 00 | 04 05 07 00 | 09 | 26 | 00
        //   HUB 记录 : {"vid":21075,"pid":16,"fwVersion":460036,"connectMode":1,
        //               "connectStatus":1,"inReserved":0,"batteryLevel":38,"chargeStatus":0}
        //   → vid=0x5253 pid=0x0010(16) fw=460036 connectMode=1 connectStatus=1 battery=38 charge=0
        //
        // 角色位（字节 0x09）按低位优先拆分才自洽：0x09 = 0b0000_1001
        //   connectMode = bits & 0x07          = 1  （1 = 2.4G 无线，与 HUB 的取值一致）
        //   connectStatus = (bits >> 3) & 1    = 1  （鼠标在线）
        //   inReserved = bits >> 4             = 0
        // ===================================================================================

        const ushort PAGE_A7_PROTO = 0xFF01;      // 命令/应答通道（usage 0x0001）
        const ushort PAGE_A7_PROTO_ALT = 0xFF0B;  // 备用厂商通道（usage 0x0104，部分固件）
        const byte A7_RID_FEATURE = 0x11;         // 特征报告 ID：既发命令也读应答
        const byte A7_CMD_DEVICE_INFO = 0x06;     // 子命令 0x06：设备信息 + 电量
        const int A7_CMD_TRAIL_ZEROS = 19;        // 命令尾部 19 个 0x00
        const int A7_MIN_FEATURE_LEN = 21;        // 1(报告ID) + 20(载荷)
        const int A7_CYCLES = 3;                  // 整体重试轮数
        const int A7_WARMUP_READS = 2;            // 发命令前先读几次邮箱（消费残留应答）
        const int A7_CMD_SETTLE_MS = 250;         // 命令下发后等待设备处理
        const int A7_READ_ATTEMPTS = 8;           // 每轮回读次数
        const int A7_READ_GAP_MS = 120;

        // ⚠ 两个实测要点，改动前务必先读：
        //
        // 1) 【必须预热读】特征报告 0x11 是"邮箱"式的：它保存着上一次命令的应答。
        //    若直接写入新命令，同步句柄下实测会退化成"只回显刚写入的内容"，
        //    读 1.5 秒也拿不到真实应答（Python / C# 均复现）。
        //    先空读 1~2 次把残留应答消费掉，再下发命令，即可稳定拿到新应答。
        //
        // 2) 【命令回显做新鲜度校验】应答帧第 2 字节是命令回显（混淆形式下为 cmd^0xFF）。
        //    只有它等于 0x06 时才是本次查询的应答，可彻底排除陈旧帧。
        //
        // 另注：回读缓冲区长度必须取 FeatureReportByteLength（本机 65）；
        //       用命令自身长度 21 去读同样只会拿到回显。

        static bool IsA7Channel(HIDP_CAPS caps)
        {
            return caps.UsagePage == PAGE_A7_PROTO || caps.UsagePage == PAGE_A7_PROTO_ALT;
        }

        /// <summary>读取一次特征报告 0x11（邮箱），失败返回 null</summary>
        static byte[] A7ReadMailbox(IntPtr h, int len)
        {
            byte[] buf = new byte[len];
            buf[0] = A7_RID_FEATURE;
            return HidD_GetFeature(h, buf, len) ? buf : null;
        }

        /// <summary>
        /// 走迈从私有协议读取 A7 Pro 电量。
        /// 步骤：预热读邮箱 → 写入 11 06（载荷 XOR 0xFF）→ 回读邮箱并解码校验。
        /// </summary>
        static int ReadA7Battery(string path, HIDP_CAPS caps, out VendorResult vr)
        {
            vr = new VendorResult();
            vr.Percent = -1;

            if (!IsA7Channel(caps)) return -1;

            // 缓冲区长度必须用该集合声明的最大特征报告长度（本机 65）
            int len = caps.FeatureReportByteLength;
            if (len < A7_MIN_FEATURE_LEN) return -1;

            IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE)
            {
                h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            }
            if (h == INVALID_HANDLE_VALUE) return -1;

            try
            {
                // 命令帧：报告 ID 0x11 + (11 06 00×19 的载荷逐字节 XOR 0xFF)，尾部用 0xFF 填充
                byte[] frame = new byte[len];
                for (int i = 0; i < len; i++) frame[i] = 0xFF;
                frame[0] = A7_RID_FEATURE;
                frame[1] = (byte)(A7_CMD_DEVICE_INFO ^ 0xFF);     // 0xF9
                for (int i = 0; i < A7_CMD_TRAIL_ZEROS; i++)
                    frame[2 + i] = (byte)(0x00 ^ 0xFF);           // 0xFF ×19

                byte[] last = null;
                bool anyWriteOk = false;
                bool anyReadTried = false;
                for (int cycle = 0; cycle < A7_CYCLES; cycle++)
                {
                    // 1) 预热读：消费掉邮箱里残留的上一次应答（关键，勿删）
                    for (int w = 0; w < A7_WARMUP_READS; w++)
                    {
                        A7ReadMailbox(h, len);
                        System.Threading.Thread.Sleep(30);
                    }

                    // 2) 下发命令
                    if (!HidD_SetFeature(h, frame, len)) continue;   // 该通道不可写，换下一轮
                    anyWriteOk = true;
                    System.Threading.Thread.Sleep(A7_CMD_SETTLE_MS);

                    // 3) 回读并校验（vid/pid 自校验 + 命令回显做新鲜度校验）
                    for (int attempt = 0; attempt < A7_READ_ATTEMPTS; attempt++)
                    {
                        byte[] resp = A7ReadMailbox(h, len);
                        anyReadTried = true;
                        if (resp != null)
                        {
                            last = resp;
                            if (TryParseA7(resp, vr)) return vr.Percent;
                        }
                        System.Threading.Thread.Sleep(A7_READ_GAP_MS);
                    }
                }

                // 失败原因只记一条，避免备用通道刷屏
                if (last != null)
                {
                    CrashLog.Write(string.Format(
                        "A7 应答无法识别（{0} 轮 × {1} 次）。最后一次原始帧: {2} / XOR-FF: {3}",
                        A7_CYCLES, A7_READ_ATTEMPTS, Hex(last, 16), XorFFHex(last, 1, 16)));
                }
                else if (anyWriteOk && anyReadTried)
                {
                    CrashLog.Write("A7 应答读取失败：HidD_GetFeature 未返回数据（是否被安全软件拦截？）");
                }
                else if (!anyWriteOk)
                {
                    CrashLog.Write(string.Format("A7 通道不可写（usagePage=0x{0:X4}），已跳过",
                        caps.UsagePage));
                }
                return -1;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>
        /// 解析 A7 应答帧。结构体自 vid 起算（vid/pid 与真实 USB 描述符一致），
        /// 配合"命令回显 == 0x06"做双重校验，避免把回显或陈旧帧误当成电量。
        /// </summary>
        static bool TryParseA7(byte[] resp, VendorResult vr)
        {
            if (resp.Length < 3 + 11) return false;
            byte echo = resp[1];

            for (int mode = 0; mode < 2; mode++)
            {
                // mode 0：整帧 XOR 0xFF 混淆（MCHOSE HUB 期望的形式）
                // mode 1：明文形式（设备在同一缓冲区上交替呈现两种形式）
                byte cmdEcho = (mode == 0) ? (byte)(echo ^ 0xFF) : echo;
                if (cmdEcho != A7_CMD_DEVICE_INFO) continue;

                int n = resp.Length - 2;
                byte[] d = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    byte b = resp[i + 2];
                    d[i] = (mode == 0) ? (byte)(b ^ 0xFF) : b;
                }

                int vid = d[0] | (d[1] << 8);
                int pid = d[2] | (d[3] << 8);
                if (vid != VID_MCHOSE) continue;                     // 自校验：必须是迈从
                if (pid != PID_A7PRO && pid != 0x0010) continue;     // 接收器 PID（实测 0x0010）
                if (d[9] > 100) continue;                            // 电量为百分比，越界即解析错位

                int bits = d[8];
                vr.Percent = d[9];
                vr.Charging = d[10] != 0;
                vr.ConnectMode = bits & 0x07;
                vr.Connected = ((bits >> 3) & 0x01) != 0;
                vr.FwVersion = d[4] | (d[5] << 8) | (d[6] << 16) | (d[7] << 24);
                vr.Detail = string.Format(
                    "vid=0x{0:X4} pid=0x{1:X4} fw={2} connectMode={3} connectStatus={4} battery={5}% charge={6}（{7}形式）",
                    vid, pid, vr.FwVersion, vr.ConnectMode, vr.Connected ? 1 : 0,
                    vr.Percent, d[10], mode == 0 ? "XOR-FF" : "明文");
                return true;
            }
            return false;
        }

        /// <summary>紧凑十六进制串，用于日志</summary>
        static string XorFFHex(byte[] b, int offset, int count)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < count && offset + i < b.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(((byte)(b[offset + i] ^ 0xFF)).ToString("X2"));
            }
            return sb.ToString();
        }

        // ---------- 厂商私有协议探测（17 字节命令，Pulsar/Kysona/VXE 系通用格式） ----------
        //
        // 命令帧：report ID 0x08 + 命令字 + 14 字节保留 + 校验和
        // 校验和 = (0x55 - 前 16 字节之和) & 0xFF
        // 命令 0x04 = 查询电量：响应通常为 [0x08, 0x04, 电量%, 充电标志, 电压mV(Low/High), ...]
        // 命令 0x03 = 查询在线状态

        static bool IsVendorTlc(HIDP_CAPS caps)
        {
            return caps.UsagePage >= 0xFF00 && caps.UsagePage <= 0xFFFF
                && ((caps.OutputReportByteLength >= 17 && caps.OutputReportByteLength <= 64)
                    || (caps.FeatureReportByteLength >= 17 && caps.FeatureReportByteLength <= 64));
        }

        static byte[] BuildVendorCommand(byte commandId)
        {
            byte[] b = new byte[17];
            b[0] = 0x08;
            b[1] = commandId;
            int sum = 0;
            for (int i = 0; i < 16; i++) sum += b[i];
            b[16] = (byte)((0x55 - sum) & 0xFF);
            return b;
        }

        static VendorResult ParseVendorResponse(byte[] rep)
        {
            VendorResult vr = new VendorResult();
            vr.Percent = -1;
            if (rep == null || rep.Length < 5) return vr;
            int pct = -1;
            bool chg = false;
            // 常见布局：[0]=0x08 报告ID, [1]=命令回显, [2]=电量, [3]=充电标志, [4..5]=电压mV
            if (rep[2] <= 100)
            {
                pct = rep[2];
                chg = rep[3] != 0;
            }
            else if (rep[1] <= 100 && rep[1] != 0x04 && rep[1] != 0x08)
            {
                // 少数固件不带命令回显：[1]=电量, [2]=充电标志
                pct = rep[1];
                chg = rep[2] != 0;
            }
            if (pct >= 0)
            {
                vr.Percent = pct;
                vr.Charging = chg;
            }
            return vr;
        }

        /// <summary>
        /// 对厂商定义的 TLC 发送 17 字节电量查询并等待响应（重叠 I/O，带超时）。
        /// 只发送只读查询命令，不支持该协议的设备会忽略/无响应，安全无害。
        /// </summary>
        static VendorResult ProbeVendorBattery(IntPtr hNormal, string path, HIDP_CAPS caps)
        {
            VendorResult vr = new VendorResult();
            vr.Percent = -1;

            // 用单独的重叠 I/O 句柄做写+读，避免阻塞轮询线程
            IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE) return vr;

            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, null);
            if (ev == IntPtr.Zero) { CloseHandle(h); return vr; }

            try
            {
                byte[] cmd = BuildVendorCommand(0x04);
                bool sent = false;

                if (caps.OutputReportByteLength >= 17)
                {
                    byte[] outb = new byte[Math.Max(17, (int)caps.OutputReportByteLength)];
                    Array.Copy(cmd, outb, 17);
                    sent = WriteFileWithTimeout(h, outb, ev, 500);
                }
                if (!sent && caps.FeatureReportByteLength >= 17)
                {
                    byte[] fb = new byte[Math.Max(17, (int)caps.FeatureReportByteLength)];
                    Array.Copy(cmd, fb, 17);
                    sent = HidD_SetFeature(h, fb, fb.Length);
                }
                if (!sent) return vr;

                byte[] resp = ReadFileWithTimeout(h, 17, ev, 600);
                if (resp == null) return vr;
                return ParseVendorResponse(resp);
            }
            finally
            {
                CloseHandle(ev);
                CloseHandle(h);
            }
        }

        /// <summary>
        /// 统一的重叠 I/O 封装：OVERLAPPED 放在非托管内存，超时后先 CancelIo
        /// 再用 bWait=true 等到内核真正收尾，之后才允许释放内存。
        /// </summary>
        static bool IoWithTimeout(IntPtr h, IntPtr pBuf, int len, bool isRead, IntPtr ev,
            int timeoutMs, out int transferred)
        {
            transferred = 0;
            int ovSize = Marshal.SizeOf(typeof(OVERLAPPED));
            IntPtr pOv = Marshal.AllocHGlobal(ovSize);
            try
            {
                for (int i = 0; i < ovSize; i++) Marshal.WriteByte(pOv, i, 0);   // 清零
                Marshal.WriteIntPtr(pOv, Marshal.OffsetOf(typeof(OVERLAPPED), "hEvent").ToInt32(), ev);

                int n = 0;
                bool ok;
                if (isRead) ok = ReadFile(h, pBuf, len, out n, pOv);
                else ok = WriteFile(h, pBuf, len, out n, pOv);

                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING) return false;
                    if (WaitForSingleObject(ev, timeoutMs) != WAIT_OBJECT_0)
                    {
                        CancelIo(h);
                        // 关键：必须等 I/O 真正结束，否则内核稍后会写进已释放的内存
                        GetOverlappedResult(h, pOv, out n, true);
                        return false;
                    }
                }
                if (!GetOverlappedResult(h, pOv, out n, true)) return false;
                transferred = n;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(pOv);
            }
        }

        static bool WriteFileWithTimeout(IntPtr h, byte[] buf, IntPtr ev, int timeoutMs)
        {
            GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                int n;
                return IoWithTimeout(h, pin.AddrOfPinnedObject(), buf.Length, false, ev, timeoutMs, out n);
            }
            finally
            {
                pin.Free();
            }
        }

        static byte[] ReadFileWithTimeout(IntPtr h, int bytesToRead, IntPtr ev, int timeoutMs)
        {
            byte[] buf = new byte[bytesToRead];
            GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                int n;
                ResetEvent(ev);
                if (!IoWithTimeout(h, pin.AddrOfPinnedObject(), buf.Length, true, ev, timeoutMs, out n))
                    return null;
                if (n < 5) return null;
                return buf;
            }
            finally
            {
                pin.Free();
            }
        }

        // ---------- 诊断报告导出（用于适配未识别的 2.4G 接收器） ----------

        /// <summary>
        /// 枚举所有 HID 设备，输出完整能力描述（TLC 用法 / 值能力 / 报告长度 / 特征报告内容），
        /// 供开发者在用户无法被识别的接收器上做协议逆向。
        /// </summary>
        public static string BuildDiagnosticsReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== MCHOSE A7 Pro 鼠标电量适配诊断报告 ===");
            sb.AppendLine("生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("提示: 请分别在 2.4G 模式下生成（如方便再补蓝牙/有线各一份），完整发回给开发者。");
            sb.AppendLine(new string('=', 60));

            Guid g = HidGuid;
            IntPtr hSet = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (hSet == IntPtr.Zero || hSet == INVALID_HANDLE_VALUE)
            {
                sb.AppendLine("枚举 HID 设备失败");
                return sb.ToString();
            }
            try
            {
                int index = 0;
                for (int i = 0; ; i++)
                {
                    SP_DEVICE_INTERFACE_DATA ifd = new SP_DEVICE_INTERFACE_DATA();
                    ifd.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(hSet, IntPtr.Zero, ref g, i, ref ifd))
                        break;

                    string path = GetInterfacePath(hSet, ref ifd);
                    if (string.IsNullOrEmpty(path)) continue;
                    index++;

                    sb.AppendLine();
                    sb.AppendLine("[设备 " + index + "]");
                    sb.AppendLine("路径: " + path);
                    DumpDevice(sb, path);
                }
                sb.AppendLine();
                sb.AppendLine("共 " + index + " 个 HID 设备接口");
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hSet);
            }
            return sb.ToString();
        }

        static void DumpDevice(StringBuilder sb, string path)
        {
            IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE)
            {
                h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            }
            if (h == INVALID_HANDLE_VALUE)
            {
                sb.AppendLine("打开失败，Win32 错误码: " + Marshal.GetLastWin32Error());
                return;
            }
            try
            {
                IntPtr ppd = IntPtr.Zero;
                if (!HidD_GetPreparsedData(h, out ppd) || ppd == IntPtr.Zero)
                {
                    sb.AppendLine("获取 PreparsedData 失败");
                    return;
                }
                try
                {
                    HIDP_CAPS caps;
                    if (HidP_GetCaps(ppd, out caps) != HIDP_STATUS_SUCCESS)
                    {
                        sb.AppendLine("获取 Caps 失败");
                        return;
                    }

                    HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
                    attr.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                    if (HidD_GetAttributes(h, ref attr))
                    {
                        sb.AppendLine(string.Format("VID/PID/版本: 0x{0:X4} / 0x{1:X4} / 0x{2:X4}",
                            attr.VendorID, attr.ProductID, attr.VersionNumber));
                    }

                    StringBuilder sbP = new StringBuilder(128);
                    if (HidD_GetProductString(h, sbP, sbP.Capacity * 2) && sbP.Length > 0)
                        sb.AppendLine("产品名: " + sbP.ToString());
                    StringBuilder sbM = new StringBuilder(128);
                    if (HidD_GetManufacturerString(h, sbM, sbM.Capacity * 2) && sbM.Length > 0)
                        sb.AppendLine("厂商: " + sbM.ToString());

                    sb.AppendLine(string.Format(
                        "TLC: UsagePage=0x{0:X4} Usage=0x{1:X4}  Input={2}B Output={3}B Feature={4}B",
                        caps.UsagePage, caps.Usage,
                        caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength));
                    sb.AppendLine(string.Format(
                        "值能力: 输入{0} 特征{1}", caps.NumberInputValueCaps, caps.NumberFeatureValueCaps));

                    HIDP_VALUE_CAPS[] vin = GetValueCaps(ppd, caps, HidP_Input);
                    if (vin != null) DumpValueCaps(sb, "输入", vin);
                    HIDP_VALUE_CAPS[] vfe = GetValueCaps(ppd, caps, HidP_Feature);
                    if (vfe != null) DumpValueCaps(sb, "特征", vfe);

                    // 特征报告探测：逐个 report ID 尝试 GET_REPORT，输出原始字节
                    if (caps.FeatureReportByteLength > 1)
                    {
                        sb.AppendLine("特征报告探测 (GET_REPORT):");
                        for (byte rid = 0; rid <= 12; rid++)
                        {
                            if (rid == 0 && caps.FeatureReportByteLength > 0)
                            {
                                // report ID 0 也探测
                            }
                            byte[] rep = new byte[caps.FeatureReportByteLength];
                            rep[0] = rid;
                            if (HidD_GetFeature(h, rep, rep.Length))
                            {
                                sb.AppendLine("  ReportID 0x" + rid.ToString("X2") + ": " + Hex(rep, 24));
                            }
                        }
                    }
                }
                finally
                {
                    HidD_FreePreparsedData(ppd);
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }

        static void DumpValueCaps(StringBuilder sb, string kind, HIDP_VALUE_CAPS[] caps)
        {
            foreach (HIDP_VALUE_CAPS c in caps)
            {
                string usage = c.IsRange != 0
                    ? string.Format("范围 0x{0:X4}-0x{1:X4}", c.Usage, c.UsageMax)
                    : string.Format("0x{0:X4}", c.Usage);
                sb.AppendLine(string.Format(
                    "  [{0}] ReportID=0x{1:X2} 用法页=0x{2:X4} 用法={3} 位宽={4} 逻辑=[{5}..{6}] LinkCollection={7}",
                    kind, c.ReportID, c.UsagePage, usage, c.BitSize, c.LogicalMin, c.LogicalMax, c.LinkCollection));
            }
        }

        static string Hex(byte[] data, int max)
        {
            StringBuilder sb = new StringBuilder();
            int n = Math.Min(data.Length, max);
            for (int i = 0; i < n; i++)
            {
                sb.Append(data[i].ToString("X2"));
                if (i < n - 1) sb.Append(' ');
            }
            if (data.Length > max) sb.Append(" ...");
            return sb.ToString();
        }

        // ---------- P/Invoke 声明 ----------

        [DllImport("hid.dll")]
        static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool HidD_GetPreparsedData(IntPtr hDevice, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        static extern int HidP_GetValueCaps(int reportType, IntPtr valueCaps, ref short valueCapsLength, IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
            out uint usageValue, IntPtr preparsedData, byte[] report, int reportLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool HidD_GetInputReport(IntPtr hDevice, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool HidD_GetFeature(IntPtr hDevice, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool HidD_GetProductString(IntPtr hDevice, [Out] StringBuilder buffer, int bufferLengthInBytes);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool HidD_GetAttributes(IntPtr hDevice, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool HidD_SetFeature(IntPtr hDevice, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool HidD_GetManufacturerString(IntPtr hDevice, [Out] StringBuilder buffer, int bufferLengthInBytes);

        [StructLayout(LayoutKind.Sequential)]
        struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint Offset;
            public uint OffsetHigh;
            public IntPtr hEvent;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ResetEvent(IntPtr hEvent);

        // 注意：异步 I/O 绝不能把托管 byte[] 或栈上的 OVERLAPPED 直接交给内核——
        // P/Invoke 只在本次调用期间固定缓冲区，I/O 挂起后 GC 可能移动数组、栈帧也会销毁，
        // 内核继续往旧地址写就是堆/栈破坏（STATUS_HEAP_CORRUPTION）。
        // 因此这里统一用 IntPtr 版本：缓冲区由调用方 GCHandle 固定，OVERLAPPED 分配在非托管内存。

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool WriteFile(IntPtr hFile, IntPtr lpBuffer, int nNumberOfBytesToWrite,
            out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ReadFile(IntPtr hFile, IntPtr lpBuffer, int nNumberOfBytesToRead,
            out int lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetOverlappedResult(IntPtr hFile, IntPtr lpOverlapped,
            out int lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CancelIo(IntPtr hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int WaitForSingleObject(IntPtr hHandle, int dwMilliseconds);

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        // HIDP_CAPS：注意 Usage 在 UsagePage 之前（与 hidpi.h 一致）
        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage;                    // 0
            public ushort UsagePage;                // 2
            public ushort InputReportByteLength;    // 4
            public ushort OutputReportByteLength;   // 6
            public ushort FeatureReportByteLength;  // 8
            public ushort Reserved1;                // 10-43（17 个 USHORT 预留）
            public ushort Reserved2;
            public ushort Reserved3;
            public ushort Reserved4;
            public ushort Reserved5;
            public ushort Reserved6;
            public ushort Reserved7;
            public ushort Reserved8;
            public ushort Reserved9;
            public ushort Reserved10;
            public ushort Reserved11;
            public ushort Reserved12;
            public ushort Reserved13;
            public ushort Reserved14;
            public ushort Reserved15;
            public ushort Reserved16;
            public ushort Reserved17;
            public ushort NumberLinkCollectionNodes;   // 44
            public ushort NumberInputButtonCaps;       // 46
            public ushort NumberInputValueCaps;        // 48
            public ushort NumberInputDataIndices;      // 50
            public ushort NumberOutputButtonCaps;      // 52
            public ushort NumberOutputValueCaps;       // 54
            public ushort NumberOutputDataIndices;     // 56
            public ushort NumberFeatureButtonCaps;     // 58
            public ushort NumberFeatureValueCaps;      // 60
            public ushort NumberFeatureDataIndices;    // 62
        }

        // HIDP_VALUE_CAPS：严格对照 hidpi.h，真实大小 = 72 字节（不是 64！）
        // 关键点是 ReportCount 之后还有 USHORT Reserved2[5]（10 字节），漏掉它会导致：
        //   1) 缓冲区按 64*n 分配、hid.dll 实际写 72*n → 堆溢出 → STATUS_HEAP_CORRUPTION(0xc0000374)
        //   2) UnitsExp / LogicalMin / LogicalMax / Usage 全部错位 → 永远读不到正确电量
        // 这里刻意不用 ByValArray 数组（marshaller 对齐有坑），改用 5 个独立 ushort 保证偏移精确。
        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;        // 0
            public byte ReportID;           // 2
            public byte IsAlias;            // 3
            public ushort BitField;         // 4
            public ushort LinkCollection;   // 6
            public ushort LinkUsage;        // 8
            public ushort LinkUsagePage;    // 10
            public byte IsRange;            // 12
            public byte IsStringRange;      // 13
            public byte IsDesignatorRange;  // 14
            public byte IsAbsolute;         // 15
            public byte HasNull;            // 16
            public byte Reserved;           // 17
            public ushort BitSize;          // 18
            public ushort ReportCount;      // 20
            public ushort Reserved2a;       // 22
            public ushort Reserved2b;       // 24
            public ushort Reserved2c;       // 26
            public ushort Reserved2d;       // 28
            public ushort Reserved2e;       // 30
            public uint UnitsExp;           // 32
            public uint Units;              // 36
            public int LogicalMin;          // 40
            public int LogicalMax;          // 44
            public int PhysicalMin;         // 48
            public int PhysicalMax;         // 52
            // 联合体部分（NotRange: Usage/... ；Range: UsageMin=Usage, UsageMax=UsageMax）
            public ushort Usage;            // 56
            public ushort UsageMax;         // 58（NotRange 模式下为保留字段）
            public ushort StringIndex;      // 60
            public ushort Reserved4;        // 62
            public ushort DesignatorIndex;  // 64
            public ushort Reserved5;        // 66
            public ushort DataIndex;        // 68
            public ushort Reserved6;        // 70
        }

        /// <summary>hidpi.h 中 sizeof(HIDP_VALUE_CAPS)。缓冲区一律按此常量分配，不依赖 Marshal.SizeOf。</summary>
        const int HIDP_VALUE_CAPS_SIZE = 72;

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr hDevInfo, IntPtr deviceInfoData,
            ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA interfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr hDevInfo, ref SP_DEVICE_INTERFACE_DATA interfaceData,
            IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize,
            out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr hDevInfo);

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);
    }
}
