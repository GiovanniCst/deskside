// Connected keyboards and input layouts.
//
// Worth knowing: a USB keyboard does not tell Windows which layout it has. The
// HID descriptor's bCountryCode field exists for exactly that, but it reads 0
// ("not localised") on essentially every keyboard, and Windows does not surface
// it anywhere regardless. tools/Get-HidCountryCode.ps1 reads it straight off the
// hardware if you want to check your own. So the layout cannot be deduced: it
// is mapped once against the VID/PID and remembered.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Deskside
{
    public sealed class KeyboardDevice
    {
        /// <summary>"VID_2F68&amp;PID_0082": identifies the model, not the individual unit.</summary>
        public string Key = "";
        public string Path = "";
        public string Name = "";
        /// <summary>false for a laptop's built-in keyboard (PS/2, ACPI).</summary>
        public bool IsUsb;
    }

    public static class Keyboards
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        const uint RIM_TYPEKEYBOARD = 1;
        const uint RIDI_DEVICENAME = 0x20000007;

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, StringBuilder data, ref uint size);

        static readonly Regex VidPid = new Regex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);

        /// <summary>Keyboards physically present, one entry per model.</summary>
        public static List<KeyboardDevice> List()
        {
            List<KeyboardDevice> result = new List<KeyboardDevice>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            uint count = 0;
            uint size = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            if (GetRawInputDeviceList(null, ref count, size) == unchecked((uint)-1) || count == 0) return result;

            RAWINPUTDEVICELIST[] list = new RAWINPUTDEVICELIST[count];
            if (GetRawInputDeviceList(list, ref count, size) == unchecked((uint)-1)) return result;

            foreach (RAWINPUTDEVICELIST d in list)
            {
                if (d.dwType != RIM_TYPEKEYBOARD) continue;

                uint len = 0;
                GetRawInputDeviceInfoW(d.hDevice, RIDI_DEVICENAME, null, ref len);
                if (len == 0) continue;
                StringBuilder sb = new StringBuilder((int)len + 2);
                if (GetRawInputDeviceInfoW(d.hDevice, RIDI_DEVICENAME, sb, ref len) == unchecked((uint)-1)) continue;

                string path = sb.ToString();
                // Remote Desktop's phantom devices are not real keyboards
                if (path.IndexOf("RDP_", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                Match m = VidPid.Match(path);
                KeyboardDevice k = new KeyboardDevice();
                k.Path = path;
                k.IsUsb = m.Success;
                k.Key = m.Success
                    ? ("VID_" + m.Groups[1].Value.ToUpperInvariant() + "&PID_" + m.Groups[2].Value.ToUpperInvariant())
                    : "BUILTIN";
                if (seen.ContainsKey(k.Key)) continue;
                seen[k.Key] = true;

                k.Name = NameOf(path, k);
                result.Add(k);
            }
            return result;
        }

        /// <summary>Readable name, looked up in the registry from the device path.</summary>
        static string NameOf(string path, KeyboardDevice k)
        {
            try
            {
                // "\\?\HID#VID_2F68&PID_0082&MI_00#7&2417bb84&1&0000#{guid}"
                string p = path.TrimStart('\\', '?').TrimStart('\\');
                if (p.IndexOf('#') > 0)
                {
                    string[] parts = p.Split('#');
                    if (parts.Length >= 3)
                    {
                        string sub = parts[0] + "\\" + parts[1] + "\\" + parts[2];
                        using (RegistryKey reg = Registry.LocalMachine.OpenSubKey(
                                   @"SYSTEM\CurrentControlSet\Enum\" + sub))
                        {
                            if (reg != null)
                            {
                                object v = reg.GetValue("DeviceDesc");
                                if (v != null)
                                {
                                    string s = v.ToString();
                                    int semi = s.LastIndexOf(';');
                                    if (semi >= 0) s = s.Substring(semi + 1);
                                    return s;
                                }
                            }
                        }
                    }
                }
            }
            catch { /* the name is a nicety; the key is what matters */ }
            return k.IsUsb ? k.Key : L.T("Built-in keyboard");
        }
    }

    public static class Layouts
    {
        const uint KLF_ACTIVATE = 0x00000001;
        const uint KLF_SETFORPROCESS = 0x00000100;
        const int WM_INPUTLANGCHANGEREQUEST = 0x0050;
        const int INPUTLANGCHANGE_SYSCHARSET = 0x0001;
        const uint SPI_SETDEFAULTINPUTLANG = 0x005A;
        const uint SPIF_SENDCHANGE = 0x02;
        static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr LoadKeyboardLayoutW(string klid, uint flags);
        [DllImport("user32.dll")]
        static extern IntPtr GetKeyboardLayout(uint threadId);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
        [DllImport("user32.dll")]
        static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool SystemParametersInfoW(uint action, uint param, ref IntPtr data, uint winIni);

        /// <summary>Layout active in the foreground window, as a KLID ("00000409").</summary>
        public static string Current()
        {
            IntPtr hwnd = GetForegroundWindow();
            uint tid = hwnd == IntPtr.Zero ? 0 : GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            return KlidOf(GetKeyboardLayout(tid));
        }

        /// <summary>
        /// An HKL holds the language in its low word and the layout in the high
        /// word. When the high word is zero or equal to the language, the KLID
        /// is just the language padded to eight hex digits.
        /// </summary>
        public static string KlidOf(IntPtr hkl)
        {
            int v = hkl.ToInt32();
            int lang = v & 0xFFFF;
            int layout = (v >> 16) & 0xFFFF;
            if (layout == 0 || layout == lang) return lang.ToString("X8");
            if ((layout & 0xF000) == 0xF000) return lang.ToString("X8");   // substituted layout
            return layout.ToString("X8");
        }

        /// <summary>Applies the layout session-wide. true if it ended up active.</summary>
        public static bool Apply(string klid)
        {
            IntPtr hkl = LoadKeyboardLayoutW(klid, KLF_ACTIVATE | KLF_SETFORPROCESS);
            if (hkl == IntPtr.Zero) return false;

            // every window keeps its own layout, so every window has to be asked
            PostMessageW(HWND_BROADCAST, WM_INPUTLANGCHANGEREQUEST,
                         new IntPtr(INPUTLANGCHANGE_SYSCHARSET), hkl);
            // and the default moves too, for processes started later
            IntPtr copy = hkl;
            SystemParametersInfoW(SPI_SETDEFAULTINPUTLANG, 0, ref copy, SPIF_SENDCHANGE);
            return true;
        }

        /// <summary>Layouts present in the user's input list, KLID -> name.</summary>
        public static List<KeyValuePair<string, string>> Installed()
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            using (RegistryKey pre = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload"))
            {
                if (pre == null) return result;
                List<string> names = new List<string>(pre.GetValueNames());
                names.Sort();
                foreach (string n in names)
                {
                    object v = pre.GetValue(n);
                    if (v == null) continue;
                    string klid = v.ToString();
                    result.Add(new KeyValuePair<string, string>(klid, NameOf(klid)));
                }
            }
            return result;
        }

        public static string NameOf(string klid)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                           @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\" + klid))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("Layout Display Name") ?? k.GetValue("Layout Text");
                        if (v != null)
                        {
                            string s = v.ToString();
                            // "Layout Display Name" can be a resource reference
                            // ("@dll,-id"); fall back to the plain text in that case
                            if (s.StartsWith("@"))
                            {
                                object t = k.GetValue("Layout Text");
                                if (t != null) s = t.ToString();
                            }
                            return s;
                        }
                    }
                }
            }
            catch { }
            return klid;
        }
    }
}
