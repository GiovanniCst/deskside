<#
.SYNOPSIS
    Misura come e in quanto tempo il monitor collegato risponde ai comandi
    DDC/CI: latenza di lettura e di scrittura, pacchetti persi, e soprattutto
    il tempo di assestamento — quanto passa fra una scrittura e il momento in
    cui la rilettura restituisce il valore appena scritto.

.DESCRIPTION
    Serve a tarare i ritardi dell'applicazione con dei numeri invece che a
    occhio. Il dato che conta e' l'assestamento: se il monitor accetta la
    scrittura ma per qualche decina di millisecondi continua a rispondere il
    valore vecchio, una rilettura di verifica troppo pronta lo dichiara
    "rifiutato" mentre invece e' andato a buon fine.

    I test sono non distruttivi: ogni valore scritto viene ripristinato.
    Gli elenchi (ingresso, preset, alimentazione) non vengono toccati se non
    con -IncludeChoices, e ingresso e alimentazione mai — cambiarli spegne lo
    schermo o lo porta su un'altra sorgente.

.PARAMETER Monitor
    Indice del monitor, come da Get-Monitors. Default 0.

.PARAMETER Samples
    Quante misure per ogni latenza. Default 7.

.PARAMETER Codes
    Codici da provare (es. 0x10,0x16). Se omesso li scopre da solo.

.PARAMETER IncludeChoices
    Prova anche i codici a scelta multipla (preset colore, scalatura, muto).
    Si vedono a schermo mentre cambiano. Ingresso e alimentazione restano
    comunque esclusi.

.PARAMETER SkipWrites
    Solo letture: non scrive nulla, quindi niente assestamento.

.EXAMPLE
    .\tools\Test-MonitorTiming.ps1
.EXAMPLE
    .\tools\Test-MonitorTiming.ps1 -Monitor 1 -Samples 15 -IncludeChoices
.EXAMPLE
    .\tools\Test-MonitorTiming.ps1 -Codes 0x10,0x16 -SkipWrites
#>
[CmdletBinding()]
param(
    [int]$Monitor = 0,
    [ValidateRange(1, 200)][int]$Samples = 7,
    [string[]]$Codes,
    [switch]$IncludeChoices,
    [switch]$SkipWrites,
    [ValidateRange(0, 500)][int]$PollMs = 0,
    [ValidateRange(100, 20000)][int]$MaxWaitMs = 3000,
    # quanto aspetta l'applicazione prima di verificare (TrayApp._verify)
    [ValidateRange(0, 10000)][int]$VerifyMs = 700,
    [ValidateRange(1, 20)][int]$BurstRounds = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'DdcCi.ps1')

# codice riservato che nessun monitor implementa: fa da sentinella per
# riconoscere chi risponde anche a cio' che non ha (vedi README)
$Sentinel = [byte]0x33
# mai scritti: cambiare ingresso porta lo schermo su un'altra sorgente,
# l'alimentazione lo spegne
$NeverWrite = @([byte]0x60, [byte]0xD6)

$SliderCodes = [byte[]](0x10, 0x12, 0x62, 0x0C, 0x87, 0x16, 0x18, 0x1A, 0x6C, 0x6E, 0x70)
$ChoiceCodes = [byte[]](0x60, 0x14, 0x86, 0x8D, 0xCC, 0xE0, 0xEA)

$Names = @{
    0x10 = 'luminosita'; 0x12 = 'contrasto'; 0x62 = 'volume'; 0x0C = 'temp. colore'
    0x87 = 'nitidezza'; 0x16 = 'guadagno rosso'; 0x18 = 'guadagno verde'
    0x1A = 'guadagno blu'; 0x6C = 'nero rosso'; 0x6E = 'nero verde'; 0x70 = 'nero blu'
    0x60 = 'ingresso'; 0x14 = 'preset colore'; 0x86 = 'scalatura'; 0x8D = 'audio'
    0xCC = 'lingua OSD'
    # vendor: questi due significano Over Drive e Dynamic Contrast SU LENOVO.
    # Su un'altra marca lo stesso numero puo' voler dire qualunque cosa, quindi
    # qui si segnalano come vendor e basta.
    0xE0 = 'vendor (0xE0)'; 0xEA = 'vendor (0xEA)'
}

function Format-Code([byte]$c) {
    $n = if ($Names.ContainsKey([int]$c)) { $Names[[int]$c] } else { '' }
    if ($n) { '0x{0:X2} {1}' -f $c, $n } else { '0x{0:X2}' -f $c }
}

# ------------------------------------------------------------- primitive --
# Lettura e scrittura in un colpo solo, senza i ritentativi del modulo: qui
# serve sapere quante volte il bus perde davvero un pacchetto.

function Read-Once {
    param([IntPtr]$Handle, [byte]$Code)
    $type = 0; $cur = 0; $max = 0
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $ok = [DdcCi]::GetVCPFeatureAndVCPFeatureReply($Handle, $Code, [ref]$type, [ref]$cur, [ref]$max)
    $sw.Stop()
    [pscustomobject]@{
        Ok = $ok; Current = [int]$cur; Maximum = [int]$max
        Ms = $sw.Elapsed.TotalMilliseconds
    }
}

function Write-Once {
    param([IntPtr]$Handle, [byte]$Code, [int]$Value)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $ok = [DdcCi]::SetVCPFeature($Handle, $Code, [uint32]$Value)
    $sw.Stop()
    [pscustomobject]@{ Ok = $ok; Ms = $sw.Elapsed.TotalMilliseconds }
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

function Get-Stats([double[]]$values) {
    if ($values.Count -eq 0) { return $null }
    $s = $values | Sort-Object
    [pscustomobject]@{
        Min    = $s[0]
        Median = $s[[int]([Math]::Floor($s.Count / 2))]
        Max    = $s[-1]
        Mean   = ($values | Measure-Object -Average).Average
    }
}

function Same-Reply($a, $b) {
    return ($a.Ok -and $b.Ok -and $a.Current -eq $b.Current -and $a.Maximum -eq $b.Maximum)
}

function Write-Section([string]$t) {
    Write-Host ''
    Write-Host $t -ForegroundColor Cyan
    Write-Host ('-' * $t.Length) -ForegroundColor DarkGray
}

# ------------------------------------------------------------- apparecchio --
$all = @([DdcCi]::Enumerate())
if ($all.Count -eq 0) { throw 'Nessun monitor fisico trovato.' }
if ($Monitor -lt 0 -or $Monitor -ge $all.Count) {
    throw "Indice monitor $Monitor fuori range (0..$($all.Count - 1))."
}
$h = $all[$Monitor].hPhysicalMonitor
$desc = $all[$Monitor].szPhysicalMonitorDescription

try {
    Write-Host ''
    Write-Host "Deskside - prova dei tempi di risposta DDC/CI" -ForegroundColor White
    Write-Host "monitor $Monitor : $desc"

    if (Get-Process Deskside -ErrorAction SilentlyContinue) {
        Write-Host 'ATTENZIONE: Deskside.exe e'' in esecuzione e usa lo stesso bus.' -ForegroundColor Yellow
        Write-Host '            I tempi saranno peggiori del vero. Chiudilo per una misura pulita.' -ForegroundColor Yellow
    }

    # -------------------------------------------------- capability string --
    Write-Section 'Stringa di capability'
    $len = 0
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $okLen = [DdcCi]::GetCapabilitiesStringLength($h, [ref]$len)
    $caps = ''
    if ($okLen -and $len -gt 0) {
        $sb = New-Object System.Text.StringBuilder ([int]$len)
        if ([DdcCi]::CapabilitiesRequestAndCapabilitiesReply($h, $sb, $len)) { $caps = $sb.ToString() }
    }
    $sw.Stop()
    Write-Host ("lettura        : {0,7:F0} ms, {1} caratteri" -f $sw.Elapsed.TotalMilliseconds, $caps.Length)
    $declared = @{}
    foreach ($t in [regex]::Matches($caps, '(?m)\b([0-9A-Fa-f]{2})(?=\s|\()')) {
        $b = [Convert]::ToInt32($t.Groups[1].Value, 16)
        $declared[$b] = $true
    }
    Write-Host ("codici citati  : {0}" -f $declared.Count)

    # ------------------------------------------------------------- eco --
    Write-Section 'Risposte inventate (eco)'
    $echo = Read-Once $h $Sentinel
    $echoes = $echo.Ok
    if ($echoes) {
        Write-Host ("il codice riservato 0x{0:X2} RISPONDE: cur={1} max={2}" -f $Sentinel, $echo.Current, $echo.Maximum) -ForegroundColor Yellow
        Write-Host 'Questo monitor risponde anche ai codici che non ha, ripetendo'
        Write-Host 'l''ultima risposta valida. Un codice conta come reale solo se la'
        Write-Host 'sua risposta e'' diversa dalla precedente.'
    }
    else {
        Write-Host ("il codice riservato 0x{0:X2} non risponde: il monitor e'' onesto" -f $Sentinel) -ForegroundColor Green
    }

    # ------------------------------------------------ codici da provare --
    Write-Section 'Codici che rispondono davvero'
    if ($Codes) {
        $candidates = [byte[]]@($Codes | ForEach-Object { [byte][Convert]::ToInt32(($_ -replace '^0x', ''), 16) })
    }
    else {
        $candidates = [byte[]]@($SliderCodes + $ChoiceCodes)
    }

    $live = @()
    $rejected = @()
    $prev = $echo
    foreach ($c in $candidates) {
        $v = Read-Retrying $h $c
        if (-not $v.Ok) { $rejected += [pscustomobject]@{ Code = $c; Why = 'non risponde' }; continue }
        $genuine = $true
        if ($echoes -and (Same-Reply $v $prev)) {
            # ambiguo: o non lo supporta, o il suo valore coincide con l'eco.
            # si rinnesca l'eco con un codice gia' promosso di valore diverso
            $primer = $live | Where-Object { -not (Same-Reply $_.Value $v) } | Select-Object -First 1
            if ($primer) {
                [void](Read-Once $h $primer.Code)
                $prev = $primer.Value
                $v = Read-Retrying $h $c
                $genuine = ($v.Ok -and -not (Same-Reply $v $prev))
            }
            else { $genuine = $declared.ContainsKey([int]$c) }
        }
        if ($v.Ok) { $prev = $v }
        if (-not $genuine) { $rejected += [pscustomobject]@{ Code = $c; Why = 'eco del precedente' }; continue }
        $live += [pscustomobject]@{
            Code   = $c
            Value  = $v
            Choice = ($ChoiceCodes -contains $c)
        }
    }

    foreach ($l in $live) {
        Write-Host ("  {0,-24} {1,5} / {2}" -f (Format-Code $l.Code), $l.Value.Current, $l.Value.Maximum)
    }
    foreach ($r in $rejected) {
        Write-Host ("  {0,-24} {1}" -f (Format-Code $r.Code), $r.Why) -ForegroundColor DarkGray
    }
    if ($live.Count -eq 0) { throw 'Nessun codice utilizzabile: non c''e'' niente da misurare.' }

    # ------------------------------------------------- latenza in lettura --
    Write-Section "Latenza di lettura ($Samples misure per codice)"
    $readRows = @()
    $lost = 0; $tried = 0
    foreach ($l in $live) {
        $ms = @()
        for ($i = 0; $i -lt $Samples; $i++) {
            $r = Read-Once $h $l.Code
            $tried++
            if ($r.Ok) { $ms += $r.Ms } else { $lost++ }
        }
        $st = Get-Stats $ms
        $readRows += [pscustomobject]@{
            Codice   = Format-Code $l.Code
            'min ms' = if ($st) { [Math]::Round($st.Min, 1) } else { $null }
            'med ms' = if ($st) { [Math]::Round($st.Median, 1) } else { $null }
            'max ms' = if ($st) { [Math]::Round($st.Max, 1) } else { $null }
            'persi'  = $Samples - $ms.Count
        }
    }
    $readRows | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host ("pacchetti persi: {0} su {1} letture singole ({2:P1})" -f $lost, $tried, ($lost / [Math]::Max(1, $tried)))

    if ($SkipWrites) {
        Write-Host ''
        Write-Host '-SkipWrites: scritture e assestamento saltati.' -ForegroundColor Yellow
        return
    }

    # ------------------------------------- scrittura e tempo di assestamento --
    Write-Section 'Scrittura e tempo di assestamento'
    Write-Host 'Si scrive un valore diverso, si rilegge in continuazione finche'' non'
    Write-Host 'compare, poi si ripristina. "assest." e'' il ritardo da cui in poi una'
    Write-Host 'rilettura di verifica dice la verita''.'
    Write-Host ''

    $settleRows = @()
    $worst = 0.0            # solo da riletture col valore vecchio: e' quello il vero assestamento
    $lostAfterWrite = 0
    foreach ($l in $live) {
        $c = $l.Code
        if ($NeverWrite -contains $c) {
            $settleRows += [pscustomobject]@{
                Codice = Format-Code $c; 'scritt. ms' = $null; 'assest. ms' = $null
                'letture' = $null; Esito = 'non provato (spegne o cambia sorgente)'
            }
            continue
        }
        if ($l.Choice -and -not $IncludeChoices) {
            $settleRows += [pscustomobject]@{
                Codice = Format-Code $c; 'scritt. ms' = $null; 'assest. ms' = $null
                'letture' = $null; Esito = 'saltato (usa -IncludeChoices)'
            }
            continue
        }

        $before = Read-Retrying $h $c
        if (-not $before.Ok) { continue }

        if ($l.Choice) {
            # per un elenco serve un altro valore ammesso: si prende dalle
            # capability, altrimenti non si prova
            $alt = $null
            $vals = [regex]::Match($caps, ('(?i)\b{0:X2}\s*\(([^)]*)\)' -f $c))
            if ($vals.Success) {
                foreach ($tok in ($vals.Groups[1].Value -split '\s+' | Where-Object { $_ })) {
                    $n = [Convert]::ToInt32($tok, 16)
                    if ($n -ne ($before.Current -band 0xFF)) { $alt = $n; break }
                }
            }
            if ($null -eq $alt) {
                $settleRows += [pscustomobject]@{
                    Codice = Format-Code $c; 'scritt. ms' = $null; 'assest. ms' = $null
                    'letture' = $null; Esito = 'nessun altro valore ammesso noto'
                }
                continue
            }
            $target = $alt
            $restore = $before.Current -band 0xFF
        }
        else {
            $target = if ($before.Current -ge $before.Maximum) { [Math]::Max(0, $before.Current - 1) }
                      else { $before.Current + 1 }
            $restore = $before.Current
        }

        $w = Write-Once $h $c $target
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $settled = $null; $reads = 0; $lastSeen = $null
        # una rilettura puo' non combaciare per due motivi molto diversi: il
        # bus ha perso il pacchetto, oppure il monitor risponde ancora il
        # valore vecchio. Solo il secondo e' un tempo di assestamento, e
        # confonderli porta a tarare l'attesa sul motivo sbagliato.
        $failedReads = 0; $staleReads = 0
        while ($sw.Elapsed.TotalMilliseconds -lt $MaxWaitMs) {
            $r = Read-Once $h $c
            $reads++
            if (-not $r.Ok) { $failedReads++ }
            else {
                $lastSeen = $r.Current
                if (($r.Current -band 0xFF) -eq ($target -band 0xFF)) {
                    $settled = $sw.Elapsed.TotalMilliseconds
                    break
                }
                $staleReads++
            }
            if ($PollMs -gt 0) { Start-Sleep -Milliseconds $PollMs }
        }
        $sw.Stop()

        # ripristino, e attesa che sia davvero tornato indietro
        [void](Write-Once $h $c $restore)
        $back = $null
        $sw2 = [Diagnostics.Stopwatch]::StartNew()
        while ($sw2.Elapsed.TotalMilliseconds -lt $MaxWaitMs) {
            $r = Read-Once $h $c
            if ($r.Ok -and ($r.Current -band 0xFF) -eq ($restore -band 0xFF)) { $back = $sw2.Elapsed.TotalMilliseconds; break }
            if ($PollMs -gt 0) { Start-Sleep -Milliseconds $PollMs }
        }
        $sw2.Stop()

        $esito = if (-not $w.Ok) { 'scrittura rifiutata dal bus' }
                 elseif ($null -eq $settled) { "IGNORATA: resta a $lastSeen" }
                 elseif ($reads -le 1) { 'immediata' }
                 elseif ($staleReads -gt 0 -and $failedReads -gt 0) { "$staleReads vecchie + $failedReads perse" }
                 elseif ($staleReads -gt 0) { "$staleReads riletture col valore vecchio" }
                 else { "$failedReads pacchetti persi (non e'' assestamento)" }
        if ($staleReads -gt 0 -and $null -ne $settled -and $settled -gt $worst) { $worst = $settled }
        if ($failedReads -gt 0) { $lostAfterWrite++ }
        if ($null -eq $back -and $null -ne $settled) { $esito += ' / RIPRISTINO NON RIUSCITO' }

        $settleRows += [pscustomobject]@{
            Codice        = Format-Code $c
            'scritt. ms'  = [Math]::Round($w.Ms, 1)
            'assest. ms'  = if ($null -ne $settled) { [Math]::Round($settled, 1) } else { $null }
            'letture'     = $reads
            Esito         = $esito
        }
    }
    $settleRows | Format-Table -AutoSize | Out-String | Write-Host

    # -------------------------------------- assestamento in funzione del salto --
    # Un conto e' spostare un cursore di una tacca, un altro e' portarlo da 75
    # a 90: certi monitor ci arrivano gradualmente, e nel frattempo rispondono
    # un valore intermedio. E' la differenza fra "il comando e' stato rifiutato"
    # e "il comando sta ancora arrivando", e si vede solo provando salti veri.
    Write-Section 'Assestamento in funzione del salto'
    Write-Host 'Stessa prova di sopra, ma con salti di ampiezza crescente. Se i'
    Write-Host 'tempi crescono col salto, il monitor ci arriva per gradi.'
    Write-Host ''

    $jumps = @(1, 5, 15, 30)
    $rampRows = @()
    $worstRamp = 0.0
    foreach ($l in $live) {
        $c = $l.Code
        if ($l.Choice -or ($NeverWrite -contains $c)) { continue }

        $row = [ordered]@{ Codice = Format-Code $c }
        foreach ($d in $jumps) {
            $before = Read-Retrying $h $c
            if (-not $before.Ok -or $before.Maximum -lt 2) { $row["+$d"] = $null; continue }
            # il salto deve stare nel range, altrimenti il monitor lo taglia e
            # sembra che abbia ignorato il comando
            $target = if (($before.Current + $d) -le $before.Maximum) { $before.Current + $d }
                      elseif (($before.Current - $d) -ge 0) { $before.Current - $d }
                      else { $null }
            if ($null -eq $target) { $row["+$d"] = $null; continue }

            [void](Write-Once $h $c $target)
            $sw3 = [Diagnostics.Stopwatch]::StartNew()
            $ms = $null; $stale = 0
            while ($sw3.Elapsed.TotalMilliseconds -lt $MaxWaitMs) {
                $r = Read-Once $h $c
                if ($r.Ok) {
                    if ($r.Current -eq $target) { $ms = $sw3.Elapsed.TotalMilliseconds; break }
                    $stale++
                }
                if ($PollMs -gt 0) { Start-Sleep -Milliseconds $PollMs }
            }
            $sw3.Stop()
            # marcato con * quando il ritardo e' dovuto a valori intermedi
            # letti davvero, non a pacchetti persi
            $row["+$d"] = if ($null -eq $ms) { 'ignorato' }
                          elseif ($stale -gt 0) { ('{0:F0}*' -f $ms) }
                          else { '{0:F0}' -f $ms }
            if ($stale -gt 0 -and $ms -gt $worstRamp) { $worstRamp = $ms }

            [void](Write-Once $h $c $before.Current)
            $sw4 = [Diagnostics.Stopwatch]::StartNew()
            while ($sw4.Elapsed.TotalMilliseconds -lt $MaxWaitMs) {
                $r = Read-Once $h $c
                if ($r.Ok -and $r.Current -eq $before.Current) { break }
            }
            $sw4.Stop()
        }
        $rampRows += [pscustomobject]$row
    }
    if ($rampRows.Count -gt 0) {
        $rampRows | Format-Table -AutoSize | Out-String | Write-Host
        Write-Host 'ms fino alla prima rilettura che combacia. * = si sono lette'
        Write-Host 'posizioni intermedie, cioe'' il monitor ci sta arrivando per gradi.'
    }

    # ------------------------------------------------- raffica di scritture --
    # Trascinando un cursore non parte una scrittura: ne parte una raffica.
    # L'applicazione ne scarta il piu' possibile, ma qualcuna in mezzo passa,
    # e la domanda e' se il monitor resta comunque sull'ultima o si ferma a
    # una intermedia. E' il caso in cui compare "rifiutato e riportato a 88"
    # dopo aver portato un cursore a 90.
    Write-Section 'Raffica di scritture (trascinamento del cursore)'
    Write-Host ("Si scrive una sequenza crescente e poi si rilegge dopo {0} ms," -f $VerifyMs)
    Write-Host 'come fa la verifica dell''applicazione. Due modi: tutti i passaggi,'
    Write-Host 'oppure solo il valore finale.'
    Write-Host ''

    $burstRows = @()
    $burstBad = 0
    foreach ($l in $live) {
        $c = $l.Code
        if ($l.Choice -or ($NeverWrite -contains $c)) { continue }
        $before = Read-Retrying $h $c
        if (-not $before.Ok -or $before.Maximum -lt 16) { continue }

        $span = 15
        $from = if (($before.Current + $span) -le $before.Maximum) { $before.Current }
                else { [Math]::Max(0, $before.Maximum - $span) }
        $to = $from + $span

        foreach ($mode in 'tutti i passaggi', 'solo il finale') {
            $wrong = 0; $seen = @()
            for ($round = 0; $round -lt $BurstRounds; $round++) {
                [void](Write-Once $h $c $from)
                Start-Sleep -Milliseconds 300
                if ($mode -eq 'tutti i passaggi') {
                    for ($v = $from + 1; $v -le $to; $v++) { [void](Write-Once $h $c $v) }
                }
                else { [void](Write-Once $h $c $to) }
                Start-Sleep -Milliseconds $VerifyMs
                $r = Read-Retrying $h $c
                $got = if ($r.Ok) { $r.Current } else { 'errore' }
                if ($got -ne $to) { $wrong++; $seen += $got }
            }
            $burstBad += $wrong
            $burstRows += [pscustomobject]@{
                Codice   = Format-Code $c
                Modo     = $mode
                'da->a'  = "$from->$to"
                'sbagli' = "$wrong/$BurstRounds"
                'letto'  = if ($seen.Count -gt 0) { ($seen | Select-Object -Unique) -join ' ' } else { 'sempre giusto' }
            }
        }
        [void](Write-Once $h $c $before.Current)
        Start-Sleep -Milliseconds 200
    }
    if ($burstRows.Count -gt 0) { $burstRows | Format-Table -AutoSize | Out-String | Write-Host }

    # --------------------------------------------------------- conclusione --
    Write-Section 'In conclusione'
    $ignored = @($settleRows | Where-Object { $_.Esito -like 'IGNORATA*' })
    $medRead = (Get-Stats ([double[]]@($readRows | Where-Object { $_.'med ms' } | ForEach-Object { [double]$_.'med ms' }))).Median
    Write-Host ("costo di una operazione sul bus : {0:F0} ms (mediana)" -f $medRead)
    Write-Host ("assestamento peggiore misurato  : {0:F0} ms" -f $worst)
    if ($lostAfterWrite -gt 0) {
        Write-Host ("prima rilettura persa dopo la scrittura : {0} codici su {1} provati" -f $lostAfterWrite, @($settleRows | Where-Object { $null -ne $_.'letture' }).Count)
        Write-Host 'Non e'' un tempo di assestamento: e'' un pacchetto perso, e i tre'
        Write-Host 'tentativi di ogni lettura lo coprono gia''.'
    }
    if ($ignored.Count -gt 0) {
        Write-Host ("controlli che il monitor ignora : {0}" -f (($ignored | ForEach-Object { $_.Codice }) -join ', ')) -ForegroundColor Yellow
        Write-Host 'Su questi la rilettura dira'' sempre "rifiutato", ed e'' corretto:'
        Write-Host 'e'' una modalita'' del monitor che li tiene fissi (vedi README).'
    }
    Write-Host ''
    if ($worst -le 0) {
        Write-Host 'Nessuna rilettura ha restituito il valore vecchio: su questo monitor'
        Write-Host 'non serve attendere prima di verificare, basta ritentare la lettura.'
    }
    else {
        Write-Host ("ritardo di verifica consigliato : {0:F0} ms dopo l'ultima scrittura" -f ($worst * 2 + $medRead))
    }
}
finally {
    [void][DdcCi]::DestroyPhysicalMonitors(1, @($all[$Monitor]))
}
