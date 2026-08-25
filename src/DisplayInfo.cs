// The video mode Windows is driving a monitor with: resolution and refresh
// rate. None of this goes through the DDC/CI bus, so it is instant and safe to
// call from anywhere.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Deskside
{
    public sealed class DisplayMode
    {
        public string Device = "";
        public int Width, Height, Frequency;
        /// <summary>Highest rate Windows offers at this resolution.</summary>
        public int MaxFrequency;
        /// <summary>Every rate available at this resolution.</summary>
        public List<int> Rates = new List<int>();

        public bool Valid { get { return Width > 0 && Height > 0; } }

        public override string ToString()
        {
            if (!Valid) return "";
            string s = string.Format("{0}x{1} @ {2} Hz", Width, Height, Frequency);
            s += " " + (MaxFrequency > Frequency
                            ? L.F("(up to {0} Hz available)", MaxFrequency)
                            : L.T("(highest at this resolution)"));
            return s;
        }
    }

    public static class DisplayInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        const int ENUM_CURRENT_SETTINGS = -1;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevicesW(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int ChangeDisplaySettingsExW(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);

        const uint DM_PELSWIDTH = 0x00080000, DM_PELSHEIGHT = 0x00100000, DM_DISPLAYFREQUENCY = 0x00400000;
        const uint CDS_UPDATEREGISTRY = 0x00000001;

        /// <summary>
        /// PnP id of the monitor on that output, such as "LEN64BC". It tells one
        /// monitor from another and survives unplugging, which makes it a good
        /// key for a saved profile.
        /// </summary>
        public static string MonitorIdOf(IntPtr hMonitor)
        {
            string device = DeviceOf(hMonitor);
            if (string.IsNullOrEmpty(device)) return "";

            DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            if (!EnumDisplayDevicesW(device, 0, ref dd, 0)) return "";

            // "MONITOR\LEN64BC\{guid}\0004" -> "LEN64BC"
            string[] parts = (dd.DeviceID ?? "").Split('\\');
            return parts.Length >= 2 ? parts[1] : "";
        }

        static DEVMODE NewDevMode()
        {
            DEVMODE dm = new DEVMODE();
            dm.dmDeviceName = "";
            dm.dmFormName = "";
            dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
            return dm;
        }

        /// <summary>Device name ("\\.\DISPLAY2") of the given physical monitor.</summary>
        public static string DeviceOf(IntPtr hMonitor)
        {
            if (hMonitor == IntPtr.Zero) return null;
            MONITORINFOEX mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            mi.szDevice = "";
            return GetMonitorInfoW(hMonitor, ref mi) ? mi.szDevice : null;
        }

        /// <summary>
        /// Changes the refresh rate, keeping the resolution. This does not go
        /// through DDC/CI: the graphics card decides the signal and the monitor
        /// simply accepts it.
        /// </summary>
        public static string SetRefreshRate(string device, int width, int height, int hz)
        {
            DEVMODE dm = NewDevMode();
            dm.dmPelsWidth = (uint)width;
            dm.dmPelsHeight = (uint)height;
            dm.dmDisplayFrequency = (uint)hz;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

            int r = ChangeDisplaySettingsExW(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            switch (r)
            {
                case 0:  return null;                     // done
                case 1:  return L.T("a restart is required");
                case -1: return L.T("mode not supported");
                case -2: return L.T("the driver refused the change");
                default: return L.F("error {0}", r);
            }
        }

        public static DisplayMode ForMonitor(IntPtr hMonitor)
        {
            DisplayMode result = new DisplayMode();
            string device = DeviceOf(hMonitor);   // null means the current display
            result.Device = device ?? "";

            DEVMODE cur = NewDevMode();
            if (!EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref cur)) return result;

            result.Width = (int)cur.dmPelsWidth;
            result.Height = (int)cur.dmPelsHeight;
            result.Frequency = (int)cur.dmDisplayFrequency;
            result.MaxFrequency = result.Frequency;

            for (int i = 0; ; i++)
            {
                DEVMODE m = NewDevMode();
                if (!EnumDisplaySettingsW(device, i, ref m)) break;
                if (m.dmPelsWidth != cur.dmPelsWidth || m.dmPelsHeight != cur.dmPelsHeight) continue;
                if (m.dmBitsPerPel < 32) continue;
                int hz = (int)m.dmDisplayFrequency;
                if (hz <= 1) continue;                       // 0 and 1 mean "hardware default"
                if (!result.Rates.Contains(hz)) result.Rates.Add(hz);
                if (hz > result.MaxFrequency) result.MaxFrequency = hz;
            }
            result.Rates.Sort();
            return result;
        }
    }
}
