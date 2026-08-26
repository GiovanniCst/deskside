// Tray icon, control panel, on-screen feedback and global hotkeys.
//
// No DDC call happens on this thread: everything goes through DdcWorker. The UI
// shows the last known value straight away and corrects itself when the
// monitor's answer arrives.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Deskside
{
    /// <summary>A control the monitor actually exposes.</summary>
    public sealed class FeatureDef
    {
        public byte Code;
        public string Label;
        public bool IsChoice;
        public int Maximum;
        public List<KeyValuePair<int, string>> Choices = new List<KeyValuePair<int, string>>();
    }

    /// <summary>Small confirmation window that never steals focus.</summary>
    public sealed class OsdForm : Form
    {
        readonly Label _label = new Label();
        readonly Timer _hide = new Timer();

        public OsdForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(24, 24, 27);
            Opacity = 0.96;   // low enough to feel like an overlay, high
                              // enough that dark text behind does not bleed through
            Size = new Size(340, 76);

            _label.Dock = DockStyle.Fill;
            _label.TextAlign = ContentAlignment.MiddleCenter;
            _label.ForeColor = Color.White;
            _label.Font = new Font("Segoe UI", 11.5f);
            Controls.Add(_label);

            _hide.Interval = 1500;
            _hide.Tick += delegate { _hide.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
                return cp;
            }
        }

        public void Flash(string text)
        {
            _label.Text = text;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + wa.Height - Height - 90);
            Show();
            _hide.Stop();
            _hide.Start();
        }
    }

    /// <summary>
    /// Hidden window: receives WM_HOTKEY for the global shortcuts and
    /// WM_DEVICECHANGE when hardware comes and goes.
    /// </summary>
    public sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        const int WM_HOTKEY = 0x0312;
        const int WM_DEVICECHANGE = 0x0219;
        const int DBT_DEVNODES_CHANGED = 0x0007;
        const int DBT_DEVICEARRIVAL = 0x8000;
        const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        const uint MOD_NOREPEAT = 0x4000;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        readonly Dictionary<int, Action> _actions = new Dictionary<int, Action>();
        int _next = 1;

        /// <summary>A device appeared or disappeared.</summary>
        public Action DevicesChanged;

        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        /// <summary>Registers a shortcut; false if something else already owns it.</summary>
        public bool Register(uint mods, uint vk, Action action)
        {
            int id = _next++;
            if (!RegisterHotKey(Handle, id, mods | MOD_NOREPEAT, vk)) return false;
            _actions[id] = action;
            return true;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                Action a;
                if (_actions.TryGetValue(m.WParam.ToInt32(), out a)) { a(); return; }
            }
            else if (m.Msg == WM_DEVICECHANGE && DevicesChanged != null)
            {
                int ev = m.WParam.ToInt32();
                if (ev == DBT_DEVNODES_CHANGED || ev == DBT_DEVICEARRIVAL || ev == DBT_DEVICEREMOVECOMPLETE)
                    DevicesChanged();
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            for (int i = 1; i < _next; i++) UnregisterHotKey(Handle, i);
            DestroyHandle();
        }
    }

    public sealed class TrayApp : ApplicationContext
    {
        // ---- candidate controls, in the order they appear in the panel ----
        static readonly byte[] SliderCandidates = {
            Vcp.Brightness, Vcp.Contrast, Vcp.Volume, Vcp.ColorTemp, Vcp.Sharpness,
            Vcp.RedGain, Vcp.GreenGain, Vcp.BlueGain,
            Vcp.RedBlack, Vcp.GreenBlack, Vcp.BlueBlack
        };
        static readonly byte[] ChoiceCandidates = {
            Vcp.Input, Vcp.Preset, Vcp.DellPreset, Vcp.DynamicContrast, Vcp.OverDrive,
            Vcp.Scaling, Vcp.Mute, Vcp.OsdLanguage
        };
        // 0xE0 and 0xEA are not standard, they are vendor codes. On a Lenovo they
        // mean Over Drive and Dynamic Contrast (verified by experiment); on
        // another brand the same numbers could mean anything, so they are only
        // offered on Lenovo panels.
        static readonly byte[] LenovoOnly = { Vcp.OverDrive, Vcp.DynamicContrast };
        // 0xE2 is Dell's: it reports the OSD preset mode. Offered on Dell panels only.
        static readonly byte[] DellOnly = { Vcp.DellPreset };

        /// <summary>
        /// How a Dell preset is set. 0xE2 only reports; writing it is accepted
        /// and ignored. Found by sniffing Dell Display and Peripheral Manager
        /// (tools\Sniff-Ddc.py): Standard and Game 1 go through the standard
        /// Display Application code, the game modes and ComfortView through
        /// vendor 0xF0, the colour ones through the standard colour preset.
        /// Rows: { value 0xE2 reports, code to write, value to write }.
        /// </summary>
        static readonly int[,] DellPresetWrite = {
            { 0x00, 0xDC, 0x00 },   // Standard
            { 0x04, 0xDC, 0x05 },   // Game 1
            { 0x1E, 0xF0, 0x0D },   // Game 2
            { 0x1F, 0xF0, 0x0E },   // Game 3
            { 0x1D, 0xF0, 0x0C },   // ComfortView
            { 0x22, 0xF0, 0x11 },   // FPS
            { 0x21, 0xF0, 0x10 },   // RPG
            { 0x20, 0xF0, 0x0F },   // RTS
            { 0x12, 0x14, 0x08 },   // Cool
            { 0x0E, 0x14, 0x0B },   // Warm
            { 0x14, 0x14, 0x0C },   // Custom Color
        };

        static string LabelOf(byte code) { return L.T(LabelEn(code)); }

        /// <summary>English label, which doubles as the translation key.</summary>
        static string LabelEn(byte code)
        {
            switch (code)
            {
                case Vcp.Brightness: return "Brightness";
                case Vcp.Contrast: return "Contrast";
                case Vcp.Volume: return "Volume";
                case Vcp.ColorTemp: return "Colour temp.";
                case Vcp.Sharpness: return "Sharpness";
                case Vcp.RedGain: return "Red gain";
                case Vcp.GreenGain: return "Green gain";
                case Vcp.BlueGain: return "Blue gain";
                case Vcp.RedBlack: return "Red black";
                case Vcp.GreenBlack: return "Green black";
                case Vcp.BlueBlack: return "Blue black";
                case Vcp.Input: return "Input";
                case Vcp.Preset: return "Colour preset";
                case Vcp.DellPreset: return "Preset";
                case Vcp.Scaling: return "Scaling";
                case Vcp.Mute: return "Audio";
                case Vcp.OsdLanguage: return "OSD language";
                case Vcp.OverDrive: return "Over Drive";
                case Vcp.DynamicContrast: return "Dynamic Contrast";
                default: return "0x" + code.ToString("X2");
            }
        }

        const int Step = 5;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = AppInfo.Name;

        readonly Control _marshaller = new Control();   // only so the worker can BeginInvoke
        readonly DdcWorker _worker;
        readonly NotifyIcon _tray = new NotifyIcon();
        readonly OsdForm _osd = new OsdForm();
        readonly Form _panel = new Form();
        readonly HotkeyWindow _hotkeys = new HotkeyWindow();
        readonly Timer _verify = new Timer();
        readonly Icon _icon;

        readonly List<FeatureDef> _features = new List<FeatureDef>();
        readonly Dictionary<byte, int> _cache = new Dictionary<byte, int>();
        readonly Dictionary<byte, TrackBar> _bars = new Dictionary<byte, TrackBar>();
        readonly Dictionary<byte, Label> _barValues = new Dictionary<byte, Label>();
        readonly Dictionary<byte, ComboBox> _combos = new Dictionary<byte, ComboBox>();
        readonly HashSet<byte> _dirty = new HashSet<byte>();

        Label _hint;
        Label _header;
        bool _isLenovo;
        bool _isDell;
        bool _echoes;                  // the monitor answers codes it does not have
        bool _loading;
        string _title = "Monitor";
        string _mode = "";
        DisplayMode _modeInfo = new DisplayMode();
        string _monitorKey = "";
        string _appliedKey = "";        // profile already applied for this monitor
        DateTime _panelHiddenAt = DateTime.MinValue;
        DateTime _panelShownAt = DateTime.MinValue;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        readonly Timer _displayChanged = new Timer();
        readonly List<string[]> _hotkeyDocs = new List<string[]>();   // keys, description, taken

        // ---- keyboard ----
        readonly Timer _devicesChanged = new Timer();
        readonly Timer _layoutGuard = new Timer();
        Dictionary<string, string> _kbMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<KeyboardDevice> _keyboards = new List<KeyboardDevice>();
        KeyboardDevice _activeKb;       // the external keyboard that has a mapped layout
        string _wantedKlid = "";
        bool _keepLayout;

        public TrayApp()
        {
            L.Load(SettingsStore.GetString("language", L.Auto));
            L.Changed = delegate
            {
                // labels live inside the controls, so the panel is rebuilt; the
                // menu rebuilds itself every time it opens
                BuildPanel();
                _osd.Flash(AppInfo.Name);
            };
            _keepLayout = SettingsStore.GetBool("keepLayout", true);
            _icon = LoadIcon();
            _worker = new DdcWorker(_marshaller);

            _tray.Icon = _icon;
            _tray.Text = AppInfo.Name;
            _tray.Visible = true;
            _tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) TogglePanel();
            };
            _tray.ContextMenuStrip = new ContextMenuStrip();
            _tray.ContextMenuStrip.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                BuildMenu();
                RefreshValues();     // the menu opens at once, values catch up after
            };

            _panel.Text = AppInfo.Name;
            _panel.Icon = _icon;
            _panel.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            _panel.StartPosition = FormStartPosition.Manual;
            _panel.ShowInTaskbar = false;
            _panel.TopMost = true;
            _panel.KeyPreview = true;
            _panel.ClientSize = new Size(340, 60);
            _panel.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) HidePanel();
            };
            _panel.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HidePanel(); }
            };
            // Closes itself when you click elsewhere. The OSD does not disturb
            // it: being WS_EX_NOACTIVATE, showing it does not take the focus.
            _panel.Deactivate += delegate
            {
                // Opened from a global shortcut, the panel is not always
                // granted the foreground straight away, and the deactivation
                // that follows would close it the instant it appeared.
                if ((DateTime.UtcNow - _panelShownAt).TotalMilliseconds < 600) return;
                HidePanel();
            };

            // Deferred verification: some time after the last change, re-read
            // what was touched, so a value the monitor ignored gets noticed.
            _verify.Interval = 700;
            _verify.Tick += delegate { _verify.Stop(); VerifyDirty(); };

            // Plugging or unplugging a monitor changes the desktop settings:
            // that is the signal, and it costs no polling at all.
            _displayChanged.Interval = 2500;   // Windows sends bursts of these; wait for calm
            _displayChanged.Tick += delegate { _displayChanged.Stop(); Discover(); };
            SystemEvents.DisplaySettingsChanged += delegate
            {
                _displayChanged.Stop();
                _displayChanged.Start();
            };

            // keyboard plugged or unplugged
            _devicesChanged.Interval = 1200;   // these arrive in bursts too
            _devicesChanged.Tick += delegate { _devicesChanged.Stop(); ScanKeyboards(true); };
            _hotkeys.DevicesChanged = delegate { _devicesChanged.Stop(); _devicesChanged.Start(); };

            // Unlocking is what puts the system's default layout back, so that
            // is the moment to put ours back too.
            SystemEvents.SessionSwitch += delegate(object s2, SessionSwitchEventArgs e2)
            {
                if (e2.Reason == SessionSwitchReason.SessionUnlock ||
                    e2.Reason == SessionSwitchReason.SessionLogon ||
                    e2.Reason == SessionSwitchReason.ConsoleConnect ||
                    e2.Reason == SessionSwitchReason.RemoteConnect)
                {
                    // the shell restores its own layout right after the unlock,
                    // so give it time to finish first
                    _devicesChanged.Stop();
                    _devicesChanged.Start();
                }
            };

            // Guard: if anything puts the system layout back, put ours back again.
            _layoutGuard.Interval = 4000;
            _layoutGuard.Tick += delegate { EnforceLayout(false); };

            RegisterHotkeys();
            Discover();
            ScanKeyboards(false);
        }

        // -------------------------------------------------------- keyboard --
        /// <summary>
        /// Finds the first external keyboard that has a mapped layout. The
        /// layout cannot be read from the hardware, so the saved VID/PID -> KLID
        /// mapping is what decides.
        /// </summary>
        void ScanKeyboards(bool announce)
        {
            _kbMap = SettingsStore.KeyboardMap();
            _keyboards = Keyboards.List();

            KeyboardDevice found = null;
            foreach (KeyboardDevice k in _keyboards)
            {
                if (!k.IsUsb) continue;
                if (_kbMap.ContainsKey(k.Key)) { found = k; break; }
            }

            bool changed = (found == null) != (_activeKb == null) ||
                           (found != null && _activeKb != null && found.Key != _activeKb.Key);
            _activeKb = found;
            _wantedKlid = found == null ? "" : _kbMap[found.Key];

            if (found == null)
            {
                _layoutGuard.Stop();
                if (changed && announce) _osd.Flash(L.T("External keyboard unplugged\r\nlayout left to Windows"));
                return;
            }

            _layoutGuard.Enabled = _keepLayout;
            EnforceLayout(changed && announce);
        }

        /// <summary>Puts the wanted layout back, unless it is already active.</summary>
        void EnforceLayout(bool announce)
        {
            if (_activeKb == null || _wantedKlid.Length == 0) return;
            if (string.Equals(Layouts.Current(), _wantedKlid, StringComparison.OrdinalIgnoreCase))
            {
                if (announce) _osd.Flash(L.F("{0}\r\nlayout {1}", _activeKb.Name, Layouts.NameOf(_wantedKlid)));
                return;
            }
            if (Layouts.Apply(_wantedKlid))
                _osd.Flash(L.F("{0}\r\nlayout {1}", _activeKb.Name, Layouts.NameOf(_wantedKlid)));
            else
                _osd.Flash(L.F("Could not apply layout {0}", _wantedKlid));
        }

        /// <summary>Associates a layout with the given keyboard and saves it.</summary>
        void MapKeyboard(KeyboardDevice kb, string klid)
        {
            _kbMap[kb.Key] = klid;
            SettingsStore.SaveKeyboardMap(_kbMap);
            ScanKeyboards(false);
            _osd.Flash(L.F("{0}\r\n-> {1}", kb.Name, Layouts.NameOf(klid)));
        }

        void UnmapKeyboard(KeyboardDevice kb)
        {
            if (!_kbMap.Remove(kb.Key)) return;
            SettingsStore.SaveKeyboardMap(_kbMap);
            ScanKeyboards(false);
            _osd.Flash(L.F("{0}\r\nmapping removed", kb.Name));
        }

        void HidePanel()
        {
            if (!_panel.Visible) return;
            _panel.Hide();
            _panelHiddenAt = DateTime.UtcNow;
        }

        // -------------------------------------------------------- discovery --
        void Discover()
        {
            _worker.Rescan(delegate(List<MonitorTarget> targets)
            {
                MonitorTarget t = _worker.Active;
                // With no controllable monitor the icon disappears, and comes
                // back on its own when you plug one in.
                _tray.Visible = (t != null);
                // Unplugging forgets which profile was applied, so plugging the
                // same monitor back in applies the defaults again.
                if (t == null) _appliedKey = "";
                _monitorKey = (t == null) ? "" : (t.MonitorId.Length > 0 ? t.MonitorId : t.Title);
                _title = (t == null) ? L.T("no DDC/CI monitor") : t.Title;
                _modeInfo = (t == null) ? new DisplayMode() : t.Mode;
                _mode = _modeInfo.Valid ? _modeInfo.ToString() : "";
                // the tray tooltip is capped at 63 characters
                string tip = _mode.Length > 0 ? _title + " - " + _mode : _title;
                _tray.Text = tip.Length > 60 ? tip.Substring(0, 60) : tip;
                _panel.Text = _title;
                if (_header != null) _header.Text = _mode;
                if (t == null) return;

                Dictionary<byte, List<int>> declared = Vcp.ParseVcp(t.Capabilities);
                _isLenovo = t.MonitorId.StartsWith("LEN", StringComparison.OrdinalIgnoreCase);
                _isDell = t.MonitorId.StartsWith("DEL", StringComparison.OrdinalIgnoreCase);
                List<byte> candidates = Candidates(_isLenovo, _isDell);

                _worker.Run(delegate
                {
                    MonitorTarget cur = _worker.Active;
                    if (cur == null) return;

                    bool echoes, contended;
                    Dictionary<byte, int> values;
                    List<FeatureDef> found = ProbeFeatures(cur.Handle, candidates, declared,
                                                           out echoes, out values, out contended);
                    _worker.Post(delegate
                    {
                        _echoes = echoes;
                        _features.Clear();
                        _features.AddRange(found);
                        foreach (KeyValuePair<byte, int> kv in values) _cache[kv.Key] = kv.Value;
                        BuildPanel();
                        ApplyProfileIfNewMonitor();
                        // Better to say so than to show a panel that was
                        // assembled from someone else's replies.
                        if (contended)
                            _osd.Flash(L.T("Another program is using the monitor's DDC/CI bus:\r\n" +
                                           "the controls found may be wrong. Close it and refresh."));
                    });
                });
            });
        }

        /// <summary>Codes to probe, in the order they appear in the panel.</summary>
        internal static List<byte> Candidates(bool isLenovo, bool isDell)
        {
            List<byte> candidates = new List<byte>();
            candidates.AddRange(SliderCandidates);
            foreach (byte cc in ChoiceCandidates)
            {
                if (!isLenovo && Array.IndexOf(LenovoOnly, cc) >= 0) continue;
                if (!isDell && Array.IndexOf(DellOnly, cc) >= 0) continue;
                candidates.Add(cc);
            }
            return candidates;
        }

        /// <summary>
        /// A reserved VCP code no monitor implements, used as a sentinel to
        /// recognise the monitors that answer everything.
        /// </summary>
        const byte Sentinel = 0x33;

        /// <summary>Shown at the top of the scan when the monitor answers everything.</summary>
        const string EchoNote =
            "This monitor answers codes it does not have, by repeating its last\r\n" +
            "valid reply. Codes whose answer is identical to the one before are\r\n" +
            "listed apart as echoes: {0} out of 256.";

        /// <summary>
        /// Asks each candidate code and keeps the ones the monitor really has.
        ///
        /// "It answered, so it exists" does not hold on every monitor. The Dell
        /// S2719DGF answers ANY code, including ones that do not exist, by
        /// repeating its last valid reply. Taken at face value it claims to
        /// have everything, and the invented controls then sit there empty,
        /// because the value read matches none of the choices offered.
        ///
        /// If the sentinel answers we are on such a monitor, and a stricter
        /// rule applies: a code is real only if its reply differs from the last
        /// one received. The sentinel need not be re-read each time — after any
        /// read the echo is that reply — so the cost stays one read per code.
        ///
        /// One case stays ambiguous: a real code whose value happens to equal
        /// the echo. It is re-checked by priming the echo with an already
        /// promoted code of a different value; with none yet promoted, the
        /// capability string decides.
        ///
        /// Runs on the DDC thread: reads are sequential here, and each reply is
        /// needed to interpret the next.
        /// </summary>
        internal static List<FeatureDef> ProbeFeatures(IntPtr h, List<byte> candidates,
                                                       Dictionary<byte, List<int>> declared,
                                                       out bool echoes, out Dictionary<byte, int> values)
        {
            bool contended;
            return ProbeFeatures(h, candidates, declared, out echoes, out values, out contended);
        }

        /// <summary>How many times a probe disturbed by another bus user is redone.</summary>
        const int ProbeAttempts = 3;

        /// <summary>Sentinel reads at the end of a probe, to catch another bus user.</summary>
        const int ContentionReads = 3;

        /// <param name="contended">
        /// True when every attempt found another program on the bus, so the
        /// result is the last attempt's and may list controls that are not there.
        /// </param>
        internal static List<FeatureDef> ProbeFeatures(IntPtr h, List<byte> candidates,
                                                       Dictionary<byte, List<int>> declared,
                                                       out bool echoes, out Dictionary<byte, int> values,
                                                       out bool contended)
        {
            List<FeatureDef> found = null;
            echoes = false; values = null; contended = false;
            for (int attempt = 0; attempt < ProbeAttempts; attempt++)
            {
                if (attempt > 0) System.Threading.Thread.Sleep(500);
                found = ProbeOnce(h, candidates, declared, out echoes, out values, out contended);
                if (!contended) break;
            }
            return found;
        }

        static List<FeatureDef> ProbeOnce(IntPtr h, List<byte> candidates,
                                          Dictionary<byte, List<int>> declared,
                                          out bool echoes, out Dictionary<byte, int> values,
                                          out bool contended)
        {
            List<FeatureDef> found = new List<FeatureDef>();
            List<VcpValue> real = new List<VcpValue>();   // promoted, used to prime the echo
            values = new Dictionary<byte, int>();
            contended = false;

            // Read with the usual retries: a single dropped packet here — and
            // the probe runs right after a mode change, when packets do drop —
            // would pass an echoing monitor off as honest, and every candidate
            // would be promoted.
            VcpValue echo = Ddc.Get(h, Sentinel);
            echoes = echo.Ok;

            List<byte> pending = new List<byte>(candidates);
            List<byte> failed = new List<byte>();
            List<byte> deferred = new List<byte>();

            // Pass 0 asks every candidate; pass 1 retries the reads that
            // dropped; pass 2 comes back to the ambiguous codes, by then with
            // promoted codes to prime the echo with.
            for (int pass = 0; pass < 3; pass++)
            {
                if (pass == 1)
                {
                    // A dropped read used to make a control vanish silently:
                    // right after a video mode change the monitor re-locks the
                    // signal and the DDC bus misses beats, so failures get one
                    // more attempt.
                    if (failed.Count == 0) continue;
                    pending = failed;
                    System.Threading.Thread.Sleep(400);   // let the bus breathe
                }
                else if (pass == 2)
                {
                    if (deferred.Count == 0) break;
                    pending = deferred;
                }

                foreach (byte c in pending)
                {
                    VcpValue v = Ddc.Get(h, c);
                    if (!v.Ok) { if (pass == 0) failed.Add(c); continue; }

                    bool genuine = true;
                    if (echoes && SameReply(v, echo))
                    {
                        VcpValue primer = FirstDifferent(real, v);
                        if (primer.Ok)
                        {
                            Ddc.Get(h, primer.Code);      // the echo is now its reply
                            echo = primer;
                            v = Ddc.Get(h, c);
                            genuine = v.Ok && !SameReply(v, echo);
                        }
                        else if (pass < 2)
                        {
                            // Nothing promoted yet to tell a real code from the
                            // echo: come back to it once there is.
                            deferred.Add(c);
                            continue;
                        }
                        else genuine = declared.ContainsKey(c);   // nothing at all was promoted
                    }
                    if (v.Ok) echo = v;
                    if (!genuine) continue;

                    FeatureDef f = MakeFeature(c, v, declared);
                    if (f == null) continue;
                    found.Add(f);
                    real.Add(v);
                    values[c] = Current(c, v);
                }
            }

            // With the bus to ourselves the echo is exactly the last valid
            // reply we received. If the sentinel now says otherwise, another
            // program read the monitor in the meantime — and it made every
            // reply look "different from the one before", promoting codes
            // that do not exist. A single read can miss it — the other
            // program may just have asked the same code we did — so the
            // sentinel is read a few times: any mismatch is proof.
            if (echoes)
            {
                for (int i = 0; i < ContentionReads && !contended; i++)
                {
                    VcpValue after = Ddc.Get(h, Sentinel);
                    contended = after.Ok && !SameReply(after, echo);
                }
            }

            // Deferred codes were promoted out of order: the panel follows
            // the candidate order.
            found.Sort(delegate(FeatureDef a, FeatureDef b)
            {
                return candidates.IndexOf(a.Code).CompareTo(candidates.IndexOf(b.Code));
            });
            return found;
        }

        static bool SameReply(VcpValue a, VcpValue b)
        {
            return a.Ok && b.Ok && a.Current == b.Current && a.Maximum == b.Maximum;
        }

        static VcpValue FirstDifferent(List<VcpValue> known, VcpValue v)
        {
            foreach (VcpValue k in known) if (!SameReply(k, v)) return k;
            return VcpValue.Fail(0);
        }

        static FeatureDef MakeFeature(byte c, VcpValue v, Dictionary<byte, List<int>> declared)
        {
            FeatureDef f = new FeatureDef();
            f.Code = c;
            f.Label = LabelOf(c);
            f.IsChoice = IsChoice(c);
            f.Maximum = v.Maximum;
            if (f.IsChoice)
            {
                List<int> vals;
                if (declared.TryGetValue(c, out vals) && vals.Count > 1)
                    foreach (int val in vals)
                        f.Choices.Add(new KeyValuePair<int, string>(val, Vcp.ValueName(c, val)));
                else
                    f.Choices = Vcp.Known(c);
                if (f.Choices.Count < 2) return null;   // one option is not a choice
            }
            else if (v.Maximum <= 0) return null;
            return f;
        }

        static bool IsChoice(byte code) { return Array.IndexOf(ChoiceCandidates, code) >= 0; }

        /// <summary>
        /// The value read, normalised. On non-continuous codes the value lives
        /// in the low byte: the high byte is reserved and some monitors fill it
        /// in anyway (the Dell answers 0x1212 for HDMI 2). Without the mask no
        /// choice matches and the dropdown stays empty.
        /// </summary>
        static int Current(byte code, VcpValue v)
        {
            return IsChoice(code) ? (v.Current & 0xFF) : v.Current;
        }

        // ------------------------------------------------------------ panel --
        void BuildPanel()
        {
            _panel.SuspendLayout();
            _panel.Controls.Clear();
            _bars.Clear(); _barValues.Clear(); _combos.Clear();

            _features.Sort(delegate(FeatureDef a, FeatureDef b)
            {
                return OrderOf(a).CompareTo(OrderOf(b));
            });

            int top = 10;
            _header = new Label();
            _header.Text = _mode;
            _header.Location = new Point(10, top);
            _header.Size = new Size(320, 18);
            _header.ForeColor = Color.FromArgb(90, 90, 96);
            _panel.Controls.Add(_header);
            top += 24;

            foreach (FeatureDef f in _features)
            {
                if (f.IsChoice) { AddChoice(f, top); top += 28; }
                else { AddSlider(f, top); top += 32; }
            }

            AddRefreshRow(ref top);
            AddOrientationRow(ref top);

            _hint = new Label();
            _hint.Location = new Point(10, top);
            _hint.Size = new Size(320, 30);
            _hint.ForeColor = Color.FromArgb(150, 90, 0);
            _panel.Controls.Add(_hint);
            top += 32;

            AddButton(L.T("Save defaults"), 10, top, delegate { SaveProfile(); });
            AddButton(L.T("Apply defaults"), 114, top, delegate { ApplySavedProfileNow(); });
            AddButton(L.T("Refresh"), 218, top, delegate { Discover(); });
            top += 30;
            AddButton(L.T("Menu"), 10, top, delegate { ShowMenuFromPanel(); });
            AddButton(L.T("Turn off"), 114, top, delegate { HidePanel(); _worker.Write(Vcp.Power, 4); });
            AddButton(L.T("Factory reset"), 218, top, delegate
            {
                _worker.Write(Vcp.ResetAll, 1);
                _osd.Flash(L.T("Factory reset sent"));
                RefreshValues();
            });
            top += 34;

            _panel.ClientSize = new Size(340, top);
            _panel.ResumeLayout();
            RefreshValues();
        }

        /// <summary>
        /// Opens the tray menu from the panel. Everything in it is otherwise
        /// only reachable by right-clicking the tray icon, which is not an
        /// obvious thing to try.
        /// </summary>
        void ShowMenuFromPanel()
        {
            BuildMenu();
            Point at = _panel.PointToScreen(new Point(10, _panel.ClientSize.Height - 10));
            _tray.ContextMenuStrip.Show(at);
        }

        static int OrderOf(FeatureDef f)
        {
            int i = Array.IndexOf(SliderCandidates, f.Code);
            if (i >= 0) return i;
            return 100 + Array.IndexOf(ChoiceCandidates, f.Code);
        }

        void AddSlider(FeatureDef f, int top)
        {
            Label l = new Label();
            l.Text = f.Label;
            l.Location = new Point(10, top + 4);
            l.Size = new Size(112, 18);

            TrackBar bar = new TrackBar();
            bar.AutoSize = false;                 // otherwise it grows and overlaps the next row
            bar.Location = new Point(122, top);
            bar.Size = new Size(160, 26);
            bar.TickStyle = TickStyle.None;
            bar.Minimum = 0;
            bar.Maximum = Math.Max(1, f.Maximum);
            bar.SmallChange = 1;
            bar.LargeChange = 10;

            Label val = new Label();
            val.Location = new Point(288, top + 4);
            val.Size = new Size(42, 18);

            byte code = f.Code;
            bar.ValueChanged += delegate
            {
                val.Text = bar.Value.ToString();
                if (_loading) return;
                // optimistic: the slider moves at once, the write is queued and
                // the intermediate values are discarded by the worker
                _cache[code] = bar.Value;
                _worker.Write(code, bar.Value);
                MarkDirty(code);
            };

            _panel.Controls.Add(l);
            _panel.Controls.Add(bar);
            _panel.Controls.Add(val);
            _bars[code] = bar;
            _barValues[code] = val;
        }

        void AddChoice(FeatureDef f, int top)
        {
            Label l = new Label();
            l.Text = f.Label;
            l.Location = new Point(10, top + 4);
            l.Size = new Size(112, 18);

            ComboBox box = new ComboBox();
            box.Location = new Point(122, top);
            box.Size = new Size(208, 22);
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (KeyValuePair<int, string> kv in f.Choices) box.Items.Add(L.T(kv.Value));
            box.Tag = f;

            byte code = f.Code;
            box.SelectedIndexChanged += delegate
            {
                if (_loading || box.SelectedIndex < 0) return;
                int value = f.Choices[box.SelectedIndex].Key;
                _cache[code] = value;
                WriteFeature(code, value);
                MarkDirty(code);
                // changing preset or Dynamic Contrast frees or freezes other controls
                RefreshValues();
            };

            _panel.Controls.Add(l);
            _panel.Controls.Add(box);
            _combos[code] = box;
        }

        /// <summary>
        /// Refresh rate. Not a DDC/CI control: the graphics card decides it and
        /// the monitor just accepts the signal. Shown disabled when the current
        /// resolution offers only one rate, so it does not look like an omission.
        /// </summary>
        void AddRefreshRow(ref int top)
        {
            if (!_modeInfo.Valid || _modeInfo.Rates.Count == 0) return;

            Label l = new Label();
            l.Text = L.T("Refresh rate");
            l.Location = new Point(10, top + 4);
            l.Size = new Size(112, 18);

            ComboBox box = new ComboBox();
            box.Location = new Point(122, top);
            box.Size = new Size(208, 22);
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (int hz in _modeInfo.Rates) box.Items.Add(hz + " Hz");
            box.SelectedIndex = _modeInfo.Rates.IndexOf(_modeInfo.Frequency);
            box.Enabled = _modeInfo.Rates.Count > 1;

            box.SelectedIndexChanged += delegate
            {
                if (_loading || box.SelectedIndex < 0) return;
                int hz = _modeInfo.Rates[box.SelectedIndex];
                if (hz == _modeInfo.Frequency) return;
                string err = DisplayInfo.SetRefreshRate(_modeInfo.Device,
                                                        _modeInfo.Width, _modeInfo.Height, hz);
                if (err == null) _osd.Flash(L.F("Refresh rate: {0} Hz", hz));
                else
                {
                    _osd.Flash(L.F("{0} Hz refused\r\n{1}", hz, err));
                    _loading = true;
                    box.SelectedIndex = _modeInfo.Rates.IndexOf(_modeInfo.Frequency);
                    _loading = false;
                }
                // the mode change triggers a fresh Discover on its own
            };

            _panel.Controls.Add(l);
            _panel.Controls.Add(box);
            top += 28;
        }

        static readonly string[] OrientationNames = {
            "Landscape", "Portrait", "Landscape (flipped)", "Portrait (flipped)"
        };

        /// <summary>
        /// Desktop orientation, the four entries of the Windows display
        /// settings. Like the refresh rate it is not a DDC/CI control: Windows
        /// rotates the picture, the monitor knows nothing about it. It is
        /// saved in the profile, because a monitor that stands upright on one
        /// desk should come back upright when it is plugged in again.
        /// </summary>
        void AddOrientationRow(ref int top)
        {
            if (!_modeInfo.Valid) return;

            Label l = new Label();
            l.Text = L.T("Orientation");
            l.Location = new Point(10, top + 4);
            l.Size = new Size(112, 18);

            ComboBox box = new ComboBox();
            box.Location = new Point(122, top);
            box.Size = new Size(208, 22);
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (string name in OrientationNames) box.Items.Add(L.T(name));
            box.SelectedIndex = Math.Max(0, Math.Min(3, _modeInfo.Orientation));

            box.SelectedIndexChanged += delegate
            {
                if (_loading || box.SelectedIndex < 0) return;
                int o = box.SelectedIndex;
                if (o == _modeInfo.Orientation) return;
                string err = DisplayInfo.SetOrientation(_modeInfo.Device, o);
                if (err == null) _osd.Flash(L.F("Orientation: {0}", L.T(OrientationNames[o])));
                else
                {
                    _osd.Flash(L.F("Rotation refused\r\n{0}", err));
                    _loading = true;
                    box.SelectedIndex = _modeInfo.Orientation;
                    _loading = false;
                }
                // the mode change triggers a fresh Discover on its own
            };

            _panel.Controls.Add(l);
            _panel.Controls.Add(box);
            top += 28;
        }

        /// <summary>
        /// Queues a write for a control. Almost always the code itself; the
        /// Dell preset is the exception, where the reported code is read-only
        /// and the choice goes to a different register (see DellPresetWrite).
        /// The verification still reads 0xE2 back, which is what changes.
        /// </summary>
        void WriteFeature(byte code, int value)
        {
            if (code == Vcp.DellPreset)
            {
                for (int i = 0; i < DellPresetWrite.GetLength(0); i++)
                    if (DellPresetWrite[i, 0] == value)
                    {
                        _worker.Write((byte)DellPresetWrite[i, 1], DellPresetWrite[i, 2]);
                        return;
                    }
                return;   // a value we have no recipe for: better nothing than a wrong register
            }
            _worker.Write(code, value);
        }

        void AddButton(string text, int x, int y, Action click)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(96, 26);
            b.Click += delegate { click(); };
            _panel.Controls.Add(b);
        }

        /// <summary>Re-reads everything in the background, updating controls as answers arrive.</summary>
        void RefreshValues()
        {
            foreach (FeatureDef f in _features)
            {
                byte code = f.Code;
                _worker.Read(code, delegate(VcpValue v)
                {
                    // A read queued before a write of the same code runs
                    // first, legitimately, and comes back with the old value.
                    // Letting it overwrite the cache made the slider jump back
                    // and the deferred verification compare the monitor against
                    // a value nobody asked for any more. VerifyDirty owns the
                    // code until it has checked it.
                    if (!v.Ok || _dirty.Contains(code)) return;
                    _cache[code] = Current(code, v);
                    Apply(code, v);
                });
            }
        }

        void Apply(byte code, VcpValue v)
        {
            _loading = true;
            try
            {
                TrackBar bar;
                if (_bars.TryGetValue(code, out bar))
                {
                    bar.Maximum = Math.Max(1, v.Maximum);
                    bar.Value = Math.Max(0, Math.Min(bar.Maximum, v.Current));
                }
                ComboBox box;
                if (_combos.TryGetValue(code, out box))
                {
                    FeatureDef f = (FeatureDef)box.Tag;
                    int cur = Current(code, v);
                    for (int i = 0; i < f.Choices.Count; i++)
                        if (f.Choices[i].Key == cur) { box.SelectedIndex = i; break; }
                }
            }
            finally { _loading = false; }

            if (code == Vcp.DynamicContrast && _isLenovo) ApplyDcrLock(Current(code, v) == 1);
        }

        /// <summary>
        /// With Dynamic Contrast on, the monitor accepts brightness and contrast
        /// commands and then ignores them. Better to say so than to leave two
        /// sliders that do nothing.
        /// </summary>
        void ApplyDcrLock(bool on)
        {
            foreach (byte code in new byte[] { Vcp.Brightness, Vcp.Contrast })
            {
                TrackBar bar;
                if (_bars.TryGetValue(code, out bar)) bar.Enabled = !on;
                Label val;
                if (_barValues.TryGetValue(code, out val)) val.Enabled = !on;
            }
            if (_hint != null)
                _hint.Text = on
                    ? L.T("Dynamic Contrast is on: the monitor is holding\r\nbrightness and contrast fixed.")
                    : "";
        }

        // --------------------------------------------------------- profiles --
        /// <summary>
        /// Applies the saved profile only when the monitor genuinely changes
        /// (startup, or a different one plugged in). A manual Refresh must not
        /// overwrite what you are adjusting at that moment.
        /// </summary>
        void ApplyProfileIfNewMonitor()
        {
            if (_monitorKey.Length == 0 || _monitorKey == _appliedKey) return;
            _appliedKey = _monitorKey;
            Profile p = SettingsStore.For(_monitorKey);
            if (p == null || p.IsEmpty) return;   // no profile is not an error, so stay quiet
            ApplyProfile(p, true);
        }

        void ApplyProfile(Profile p, bool announce)
        {
            // Order matters: with Dynamic Contrast on, brightness and contrast
            // would be ignored, and the colour preset constrains the colour
            // temperature, so those two go first.
            byte[] first = { Vcp.DynamicContrast, Vcp.Preset, Vcp.DellPreset };
            List<byte> order = new List<byte>();
            foreach (byte c in first) if (p.Values.ContainsKey(c)) order.Add(c);
            foreach (KeyValuePair<byte, int> kv in p.Values)
                if (order.IndexOf(kv.Key) < 0) order.Add(kv.Key);

            foreach (byte code in order)
            {
                byte c = code;
                int v = p.Values[c];
                _cache[c] = v;
                WriteFeature(c, v);
            }
            // Orientation is Windows' business, not the monitor's: it does not
            // go through the queue. The mode change makes Discover run again.
            if (p.Orientation >= 0 && _modeInfo.Valid && p.Orientation != _modeInfo.Orientation)
                DisplayInfo.SetOrientation(_modeInfo.Device, p.Orientation);
            _worker.Run(delegate { _worker.Post(delegate
            {
                RefreshValues();
                if (announce) _osd.Flash(L.F("{0}\r\nprofile applied ({1} settings)", _title, p.Values.Count));
            }); });
        }

        void SaveProfile()
        {
            if (_monitorKey.Length == 0) { _osd.Flash(L.T("No monitor to save")); return; }
            Profile p = new Profile();
            p.Key = _monitorKey;
            p.Name = _title;
            foreach (FeatureDef f in _features)
            {
                // the OSD language is not a picture preference, and rewriting it
                // on every connect is just traffic on the bus
                if (f.Code == Vcp.OsdLanguage) continue;
                int v;
                if (_cache.TryGetValue(f.Code, out v)) p.Values[f.Code] = v;
            }
            p.Orientation = _modeInfo.Valid ? _modeInfo.Orientation : -1;
            SettingsStore.Save(p);
            _appliedKey = _monitorKey;
            _osd.Flash(L.F("{0}\r\n{1} settings saved as defaults", _title, p.Values.Count));
        }

        void ApplySavedProfileNow()
        {
            Profile p = SettingsStore.For(_monitorKey);
            if (p == null || p.IsEmpty) { _osd.Flash(L.T("No profile saved for this monitor")); return; }
            ApplyProfile(p, true);
        }

        void MarkDirty(byte code)
        {
            _dirty.Add(code);
            _verify.Stop();
            _verify.Start();
        }

        void VerifyDirty()
        {
            byte[] codes = new byte[_dirty.Count];
            _dirty.CopyTo(codes);
            _dirty.Clear();
            foreach (byte code in codes)
            {
                byte c = code;
                int wanted;
                if (!_cache.TryGetValue(c, out wanted)) continue;
                int want = wanted;
                _worker.Read(c, delegate(VcpValue v)
                {
                    int got = Current(c, v);
                    if (!v.Ok || got == want) return;
                    _cache[c] = got;
                    Apply(c, v);
                    _osd.Flash(L.F("{0}: the monitor refused {1}\r\nand left it at {2}",
                                   LabelOf(c), want, got));
                });
            }
        }

        void TogglePanel()
        {
            if (_panel.Visible) { HidePanel(); return; }
            // Clicking the icon while the panel is open already hid it through
            // Deactivate, so it must not reopen straight away.
            if ((DateTime.UtcNow - _panelHiddenAt).TotalMilliseconds < 300) return;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            _panel.Location = new Point(wa.X + wa.Width - _panel.Width - 12,
                                        wa.Y + wa.Height - _panel.Height - 12);
            _panelShownAt = DateTime.UtcNow;
            _panel.Show();
            _panel.Activate();
            SetForegroundWindow(_panel.Handle);
            RefreshValues();
        }

        // ------------------------------------------------------------- menu --
        void BuildMenu()
        {
            ContextMenuStrip m = _tray.ContextMenuStrip;
            m.Items.Clear();

            ToolStripMenuItem header = new ToolStripMenuItem(
                _mode.Length > 0 ? _title + "   -   " + _mode : _title);
            header.Enabled = false;
            m.Items.Add(header);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item(L.T("Control panel"), delegate { TogglePanel(); }, false));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item(L.T("Save settings as defaults"), delegate { SaveProfile(); }, false));
            m.Items.Add(Item(L.T("Apply defaults"), delegate { ApplySavedProfileNow(); }, false));
            m.Items.Add(new ToolStripSeparator());

            foreach (FeatureDef f in _features)
            {
                if (!f.IsChoice) continue;
                int cur;
                _cache.TryGetValue(f.Code, out cur);
                ToolStripMenuItem sub = new ToolStripMenuItem(f.Label + "   " + L.T(Vcp.ValueName(f.Code, cur)));
                foreach (KeyValuePair<int, string> kv in f.Choices)
                {
                    byte code = f.Code;
                    int value = kv.Key;
                    string label = f.Label;
                    sub.DropDownItems.Add(Item(L.T(kv.Value), delegate
                    {
                        _cache[code] = value;
                        _worker.Write(code, value);
                        MarkDirty(code);
                        _osd.Flash(label + ": " + L.T(Vcp.ValueName(code, value)));
                    }, kv.Key == cur));
                }
                m.Items.Add(sub);
            }

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item(L.T("Turn the monitor off"), delegate { _worker.Write(Vcp.Power, 4); }, false));
            ToolStripMenuItem reset = new ToolStripMenuItem(L.T("Factory reset"));
            reset.DropDownItems.Add(Item(L.T("Everything"), delegate { _worker.Write(Vcp.ResetAll, 1); RefreshValues(); }, false));
            reset.DropDownItems.Add(Item(L.T("Brightness and contrast"), delegate { _worker.Write(Vcp.ResetBrightness, 1); RefreshValues(); }, false));
            reset.DropDownItems.Add(Item(L.T("Colour"), delegate { _worker.Write(Vcp.ResetColor, 1); RefreshValues(); }, false));
            m.Items.Add(reset);

            // ---- keyboard ----
            ToolStripMenuItem kb = new ToolStripMenuItem(
                _activeKb != null
                    ? L.F("Keyboard:  {0}", Layouts.NameOf(_wantedKlid))
                    : L.F("Keyboard:  {0}", L.T("not mapped")));

            List<KeyValuePair<string, string>> layouts = Layouts.Installed();
            foreach (KeyboardDevice dev in _keyboards)
            {
                if (!dev.IsUsb) continue;                 // Windows can keep the built-in one
                KeyboardDevice d = dev;
                string mapped;
                bool hasMap = _kbMap.TryGetValue(d.Key, out mapped);

                ToolStripMenuItem item = new ToolStripMenuItem(
                    d.Name + (hasMap ? "   [" + Layouts.NameOf(mapped) + "]" : ""));
                foreach (KeyValuePair<string, string> l in layouts)
                {
                    string klid = l.Key;
                    item.DropDownItems.Add(Item(l.Value, delegate { MapKeyboard(d, klid); },
                                                hasMap && string.Equals(mapped, klid, StringComparison.OrdinalIgnoreCase)));
                }
                if (hasMap)
                {
                    item.DropDownItems.Add(new ToolStripSeparator());
                    item.DropDownItems.Add(Item(L.T("Remove mapping"), delegate { UnmapKeyboard(d); }, false));
                }
                kb.DropDownItems.Add(item);
            }
            kb.DropDownItems.Add(new ToolStripSeparator());
            kb.DropDownItems.Add(Item(L.T("Keep the layout enforced"), delegate { ToggleKeepLayout(); }, _keepLayout));
            kb.DropDownItems.Add(Item(L.T("Apply now"), delegate { EnforceLayout(true); }, false));
            m.Items.Add(kb);
            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem more = new ToolStripMenuItem(L.T("More"));
            more.DropDownItems.Add(Item(L.T("Start with Windows"), delegate { ToggleAutostart(); }, IsAutostart()));
            more.DropDownItems.Add(Item(L.T("Detect monitors again"), delegate { Discover(); }, false));
            more.DropDownItems.Add(Item(L.T("Diagnostics..."), delegate { ShowDiagnostics(); }, false));
            more.DropDownItems.Add(Item(L.T("Full VCP scan..."), delegate { ShowFullScan(); }, false));
            more.DropDownItems.Add(Item(L.T("Keyboard shortcuts"), delegate
            {
                ReportForm.Open(L.F("{0} - shortcuts", AppInfo.Name), HotkeyHelp(), _icon);
            }, false));
            ToolStripMenuItem lang = new ToolStripMenuItem(L.T("Language"));
            foreach (KeyValuePair<string, string> opt in L.Available)
            {
                string code = opt.Key;
                string label = code == L.Auto ? L.T(opt.Value) : opt.Value;
                lang.DropDownItems.Add(Item(label, delegate { L.Set(code); },
                    string.Equals(L.Setting, code, StringComparison.OrdinalIgnoreCase)));
            }
            more.DropDownItems.Add(lang);

            more.DropDownItems.Add(Item(L.T("Open the settings file"), delegate
            {
                string path = SettingsStore.FilePath();
                if (System.IO.File.Exists(path)) System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\"");
                else _osd.Flash(L.T("Nothing saved yet"));
            }, false));
            m.Items.Add(more);

            m.Items.Add(Item(L.F("About {0}...", AppInfo.Name), delegate
            {
                using (AboutForm f = new AboutForm(_icon)) f.ShowDialog();
            }, false));

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item(L.T("Quit"), delegate { Quit(); }, false));
        }

        static ToolStripMenuItem Item(string text, Action click, bool @checked)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Checked = @checked;
            it.Click += delegate { click(); };
            return it;
        }

        void ToggleKeepLayout()
        {
            _keepLayout = !_keepLayout;
            SettingsStore.SetBool("keepLayout", _keepLayout);
            _layoutGuard.Enabled = _keepLayout && _activeKb != null;
            _osd.Flash(L.T(_keepLayout
                ? "Layout enforced\r\nwhile the keyboard is plugged in"
                : "Layout no longer enforced\r\nonly on unlock and on plug-in"));
        }

        // ------------------------------------------------------ diagnostics --
        void ShowDiagnostics()
        {
            _osd.Flash(L.T("Running diagnostics..."));
            List<FeatureDef> sliders = _features.FindAll(delegate(FeatureDef f) { return !f.IsChoice; });
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(_title);
            if (_mode.Length > 0) sb.AppendLine(_mode);
            sb.AppendLine();

            _worker.Run(delegate
            {
                MonitorTarget t = _worker.Active;
                if (t == null) return;
                foreach (FeatureDef f in sliders)
                {
                    VcpValue cur = Ddc.Get(t.Handle, f.Code);
                    if (!cur.Ok) { sb.AppendLine(f.Label.PadRight(16) + L.T("unreadable")); continue; }
                    // The probe value has to stay in range, or the monitor clamps
                    // it and the write looks as if it had been ignored.
                    int probe = cur.Current >= cur.Maximum ? Math.Max(0, cur.Current - 1) : cur.Current + 1;
                    Ddc.Set(t.Handle, f.Code, probe);
                    System.Threading.Thread.Sleep(200);
                    VcpValue after = Ddc.Get(t.Handle, f.Code);
                    bool ok = after.Ok && after.Current == probe;
                    Ddc.Set(t.Handle, f.Code, cur.Current);
                    sb.AppendLine(string.Format("{0} {1,3}/{2,-3} {3}",
                        f.Label.PadRight(16), cur.Current, cur.Maximum,
                        L.T(ok ? "write: OK" : "write: IGNORED by the monitor")));
                }
                sb.AppendLine();
                sb.AppendLine(L.T("A control reported as IGNORED is being held fixed by a\r\n"
                                + "mode in the monitor's own menu. Dynamic Contrast locks\r\n"
                                + "brightness and contrast, an sRGB preset locks contrast,\r\n"
                                + "and colour temperature only moves outside named presets."));

                _worker.Post(delegate
                {
                    RefreshValues();
                    ReportForm.Open(L.F("{0} - diagnostics", AppInfo.Name), sb.ToString(), _icon);
                });
            });
        }

        // --------------------------------------------------------- full scan --
        /// <summary>
        /// Asks all 256 VCP codes and reports which ones answer. Useful on a new
        /// monitor, to see whether it exposes anything the standard candidates
        /// miss. Read-only: it writes nothing.
        /// </summary>
        void ShowFullScan()
        {
            _osd.Flash(L.T("Scanning all 256 VCP codes...\r\nabout 15 seconds"));
            List<byte> shown = new List<byte>();
            foreach (FeatureDef f in _features) shown.Add(f.Code);

            _worker.Run(delegate
            {
                MonitorTarget t = _worker.Active;
                if (t == null) return;

                Dictionary<byte, List<int>> declared = Vcp.ParseVcp(t.Capabilities);
                StringBuilder inPanel = new StringBuilder();
                StringBuilder extra = new StringBuilder();
                StringBuilder echoed = new StringBuilder();
                int total = 0, fake = 0;

                // If it answers a reserved code too, every one of its answers
                // has to be read for what it is: a repeat of the last valid one.
                VcpValue echo = Ddc.Get(t.Handle, Sentinel, 1);
                bool echoes = echo.Ok;

                for (int i = 0; i <= 255; i++)
                {
                    byte c = (byte)i;
                    VcpValue v = Ddc.Get(t.Handle, c, 1);   // one attempt only: there are 256
                    if (!v.Ok) continue;

                    bool isEcho = echoes && SameReply(v, echo);
                    echo = v;

                    string name = Vcp.MccsName(c);
                    if (name.Length == 0) name = L.T(declared.ContainsKey(c) ? "(vendor, declared)" : "(unknown)");
                    string line = string.Format("  0x{0:X2}  {1,-28} {2,6} / {3}\r\n",
                                                c, name, v.Current, v.Maximum);
                    if (isEcho) { fake++; echoed.Append(line); }
                    else
                    {
                        total++;
                        if (shown.Contains(c)) inPanel.Append(line); else extra.Append(line);
                    }
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine(_title + "   (" + t.MonitorId + ")");
                sb.AppendLine(L.F("declared by the monitor: {0}   -   actually answering: {1}",
                                  declared.Count, total));
                if (echoes)
                {
                    sb.AppendLine();
                    sb.AppendLine(L.F(EchoNote, fake));
                }
                sb.AppendLine();
                sb.AppendLine(L.T("ALREADY IN THE PANEL"));
                sb.Append(inPanel.Length > 0 ? inPanel.ToString() : L.T("  none\r\n"));
                sb.AppendLine();
                sb.AppendLine(L.T("ANSWERING BUT NOT IN THE PANEL"));
                sb.Append(extra.Length > 0 ? extra.ToString() : L.T("  none\r\n"));
                if (echoed.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(L.T("ECHOES: SAME ANSWER AS THE CODE BEFORE, NOT REAL CONTROLS"));
                    sb.Append(echoed.ToString());
                }
                sb.AppendLine();
                sb.AppendLine(L.T("The scan is read-only: answering does not mean accepting\r\n"
                                + "writes. Use Diagnostics to test those."));

                _worker.Post(delegate
                {
                    ReportForm.Open(L.F("{0} - full VCP scan", AppInfo.Name), sb.ToString(), _icon);
                });
            });
        }

        // ---------------------------------------------------------- hotkeys --
        void RegisterHotkeys()
        {
            const uint ALT = 1, CTRL = 2, SHIFT = 4;
            const uint VK_UP = 0x26, VK_DOWN = 0x28, VK_LEFT = 0x25, VK_RIGHT = 0x27,
                       VK_M = 0x4D, VK_I = 0x49, VK_NEXT = 0x22;

            Add(CTRL | ALT, VK_UP, "Ctrl+Alt+Up", "brightness +5", delegate { Nudge(Vcp.Brightness, Step); });
            Add(CTRL | ALT, VK_DOWN, "Ctrl+Alt+Down", "brightness -5", delegate { Nudge(Vcp.Brightness, -Step); });
            Add(CTRL | ALT | SHIFT, VK_UP, "Ctrl+Shift+Alt+Up", "contrast +5", delegate { Nudge(Vcp.Contrast, Step); });
            Add(CTRL | ALT | SHIFT, VK_DOWN, "Ctrl+Shift+Alt+Down", "contrast -5", delegate { Nudge(Vcp.Contrast, -Step); });
            Add(CTRL | ALT, VK_RIGHT, "Ctrl+Alt+Right", "volume +5", delegate { Nudge(Vcp.Volume, Step); });
            Add(CTRL | ALT, VK_LEFT, "Ctrl+Alt+Left", "volume -5", delegate { Nudge(Vcp.Volume, -Step); });
            Add(CTRL | ALT, VK_M, "Ctrl+Alt+M", "mute on/off", delegate { ToggleMute(); });
            Add(CTRL | ALT, VK_I, "Ctrl+Alt+I", "next input", delegate { NextInput(); });
            Add(CTRL | ALT, VK_NEXT, "Ctrl+Alt+PageDown", "open the panel", delegate { TogglePanel(); });
        }

        void Add(uint mods, uint vk, string keys, string description, Action a)
        {
            bool ok = _hotkeys.Register(mods, vk, a);
            _hotkeyDocs.Add(new string[] { keys, description, ok ? "" : "taken" });
        }

        /// <summary>Built on demand so it follows a language change.</summary>
        string HotkeyHelp()
        {
            StringBuilder sb = new StringBuilder();
            foreach (string[] row in _hotkeyDocs)
            {
                sb.Append(row[0].PadRight(22)).Append(L.T(row[1]));
                if (row[2].Length > 0) sb.Append(L.T("   << not registered: already taken"));
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>
        /// Starts from the cached value instead of reading it back: that saves a
        /// 60 ms round trip, which is what makes the shortcut feel immediate.
        /// </summary>
        void Nudge(byte code, int delta)
        {
            FeatureDef f = _features.Find(delegate(FeatureDef x) { return x.Code == code; });
            if (f == null) { _osd.Flash(L.F("{0} not available", LabelOf(code))); return; }

            int cur;
            if (!_cache.TryGetValue(code, out cur)) { RefreshValues(); return; }
            int next = Math.Max(0, Math.Min(f.Maximum, cur + delta));
            _cache[code] = next;
            _worker.Write(code, next);
            MarkDirty(code);

            TrackBar bar;
            if (_bars.TryGetValue(code, out bar))
            {
                _loading = true;
                bar.Value = Math.Min(bar.Maximum, next);
                _loading = false;
            }
            _osd.Flash(LabelOf(code) + "\r\n" + Bar(next, f.Maximum) + "   " + next);
        }

        static string Bar(int cur, int max)
        {
            if (max <= 0 || cur > max) return "";
            int n = Math.Max(0, Math.Min(14, (int)Math.Round(14.0 * cur / max)));
            return new string('=', n) + new string('-', 14 - n);
        }

        void ToggleMute()
        {
            int cur;
            int next = (_cache.TryGetValue(Vcp.Mute, out cur) && cur == 1) ? 2 : 1;
            _cache[Vcp.Mute] = next;
            _worker.Write(Vcp.Mute, next);
            MarkDirty(Vcp.Mute);
            _osd.Flash(L.F("Audio: {0}", L.T(Vcp.ValueName(Vcp.Mute, next))));
        }

        void NextInput()
        {
            FeatureDef f = _features.Find(delegate(FeatureDef x) { return x.Code == Vcp.Input; });
            if (f == null || f.Choices.Count < 2) { _osd.Flash(L.T("Only one input available")); return; }
            int cur;
            _cache.TryGetValue(Vcp.Input, out cur);
            int i = f.Choices.FindIndex(delegate(KeyValuePair<int, string> kv) { return kv.Key == cur; });
            KeyValuePair<int, string> next = f.Choices[(i + 1) % f.Choices.Count];
            _cache[Vcp.Input] = next.Key;
            _worker.Write(Vcp.Input, next.Key);
            MarkDirty(Vcp.Input);
            _osd.Flash(L.F("Input: {0}", L.T(next.Value)));
        }

        // -------------------------------------------------------- autostart --
        static bool IsAutostart()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        void ToggleAutostart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (k.GetValue(RunName) != null)
                    {
                        k.DeleteValue(RunName, false);
                        _osd.Flash(L.T("Will no longer start with Windows"));
                    }
                    else
                    {
                        k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                        _osd.Flash(L.T("Will start with Windows"));
                    }
                }
            }
            catch (Exception ex)
            {
                _osd.Flash(L.F("Could not change autostart\r\n{0}", ex.Message));
            }
        }

        // ------------------------------------------------------------ misc --
        /// <summary>
        /// The icon is compiled into the executable, so it is read back from
        /// there rather than from a file next to it.
        /// </summary>
        static Icon LoadIcon()
        {
            try
            {
                Icon i = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (i != null) return i;
            }
            catch { }
            return SystemIcons.Application;
        }

        void Quit()
        {
            _verify.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            _layoutGuard.Stop();
            _devicesChanged.Stop();
            _displayChanged.Stop();
            _hotkeys.Dispose();
            _worker.Dispose();
            ExitThread();
        }
    }
}
