using System.Collections.Generic;
using System.Globalization;

namespace BlazorStudio.Setup
{
    /// <summary>Texte des Setups (Deutsch/Englisch je nach Windows-Sprache).</summary>
    public static class Text
    {
        private static readonly bool German = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";

        private static readonly Dictionary<string, (string De, string En)> T = new Dictionary<string, (string, string)>
        {
            ["app.description"] = ("Die visuelle IDE für Blazor", "The visual IDE for Blazor"),
            ["title.install"] = ("Blazor Studio installieren", "Install Blazor Studio"),
            ["title.update"] = ("Blazor Studio aktualisieren", "Update Blazor Studio"),
            ["title.uninstall"] = ("Blazor Studio deinstallieren", "Uninstall Blazor Studio"),
            ["lead"] = ("Komponenten per Drag & Drop bauen, Styles und Bindings im Inspector setzen, Code mit Vervollständigung schreiben und C# debuggen.",
                        "Build components with drag & drop, set styles and bindings in the inspector, write code with completion and debug C#."),
            ["lead.update"] = ("Version {0} ist installiert. Das Update ersetzt die Programmdateien – Einstellungen und Projekte bleiben erhalten.",
                               "Version {0} is installed. The update replaces the program files – settings and projects are kept."),
            ["lead.uninstall"] = ("Blazor Studio wird von diesem Computer entfernt. Deine Projekte bleiben unberührt.",
                                  "Blazor Studio will be removed from this computer. Your projects stay untouched."),
            ["target"] = ("Installationsordner", "Install folder"),
            ["browse"] = ("Ändern …", "Change …"),
            ["opt.desktop"] = ("Verknüpfung auf dem Desktop", "Desktop shortcut"),
            ["opt.startmenu"] = ("Eintrag im Startmenü", "Start menu entry"),
            ["opt.folder"] = ("„Mit Blazor Studio öffnen“ im Explorer-Kontextmenü", "\"Open with Blazor Studio\" in the Explorer context menu"),
            ["opt.removeSettings"] = ("Auch Einstellungen und zuletzt geöffnete Projekte entfernen", "Also remove settings and recent projects"),
            ["btn.install"] = ("Installieren", "Install"),
            ["btn.update"] = ("Aktualisieren", "Update"),
            ["btn.uninstall"] = ("Deinstallieren", "Uninstall"),
            ["btn.cancel"] = ("Abbrechen", "Cancel"),
            ["btn.launch"] = ("Blazor Studio starten", "Launch Blazor Studio"),
            ["btn.close"] = ("Schließen", "Close"),
            ["space"] = ("Benötigt ca. {0} MB · pro Benutzer, keine Administratorrechte nötig", "Requires about {0} MB · per user, no administrator rights needed"),
            ["st.prepare"] = ("Vorbereiten …", "Preparing …"),
            ["st.removeOld"] = ("Alte Version entfernen …", "Removing previous version …"),
            ["st.copy"] = ("Dateien kopieren", "Copying files"),
            ["st.shortcuts"] = ("Verknüpfungen …", "Shortcuts …"),
            ["st.register"] = ("Registrieren …", "Registering …"),
            ["st.done"] = ("Fertig", "Done"),
            ["st.removeFiles"] = ("Dateien entfernen …", "Removing files …"),
            ["st.uninstalled"] = ("Entfernt", "Removed"),
            ["done.title"] = ("Alles bereit!", "All set!"),
            ["done.text"] = ("Blazor Studio wurde installiert. Du findest es auf dem Desktop und im Startmenü.", "Blazor Studio has been installed. You'll find it on the desktop and in the start menu."),
            ["done.uninstalled"] = ("Blazor Studio wurde entfernt.", "Blazor Studio has been removed."),
            ["done.uninstalledText"] = ("Danke fürs Ausprobieren!", "Thanks for trying it!"),
            ["warn.webview"] = ("Die Microsoft Edge WebView2-Runtime fehlt. Blazor Studio braucht sie zur Anzeige – bitte von Microsoft installieren.",
                                "The Microsoft Edge WebView2 runtime is missing. Blazor Studio needs it – please install it from Microsoft."),
            ["warn.sdk"] = ("Kein .NET SDK gefunden. Zum Bauen und Starten von Projekten wird das .NET SDK (10 oder neuer) benötigt.",
                            "No .NET SDK found. Building and running projects requires the .NET SDK (10 or newer)."),
            ["warn.running"] = ("Blazor Studio läuft gerade. Es wird für das Update automatisch beendet – ungespeicherte Änderungen vorher sichern.",
                                "Blazor Studio is running. It will be closed for the update – save your work first."),
            ["link.webview"] = ("WebView2 herunterladen", "Download WebView2"),
            ["link.sdk"] = (".NET SDK herunterladen", "Download .NET SDK"),
            ["err.title"] = ("Das hat nicht geklappt", "That didn't work"),
            ["err.noPayload"] = ("Das Setup enthält keine Programmdateien.", "The setup contains no program files."),
            ["err.locked"] = ("Eine Datei ist gesperrt. Bitte Blazor Studio schließen und erneut versuchen.", "A file is locked. Please close Blazor Studio and try again."),
            ["pick.folder"] = ("Installationsordner wählen", "Choose install folder"),
            ["shell.open"] = ("Mit Blazor Studio öffnen", "Open with Blazor Studio"),
            ["version"] = ("Version {0}", "Version {0}"),
        };

        public static string Get(string key) => T.TryGetValue(key, out var v) ? (German ? v.De : v.En) : key;

        public static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }
}
