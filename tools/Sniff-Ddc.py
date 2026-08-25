"""
Sniff-Ddc.py - logs every DDC/CI call a Windows process makes to the monitor.

Purpose: find out how Dell Display Manager (or any other tool) changes a
setting that plain SetVCPFeature cannot, such as the S2719DGF's preset modes
(see ROADBLOCK.md, point 1). It hooks, inside the target process:

  dxva2.dll   SetVCPFeature, GetVCPFeatureAndVCPFeatureReply,
              CapabilitiesRequestAndCapabilitiesReply, SaveCurrentSettings
  gdi32.dll   D3DKMTEscape           - vendor escapes to the display driver,
                                        the usual back door for raw I2C
  kernel32    DeviceIoControl        - anything else that talks to a driver

Every call is printed with a timestamp, the VCP code, the value written or
read back, and for escapes/ioctls a hex dump of the first bytes. Nothing is
modified: the hooks only observe.

Requires:  pip install frida-tools     (Python 3)
Usage:     python Sniff-Ddc.py DDM.exe            attach to a running process
           python Sniff-Ddc.py --spawn "C:\\...\\DDM.exe"   start it hooked
           python Sniff-Ddc.py 1234               attach by pid
Output goes to the console and to Sniff-Ddc.log next to this script.
Stop with Ctrl+C.

While it runs, change the preset from DDM's window and look for what appears
between the reads: a write to a code we never tried, a D3DKMTEscape with an
I2C payload, or an ioctl - that is the channel DDM uses.
"""
import sys, os, time, frida

SCRIPT = r"""
function hex(p, n) {
    if (p.isNull() || n <= 0) return "";
    n = Math.min(n, 96);
    var b = p.readByteArray(n), a = new Uint8Array(b), s = [];
    for (var i = 0; i < a.length; i++) s.push(("0" + a[i].toString(16)).slice(-2));
    return s.join(" ");
}
function hook(mod, name, onEnter, onLeave) {
    var m = Process.findModuleByName(mod);
    if (m === null) {
        // not loaded yet (dxva2 comes in lazily): loading it ourselves is harmless
        try { m = Module.load(mod); } catch (e) { send("no module " + mod + ": " + e); return; }
    }
    var addr = m.findExportByName(name);
    if (addr === null) { send("no export " + mod + "!" + name); return; }
    Interceptor.attach(addr, { onEnter: onEnter, onLeave: onLeave });
    send("hooked " + mod + "!" + name);
}

hook("dxva2.dll", "SetVCPFeature",
    function (a) { this.c = a[1].toInt32() & 0xff; this.v = a[2].toInt32(); },
    function (r) { send("SET   vcp 0x" + this.c.toString(16).padStart(2,"0") +
                        " <- 0x" + this.v.toString(16).padStart(4,"0") + "  (" + this.v + ")  ok=" + r.toInt32()); });

hook("dxva2.dll", "GetVCPFeatureAndVCPFeatureReply",
    function (a) { this.c = a[1].toInt32() & 0xff; this.cur = a[3]; this.max = a[4]; },
    function (r) {
        var cur = this.cur.isNull() ? -1 : this.cur.readU32();
        var max = this.max.isNull() ? -1 : this.max.readU32();
        send("GET   vcp 0x" + this.c.toString(16).padStart(2,"0") +
             " -> cur=0x" + cur.toString(16).padStart(4,"0") + " (" + cur + ") max=" + max + "  ok=" + r.toInt32()); });

hook("dxva2.dll", "CapabilitiesRequestAndCapabilitiesReply",
    function (a) {}, function (r) { send("CAPS  request  ok=" + r.toInt32()); });

hook("dxva2.dll", "SaveCurrentSettings",
    function (a) {}, function (r) { send("SAVE  current settings  ok=" + r.toInt32()); });

// D3DKMTEscape(D3DKMT_ESCAPE*): { hAdapter, hDevice, Type, Flags, pPrivateDriverData, PrivateDriverDataSize, hContext }
hook("gdi32.dll", "D3DKMTEscape",
    function (a) {
        var p = a[0];
        var type = p.add(8).readU32();
        var data = p.add(16).readPointer();
        var size = p.add(24).readU32();
        this.line = "ESC   type=" + type + " size=" + size + "  " + hex(data, size);
        this.data = data; this.size = size;
    },
    function (r) { send(this.line + "  -> ret=0x" + (r.toInt32() >>> 0).toString(16) +
                        (this.size > 0 ? "  after: " + hex(this.data, this.size) : "")); });

// Intel Graphics Control Library: raw I2C / DP-AUX to the monitor, the way
// Dell Display and Peripheral Manager reaches what MCCS does not expose.
// ctl_i2c_access_args_t: { Size u32, Version u8, DataSize u32, Address u32,
//   OpType u32 (1=read 2=write), Offset u32, Flags u32, Data[128] }
["ctlI2CAccess", "ctlI2CAccessOnPort", "ctlAUXAccess"].forEach(function (name) {
    if (Process.findModuleByName("IntelControlLib.dll") === null) return;
    hook("IntelControlLib.dll", name,
        function (a) { this.p = a[1]; this.line = "IGCL  " + name + "  args=" + hex(this.p, 160); },
        function (r) { send(this.line + "  -> ret=0x" + (r.toInt32() >>> 0).toString(16) +
                            "  after=" + hex(this.p, 160)); });
});

hook("kernel32.dll", "DeviceIoControl",
    function (a) {
        this.code = a[1].toInt32() >>> 0; this.inb = a[2]; this.inn = a[3].toInt32();
        this.outb = a[4]; this.outn = a[5].toInt32();
    },
    function (r) {
        send("IOCTL 0x" + this.code.toString(16).padStart(8,"0") + " in[" + this.inn + "]=" + hex(this.inb, this.inn) +
             "  out[" + this.outn + "]=" + hex(this.outb, this.outn) + "  ok=" + r.toInt32()); });
"""

def main():
    if len(sys.argv) < 2:
        print(__doc__); sys.exit(1)
    log = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "Sniff-Ddc.log"), "a", encoding="utf-8")
    t0 = time.time()

    def out(msg):
        line = "%8.3f  %s" % (time.time() - t0, msg)
        print(line, flush=True); log.write(line + "\n"); log.flush()

    def on_message(m, data):
        out(m.get("payload") if m.get("type") == "send" else str(m))

    dev = frida.get_local_device()
    if sys.argv[1] == "--spawn":
        pid = dev.spawn(sys.argv[2:]); session = dev.attach(pid); spawned = True
    else:
        target = int(sys.argv[1]) if sys.argv[1].isdigit() else sys.argv[1]
        session = dev.attach(target); spawned = False
    script = session.create_script(SCRIPT)
    script.on("message", on_message)
    script.load()
    if spawned: dev.resume(pid)
    out("attached to %s - change the setting now; Ctrl+C to stop" % sys.argv[1:])
    try:
        while True: time.sleep(0.5)
    except KeyboardInterrupt:
        pass
    finally:
        session.detach(); log.close()

if __name__ == "__main__":
    main()
