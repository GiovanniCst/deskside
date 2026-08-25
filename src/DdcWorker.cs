// The one thread that talks to the monitor. The UI never calls Ddc directly:
// it queues operations and gets results back on the UI thread.
//
// Two details do most of the work for perceived speed, given that every bus
// operation costs about 60 ms:
//   - writes to the same code coalesce, so dragging a slider only sends the
//     value the user landed on, not every value it passed through;
//   - reads already queued for the same code merge into one.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace Deskside
{
    public sealed class DdcWorker : IDisposable
    {
        abstract class Op { public byte Code; }
        sealed class ReadOp   : Op { public Action<VcpValue> Done; }
        sealed class WriteOp  : Op { public int Value; public Action<bool> Done; }
        sealed class CustomOp : Op { public Action Work; }

        readonly List<Op> _queue = new List<Op>();
        readonly object _lock = new object();
        readonly AutoResetEvent _signal = new AutoResetEvent(false);
        readonly Thread _thread;
        readonly Control _ui;          // only used to get back onto the UI thread
        volatile bool _stop;

        List<MonitorTarget> _targets = new List<MonitorTarget>();
        int _active;

        /// <param name="uiMarshaller">
        /// A control whose handle already exists, used to deliver results on
        /// the UI thread.
        /// </param>
        public DdcWorker(Control uiMarshaller)
        {
            _ui = uiMarshaller;
            IntPtr force = _ui.Handle;   // BeginInvoke needs a created handle
            GC.KeepAlive(force);
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "ddc";
            _thread.Start();
        }

        /// <summary>The monitor currently being driven. Only valid after Rescan.</summary>
        public MonitorTarget Active
        {
            get
            {
                lock (_lock)
                    return (_active >= 0 && _active < _targets.Count) ? _targets[_active] : null;
            }
        }

        public List<MonitorTarget> Targets { get { lock (_lock) return new List<MonitorTarget>(_targets); } }

        public void SetActive(int index)
        {
            lock (_lock) if (index >= 0 && index < _targets.Count) _active = index;
        }

        // ----------------------------------------------------------- queue --
        void Enqueue(Op op, bool coalesce)
        {
            lock (_lock)
            {
                if (coalesce)
                {
                    WriteOp w = op as WriteOp;
                    if (w != null)
                    {
                        // a newer write makes the pending ones pointless
                        _queue.RemoveAll(delegate(Op o)
                        {
                            WriteOp p = o as WriteOp;
                            return p != null && p.Code == w.Code && p.Done == null;
                        });
                    }
                    ReadOp r = op as ReadOp;
                    if (r != null)
                    {
                        // A read already waiting answers this one too, but only
                        // if no write of the same code is waiting behind it:
                        // that read runs BEFORE the write and returns the old
                        // value, which makes the deferred verification announce
                        // a rejection that never happened. Scan from the back:
                        // what matters is who comes last.
                        for (int i = _queue.Count - 1; i >= 0; i--)
                        {
                            if (_queue[i] is CustomOp || _queue[i].Code != r.Code) continue;
                            ReadOp p = _queue[i] as ReadOp;
                            if (p != null) { p.Done += r.Done; return; }
                            break;   // it is a write: this read must queue after it
                        }
                    }
                }
                _queue.Add(op);
            }
            _signal.Set();
        }

        public void Read(byte code, Action<VcpValue> done)
        {
            ReadOp op = new ReadOp();
            op.Code = code; op.Done = done;
            Enqueue(op, true);
        }

        public void Write(byte code, int value, Action<bool> done)
        {
            WriteOp op = new WriteOp();
            op.Code = code; op.Value = value; op.Done = done;
            Enqueue(op, true);
        }

        public void Write(byte code, int value) { Write(code, value, null); }

        /// <summary>Runs work on the DDC thread, for sequences that must stay together.</summary>
        public void Run(Action work)
        {
            CustomOp op = new CustomOp();
            op.Work = work;
            Enqueue(op, false);
        }

        /// <summary>Re-enumerates the monitors and re-reads their capabilities.</summary>
        public void Rescan(Action<List<MonitorTarget>> done)
        {
            Run(delegate
            {
                List<MonitorTarget> old;
                lock (_lock) { old = _targets; }
                Ddc.Release(old);

                // A laptop's own panel shows up among the physical monitors but
                // does not speak DDC/CI. Keeping it in the list means that with
                // two screens attached there is a one-in-two chance of driving
                // the wrong one, since the active target is always the first.
                List<MonitorTarget> found = new List<MonitorTarget>();
                List<MonitorTarget> mute = new List<MonitorTarget>();
                foreach (MonitorTarget t in Ddc.Enumerate())
                {
                    t.Capabilities = Ddc.ReadCapabilities(t.Handle);
                    if (t.Capabilities.Length == 0 && !Ddc.Get(t.Handle, Vcp.Brightness, 1).Ok)
                    {
                        mute.Add(t);
                        continue;
                    }
                    t.Model = Vcp.Section(t.Capabilities, "model");
                    t.Mode = DisplayInfo.ForMonitor(t.HMonitor);       // no DDC involved
                    t.MonitorId = DisplayInfo.MonitorIdOf(t.HMonitor); // likewise
                    found.Add(t);
                }
                Ddc.Release(mute);
                lock (_lock)
                {
                    _targets = found;
                    if (_active >= found.Count) _active = 0;
                }
                if (done != null) Post(delegate { done(found); });
            });
        }

        public void Post(Action a)
        {
            if (_stop || !_ui.IsHandleCreated) return;
            try { _ui.BeginInvoke(a); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
        }

        // ------------------------------------------------------------ loop --
        void Loop()
        {
            while (!_stop)
            {
                Op op = null;
                lock (_lock)
                {
                    if (_queue.Count > 0) { op = _queue[0]; _queue.RemoveAt(0); }
                }
                if (op == null) { _signal.WaitOne(250); continue; }

                IntPtr h = IntPtr.Zero;
                MonitorTarget t = Active;
                if (t != null) h = t.Handle;

                CustomOp c = op as CustomOp;
                if (c != null) { SafeRun(c.Work); continue; }

                ReadOp r = op as ReadOp;
                if (r != null)
                {
                    VcpValue v = (t == null) ? VcpValue.Fail(r.Code) : Ddc.Get(h, r.Code);
                    Action<VcpValue> cb = r.Done;
                    if (cb != null) Post(delegate { cb(v); });
                    continue;
                }

                WriteOp w = op as WriteOp;
                if (w != null)
                {
                    bool ok = (t != null) && Ddc.Set(h, w.Code, w.Value);
                    Action<bool> cb = w.Done;
                    if (cb != null) Post(delegate { cb(ok); });
                }
            }

            Ddc.Release(_targets);
        }

        static void SafeRun(Action a)
        {
            try { a(); } catch { /* the bus can vanish mid-operation; that is not fatal */ }
        }

        public void Dispose()
        {
            _stop = true;
            _signal.Set();
            _thread.Join(1500);
        }
    }
}
