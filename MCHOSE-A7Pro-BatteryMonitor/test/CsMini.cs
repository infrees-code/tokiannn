using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MouseBattery
{
    /// <summary>
    /// Minimal standalone probe: only the A7 conversation, no enumeration, no HidP_*.
    /// Isolates the .NET/P-Invoke layer from the app's logic.
    /// </summary>
    internal static class CsMini
    {
        const uint GENERIC_READ = 0x80000000u, GENERIC_WRITE = 0x40000000u;
        const uint SHARE_RW = 3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000u;
        static readonly IntPtr INVALID = new IntPtr(-1);

        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_SetFeature(IntPtr h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFileW(string p, uint acc, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("hid.dll")]
        static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, uint f);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr hs, IntPtr di, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr hs, ref SP_DEVICE_INTERFACE_DATA d, IntPtr buf, int sz, out int need, IntPtr di);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr hs);

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        static void Main(string[] args)
        {
            StringBuilder log = new StringBuilder();
            Guid g;
            HidD_GetHidGuid(out g);
            IntPtr hs = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
            string target = null;
            for (uint i = 0; ; i++)
            {
                SP_DEVICE_INTERFACE_DATA d = new SP_DEVICE_INTERFACE_DATA();
                d.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                if (!SetupDiEnumDeviceInterfaces(hs, IntPtr.Zero, ref g, i, ref d)) break;
                int need;
                SetupDiGetDeviceInterfaceDetail(hs, ref d, IntPtr.Zero, 0, out need, IntPtr.Zero);
                if (need <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(need + 8);
                try
                {
                    for (int k = 0; k < need + 8; k++) Marshal.WriteByte(buf, k, 0);
                    Marshal.WriteInt32(buf, 0, 8);
                    int n2;
                    if (!SetupDiGetDeviceInterfaceDetail(hs, ref d, buf, need, out n2, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    if (path == null) continue;
                    if (path.ToLowerInvariant().Contains("vid_5253"))
                    {
                        log.AppendLine("  cand: " + path);
                        if (path.Contains("mi_02&col02")) target = path;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            SetupDiDestroyDeviceInfoList(hs);
            if (target == null) { log.AppendLine("no mi_02&col02 target"); Write(log); return; }
            log.AppendLine("target: " + target);

            foreach (uint flags in new uint[] { FILE_FLAG_OVERLAPPED, 0 })
            {
                log.AppendLine();
                log.AppendLine("### flags=0x" + flags.ToString("X") + " ###");
                log.AppendLine("  IntPtr.Size=" + IntPtr.Size + "  Is64BitProcess=" + Environment.Is64BitProcess
                    + "  Is64BitOS=" + Environment.Is64BitOperatingSystem);
                IntPtr h = CreateFileW(target, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
                log.AppendLine("  CreateFile -> " + h + " err=" + Marshal.GetLastWin32Error());
                if (h == INVALID) continue;
                HIDD_ATTRIBUTES a = new HIDD_ATTRIBUTES(); a.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                bool ga = HidD_GetAttributes(h, ref a);
                log.AppendLine("  attributes ok=" + ga + " vid=0x" + a.VendorID.ToString("X4") + " pid=0x" + a.ProductID.ToString("X4"));
                try
                {
                    const int LEN = 65;

                    // ---- baseline: is there already an answer in the mailbox? ----
                    log.AppendLine("  --- baseline reads (before any write) ---");
                    for (int k = 0; k < 3; k++) log.AppendLine("    " + Dump(h, LEN));

                    // ---- send cmd 0x04 (getVersion, parser LXe) ----
                    log.AppendLine("  --- send cmd 0x04 (getVersion) ---");
                    log.AppendLine("    SetFeature -> " + Write(h, LEN, 0x04) + " err=" + Marshal.GetLastWin32Error());
                    System.Threading.Thread.Sleep(400);
                    for (int k = 0; k < 4; k++) log.AppendLine("    " + Dump(h, LEN));

                    // ---- send cmd 0x06 (device info + battery) ----
                    log.AppendLine("  --- send cmd 0x06 (device info + battery) ---");
                    log.AppendLine("    SetFeature -> " + Write(h, LEN, 0x06) + " err=" + Marshal.GetLastWin32Error());
                    System.Threading.Thread.Sleep(400);
                    for (int k = 0; k < 4; k++) log.AppendLine("    " + Dump(h, LEN));
                }
                finally { CloseHandle(h); }
            }
            Write(log);
        }

        static bool Write(IntPtr h, int LEN, byte cmd)
        {
            byte[] frame = new byte[LEN];
            for (int i = 0; i < LEN; i++) frame[i] = 0xFF;
            frame[0] = 0x11;
            frame[1] = (byte)(cmd ^ 0xFF);
            for (int i = 0; i < 19; i++) frame[2 + i] = (byte)(0x00 ^ 0xFF);
            return HidD_SetFeature(h, frame, LEN);
        }

        static string Dump(IntPtr h, int LEN)
        {
            byte[] r = new byte[LEN];
            r[0] = 0x11;
            if (!HidD_GetFeature(h, r, LEN)) return "GetFeature FAILED err=" + Marshal.GetLastWin32Error();
            byte[] x = new byte[LEN - 2];
            for (int i = 0; i < LEN - 2; i++) x[i] = (byte)(r[i + 2] ^ 0xFF);
            string extra = "";
            if (x.Length >= 11 && (x[0] | (x[1] << 8)) == 0x5253)
                extra = string.Format("  *** BATTERY vid={0} pid={1} fw={2} bits=0x{3:X2} batt={4}%% charge={5}",
                    x[0] | (x[1] << 8), x[2] | (x[3] << 8), x[4] | (x[5] << 8) | (x[6] << 16) | (x[7] << 24), x[8], x[9], x[10]);
            else if (x.Length > 2 && x[0] == 0x04)
                extra = string.Format("  *** VERSION len={0} '{1}'", x[1], Asc(x, 2, x[1]));
            return Hex(r, 16) + extra;
        }

        static string Asc(byte[] b, int off, int len)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < len && off + i < b.Length; i++)
                sb.Append(b[off + i] >= 32 && b[off + i] < 127 ? (char)b[off + i] : '.');
            return sb.ToString();
        }

        static string Hex(byte[] b, int n)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n && i < b.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(b[i].ToString("X2")); }
            return sb.ToString();
        }

        static void Write(StringBuilder sb)
        {
            Console.Write(sb.ToString());
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mini_out.txt"),
                sb.ToString(), Encoding.UTF8);
        }
    }
}
