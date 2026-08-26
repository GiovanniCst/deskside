// Localisation.
//
// The English text is the key, so the code reads as plain English and a missing
// translation degrades to English instead of to a bare identifier. Only the
// translated languages need a table.
//
// Adding a language: write another dictionary, register it in Register(), add it
// to Available. No resource compiler, no satellite assemblies, no build step.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Deskside
{
    public static class L
    {
        /// <summary>"auto" follows the Windows display language.</summary>
        public const string Auto = "auto";

        static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        static Dictionary<string, string> _active;
        static string _setting = Auto;

        /// <summary>Language codes offered in the menu, with the name to show.</summary>
        public static readonly List<KeyValuePair<string, string>> Available =
            new List<KeyValuePair<string, string>>();

        /// <summary>Raised after the language changes, so the UI can rebuild.</summary>
        public static Action Changed;

        static L()
        {
            Available.Add(new KeyValuePair<string, string>(Auto, "Automatic"));
            Available.Add(new KeyValuePair<string, string>("en", "English"));
            Available.Add(new KeyValuePair<string, string>("it", "Italiano"));
            Register("it", Italian());
        }

        static void Register(string code, Dictionary<string, string> table) { Tables[code] = table; }

        public static string Setting { get { return _setting; } }

        /// <summary>The language actually in use once "auto" is resolved.</summary>
        public static string Effective
        {
            get
            {
                if (!string.Equals(_setting, Auto, StringComparison.OrdinalIgnoreCase)) return _setting;
                try { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName; }
                catch { return "en"; }
            }
        }

        public static void Load(string setting)
        {
            _setting = string.IsNullOrEmpty(setting) ? Auto : setting;
            Dictionary<string, string> t;
            _active = Tables.TryGetValue(Effective, out t) ? t : null;
        }

        public static void Set(string setting)
        {
            Load(setting);
            SettingsStore.SetString("language", _setting);
            if (Changed != null) Changed();
        }

        /// <summary>Translates a string. Unknown text is returned unchanged.</summary>
        public static string T(string english)
        {
            if (_active == null || english == null) return english;
            string s;
            return _active.TryGetValue(english, out s) ? s : english;
        }

        /// <summary>Translates and formats. The placeholders live in the English text.</summary>
        public static string F(string english, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, T(english), args);
        }

        static Dictionary<string, string> Italian()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            Action<string, string> a = delegate(string en, string it) { d[en] = it; };

            // --- control names ---
            a("Brightness", "Luminosità");
            a("Contrast", "Contrasto");
            a("Volume", "Volume");
            a("Colour temp.", "Temp. colore");
            a("Sharpness", "Nitidezza");
            a("Red gain", "Guadagno rosso");
            a("Green gain", "Guadagno verde");
            a("Blue gain", "Guadagno blu");
            a("Red black", "Nero rosso");
            a("Green black", "Nero verde");
            a("Blue black", "Nero blu");
            a("Input", "Ingresso");
            a("Colour preset", "Preset colore");
            a("Scaling", "Scalatura");
            a("Audio", "Audio");
            a("OSD language", "Lingua OSD");
            a("Refresh rate", "Frequenza");
            a("Preset", "Preset");
            a("Orientation", "Orientamento");
            a("Landscape", "Orizzontale");
            a("Portrait", "Verticale");
            a("Landscape (flipped)", "Orizzontale (capovolto)");
            a("Portrait (flipped)", "Verticale (capovolto)");
            a("Orientation: {0}", "Orientamento: {0}");
            a("Rotation refused\r\n{0}", "Rotazione rifiutata\r\n{0}");
            // Dell preset names, as Dell Display Manager shows them in Italian
            a("Warm", "Caldo");
            a("Cool", "Freddo");
            a("Custom Color", "Colore personalizzato");

            // --- values a control can take ---
            a("user", "utente");
            a("no scaling", "nessuna");
            a("fit (keep aspect)", "adatta (proporzioni)");
            a("fill (stretch)", "riempi (deforma)");
            a("fit width", "adatta in larghezza");
            a("fit height", "adatta in altezza");
            a("muted", "muto");
            a("unmuted", "audio attivo");
            a("on", "acceso");
            a("off", "spento");
            a("standby", "standby");
            a("suspend", "sospeso");
            a("hard off", "spento (hard)");
            a("normal", "normale");
            a("max", "massimo");
            a("Chinese", "Cinese");
            a("English", "Inglese");
            a("French", "Francese");
            a("German", "Tedesco");
            a("Italian", "Italiano");
            a("Japanese", "Giapponese");
            a("Russian", "Russo");
            a("Spanish", "Spagnolo");
            a("Portuguese", "Portoghese");
            a("Dutch", "Olandese");
            a("Korean", "Coreano");

            // --- panel ---
            a("Save defaults", "Salva default");
            a("Apply defaults", "Applica default");
            a("Refresh", "Aggiorna");
            a("Turn off", "Spegni");
            a("Factory reset", "Ripristina");
            a("Dynamic Contrast is on: the monitor is holding\r\nbrightness and contrast fixed.",
              "Dynamic Contrast acceso: il monitor tiene fissi\r\nluminosità e contrasto.");

            // --- menu ---
            a("Control panel", "Pannello di controllo");
            a("Save settings as defaults", "Salva le impostazioni come predefinite");
            a("Turn the monitor off", "Spegni il monitor");
            a("Everything", "Tutto");
            a("Brightness and contrast", "Luminosità e contrasto");
            a("Colour", "Colore");
            a("Keyboard:  {0}", "Tastiera:  {0}");
            a("not mapped", "nessuna associazione");
            a("Remove mapping", "Rimuovi associazione");
            a("Keep the layout enforced", "Mantieni il layout forzato");
            a("Apply now", "Applica ora");
            a("More", "Altro");
            a("Language", "Lingua");
            a("Automatic", "Automatica");
            a("Start with Windows", "Avvia con Windows");
            a("Detect monitors again", "Rileva di nuovo i monitor");
            a("Diagnostics...", "Diagnostica...");
            a("Full VCP scan...", "Scansione completa dei codici VCP...");
            a("Keyboard shortcuts", "Scorciatoie da tastiera");
            a("Open the settings file", "Apri il file delle impostazioni");
            a("About {0}...", "Informazioni su {0}...");
            a("Quit", "Esci");
            a("no DDC/CI monitor", "nessun monitor DDC/CI");

            // --- on-screen messages ---
            a("External keyboard unplugged\r\nlayout left to Windows",
              "Tastiera esterna scollegata\r\nlayout lasciato a Windows");
            a("{0}\r\nlayout {1}", "{0}\r\nlayout {1}");
            a("Could not apply layout {0}", "Impossibile applicare il layout {0}");
            a("{0}\r\nmapping removed", "{0}\r\nassociazione rimossa");
            a("{0}\r\n-> {1}", "{0}\r\n-> {1}");
            a("Layout enforced\r\nwhile the keyboard is plugged in",
              "Layout mantenuto forzato\r\nfinché la tastiera è collegata");
            a("Layout no longer enforced\r\nonly on unlock and on plug-in",
              "Layout non più forzato\r\nsolo allo sblocco e al collegamento");
            a("Factory reset sent", "Ripristino di fabbrica inviato");
            a("No monitor to save", "Nessun monitor da salvare");
            a("{0}\r\n{1} settings saved as defaults", "{0}\r\n{1} impostazioni salvate come predefinite");
            a("No profile saved for this monitor", "Nessun profilo salvato per questo monitor");
            a("Another program is using the monitor's DDC/CI bus:\r\nthe controls found may be wrong. Close it and refresh.",
              "Un altro programma sta usando il bus DDC/CI del monitor:\r\ni controlli trovati potrebbero essere sbagliati. Chiudilo e aggiorna.");
            a("{0}\r\nprofile applied ({1} settings)", "{0}\r\nprofilo applicato ({1} impostazioni)");
            a("{0}: the monitor refused {1}\r\nand left it at {2}",
              "{0}: il monitor ha rifiutato {1}\r\ne l'ha lasciato a {2}");
            a("Refresh rate: {0} Hz", "Frequenza: {0} Hz");
            a("{0} Hz refused\r\n{1}", "{0} Hz rifiutata\r\n{1}");
            a("{0} not available", "{0} non disponibile");
            a("Only one input available", "Un solo ingresso disponibile");
            a("Input: {0}", "Ingresso: {0}");
            a("Audio: {0}", "Audio: {0}");
            a("Will start with Windows", "Avvio automatico attivato");
            a("Will no longer start with Windows", "Avvio automatico disattivato");
            a("Could not change autostart\r\n{0}", "Impossibile cambiare l'avvio automatico\r\n{0}");
            a("Nothing saved yet", "Non c'è ancora niente di salvato");
            a("Running diagnostics...", "Diagnostica in corso...");
            a("Scanning all 256 VCP codes...\r\nabout 15 seconds",
              "Scansione dei 256 codici VCP...\r\ncirca 15 secondi");

            // --- refresh rate, from DisplayInfo ---
            a("(up to {0} Hz available)", "(disponibili fino a {0} Hz)");
            a("(highest at this resolution)", "(massimo a questa risoluzione)");
            a("a restart is required", "serve un riavvio");
            a("mode not supported", "modo non supportato");
            a("the driver refused the change", "il driver ha rifiutato il cambio");
            a("error {0}", "errore {0}");

            // --- keyboards ---
            a("Built-in keyboard", "Tastiera integrata");

            // --- diagnostics ---
            a("unreadable", "non leggibile");
            a("write: OK", "scrittura: OK");
            a("write: IGNORED by the monitor", "scrittura: IGNORATA dal monitor");
            a("A control reported as IGNORED is being held fixed by a\r\n"
              + "mode in the monitor's own menu. Dynamic Contrast locks\r\n"
              + "brightness and contrast, an sRGB preset locks contrast,\r\n"
              + "and colour temperature only moves outside named presets.",
              "Un controllo IGNORATO è tenuto fisso da una modalità del\r\n"
              + "monitor stesso. Il Dynamic Contrast blocca luminosità e\r\n"
              + "contrasto, il preset sRGB blocca il contrasto, e la\r\n"
              + "temperatura colore si muove solo fuori dai preset nominali.");
            a("{0} - diagnostics", "{0} - diagnostica");

            // --- full scan ---
            a("{0} - full VCP scan", "{0} - scansione completa");
            a("declared by the monitor: {0}   -   actually answering: {1}",
              "dichiarati dal monitor: {0}   -   rispondono davvero: {1}");
            a("ALREADY IN THE PANEL", "GIÀ NEL PANNELLO");
            a("ANSWERING BUT NOT IN THE PANEL", "RISPONDONO MA NON SONO NEL PANNELLO");
            a("  none\r\n", "  nessuno\r\n");
            a("(vendor, declared)", "(vendor, dichiarato)");
            a("(unknown)", "(sconosciuto)");
            a("The scan is read-only: answering does not mean accepting\r\n"
              + "writes. Use Diagnostics to test those.",
              "La scansione è di sola lettura: rispondere non significa\r\n"
              + "accettare le scritture. Per provarle usa Diagnostica.");
            a("This monitor answers codes it does not have, by repeating its last\r\n"
              + "valid reply. Codes whose answer is identical to the one before are\r\n"
              + "listed apart as echoes: {0} out of 256.",
              "Questo monitor risponde anche ai codici che non ha, ripetendo\r\n"
              + "l'ultima risposta valida. I codici la cui risposta è identica alla\r\n"
              + "precedente sono elencati a parte come eco: {0} su 256.");
            a("ECHOES: SAME ANSWER AS THE CODE BEFORE, NOT REAL CONTROLS",
              "ECO: RISPOSTA UGUALE AL CODICE PRECEDENTE, NON SONO FUNZIONI");

            // --- report window ---
            a("Copy", "Copia");
            a("Copied", "Copiato");
            a("Copy failed", "Non copiato");

            // --- shortcuts ---
            a("{0} - shortcuts", "{0} - scorciatoie");
            a("brightness +5", "luminosità +5");
            a("brightness -5", "luminosità -5");
            a("contrast +5", "contrasto +5");
            a("contrast -5", "contrasto -5");
            a("volume +5", "volume +5");
            a("volume -5", "volume -5");
            a("mute on/off", "muto on/off");
            a("next input", "ingresso successivo");
            a("open the panel", "apre il pannello");
            a("   << not registered: already taken", "   << non registrata: già in uso");

            // --- about ---
            a("About {0}", "Informazioni su {0}");
            a("Your desk, the way you left it.", "La tua scrivania, come l'avevi lasciata.");
            a("Monitor control over DDC/CI, and keyboard\r\nlayout locking, for docked Windows laptops.",
              "Controllo del monitor via DDC/CI e blocco del layout\r\ndi tastiera, per portatili Windows in postazione.");
            a("Created by {0}", "Creato da {0}");
            a("Licensed under the {0}. You may use, modify and\r\nredistribute it, provided the original attribution is kept.",
              "Distribuito con licenza {0}. Puoi usarlo, modificarlo e\r\nridistribuirlo, purché resti la menzione dell'autore originale.");
            a("Close", "Chiudi");
            a("Could not open the link: {0}", "Impossibile aprire il link: {0}");
            a("{0} - unexpected error", "{0} - errore imprevisto");

            return d;
        }
    }
}
