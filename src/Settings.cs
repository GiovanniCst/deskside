// Everything Deskside remembers, in one plain INI under %APPDATA%\Deskside.
//
//   [app]                     application preferences
//   [keyboards]               VID_xxxx&PID_xxxx = KLID
//   [<monitor PnP id>]        VCP code = value, the saved monitor profile
//
// A monitor profile is keyed by the monitor's PnP id, so a laptop that moves
// between desks finds the right settings on its own.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Deskside
{
    public sealed class Profile
    {
        public string Key = "";
        public string Name = "";
        public Dictionary<byte, int> Values = new Dictionary<byte, int>();
        /// <summary>Desktop orientation (see DisplayInfo.SetOrientation), -1 when not saved.</summary>
        public int Orientation = -1;

        public bool IsEmpty { get { return Values.Count == 0 && Orientation < 0; } }
    }

    public static class SettingsStore
    {
        const string AppSection = "app";
        const string KeyboardSection = "keyboards";

        public static string FilePath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);
            return Path.Combine(dir, "settings.ini");
        }

        // ------------------------------------------------------ raw access --
        /// <summary>Whole file as section -> ordered key/value pairs.</summary>
        static Dictionary<string, List<KeyValuePair<string, string>>> ReadAll()
        {
            Dictionary<string, List<KeyValuePair<string, string>>> all =
                new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            string path = FilePath();
            if (!File.Exists(path)) return all;

            string section = "";
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2);
                    if (!all.ContainsKey(section)) all[section] = new List<KeyValuePair<string, string>>();
                    continue;
                }
                if (section.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                all[section].Add(new KeyValuePair<string, string>(
                    line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim()));
            }
            return all;
        }

        static void WriteAll(Dictionary<string, List<KeyValuePair<string, string>>> all)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; " + AppInfo.Name + " settings. Safe to edit by hand.");
            sb.AppendLine("; Sections named after a monitor's PnP id hold VCP code = value pairs.");
            foreach (KeyValuePair<string, List<KeyValuePair<string, string>>> s in all)
            {
                if (s.Value.Count == 0) continue;
                sb.AppendLine();
                sb.AppendLine("[" + s.Key + "]");
                foreach (KeyValuePair<string, string> kv in s.Value)
                    sb.AppendLine(kv.Key + "=" + kv.Value);
            }
            string path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        static void Replace(Dictionary<string, List<KeyValuePair<string, string>>> all,
                            string section, List<KeyValuePair<string, string>> rows)
        {
            all[section] = rows;
        }

        // ------------------------------------------------- app preferences --
        public static bool GetBool(string key, bool fallback)
        {
            Dictionary<string, List<KeyValuePair<string, string>>> all = ReadAll();
            List<KeyValuePair<string, string>> rows;
            if (!all.TryGetValue(AppSection, out rows)) return fallback;
            foreach (KeyValuePair<string, string> kv in rows)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value == "1" || string.Equals(kv.Value, "true", StringComparison.OrdinalIgnoreCase);
            return fallback;
        }

        public static string GetString(string key, string fallback)
        {
            Dictionary<string, List<KeyValuePair<string, string>>> all = ReadAll();
            List<KeyValuePair<string, string>> rows;
            if (!all.TryGetValue(AppSection, out rows)) return fallback;
            foreach (KeyValuePair<string, string> kv in rows)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return fallback;
        }

        public static void SetString(string key, string value)
        {
            Dictionary<string, List<KeyValuePair<string, string>>> all = ReadAll();
            List<KeyValuePair<string, string>> rows;
            if (!all.TryGetValue(AppSection, out rows)) { rows = new List<KeyValuePair<string, string>>(); }
            rows.RemoveAll(delegate(KeyValuePair<string, string> kv)
            {
                return string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase);
            });
            rows.Add(new KeyValuePair<string, string>(key, value));
            Replace(all, AppSection, rows);
            WriteAll(all);
        }

        public static void SetBool(string key, bool value)
        {
            Dictionary<string, List<KeyValuePair<string, string>>> all = ReadAll();
            List<KeyValuePair<string, string>> rows;
            if (!all.TryGetValue(AppSection, out rows)) { rows = new List<KeyValuePair<string, string>>(); }
            rows.RemoveAll(delegate(KeyValuePair<string, string> kv)
            {
                return string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase);
            });
            rows.Add(new KeyValuePair<string, string>(key, value ? "1" : "0"));
            Replace(all, AppSection, rows);
            WriteAll(all);
        }

        // ---------------------------------------------- keyboard -> layout --
        public static Dictionary<string, string> KeyboardMap()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<KeyValuePair<string, string>>> all = ReadAll();
            List<KeyValuePair<string, string>> rows;
            if (!all.TryGetValue(KeyboardSection, out rows)) return map;
            foreach (KeyValuePair<string, string> kv in rows) map[kv.Key] = kv.Value;
            return map;
        }

        public static void SaveKeyboardMap(Dictionary<string, string> map)
        {
            Dictionary<string, List<KeyValuePair<string, string>>> all = ReadAll();
            List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> kv in map)
                rows.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
            Replace(all, KeyboardSection, rows);
            WriteAll(all);
        }

        // ------------------------------------------------ monitor profiles --
        public static Profile For(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            Dictionary<string, List<KeyValuePair<string, string>>> all = ReadAll();
            List<KeyValuePair<string, string>> rows;
            if (!all.TryGetValue(key, out rows)) return null;

            Profile p = new Profile();
            p.Key = key;
            foreach (KeyValuePair<string, string> kv in rows)
            {
                if (string.Equals(kv.Key, "name", StringComparison.OrdinalIgnoreCase)) { p.Name = kv.Value; continue; }
                if (string.Equals(kv.Key, "orientation", StringComparison.OrdinalIgnoreCase))
                {
                    int o;
                    if (int.TryParse(kv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out o) && o >= 0 && o <= 3)
                        p.Orientation = o;
                    continue;
                }
                if (!kv.Key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    byte code = Convert.ToByte(kv.Key.Substring(2), 16);
                    p.Values[code] = int.Parse(kv.Value, CultureInfo.InvariantCulture);
                }
                catch { /* a malformed line is not worth an error */ }
            }
            return p;
        }

        public static void Save(Profile profile)
        {
            if (profile == null || string.IsNullOrEmpty(profile.Key)) return;
            Dictionary<string, List<KeyValuePair<string, string>>> all = ReadAll();

            List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrEmpty(profile.Name))
                rows.Add(new KeyValuePair<string, string>("name", profile.Name));
            if (profile.Orientation >= 0)
                rows.Add(new KeyValuePair<string, string>("orientation",
                    profile.Orientation.ToString(CultureInfo.InvariantCulture)));
            foreach (KeyValuePair<byte, int> v in profile.Values)
                rows.Add(new KeyValuePair<string, string>(
                    string.Format("0x{0:X2}", v.Key), v.Value.ToString(CultureInfo.InvariantCulture)));

            Replace(all, profile.Key, rows);
            WriteAll(all);
        }
    }
}
