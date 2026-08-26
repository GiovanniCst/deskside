// Exercises the DDC work queue against the real monitor.
//
// The queue merges a read into an identical one already waiting, to avoid
// paying twice for the same 60 ms of bus. This checks the merge cannot reach
// back across a pending write of the same code: that read runs BEFORE the
// write and returns the old value, which makes the deferred verification
// announce a rejection that never happened.
//
// Build and run with tools\Test-DdcQueue.cmd. Writes to one continuous slider
// and puts it back where it was.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace Deskside
{
    static class TestDdcQueue
    {
        static Form _ui;
        static DdcWorker _worker;
        static int _failed;

        [STAThread]
        static int Main()
        {
            _ui = new Form();
            _ui.ShowInTaskbar = false;
            _ui.WindowState = FormWindowState.Minimized;
            _ui.Opacity = 0;
            _ui.Load += delegate { ThreadPool.QueueUserWorkItem(delegate { Run(); }); };
            Application.Run(_ui);
            return _failed;
        }

        static void Run()
        {
            try { Body(); }
            catch (Exception e) { Console.WriteLine("ERROR: " + e.Message); _failed++; }
            _ui.BeginInvoke((Action)delegate
            {
                Console.WriteLine();
                Console.WriteLine(_failed == 0 ? "ALL OK" : _failed + " CHECKS FAILED");
                Application.Exit();
            });
        }

        /// <summary>Queues a read and waits for it: here the answer matters, not responsiveness.</summary>
        static VcpValue Read(byte code)
        {
            VcpValue got = VcpValue.Fail(code);
            ManualResetEvent done = new ManualResetEvent(false);
            _worker.Read(code, delegate(VcpValue v) { got = v; done.Set(); });
            done.WaitOne(8000);
            return got;
        }

        static void Check(string what, bool ok, string detail)
        {
            Console.WriteLine("  [{0}] {1}{2}", ok ? "ok  " : "FAIL", what,
                              detail.Length > 0 ? "   " + detail : "");
            if (!ok) _failed++;
        }

        static void Body()
        {
            ManualResetEvent scanned = new ManualResetEvent(false);
            List<MonitorTarget> targets = null;
            _worker = new DdcWorker(_ui);
            _worker.Rescan(delegate(List<MonitorTarget> t) { targets = t; scanned.Set(); });
            scanned.WaitOne(15000);

            MonitorTarget m = _worker.Active;
            if (m == null) { Console.WriteLine("No DDC/CI monitor: nothing to test."); return; }
            Console.WriteLine(AppInfo.Name + " " + AppInfo.Version);
            Console.WriteLine("monitor: " + m.Title + "   (" + m.MonitorId + ")");
            Console.WriteLine();

            // What the panel would be built from, through the very same code
            // path the application uses.
            bool echoes, contended; Dictionary<byte, int> values;
            List<FeatureDef> feats = TrayApp.ProbeFeatures(
                m.Handle, TrayApp.Candidates(m.MonitorId.StartsWith("LEN", StringComparison.OrdinalIgnoreCase), m.MonitorId.StartsWith("DEL", StringComparison.OrdinalIgnoreCase)),
                Vcp.ParseVcp(m.Capabilities), out echoes, out values, out contended);
            Console.WriteLine("answers-everything monitor: " + echoes);
            Console.WriteLine("another program on the bus: " + contended);
            Console.WriteLine("controls found: " + feats.Count);
            foreach (FeatureDef f in feats)
                Console.WriteLine("  0x{0:X2}  {1,-16} {2}", f.Code, f.Label,
                                  f.IsChoice ? Vcp.ValueName(f.Code, values[f.Code])
                                             : values[f.Code] + "/" + f.Maximum);
            Console.WriteLine();

            // A continuous slider the monitor really has. Brightness is present
            // on every monitor worth driving; the gains are the laziest to
            // mirror a write, so they are tried first.
            byte code = 0;
            foreach (byte p in new byte[] { Vcp.RedGain, Vcp.GreenGain, Vcp.Contrast, Vcp.Brightness })
            {
                VcpValue probe = Read(p);
                if (probe.Ok && probe.Maximum > 1) { code = p; break; }
            }
            if (code == 0) { Console.WriteLine("No usable slider."); _failed++; return; }

            VcpValue start = Read(code);
            int target = start.Current >= start.Maximum ? start.Current - 1 : start.Current + 1;
            Console.WriteLine("using 0x{0:X2}: {1} -> {2} -> {1}", code, start.Current, target);
            Console.WriteLine();

            // Codes used only to congest the queue, the way refreshing the
            // panel does. They need not be supported: a failed read still
            // occupies its slot.
            byte[] filler = { Vcp.Brightness, Vcp.Contrast, Vcp.Volume, Vcp.RedGain,
                              Vcp.GreenGain, Vcp.BlueGain, Vcp.Input, Vcp.OsdLanguage };

            try
            {
                const int Rounds = 10;
                Console.WriteLine("1. read straight after a write, " + Rounds + " rounds");
                int wrong = 0; string misses = "";
                for (int i = 0; i < Rounds; i++)
                {
                    int want = (i % 2 == 0) ? target : start.Current;
                    _worker.Write(code, want);
                    VcpValue after = Read(code);
                    if (!after.Ok || after.Current != want)
                    {
                        wrong++;
                        misses += (misses.Length > 0 ? ", " : "") + "round " + (i + 1) +
                                  ": wanted " + want + " read " + (after.Ok ? after.Current.ToString() : "error");
                    }
                }
                Check("every read sees the value just written", wrong == 0,
                      wrong == 0 ? Rounds + " clean rounds" : wrong + "/" + Rounds + " -> " + misses);

                Console.WriteLine();
                Console.WriteLine("2. a read already queued before the write");
                VcpValue stale = VcpValue.Fail(code), verify = VcpValue.Fail(code);
                ManualResetEvent staleDone = new ManualResetEvent(false);
                ManualResetEvent verifyDone = new ManualResetEvent(false);

                // congest the queue as refreshing the panel does, then write and
                // verify without letting the worker drain in between
                foreach (byte f in filler) _worker.Read(f, delegate(VcpValue v) { });
                _worker.Read(code, delegate(VcpValue v) { stale = v; staleDone.Set(); });
                _worker.Write(code, target);
                _worker.Read(code, delegate(VcpValue v) { verify = v; verifyDone.Set(); });

                staleDone.WaitOne(15000);
                verifyDone.WaitOne(15000);
                Check("the older read stays before the write",
                      stale.Ok && stale.Current == start.Current,
                      "read " + (stale.Ok ? stale.Current.ToString() : "error"));
                Check("the verification sees the value written",
                      verify.Ok && verify.Current == target,
                      "read " + (verify.Ok ? verify.Current.ToString() : "error"));
            }
            finally
            {
                _worker.Write(code, start.Current);
                VcpValue end = Read(code);
                Console.WriteLine();
                Console.WriteLine("0x{0:X2} put back to {1}", code, end.Ok ? end.Current.ToString() : "?");
            }
        }
    }
}
