<#
.SYNOPSIS
    Legge il bCountryCode dal descrittore HID delle tastiere USB, interrogando
    direttamente l'hub a cui sono collegate.

.DESCRIPTION
    E' l'unico posto in cui una tastiera USB puo' dichiarare il proprio layout.
    Windows non lo espone da nessuna parte (non e' nel registro, non e' in
    HID_COLLECTION_INFORMATION), quindi bisogna chiederlo all'hardware:

      1. si enumerano gli hub USB (GUID_DEVINTERFACE_USB_HUB);
      2. per ogni porta si chiede USB_NODE_CONNECTION_INFORMATION_EX, che
         contiene il device descriptor con VID/PID;
      3. per la porta giusta si richiede il configuration descriptor con
         IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION;
      4. dentro si cerca il descrittore HID (bDescriptorType 0x21): il quinto
         byte e' bCountryCode.

    Valori: 0 = non localizzata (la stragrande maggioranza delle tastiere),
    13 = International (ISO), 33 = US, 8 = Francia, 15 = Italia...
    Tabella completa nella HID Device Class Definition, sezione 6.2.1.

.EXAMPLE
    .\Get-HidCountryCode.ps1
    .\Get-HidCountryCode.ps1 -VendorId 2F68 -ProductId 0082
#>
[CmdletBinding()]
param(
    # $PID e' una variabile riservata di PowerShell: parametri e variabili
    # locali usano nomi estesi
    [string]$VendorId,
    [string]$ProductId
)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class Usb
{
    public const uint GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
    public const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x220448;
    public const uint IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION = 0x220410;
    public const uint IOCTL_USB_GET_NODE_INFORMATION = 0x220408;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid,
        int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data,
        IntPtr detail, int detailSize, ref int required, IntPtr devInfo);
    [DllImport("setupapi.dll")]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inSize,
        byte[] outBuf, int outSize, ref int returned, IntPtr overlapped);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);

    // elenco dei percorsi \\?\usb#... degli hub
    public static string[] HubPaths()
    {
        Guid guid = new Guid("f18a0e88-c30c-11d0-8815-00a0c906bed8");   // GUID_DEVINTERFACE_USB_HUB
        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT|DEVICEINTERFACE
        if (set == (IntPtr)(-1)) return new string[0];

        System.Collections.Generic.List<string> paths = new System.Collections.Generic.List<string>();
        SP_DEVICE_INTERFACE_DATA data = new SP_DEVICE_INTERFACE_DATA();
        data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));

        for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
        {
            int need = 0;
            SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, ref need, IntPtr.Zero);
            if (need <= 0) continue;
            IntPtr buf = Marshal.AllocHGlobal(need);
            try
            {
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);   // cbSize di SP_DEVICE_INTERFACE_DETAIL_DATA
                if (SetupDiGetDeviceInterfaceDetailW(set, ref data, buf, need, ref need, IntPtr.Zero))
                    paths.Add(Marshal.PtrToStringUni((IntPtr)(buf.ToInt64() + 4)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        SetupDiDestroyDeviceInfoList(set);
        return paths.ToArray();
    }
}
'@

function Get-Bytes([int]$n) { , (New-Object byte[] $n) }

$results = @()
foreach ($hub in [Usb]::HubPaths()) {
    $h = [Usb]::CreateFileW($hub, [Usb]::GENERIC_WRITE, [Usb]::FILE_SHARE_RW, [IntPtr]::Zero, [Usb]::OPEN_EXISTING, 0, [IntPtr]::Zero)
    if ($h -eq [IntPtr](-1)) { continue }
    try {
        foreach ($port in 1..30) {
            # USB_NODE_CONNECTION_INFORMATION_EX: 4 byte indice + 18 di device descriptor + coda
            $buf = New-Object byte[] 300
            [BitConverter]::GetBytes([int]$port).CopyTo($buf, 0)
            $ret = 0
            if (-not [Usb]::DeviceIoControl($h, [Usb]::IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, $buf, $buf.Length, $buf, $buf.Length, [ref]$ret, [IntPtr]::Zero)) { continue }

            $dd = 4          # offset del device descriptor
            $devVid = [BitConverter]::ToUInt16($buf, $dd + 8)
            $devPid = [BitConverter]::ToUInt16($buf, $dd + 10)
            if ($devVid -eq 0) { continue }
            if ($VendorId  -and ([Convert]::ToInt32($VendorId, 16)  -ne $devVid)) { continue }
            if ($ProductId -and ([Convert]::ToInt32($ProductId, 16) -ne $devPid)) { continue }

            # stringhe di produttore e prodotto: gli indici stanno negli
            # ultimi byte del device descriptor
            $strings = @{}
            foreach ($f in @{n='Produttore';i=14}, @{n='Prodotto';i=15}, @{n='Seriale';i=16}) {
                $idx = $buf[$dd + $f.i]
                if ($idx -eq 0) { continue }
                $sr = New-Object byte[] 268
                [BitConverter]::GetBytes([int]$port).CopyTo($sr, 0)
                $sr[4] = 0x80; $sr[5] = 0x06
                [BitConverter]::GetBytes([uint16](0x0300 -bor $idx)).CopyTo($sr, 6)
                [BitConverter]::GetBytes([uint16]0x0409).CopyTo($sr, 8)   # inglese US
                [BitConverter]::GetBytes([uint16]($sr.Length - 12)).CopyTo($sr, 10)
                $sret = 0
                if ([Usb]::DeviceIoControl($h, [Usb]::IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION, $sr, $sr.Length, $sr, $sr.Length, [ref]$sret, [IntPtr]::Zero)) {
                    $slen = $sr[12]
                    if ($slen -gt 2) { $strings[$f.n] = [Text.Encoding]::Unicode.GetString($sr, 14, $slen - 2).Trim() }
                }
            }

            # configuration descriptor: USB_DESCRIPTOR_REQUEST (12 byte) + dati
            $req = New-Object byte[] 1036
            [BitConverter]::GetBytes([int]$port).CopyTo($req, 0)
            $req[4] = 0x80                                   # bmRequest: device-to-host
            $req[5] = 0x06                                   # GET_DESCRIPTOR
            [BitConverter]::GetBytes([uint16]0x0200).CopyTo($req, 6)   # wValue: configuration, index 0
            [BitConverter]::GetBytes([uint16]0).CopyTo($req, 8)
            [BitConverter]::GetBytes([uint16]($req.Length - 12)).CopyTo($req, 10)
            $ret = 0
            if (-not [Usb]::DeviceIoControl($h, [Usb]::IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION, $req, $req.Length, $req, $req.Length, [ref]$ret, [IntPtr]::Zero)) { continue }

            # si scorrono i descrittori concatenati cercando quelli HID (0x21)
            $i = 12
            $end = [Math]::Min($ret, $req.Length)
            $countries = @()
            while ($i -lt $end -and $req[$i] -gt 0) {
                $len = $req[$i]; $type = $req[$i + 1]
                if ($type -eq 0x21 -and ($i + 4) -lt $end) { $countries += $req[$i + 4] }
                $i += $len
            }
            $results += [pscustomobject]@{
                Hub          = ($hub -split '#')[1]
                Porta        = $port
                VID          = ('{0:X4}' -f $devVid)
                PID          = ('{0:X4}' -f $devPid)
                Produttore   = $strings['Produttore']
                Prodotto     = $strings['Prodotto']
                CountryCodes = ($countries -join ', ')
            }
        }
    } finally { [void][Usb]::CloseHandle($h) }
}

if (-not $results) { "Nessun dispositivo USB trovato con quei criteri." ; return }
$results | Format-Table -AutoSize

@'
bCountryCode: 0 = non localizzata, 13 = International (ISO), 33 = US,
8 = Francia, 15 = Italia, 32 = Regno Unito. Se e' 0 il layout NON e'
deducibile dall'hardware: va mappato a mano.
'@
