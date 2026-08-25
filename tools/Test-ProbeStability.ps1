<#
.SYNOPSIS
    Ripete la scoperta dei controlli e riporta quali compaiono sempre, quali
    mai, e quali vanno e vengono.

.DESCRIPTION
    L'applicazione costruisce il pannello sondando i codici VCP a ogni avvio e
    a ogni cambio di monitor. Se il sondaggio non e' ripetibile, il pannello
    cambia da solo fra un avvio e l'altro — controlli che appaiono e
    scompaiono senza che sia cambiato niente.

    Questa prova fa girare lo stesso sondaggio N volte di fila e conta, per
    ogni codice, quante volte e' stato promosso. Qualunque cosa non sia 0/N o
    N/N e' un difetto: o del bus, o della regola che decide.

    E' di sola lettura.

    IMPORTANTE: chiudi Deskside.exe prima di lanciarla. Due processi sul
    bus DDC si rubano le risposte a vicenda — su un monitor che risponde
    ripetendo l'ultima risposta valida questo basta da solo a far ballare il
    risultato.

.PARAMETER Rounds
    Quante volte ripetere il sondaggio. Default 5.

.EXAMPLE
    .\tools\Test-ProbeStability.ps1
.EXAMPLE
    .\tools\Test-ProbeStability.ps1 -Rounds 10 -Monitor 1
#>
[CmdletBinding()]
param(
    [int]$Monitor = 0,
    [ValidateRange(2, 50)][int]$Rounds = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'DdcCi.ps1')

$Sentinel = [byte]0x33
$SliderCodes = [byte[]](0x10, 0x12, 0x62, 0x0C, 0x87, 0x16, 0x18, 0x1A, 0x6C, 0x6E, 0x70)
$ChoiceCodes = [byte[]](0x60, 0x14, 0x86, 0x8D, 0xCC)

$Names = @{
    0x10 = 'luminosita'; 0x12 = 'contrasto'; 0x62 = 'volume'; 0x0C = 'temp. colore'
    0x87 = 'nitidezza'; 0x16 = 'guadagno rosso'; 0x18 = 'guadagno verde'
    0x1A = 'guadagno blu'; 0x6C = 'nero rosso'; 0x6E = 'nero verde'; 0x70 = 'nero blu'
    0x60 = 'ingresso'; 0x14 = 'preset colore'; 0x86 = 'scalatura'; 0x8D = 'audio'
    0xCC = 'lingua OSD'
}

function Format-Code([byte]$c) {
    $n = if ($Names.ContainsKey([int]$c)) { $Names[[int]$c] } else { '' }
    if ($n) { '0x{0:X2} {1}' -f $c, $n } else { '0x{0:X2}' -f $c }
}

function Read-Once {
    param([IntPtr]$Handle, [byte]$Code)
    $type = 0; $cur = 0; $max = 0
    $ok = [DdcCi]::GetVCPFeatureAndVCPFeatureReply($Handle, $Code, [ref]$type, [ref]$cur, [ref]$max)
    [pscustomobject]@{ Ok = $ok; Current = [int]$cur; Maximum = [int]$max }
}

function Read-Retrying {
    param([IntPtr]$Handle, [byte]$Code, [int]$Tries = 3)
    for ($i = 0; $i -lt $Tries; $i++) {
        $r = Read-Once $Handle $Code
        if ($r.Ok) { return $r }
        Start-Sleep -Milliseconds 40
    }
    return $r
}

function Same-Reply($a, $b) {
    return ($a.Ok -and $b.Ok -and $a.Current -eq $b.Current -and $a.Maximum -eq $b.Maximum)
}

# Lo stesso ragionamento di TrayApp.ProbeFeatures, in piccolo.
function Invoke-Probe {
    param([IntPtr]$Handle, [byte[]]$Candidates, [hashtable]$Declared)
    $accepted = @{}
    $prev = Read-Once $Handle $Sentinel
    $echoes = $prev.Ok
    $real = @()
    foreach ($c in $Candidates) {
        $v = Read-Retrying $Handle $c
        if (-not $v.Ok) { continue }
        $genuine = $true
        if ($echoes -and (Same-Reply $v $prev)) {
            $primer = $real | Where-Object { -not (Same-Reply $_ $v) } | Select-Object -First 1
            if ($primer) {
                [void](Read-Once $Handle $primer.Code)
                $prev = $primer
                $v = Read-Retrying $Handle $c
                $genuine = ($v.Ok -and -not (Same-Reply $v $prev))
            }
            else { $genuine = $Declared.ContainsKey([int]$c) }
        }
        if ($v.Ok) { $prev = $v }
        if (-not $genuine) { continue }
        $accepted[[int]$c] = $v.Current
        $real += ([pscustomobject]@{ Ok = $v.Ok; Current = $v.Current; Maximum = $v.Maximum; Code = $c })
    }
    return $accepted
}

$all = @([DdcCi]::Enumerate())
if ($all.Count -eq 0) { throw 'Nessun monitor fisico trovato.' }
$h = $all[$Monitor].hPhysicalMonitor

try {
    Write-Host ''
    Write-Host 'Deskside - ripetibilita'' della scoperta dei controlli' -ForegroundColor White
    Write-Host ("monitor {0} : {1}" -f $Monitor, $all[$Monitor].szPhysicalMonitorDescription)
    if (Get-Process Deskside -ErrorAction SilentlyContinue) {
        Write-Host 'ATTENZIONE: Deskside.exe e'' in esecuzione e usa lo stesso bus:' -ForegroundColor Yellow
        Write-Host '            il risultato di questa prova non vale. Chiudilo.' -ForegroundColor Yellow
    }

    $len = 0; $caps = ''
    if ([DdcCi]::GetCapabilitiesStringLength($h, [ref]$len) -and $len -gt 0) {
        $sb = New-Object System.Text.StringBuilder ([int]$len)
        if ([DdcCi]::CapabilitiesRequestAndCapabilitiesReply($h, $sb, $len)) { $caps = $sb.ToString() }
    }
    $declared = @{}
    foreach ($t in [regex]::Matches($caps, '(?m)\b([0-9A-Fa-f]{2})(?=\s|\()')) {
        $declared[[Convert]::ToInt32($t.Groups[1].Value, 16)] = $true
    }

    $candidates = [byte[]]@($SliderCodes + $ChoiceCodes)
    $count = @{}
    $values = @{}
    foreach ($c in $candidates) { $count[[int]$c] = 0; $values[[int]$c] = @() }

    for ($r = 1; $r -le $Rounds; $r++) {
        Write-Host ("giro {0}/{1}..." -f $r, $Rounds) -NoNewline
        $got = Invoke-Probe -Handle $h -Candidates $candidates -Declared $declared
        Write-Host (" {0} controlli" -f $got.Count)
        foreach ($k in $got.Keys) {
            $count[$k]++
            $values[$k] += $got[$k]
        }
    }

    Write-Host ''
    $rows = @()
    $flaky = 0
    foreach ($c in $candidates) {
        $k = [int]$c
        $esito = if ($count[$k] -eq $Rounds) { 'sempre' }
                 elseif ($count[$k] -eq 0) { 'mai' }
                 else { 'BALLERINO' }
        if ($esito -eq 'BALLERINO') { $flaky++ }
        $rows += [pscustomobject]@{
            Codice     = Format-Code $c
            Promosso   = "$($count[$k])/$Rounds"
            Esito      = $esito
            'valori'   = (($values[$k] | Select-Object -Unique) -join ' ')
        }
    }
    $rows | Format-Table -AutoSize | Out-String | Write-Host

    if ($flaky -eq 0) {
        Write-Host ("Scoperta ripetibile: {0} giri identici." -f $Rounds) -ForegroundColor Green
    }
    else {
        Write-Host ("{0} codici ballerini: il pannello cambia da solo fra un avvio e l'altro." -f $flaky) -ForegroundColor Red
    }
}
finally {
    [void][DdcCi]::DestroyPhysicalMonitors(1, @($all[$Monitor]))
}
