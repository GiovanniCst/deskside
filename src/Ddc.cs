// DDC/CI access to external monitors (dxva2.dll) plus the MCCS tables.
//
// Every operation on the bus costs roughly 60 ms, so nothing here is cached and
// nothing here is safe to call from the UI thread. DdcWorker owns this class
// and is the only caller.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Deskside
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    public struct VcpValue
    {
        public byte Code;
        public bool Ok;
        public int Current;
        public int Maximum;

        public static VcpValue Fail(byte code) { VcpValue v = new VcpValue(); v.Code = code; return v; }
    }

    public sealed class MonitorTarget
    {
        internal PHYSICAL_MONITOR Native;
        /// <summary>HMONITOR, needed to look up the Windows display mode.</summary>
        public IntPtr HMonitor;
        public DisplayMode Mode = new DisplayMode();
        /// <summary>PnP id such as "LEN64BC"; the key a saved profile is filed under.</summary>
        public string MonitorId = "";
        public IntPtr Handle { get { return Native.hPhysicalMonitor; } }
        public string Description { get { return Native.szPhysicalMonitorDescription; } }
        public string Capabilities = "";
        public string Model = "";

        public string Title { get { return string.IsNullOrEmpty(Model) ? Description : Model; } }
    }

    public static class Ddc
    {
        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("dxva2.dll")]
        static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, ref uint n);
        [DllImport("dxva2.dll")]
        static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] a);
        [DllImport("dxva2.dll")]
        static extern bool DestroyPhysicalMonitors(uint n, PHYSICAL_MONITOR[] a);
        [DllImport("dxva2.dll")]
        static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out uint type, out uint cur, out uint max);
        [DllImport("dxva2.dll")]
        static extern bool SetVCPFeature(IntPtr h, byte code, uint val);
        [DllImport("dxva2.dll")]
        static extern bool GetCapabilitiesStringLength(IntPtr h, out uint len);
        [DllImport("dxva2.dll")]
        static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, StringBuilder s, uint len);

        // The bus drops a packet now and then, so every operation gets retried.
        const int Retries = 3;
        const int RetryDelayMs = 40;

        public static List<MonitorTarget> Enumerate()
        {
            List<MonitorTarget> list = new List<MonitorTarget>();
            MonitorEnumProc cb = delegate(IntPtr hMon, IntPtr hdc, IntPtr rc, IntPtr data)
            {
                uint n = 0;
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, ref n) && n > 0)
                {
                    PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[n];
                    if (GetPhysicalMonitorsFromHMONITOR(hMon, n, arr))
                        foreach (PHYSICAL_MONITOR pm in arr)
                        {
                            MonitorTarget t = new MonitorTarget();
                            t.Native = pm;
                            t.HMonitor = hMon;
                            list.Add(t);
                        }
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return list;
        }

        public static void Release(IEnumerable<MonitorTarget> targets)
        {
            if (targets == null) return;
            foreach (MonitorTarget t in targets)
                DestroyPhysicalMonitors(1, new PHYSICAL_MONITOR[] { t.Native });
        }

        public static VcpValue Get(IntPtr h, byte code) { return Get(h, code, Retries); }

        /// <param name="retries">
        /// Pass 1 for a sweep across every code: on unsupported codes the extra
        /// attempts buy nothing and would triple the time.
        /// </param>
        public static VcpValue Get(IntPtr h, byte code, int retries)
        {
            uint type, cur, max;
            for (int i = 0; i < retries; i++)
            {
                if (i > 0) Thread.Sleep(RetryDelayMs);
                if (GetVCPFeatureAndVCPFeatureReply(h, code, out type, out cur, out max))
                {
                    VcpValue v = new VcpValue();
                    v.Code = code; v.Ok = true;
                    v.Current = (int)cur; v.Maximum = (int)max;
                    return v;
                }
            }
            return VcpValue.Fail(code);
        }

        public static bool Set(IntPtr h, byte code, int value)
        {
            for (int i = 0; i < Retries; i++)
            {
                if (i > 0) Thread.Sleep(RetryDelayMs);
                if (SetVCPFeature(h, code, (uint)value)) return true;
            }
            return false;
        }

        public static string ReadCapabilities(IntPtr h)
        {
            uint len;
            if (!GetCapabilitiesStringLength(h, out len) || len == 0) return "";
            StringBuilder sb = new StringBuilder((int)len);
            if (!CapabilitiesRequestAndCapabilitiesReply(h, sb, len)) return "";
            return sb.ToString();
        }
    }

    /// <summary>MCCS names and value meanings, so the UI can show words instead of numbers.</summary>
    public static class Vcp
    {
        public const byte Brightness = 0x10, Contrast = 0x12, ColorTemp = 0x0C, Volume = 0x62,
                          Mute = 0x8D, Input = 0x60, Preset = 0x14, Sharpness = 0x87,
                          RedGain = 0x16, GreenGain = 0x18, BlueGain = 0x1A,
                          // Black levels: standard MCCS, but monitors often omit
                          // them from the declared capability string.
                          RedBlack = 0x6C, GreenBlack = 0x6E, BlueBlack = 0x70,
                          Scaling = 0x86, OsdLanguage = 0xCC, Power = 0xD6,
                          // Lenovo vendor codes, identified by experiment: with
                          // DynamicContrast on, brightness and contrast are frozen.
                          OverDrive = 0xE0, DynamicContrast = 0xEA,
                          ResetAll = 0x04, ResetBrightness = 0x05, ResetColor = 0x08,
                          Firmware = 0xC9, VRefresh = 0xAE, Technology = 0xB6, SubPixel = 0xB2;

        static readonly Dictionary<byte, Dictionary<int, string>> ValueNames =
            new Dictionary<byte, Dictionary<int, string>>();
        static readonly Dictionary<byte, string> MccsNames = new Dictionary<byte, string>();

        static void Add(byte code, params object[] pairs)
        {
            Dictionary<int, string> d = new Dictionary<int, string>();
            for (int i = 0; i < pairs.Length; i += 2) d[(int)pairs[i]] = (string)pairs[i + 1];
            ValueNames[code] = d;
        }

        static void Name(byte code, string name) { MccsNames[code] = name; }

        static Vcp()
        {
            Add(Preset, 0x01, "sRGB", 0x04, "5000K", 0x05, "6500K", 0x06, "7500K",
                        0x08, "9300K", 0x0B, "11500K", 0x0C, "user");
            Add(Input, 0x01, "VGA 1", 0x03, "DVI 1", 0x0F, "DisplayPort 1", 0x10, "DisplayPort 2",
                       0x11, "HDMI 1", 0x12, "HDMI 2", 0x1B, "USB-C");
            Add(Scaling, 0x01, "no scaling", 0x02, "fit (keep aspect)", 0x03, "fill (stretch)",
                         0x04, "fit width", 0x05, "fit height");
            Add(Mute, 0x01, "muted", 0x02, "unmuted");
            Add(SubPixel, 0x01, "RGB vertical", 0x02, "RGB horizontal", 0x03, "BGR vertical", 0x04, "BGR horizontal");
            Add(Technology, 0x01, "CRT", 0x03, "LCD TFT", 0x04, "passive LCD", 0x05, "plasma");
            Add(OsdLanguage, 0x01, "Chinese", 0x02, "English", 0x03, "French", 0x04, "German",
                             0x05, "Italian", 0x06, "Japanese", 0x09, "Russian", 0x0A, "Spanish",
                             0x0D, "Portuguese", 0x23, "Dutch", 0x24, "Korean");
            Add(Power, 0x01, "on", 0x02, "standby", 0x03, "suspend", 0x04, "off", 0x05, "hard off");
            Add(OverDrive, 0x00, "off", 0x01, "normal", 0x02, "max");
            Add(DynamicContrast, 0x00, "off", 0x01, "on");

            Name(0x02, "new control value");       Name(0x04, "restore factory defaults");
            Name(0x05, "restore brightness/contrast"); Name(0x06, "restore geometry");
            Name(0x08, "restore colour");          Name(0x0B, "colour temperature increment");
            Name(0x0C, "colour temperature");      Name(0x0E, "clock (analogue)");
            Name(0x10, "brightness");              Name(0x12, "contrast");
            Name(0x13, "backlight");               Name(0x14, "colour preset");
            Name(0x16, "red gain");                Name(0x18, "green gain");
            Name(0x1A, "blue gain");               Name(0x1E, "auto setup");
            Name(0x1F, "auto colour setup");       Name(0x20, "horizontal position");
            Name(0x22, "horizontal size");         Name(0x30, "vertical position");
            Name(0x32, "vertical size");           Name(0x3E, "clock phase");
            Name(0x52, "active control");          Name(0x54, "response time");
            Name(0x60, "input source");            Name(0x62, "audio volume");
            Name(0x66, "ambient light");           Name(0x6C, "red black level");
            Name(0x6E, "green black level");       Name(0x70, "blue black level");
            Name(0x72, "gamma");                   Name(0x86, "display scaling");
            Name(0x87, "sharpness");               Name(0x8D, "audio mute");
            Name(0x8F, "audio max volume");        Name(0x90, "bass");
            Name(0x94, "audio source");            Name(0xAC, "horizontal frequency");
            Name(0xAE, "vertical frequency");      Name(0xB0, "store settings");
            Name(0xB2, "sub-pixel layout");        Name(0xB6, "display technology");
            Name(0xC0, "usage hours");             Name(0xC6, "application enable key");
            Name(0xC8, "controller id");           Name(0xC9, "firmware level");
            Name(0xCA, "OSD control");             Name(0xCC, "OSD language");
            Name(0xD6, "power mode");              Name(0xDC, "display application");
            Name(0xDF, "MCCS version");            Name(0xE3, "PIP / PBP");
            Name(0xF6, "PIP source");
        }

        /// <summary>Standard MCCS name of a code, used by the full sweep.</summary>
        public static string MccsName(byte code)
        {
            string s;
            return MccsNames.TryGetValue(code, out s) ? s : "";
        }

        /// <summary>
        /// Known values for a multiple-choice code, used when the monitor does
        /// not list them in its capability string (mute, for one, rarely is).
        /// </summary>
        public static List<KeyValuePair<int, string>> Known(byte code)
        {
            List<KeyValuePair<int, string>> list = new List<KeyValuePair<int, string>>();
            Dictionary<int, string> d;
            if (!ValueNames.TryGetValue(code, out d)) return list;
            List<int> keys = new List<int>(d.Keys);
            keys.Sort();
            foreach (int k in keys) list.Add(new KeyValuePair<int, string>(k, d[k]));
            return list;
        }

        public static string ValueName(byte code, int value)
        {
            Dictionary<int, string> d;
            if (ValueNames.TryGetValue(code, out d))
            {
                string s;
                if (d.TryGetValue(value, out s)) return s;
            }
            switch (code)
            {
                case ColorTemp: return (3000 + 50 * value) + "K";
                case VRefresh:  return (value / 100.0).ToString("0.00") + " Hz";
                case Firmware:  return "v" + (value >> 8) + "." + (value & 0xFF);
            }
            return "0x" + value.ToString("X2");
        }

        /// <summary>Extracts "key(...)" honouring nested parentheses.</summary>
        public static string Section(string caps, string key)
        {
            if (string.IsNullOrEmpty(caps)) return "";
            int start = caps.IndexOf(key + "(", StringComparison.Ordinal);
            if (start < 0) return "";
            int i = start + key.Length + 1, depth = 1;
            StringBuilder sb = new StringBuilder();
            while (i < caps.Length && depth > 0)
            {
                char c = caps[i];
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0) break; }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>Codes the monitor declares, with the allowed values where it lists them.</summary>
        public static Dictionary<byte, List<int>> ParseVcp(string caps)
        {
            Dictionary<byte, List<int>> result = new Dictionary<byte, List<int>>();
            string inner = Section(caps, "vcp");
            int i = 0;
            while (i < inner.Length)
            {
                while (i < inner.Length && !IsHex(inner[i])) i++;
                if (i + 1 >= inner.Length) break;
                if (!IsHex(inner[i + 1])) { i++; continue; }

                byte code = Convert.ToByte(inner.Substring(i, 2), 16);
                i += 2;
                List<int> values = new List<int>();
                while (i < inner.Length && inner[i] == ' ') i++;
                if (i < inner.Length && inner[i] == '(')
                {
                    int close = inner.IndexOf(')', i);
                    if (close < 0) close = inner.Length - 1;
                    foreach (string tok in inner.Substring(i + 1, close - i - 1)
                                                .Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        if (tok.Length == 2 && IsHex(tok[0]) && IsHex(tok[1]))
                            values.Add(Convert.ToInt32(tok, 16));
                    i = close + 1;
                }
                result[code] = values;
            }
            return result;
        }

        static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }
    }
}
