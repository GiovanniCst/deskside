# Deskside

**Your desk, the way you left it.**

> 🇮🇹 Questo README è disponibile anche **[in italiano](#deskside-italiano)**.

A small Windows tray utility for laptops that live on a dock. It drives your
external monitor over DDC/CI — the same commands the buttons on the monitor
send — and it stops Windows from throwing away your keyboard layout every time
you unlock the machine.

No installer, no service, no dependencies. A single executable that builds from
source in about a second with the C# compiler already present on every Windows
machine. English and Italian interface.

## How it looks

The control panel, built from the controls your monitor actually answers to:

![Deskside control panel](assets/panel.png)

The menu, reachable from the tray icon or from the panel's **Menu** button:

![Deskside menu](assets/menu.png)

And the readout a keyboard shortcut leaves on screen, which never takes focus
from whatever you are typing in:

![Deskside on-screen readout](assets/osd.png)

---

## Why

Two annoyances, one tray icon.

**The monitor.** Its brightness, contrast, input and colour settings live behind
a joystick nub on the back of the panel. They are reachable from software —
every DDC/CI monitor exposes them — but Windows ships no UI for it.

**The keyboard.** Plug in a keyboard with a different layout and Windows will
still reset to your account's default language on every lock, unlock and login.
Deskside notices which keyboard is attached and puts the right layout back.

---

## What it does

### Monitor

- Brightness, contrast, volume, colour temperature, sharpness, RGB gain, RGB
  black level — whichever of these your monitor actually answers to.
- Input source, colour preset, image scaling, mute, OSD language.
- Refresh rate (this one goes through Windows, not DDC/CI).
- Turn the monitor off; factory-reset everything, or just the picture, or just
  the colour.
- **Per-monitor profiles.** Save your settings as defaults for *that* monitor,
  identified by its PnP id. Plug in a different one and Deskside recognises it
  and applies the matching profile. A laptop that moves between two desks keeps
  two profiles and needs no attention.

The panel is not a fixed list. On every monitor change Deskside probes the VCP
codes and keeps the ones that answer, so what you see is what your hardware
really supports — including controls the monitor forgets to declare.

### Keyboard

- Detects connected USB keyboards and maps each one to an input layout.
- Restores that layout when the keyboard is plugged in, on unlock, on login,
  and — optionally — every few seconds for as long as the keyboard is attached.
- The built-in laptop keyboard is left alone.

### Language

English and Italian. By default Deskside follows the Windows display language,
and falls back to English for anything that is not Italian. You can pin it from
**More → Language**.

Adding a language is one dictionary in `src/Strings.cs` and one line in its
`Available` list — no resource compiler, no satellite assemblies, no build step.
The English text doubles as the lookup key, so a missing translation falls back
to English rather than to a bare identifier.

### Global shortcuts

| Keys | Action |
|---|---|
| `Ctrl+Alt+Up` / `Down` | brightness ±5 |
| `Ctrl+Shift+Alt+Up` / `Down` | contrast ±5 |
| `Ctrl+Alt+Right` / `Left` | volume ±5 |
| `Ctrl+Alt+M` | mute on/off |
| `Ctrl+Alt+I` | next input |
| `Ctrl+Alt+PageDown` | open the panel |

---

## Install

Download `Deskside.exe` from [Releases][releases], put it anywhere, run it.
Then **More → Start with Windows**.

Or build it yourself:

```
git clone https://github.com/GiovanniCst/deskside
cd deskside
build.cmd
```

`build.cmd` uses `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, the
C# compiler that comes with the .NET Framework — present on every Windows 10 and
11 install. There is no SDK to install, no NuGet restore and no project file.

**Requirements:** Windows 10 or 11, and a monitor with DDC/CI enabled in its own
menu. Most have it on by default; a few ship with it off.

---

## Using it

- **Left click** the tray icon: opens the control panel. It closes when you
  click elsewhere, or with `Esc`.
- **Right click** the tray icon, or the panel's **Menu** button: everything
  else — the multiple-choice settings, keyboard mapping, diagnostics, language.

The icon **only appears while a DDC/CI monitor is connected** — unplug the
external screen and it goes away, plug it back in and it returns.

### Setting up a monitor profile

Adjust everything the way you like it, then **Save defaults**. From then on
Deskside applies it whenever that monitor appears.

The profile is applied when the monitor *changes* — at startup, or when you
connect a different one. A manual *Refresh* does not re-apply it, so it never
overwrites what you are adjusting at that moment.

### Setting up a keyboard

**Keyboard** → pick your keyboard → pick a layout. That mapping is remembered
against the keyboard's USB VID/PID.

**Keep the layout enforced** (on by default) re-checks every four seconds. It
solves the unlock problem completely, at the cost of not letting you switch
layouts by hand while that keyboard is plugged in. Turn it off and Deskside only
acts on plug-in, unlock and login.

### Settings file

Everything lives in `%APPDATA%\Deskside\settings.ini`, in plain text, safe to
edit by hand:

```ini
[app]
keepLayout=1
language=auto

[keyboards]
VID_2F68&PID_0082=00000809

[LEN64BC]
name=ThinkVision E27-40
0x10=100
0x12=75
```

---

## How it works

### The DDC/CI bus is slow, and that shapes everything

Every read and every write over DDC/CI costs about **60 ms** — it is an I²C bus
running at a leisurely pace, and no amount of clever code makes it faster. A
full refresh of the panel is a dozen reads, so more than a second. Do that on
the UI thread and the application freezes on every interaction.

So:

- All DDC traffic runs on **one background thread**; the UI never blocks.
- **Writes to the same control coalesce.** Drag a slider and only the value you
  land on reaches the monitor; everything it passed through is discarded before
  it hits the bus.
- **Duplicate reads merge** while they are still queued.
- Controls move **immediately** to the value you chose and correct themselves
  when the monitor answers.
- About 700 ms after the last change, a **verification read** goes out. If the
  monitor accepted the command but ignored the value, the control snaps back and
  the readout says so.
- Hotkeys work from the cached value rather than reading first, which halves the
  bus traffic and is why they feel instant.

### Monitors lie about what they support

The capability string a monitor reports is not reliable. The ThinkVision this
was written against declares 30 VCP codes but answers to 43 — sharpness, mute
and the three black-level controls all work while going undeclared.

So Deskside ignores the declared list for deciding *what to show* and probes the
codes instead. **More → Full VCP scan** sweeps all 256 codes read-only and
reports which answer, split into what is already in the panel and what is not.
**Diagnostics** goes further and tests whether each control accepts writes,
restoring the original value afterwards.

### Controls a monitor holds hostage

A monitor can accept a DDC/CI command and then quietly ignore the value. That is
not a bug in the tool — it is a mode in the monitor's own menu holding that
control fixed. Observed on the ThinkVision E27-40:

| Mode | What it freezes |
|---|---|
| Dynamic Contrast | brightness and contrast |
| sRGB colour preset | contrast |
| any named preset | colour temperature |

Deskside notices by reading the value back, and when Dynamic Contrast is on it
disables the two affected sliders and says why, rather than leaving controls
that do nothing.

Applying a profile writes Dynamic Contrast and the colour preset **first**, for
the same reason: written in the wrong order, everything after them is ignored.

### A keyboard will not tell you its layout

USB HID has a field for exactly this: `bCountryCode` in the HID descriptor.
It reads `0` — "not localised" — on essentially every keyboard sold, and Windows
does not expose it anywhere regardless: not in the registry under `Enum\HID`,
not in `HID_COLLECTION_INFORMATION`.

`tools/Get-HidCountryCode.ps1` checks yours by asking the hardware directly: it
enumerates the USB hubs, finds the port with the right VID/PID, requests the
configuration descriptor with `IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION`
and pulls the HID descriptors out of it.

```powershell
.\tools\Get-HidCountryCode.ps1                                  # everything
.\tools\Get-HidCountryCode.ps1 -VendorId 2F68 -ProductId 0082   # one device
```

If it prints `0`, and it will, the layout genuinely cannot be detected. That is
why the mapping is manual and saved.

### Vendor codes are brand-specific

`0xE0` and `0xEA` are Over Drive and Dynamic Contrast **on Lenovo monitors** —
established by experiment, not documentation. On another brand those same
numbers could mean anything at all, so Deskside only offers them when the
monitor's PnP id starts with `LEN`, and never writes them to a profile for
another make.

If you identify vendor codes on your own hardware, a pull request adding them
behind the same kind of guard is very welcome.

### Refresh rate, and a trap

The refresh-rate list comes from `EnumDisplaySettings` and is applied with
`ChangeDisplaySettingsEx`. It is a Windows setting, not a monitor command.

A warning if you go looking for supported rates yourself: WMI's
`WmiMonitorListedSupportedSourceModes` is misleading. On the monitor this was
built against it reports only 1920x1080 @ 60 Hz, while the driver actually
offers 50, 59, 60, 75 and **100 Hz** at that resolution. That WMI table holds
the EDID's standard timings, not the detailed timings where the high refresh
rates live.

---

## Compatibility

Written against a Lenovo ThinkVision E27-40 and a Durgod Taurus K320, but
nothing about it is specific to those beyond the vendor codes noted above.
Any DDC/CI monitor and any USB keyboard should work — the whole design is built
on asking the hardware what it supports rather than assuming.

Reports from other hardware, especially the output of **Full VCP scan**, are
useful and welcome in the issues.

---

## Licence

Licensed under the [Apache License 2.0](LICENSE).

You may use, modify and redistribute this software, including commercially,
provided you retain the copyright and attribution notices — see [NOTICE](NOTICE).
Derivative works must keep the credit to the original author and state that they
have been modified.

Created by **Giovanni J. Costantini** — [costantini.pw](https://costantini.pw)

[releases]: https://github.com/GiovanniCst/deskside/releases

<br>

---
---

<br>

# Deskside (italiano)

**La tua scrivania, come l'avevi lasciata.**

> 🇬🇧 This README is also available **[in English](#deskside)**.

Una piccola utility per la tray di Windows, pensata per i portatili che vivono
in postazione. Pilota il monitor esterno via DDC/CI — gli stessi comandi che
mandano i pulsanti del monitor — e impedisce a Windows di buttare via il layout
di tastiera a ogni sblocco.

Niente installer, niente servizio, nessuna dipendenza. Un solo eseguibile, che si
compila dai sorgenti in circa un secondo con il compilatore C# già presente in
ogni installazione di Windows. Interfaccia in inglese e italiano.

## Com'è fatto

Il pannello di controllo, costruito sui controlli a cui il monitor risponde
davvero:

![Pannello di controllo di Deskside](assets/panel.png)

Il menu, raggiungibile dall'icona nella tray o dal pulsante **Menu** del
pannello:

![Menu di Deskside](assets/menu.png)

E l'indicatore che una scorciatoia lascia a schermo, che non toglie mai il fuoco
a quello che stai scrivendo:

![Indicatore a schermo di Deskside](assets/osd.png)

---

## Perché

Due fastidi, una sola icona nella tray.

**Il monitor.** Luminosità, contrasto, ingresso e impostazioni colore stanno
dietro a un levettino sul retro del pannello. Sono raggiungibili via software —
ogni monitor DDC/CI le espone — ma Windows non offre nessuna interfaccia.

**La tastiera.** Colleghi una tastiera con un layout diverso e Windows continua
a rimettere la lingua predefinita dell'account a ogni blocco, sblocco e login.
Deskside riconosce quale tastiera è collegata e rimette il layout giusto.

---

## Cosa fa

### Monitor

- Luminosità, contrasto, volume, temperatura colore, nitidezza, guadagno RGB,
  livello del nero RGB — quelli, fra questi, a cui il monitor risponde davvero.
- Ingresso, preset colore, scalatura dell'immagine, muto, lingua dell'OSD.
- Frequenza di aggiornamento (questa passa da Windows, non dal DDC/CI).
- Spegnimento del monitor; ripristino di fabbrica totale, oppure della sola
  immagine, oppure del solo colore.
- **Profili per monitor.** Salvi le impostazioni come predefinite di *quel*
  monitor, identificato dal suo codice PnP. Ne colleghi un altro e Deskside lo
  riconosce e applica il profilo giusto. Un portatile che gira fra due scrivanie
  tiene due profili e non chiede attenzione.

Il pannello non è un elenco fisso. A ogni cambio di monitor Deskside interroga i
codici VCP e tiene quelli che rispondono: vedi quello che il tuo hardware
supporta per davvero, compresi i controlli che il monitor si dimentica di
dichiarare.

### Tastiera

- Rileva le tastiere USB collegate e associa a ciascuna un layout di input.
- Rimette quel layout al collegamento della tastiera, allo sblocco, al login e —
  se vuoi — ogni pochi secondi finché la tastiera resta collegata.
- La tastiera integrata del portatile viene lasciata in pace.

### Lingua

Inglese e italiano. Di default Deskside segue la lingua di Windows, e ricade
sull'inglese per qualsiasi lingua che non sia l'italiano. Puoi fissarla da
**Altro → Lingua**.

Aggiungere una lingua è un dizionario in `src/Strings.cs` e una riga nel suo
elenco `Available` — nessun compilatore di risorse, nessun assembly satellite,
nessun passaggio di build. Il testo inglese fa anche da chiave, quindi una
traduzione mancante ricade sull'inglese e non su un identificatore nudo.

### Scorciatoie globali

| Tasti | Effetto |
|---|---|
| `Ctrl+Alt+Su` / `Giù` | luminosità ±5 |
| `Ctrl+Shift+Alt+Su` / `Giù` | contrasto ±5 |
| `Ctrl+Alt+Destra` / `Sinistra` | volume ±5 |
| `Ctrl+Alt+M` | muto on/off |
| `Ctrl+Alt+I` | ingresso successivo |
| `Ctrl+Alt+PagGiù` | apre il pannello |

---

## Installazione

Scarica `Deskside.exe` dalle [Release][releases], mettilo dove vuoi, avvialo.
Poi **Altro → Avvia con Windows**.

Oppure compilalo tu:

```
git clone https://github.com/GiovanniCst/deskside
cd deskside
build.cmd
```

`build.cmd` usa `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, il
compilatore C# incluso nel .NET Framework — presente in ogni installazione di
Windows 10 e 11. Non c'è nessun SDK da installare, nessun ripristino di
pacchetti, nessun file di progetto.

**Requisiti:** Windows 10 o 11 e un monitor con il DDC/CI abilitato nel suo
menu. Quasi tutti ce l'hanno attivo di serie; qualcuno lo spedisce spento.

---

## Come si usa

- **Clic sinistro** sull'icona: apre il pannello di controllo. Si chiude
  cliccando altrove, oppure con `Esc`.
- **Clic destro** sull'icona, o il pulsante **Menu** del pannello: tutto il
  resto — le impostazioni a scelta multipla, l'associazione della tastiera, la
  diagnostica, la lingua.

L'icona **compare solo quando c'è un monitor DDC/CI collegato**: stacchi lo
schermo esterno e sparisce, lo ricolleghi e torna.

### Impostare un profilo monitor

Regola tutto come ti piace, poi **Salva default**. Da lì in poi Deskside lo
applica ogni volta che quel monitor compare.

Il profilo si applica quando il monitor *cambia*: all'avvio, o quando ne
colleghi uno diverso. Un *Aggiorna* manuale non lo riapplica, così non
sovrascrive mai quello che stai regolando in quel momento.

### Impostare una tastiera

**Tastiera** → scegli la tastiera → scegli il layout. L'associazione viene
ricordata sul VID/PID USB della tastiera.

**Mantieni il layout forzato** (attivo di default) ricontrolla ogni quattro
secondi. Risolve del tutto il problema dello sblocco, al prezzo di non lasciarti
cambiare layout a mano finché quella tastiera è collegata. Disattivalo e
Deskside agisce solo al collegamento, allo sblocco e al login.

### File delle impostazioni

Sta tutto in `%APPDATA%\Deskside\settings.ini`, in chiaro, modificabile a mano:

```ini
[app]
keepLayout=1
language=auto

[keyboards]
VID_2F68&PID_0082=00000809

[LEN64BC]
name=ThinkVision E27-40
0x10=100
0x12=75
```

---

## Come funziona

### Il bus DDC/CI è lento, e questo determina tutto il resto

Ogni lettura e ogni scrittura su DDC/CI costa circa **60 ms**: è un bus I²C che
va con comodo, e nessuna astuzia nel codice lo rende più veloce. Un aggiornamento
completo del pannello sono una dozzina di letture, quindi più di un secondo.
Farlo sul thread della UI significa un'interfaccia che si pianta a ogni
interazione.

Quindi:

- Tutto il traffico DDC sta su **un thread di lavoro**; la UI non si blocca mai.
- Le **scritture sullo stesso controllo si fondono.** Trascini un cursore e al
  monitor arriva solo il valore su cui ti fermi; tutti quelli attraversati
  vengono scartati prima di toccare il bus.
- Le **letture duplicate si uniscono** finché sono ancora in coda.
- I controlli si spostano **subito** sul valore scelto e si correggono quando il
  monitor risponde.
- Circa 700 ms dopo l'ultima modifica parte una **rilettura di verifica**. Se il
  monitor ha accettato il comando ma ignorato il valore, il controllo torna
  indietro e l'indicatore lo dice.
- Le scorciatoie partono dal valore in cache invece di rileggerlo: dimezza il
  traffico sul bus, ed è il motivo per cui rispondono all'istante.

### I monitor mentono su cosa supportano

La stringa di capability che un monitor dichiara non è affidabile. Il ThinkVision
su cui è nato questo strumento ne dichiara 30 di codici VCP ma ne risponde 43:
nitidezza, muto e i tre livelli del nero funzionano tutti pur non essendo
dichiarati.

Per questo Deskside ignora l'elenco dichiarato quando deve decidere *cosa
mostrare*, e interroga i codici. **Altro → Scansione completa dei codici VCP**
passa tutti e 256 i codici in sola lettura e riporta quali rispondono, divisi fra
quelli già nel pannello e quelli no. **Diagnostica** va oltre e prova se ogni
controllo accetta le scritture, ripristinando poi il valore originale.

### Controlli tenuti in ostaggio dal monitor

Un monitor può accettare un comando DDC/CI e poi ignorarne il valore. Non è un
bug dello strumento: è una modalità del menu del monitor che tiene fisso quel
controllo. Osservato sul ThinkVision E27-40:

| Modalità | Cosa congela |
|---|---|
| Dynamic Contrast | luminosità e contrasto |
| preset colore sRGB | contrasto |
| qualsiasi preset nominale | temperatura colore |

Deskside se ne accorge rileggendo il valore, e con il Dynamic Contrast acceso
disabilita i due cursori interessati spiegando perché, invece di lasciare
controlli che non fanno niente.

Quando applica un profilo scrive **per primi** il Dynamic Contrast e il preset
colore, per lo stesso motivo: scritti nell'ordine sbagliato, tutto quello che
viene dopo verrebbe ignorato.

### Una tastiera non ti dice che layout ha

L'USB HID ha un campo apposta: `bCountryCode`, nel descrittore HID. Vale `0` —
"non localizzata" — praticamente su ogni tastiera in commercio, e comunque
Windows non lo espone da nessuna parte: non nel registro sotto `Enum\HID`, non
in `HID_COLLECTION_INFORMATION`.

`tools/Get-HidCountryCode.ps1` verifica la tua chiedendolo direttamente
all'hardware: enumera gli hub USB, trova la porta con il VID/PID giusto, richiede
il configuration descriptor con `IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION`
e ne estrae i descrittori HID.

```powershell
.\tools\Get-HidCountryCode.ps1                                  # tutto
.\tools\Get-HidCountryCode.ps1 -VendorId 2F68 -ProductId 0082   # un dispositivo
```

Se stampa `0`, e lo stamperà, il layout non è davvero rilevabile. Ecco perché
l'associazione è manuale e viene salvata.

### I codici vendor valgono solo per la loro marca

`0xE0` e `0xEA` sono Over Drive e Dynamic Contrast **sui monitor Lenovo** —
stabilito provandoli, non leggendo una documentazione. Su un'altra marca quegli
stessi numeri possono voler dire qualunque cosa, quindi Deskside li propone solo
quando il codice PnP del monitor inizia per `LEN`, e non li scrive mai nel
profilo di un monitor di altra marca.

Se identifichi codici vendor sul tuo hardware, una pull request che li aggiunge
dietro allo stesso tipo di guardia è benvenuta.

### Frequenza di aggiornamento, e una trappola

L'elenco delle frequenze arriva da `EnumDisplaySettings` e si applica con
`ChangeDisplaySettingsEx`. È un'impostazione di Windows, non un comando al
monitor.

Un avvertimento se vai a cercarti le frequenze supportate da solo: la classe WMI
`WmiMonitorListedSupportedSourceModes` è ingannevole. Sul monitor su cui è nato
questo strumento dichiara solo 1920x1080 @ 60 Hz, mentre il driver a quella
risoluzione offre 50, 59, 60, 75 e **100 Hz**. Quella tabella WMI contiene le
timing standard dell'EDID, non le detailed timing, dove stanno le frequenze alte.

---

## Compatibilità

Nato su un Lenovo ThinkVision E27-40 e una Durgod Taurus K320, ma niente in esso
è specifico di quei due se non i codici vendor di cui sopra. Qualsiasi monitor
DDC/CI e qualsiasi tastiera USB dovrebbero funzionare: tutto il progetto è
costruito sul chiedere all'hardware cosa supporta invece che darlo per scontato.

Segnalazioni da altro hardware, soprattutto l'output della **Scansione completa
dei codici VCP**, sono utili e benvenute nelle issue.

---

## Licenza

Distribuito con [licenza Apache 2.0](LICENSE).

Puoi usare, modificare e ridistribuire questo software, anche commercialmente,
a patto di conservare le note di copyright e di attribuzione — vedi
[NOTICE](NOTICE). Le opere derivate devono mantenere la menzione dell'autore
originale e dichiarare di essere state modificate.

Creato da **Giovanni J. Costantini** — [costantini.pw](https://costantini.pw)
