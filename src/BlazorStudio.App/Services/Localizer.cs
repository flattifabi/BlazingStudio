using System.Globalization;
using BlazorStudio.Core.Catalog;
using BlazorStudio.Core.Razor;

namespace BlazorStudio.App.Services;

/// <summary>
/// Texte der Oberfläche in Deutsch und Englisch. Neue Sprachen: weiteres Wörterbuch in <see cref="Tables"/> eintragen.
/// </summary>
public sealed class Localizer
{
    public string Language { get; private set; } = "de";

    public event Action? Changed;

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = new[] { ("de", "Deutsch"), ("en", "English") };

    public void SetLanguage(string? lang)
    {
        var code = string.IsNullOrWhiteSpace(lang)
            ? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en")
            : lang;
        if (!Tables.ContainsKey(code)) code = "en";
        if (code == Language && Changed is not null) return;
        Language = code;
        Changed?.Invoke();
    }

    public string this[string key] =>
        Tables[Language].TryGetValue(key, out var v) ? v : Tables["en"].TryGetValue(key, out var e) ? e : key;

    public string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, this[key], args);

    /// <summary>Anzeigename eines Toolbox-Eintrags.</summary>
    public string Label(ComponentDefinition d) => d.LabelKey is not null && Tables["en"].ContainsKey(d.LabelKey) ? this[d.LabelKey] : d.Label;

    public string Diagnostic(RazorDiagnostic d) => d.Code switch
    {
        RazorDiagnosticCode.UnclosedTag => F("diag.unclosedTag", d.Argument),
        RazorDiagnosticCode.UnexpectedCloseTag => F("diag.unexpectedClose", d.Argument),
        RazorDiagnosticCode.UnclosedBlock => F("diag.unclosedBlock", d.Argument),
        RazorDiagnosticCode.UnclosedAttribute => F("diag.unclosedAttribute", d.Argument),
        RazorDiagnosticCode.UnclosedComment => F("diag.unclosedComment", d.Argument),
        _ => d.Code.ToString(),
    };

    /// <summary>Platzhaltertexte aus Toolbox-Vorlagen (deutsch hinterlegt) übersetzen.</summary>
    public string TemplateText(string german) =>
        Language == "de" ? german : TemplateEn.TryGetValue(german, out var en) ? en : german;

    private static readonly Dictionary<string, string> TemplateEn = new()
    {
        ["Überschrift"] = "Heading", ["Titel"] = "Title", ["Beschreibung"] = "Description", ["Text"] = "Text", ["Link"] = "Link",
        ["Label"] = "Label", ["Button"] = "Button", ["Option 1"] = "Option 1", ["Option 2"] = "Option 2", ["Option"] = "Option",
        ["Eintrag 1"] = "Item 1", ["Eintrag 2"] = "Item 2", ["Zitat"] = "Quote", ["Senden"] = "Send", ["Sichtbar"] = "Visible",
        ["Bitte wählen"] = "Please choose", ["Angemeldet"] = "Signed in", ["Nicht angemeldet"] = "Not signed in", ["Fehler"] = "Error",
        ["Willkommen"] = "Welcome", ["Los geht's"] = "Get started", ["Mehr erfahren"] = "Learn more", ["Karte 1"] = "Card 1",
        ["Karte 2"] = "Card 2", ["Karte 3"] = "Card 3", ["Marke"] = "Brand", ["Start"] = "Home", ["Über uns"] = "About",
        ["Kontakt"] = "Contact", ["Name"] = "Name", ["E-Mail"] = "Email", ["Nachricht"] = "Message",
        ["Impressum · Datenschutz"] = "Imprint · Privacy", ["© 2026 Firma"] = "© 2026 Company",
        ["Eine kurze Beschreibung deines Angebots."] = "A short description of what you offer.", ["Eingabe"] = "Input",
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Tables = new()
    {
        ["de"] = new()
        {
            ["cmd.nuget"] = "NuGet-Pakete verwalten",
            ["nuget.title"] = "NuGet-Pakete", ["nuget.browse"] = "Durchsuchen", ["nuget.installedTab"] = "Installiert", ["nuget.updates"] = "Updates",
            ["nuget.searchPlaceholder"] = "Pakete auf nuget.org suchen, z. B. MudBlazor …", ["nuget.prerelease"] = "Vorabversionen", ["nuget.updateAll"] = "Alle aktualisieren",
            ["nuget.refresh"] = "Neu laden", ["nuget.verified"] = "Verifizierter Herausgeber", ["nuget.by"] = "von {0}", ["nuget.installedBadge"] = "installiert",
            ["nuget.noUpdates"] = "Alle Pakete sind aktuell.", ["nuget.noneInstalled"] = "Noch keine Pakete installiert.", ["nuget.noResults"] = "Keine Pakete gefunden.",
            ["nuget.version"] = "Version", ["nuget.latest"] = "neueste", ["nuget.working"] = "Wird ausgeführt …", ["nuget.install"] = "Installieren",
            ["nuget.changeTo"] = "Auf {0} wechseln", ["nuget.uninstall"] = "Entfernen", ["nuget.installedVersion"] = "Installiert: {0}", ["nuget.description"] = "Beschreibung",
            ["nuget.license"] = "Lizenz", ["nuget.downloads"] = "Downloads", ["nuget.projectSite"] = "Projektseite", ["nuget.dependencies"] = "Abhängigkeiten",
            ["nuget.pick"] = "Wähle links ein Paket aus.", ["nuget.offline"] = "nuget.org ist nicht erreichbar: {0}",
            ["nuget.confirmRemoveTitle"] = "{0} entfernen?", ["nuget.confirmRemoveText"] = "Die PackageReference wird aus der .csproj entfernt.",
            ["nuget.installed"] = "{0} {1} installiert", ["nuget.removed"] = "{0} entfernt", ["nuget.failed"] = "{0}: Vorgang fehlgeschlagen – Details in der Ausgabe.",
            ["action.close"] = "Schließen",
            ["cmd.openSolution"] = "Projektmappe öffnen (.sln, .slnx, .csproj)", ["dlg.solutionFilter"] = "Projektmappen und Projekte", ["welcome.solutionHint"] = "Wie in Rider oder Visual Studio",
            ["side.solution"] = "Projekt", ["sol.filter"] = "Projektmappe filtern …", ["sol.projectCount"] = "{0} Projekte", ["sol.setStartup"] = "Als Startprojekt festlegen",
            ["sol.buildProject"] = "Projekt erstellen", ["sol.editProject"] = ".csproj bearbeiten", ["sol.notFound"] = "nicht gefunden", ["sol.dependencies"] = "Abhängigkeiten",
            ["sol.restoreHint"] = "obj/project.assets.json fehlt – einmal erstellen (Build), damit Pakete aufgelöst werden.", ["sol.frameworks"] = "Frameworks", ["sol.packages"] = "Pakete",
            ["sol.transitive"] = "Transitive Pakete", ["sol.projects"] = "Projekte", ["sol.notInSolution"] = "nicht in der Mappe", ["sol.assemblies"] = "Assemblys", ["sol.analyzers"] = "Analysetools",
            ["sol.buildOk"] = "{0} erfolgreich erstellt", ["sol.buildFailed"] = "{0}: Build fehlgeschlagen", ["toast.startupSet"] = "Startprojekt: {0}",
            ["cmd.ef"] = "Entity Framework Core", ["ef.toolMissing"] = "dotnet-ef nicht installiert", ["ef.checking"] = "prüfe dotnet-ef …",
            ["ef.migrations"] = "Migrationen", ["ef.context"] = "Kontext", ["ef.scaffold"] = "Scaffolding",
            ["ef.toolMissingText"] = "Das Werkzeug dotnet-ef fehlt. Es wird global installiert (dotnet tool install --global dotnet-ef).", ["ef.installTool"] = "dotnet-ef installieren",
            ["ef.toolInstallFailed"] = "dotnet-ef konnte nicht installiert werden – Details in der Ausgabe.",
            ["ef.designMissing"] = "Im Startprojekt {0} fehlt Microsoft.EntityFrameworkCore.Design – ohne dieses Paket funktionieren Migrationen nicht.",
            ["ef.noContexts"] = "Kein DbContext gefunden", ["ef.startup"] = "Startprojekt:", ["ef.migrationName"] = "Name der neuen Migration, z. B. AddProducts",
            ["ef.addMigration"] = "Migration hinzufügen", ["ef.updateDatabase"] = "Datenbank aktualisieren", ["ef.removeLast"] = "Letzte entfernen",
            ["ef.loading"] = "Wird geladen …", ["ef.noMigrations"] = "Noch keine Migrationen.", ["ef.applied"] = "angewendet", ["ef.notApplied"] = "ausstehend",
            ["ef.migrateTo"] = "Bis hierher", ["ef.migrateToHint"] = "Datenbank auf diese Migration bringen (vor- oder zurück)", ["ef.openFile"] = "Datei öffnen",
            ["ef.scriptAll"] = "SQL-Skript (idempotent)", ["ef.checkPending"] = "Auf Modelländerungen prüfen", ["ef.revertAll"] = "Alle zurücknehmen",
            ["ef.revertAllText"] = "Alle Migrationen werden aus der Datenbank zurückgenommen (database update 0). Daten können verloren gehen.",
            ["ef.connection"] = "Verbindung", ["ef.database"] = "Datenbank", ["ef.infoUnavailable"] = "Keine Informationen – ist das Projekt gebaut und die Verbindung konfiguriert?",
            ["ef.entities"] = "Entitäten ({0})", ["ef.createScript"] = "CREATE-Skript anzeigen", ["ef.optimize"] = "Compiled Model erzeugen", ["ef.drop"] = "Datenbank löschen",
            ["ef.dropText"] = "Die Datenbank „{0}“ wird unwiderruflich gelöscht.", ["ef.noContextsText"] = "In der Projektmappe wurde kein DbContext gefunden. Du kannst einen aus einer bestehenden Datenbank erzeugen.",
            ["ef.scaffoldText"] = "Erzeugt DbContext und Entitäten aus einer bestehenden Datenbank (Reverse Engineering).", ["ef.connectionString"] = "Verbindungszeichenfolge",
            ["ef.targetProject"] = "Zielprojekt", ["ef.outputDir"] = "Ordner für Entitäten", ["ef.contextDir"] = "Ordner für den Kontext", ["ef.contextName"] = "Name des Kontexts",
            ["ef.tables"] = "Tabellen", ["ef.tablesHint"] = "leer = alle, sonst mit Komma getrennt", ["ef.dataAnnotations"] = "Data Annotations statt Fluent API",
            ["ef.noOnConfiguring"] = "Keine Verbindung in OnConfiguring speichern", ["ef.force"] = "Vorhandene Dateien überschreiben",
            ["ef.providerHint"] = "Das Zielprojekt braucht das Provider-Paket {0} und Microsoft.EntityFrameworkCore.Design.", ["ef.runScaffold"] = "Scaffolding starten",
            ["ef.removeTitle"] = "Migration {0} entfernen?", ["ef.removeText"] = "Die Migrationsdateien werden gelöscht und der Snapshot zurückgesetzt.",
            ["ef.removeAppliedText"] = "Die Migration ist bereits angewendet – sie wird zuerst aus der Datenbank zurückgenommen (--force).",
            ["ef.migrationAdded"] = "Migration {0} angelegt", ["ef.migrationRemoved"] = "Migration entfernt", ["ef.databaseUpdated"] = "Datenbank aktualisiert",
            ["ef.databaseDropped"] = "Datenbank gelöscht", ["ef.optimized"] = "Compiled Model erzeugt", ["ef.noPending"] = "Keine ausstehenden Modelländerungen",
            ["ef.pending"] = "Es gibt Modelländerungen ohne Migration", ["ef.scaffolded"] = "Scaffolding abgeschlossen", ["ef.failed"] = "EF-Befehl fehlgeschlagen – Details in der Ausgabe",
            ["menu.file"] = "Datei", ["menu.edit"] = "Bearbeiten", ["menu.view"] = "Ansicht", ["menu.project"] = "Projekt", ["menu.build"] = "Erstellen",
            ["menu.debug"] = "Debuggen", ["menu.git"] = "Git", ["menu.tools"] = "Tools", ["menu.help"] = "Hilfe",
            ["cmd.helpShortcuts"] = "Alle Befehle & Tastenkürzel", ["cmd.helpDocs"] = "Blazor-Dokumentation", ["cmd.helpEfDocs"] = "EF-Core-Dokumentation",
            ["cmd.helpDebugger"] = "netcoredbg herunterladen", ["cmd.helpAbout"] = "Über Blazor Studio",
            ["about.text"] = "Visuelle IDE für Blazor. Laufzeit: {0} · {1}",
            ["cmd.profiler"] = "Profiler (CPU & Speicher)", ["prof.title"] = "Profiler", ["prof.attachedTo"] = "Verbunden mit {0}", ["prof.notAttached"] = "Nicht verbunden",
            ["prof.process"] = "Prozess", ["prof.noProcesses"] = "Keine .NET-Prozesse", ["prof.attach"] = "Verbinden", ["prof.detach"] = "Trennen",
            ["prof.startHint"] = "Starte die App (ohne Debugger), dann verbindet sich der Profiler mit ihrem Prozess.", ["prof.allocations"] = "Allokationen",
            ["prof.hideIdle"] = "Wartezeit ausblenden", ["prof.hideIdleHint"] = "Threads, die nur warten (Sleep, Wait, Socket), nicht als CPU-Last zählen",
            ["prof.recording"] = "Aufzeichnung läuft … noch {0} s", ["prof.stopEarly"] = "Jetzt beenden", ["prof.analyzing"] = "Wird ausgewertet …", ["prof.record"] = "Aufzeichnen",
            ["prof.empty"] = "Verbinde dich mit der laufenden App und starte eine Aufzeichnung. Nutze die App währenddessen, damit Last entsteht.",
            ["prof.tabMine"] = "Eigener Code", ["prof.tabHotspots"] = "Hotspots", ["prof.tabTree"] = "Aufrufbaum", ["prof.tabAllocTypes"] = "Speicher nach Typ",
            ["prof.tabAllocSites"] = "Speicher nach Ort", ["prof.tabGc"] = "GC & Exceptions", ["prof.summary"] = "{0} s · {1} CPU-Samples · {2} Warte-Samples",
            ["prof.mineHint"] = "Jede Probe wird der innersten Methode aus deinem Code zugeordnet, die sie verursacht hat – auch wenn die Zeit in Framework-Code verbracht wurde. Klick öffnet den Code.",
            ["prof.onlyMine"] = "Nur eigener Code", ["prof.treeHint"] = "Aufrufe von außen nach innen; eigener Code ist markiert. Doppelklick öffnet die Methode.",
            ["prof.allocSiteHint"] = "Allokationen der innersten eigenen Methode zugeordnet (Stichproben je ~100 KB). Darunter die häufigsten Typen.",
            ["prof.gcPause"] = "Pausenzeit gesamt", ["prof.gcMaxPause"] = "Längste Pause", ["prof.heapAfter"] = "Heap nach letztem GC", ["prof.allocated"] = "Allokiert (geschätzt)",
            ["prof.noExceptions"] = "Keine Exceptions im Zeitraum.", ["prof.sourceNotFound"] = "Quellcode für diese Methode nicht gefunden (Framework-Code).",
            ["prof.method"] = "Methode", ["prof.module"] = "Modul", ["prof.share"] = "Anteil", ["prof.noSamples"] = "Keine CPU-Samples – war die App beschäftigt?",
            ["prof.allocSite"] = "Verursacht von", ["prof.type"] = "Typ", ["prof.samples"] = "Stichproben", ["prof.bytes"] = "Bytes (geschätzt)",
            ["prof.noAllocations"] = "Keine Allokationen aufgezeichnet.", ["prof.recordFailed"] = "Aufzeichnung fehlgeschlagen: {0}",
            ["prof.c.cpu-usage"] = "CPU", ["prof.c.working-set"] = "Working Set", ["prof.c.gc-heap-size"] = "GC-Heap", ["prof.c.alloc-rate"] = "Allokationsrate",
            ["prof.c.gen-0-gc-count"] = "Gen-0-GCs", ["prof.c.gen-2-gc-count"] = "Gen-2-GCs", ["prof.c.threadpool-thread-count"] = "ThreadPool-Threads", ["prof.c.exception-count"] = "Exceptions",
            ["tb.tabElements"] = "Elemente", ["tb.tabProject"] = "Projekt", ["tb.tabLibraries"] = "Bibliotheken", ["tb.tabLibrariesHint"] = "Komponenten aus NuGet-Paketen wie MudBlazor, Radzen …",
            ["tb.search"] = "Toolbox durchsuchen …", ["tb.pages"] = "Seiten", ["tb.components"] = "Komponenten", ["tb.scanning"] = "Pakete werden nach Komponenten durchsucht …",
            ["tb.noLibraries"] = "Keine Komponenten-Bibliotheken gefunden. Installiere z. B. MudBlazor oder Radzen.Blazor über NuGet – sie erscheinen dann automatisch hier.",
            ["tb.findLibraries"] = "Bibliotheken suchen", ["tb.componentCount"] = "{0} Komponenten",
            ["tb.setupNeeded"] = "{0} ist noch nicht eingerichtet (Services, CSS/JS, @using, Provider).", ["tb.usingNeeded"] = "@using {0} fehlt in _Imports.razor.",
            ["tb.setUp"] = "Jetzt einrichten", ["tb.setupNothing"] = "Bereits eingerichtet", ["tb.setupDone"] = "{0} eingerichtet: {1}",
            ["cmd.toggleToolbox"] = "Toolbox ein/aus",
            ["libcat.layout"] = "Layout", ["libcat.actions"] = "Aktionen", ["libcat.inputs"] = "Eingaben", ["libcat.text"] = "Text", ["libcat.navigation"] = "Navigation",
            ["libcat.data"] = "Daten", ["libcat.feedback"] = "Rückmeldung", ["libcat.media"] = "Medien", ["libcat.other"] = "Weitere",
            ["dev.title"] = "Geräteansicht", ["dev.desktop"] = "Computer", ["dev.tablet"] = "Tablet", ["dev.phone"] = "Handy", ["dev.custom"] = "Breakpoint",
            ["dev.rotate"] = "Hoch-/Querformat", ["dev.landscape"] = "Querformat",
            ["cmd.publish"] = "Veröffentlichen (Publish)", ["pub.title"] = "Veröffentlichen", ["pub.noProfiles"] = "Noch keine Publish-Profile. Lege unten eins an.",
            ["pub.new"] = "Neues Profil", ["pub.t.Folder"] = "Ordner", ["pub.t.Zip"] = "ZIP-Archiv", ["pub.t.Container"] = "Docker-Container", ["pub.t.WebDeploy"] = "IIS (Web Deploy)", ["pub.t.Other"] = "Anderes",
            ["pub.name"] = "Profilname", ["pub.target"] = "Ziel", ["pub.configuration"] = "Konfiguration", ["pub.framework"] = "Zielframework", ["pub.fromProject"] = "aus dem Projekt",
            ["pub.runtime"] = "Zielplattform", ["pub.portable"] = "Portabel (alle)", ["pub.mode"] = "Bereitstellung", ["pub.fdd"] = "Framework-abhängig", ["pub.scd"] = "Eigenständig",
            ["pub.fddHint"] = "Braucht installiertes .NET auf dem Zielsystem – klein", ["pub.scdHint"] = ".NET wird mitgeliefert – größer, läuft ohne Installation",
            ["pub.output"] = "Zielordner", ["pub.deleteExisting"] = "Vorhandene Dateien vorher löschen", ["pub.repository"] = "Image-Name", ["pub.registry"] = "Registry",
            ["pub.registryHint"] = "leer = lokales Docker, z. B. ghcr.io", ["pub.containerHint"] = "Nutzt die eingebaute Container-Unterstützung des .NET SDK (dotnet publish -t:PublishContainer) – ohne Dockerfile.",
            ["pub.server"] = "Server (Web Deploy)", ["pub.site"] = "IIS-Site / App", ["pub.user"] = "Benutzer", ["pub.passwordHint"] = "Das Kennwort wird nicht gespeichert, sondern beim Veröffentlichen abgefragt.",
            ["pub.singleFile"] = "Einzelne Datei", ["pub.trim"] = "Trimmen", ["pub.trimHint"] = "Entfernt ungenutzten Code – vorher testen, Reflection kann brechen",
            ["pub.openFolder"] = "Ordner öffnen", ["pub.save"] = "Profil speichern", ["pub.publish"] = "Veröffentlichen", ["pub.pick"] = "Wähle links ein Profil oder lege ein neues an.",
            ["pub.saved"] = "{0} gespeichert", ["pub.deleteTitle"] = "Profil {0} löschen?", ["pub.passwordTitle"] = "Kennwort für Web Deploy", ["pub.passwordText"] = "Benutzer {0} auf {1}",
            ["pub.failed"] = "Veröffentlichen fehlgeschlagen – Details in der Ausgabe.", ["pub.zipDone"] = "{0} erstellt ({1}) in {2} s", ["pub.done"] = "{0} Dateien ({1}) in {2} s veröffentlicht",
            ["pub.doneGeneric"] = "Veröffentlicht in {0} s",
            ["inspector.search"] = "Eigenschaften, Styles, Events suchen …", ["inspector.noResults"] = "Nichts gefunden für „{0}“", ["action.clear"] = "Leeren",
            ["live.layout"] = "Layout", ["live.layoutHint"] = "Seite mit ihrem Layout (MainLayout, Navigation …) anzeigen oder nur die Seite selbst",
            ["lib.layoutRepaired"] = "{0} repariert: Bibliotheks-Provider standen im @code-Block und sind jetzt an der richtigen Stelle.",
            ["nav.noSymbol"] = "Kein Symbol an dieser Stelle", ["nav.external"] = "{0} ist in einer Bibliothek definiert: {1}", ["nav.noImpl"] = "Keine Implementierungen von {0} gefunden", ["nav.usages"] = "Verwendungen von {0}", ["nav.implementations"] = "Implementierungen von {0}", ["nav.definitions"] = "Definitionen von {0}", ["nav.rename"] = "Umbenennen", ["nav.renameText"] = "Neuer Name für „{0}“ (alle Verwendungen in der Solution werden angepasst):", ["nav.renamed"] = "{0} → {1}: {2} Stellen in {3} Dateien", ["nav.renameInvalid"] = "„{0}“ ist kein gültiger Name", ["nav.renameMetadata"] = "{0} stammt aus einer Bibliothek und kann nicht umbenannt werden", ["nav.goToDef"] = "Gehe zu Definition", ["nav.findUsages"] = "Verwendungen suchen", ["nav.goToImpl"] = "Gehe zu Implementierung", ["find.navSummary"] = "{0} Treffer in {1} Dateien",
            ["settings.scheme"] = "Farbschema im Editor", ["events.actions"] = "Aktionen", ["nuget.project"] = "Projekt", ["strip.toolbox"] = "Toolbox", ["strip.layers"] = "Ebenen", ["strip.solution"] = "Solution", ["strip.commit"] = "Commit", ["bottom.terminal"] = "Terminal", ["bottom.build"] = "Build", ["bottom.nuget"] = "NuGet", ["bottom.git"] = "Git", ["term.new"] = "Neues Terminal", ["term.interrupt"] = "Abbrechen (Strg+C)", ["term.clear"] = "Leeren", ["term.running"] = "läuft …", ["term.openHere"] = "Terminal hier öffnen", ["git.title"] = "Git", ["git.changes"] = "Änderungen", ["git.staged"] = "Gestaged", ["git.unversioned"] = "Nicht versioniert", ["git.log"] = "Log", ["git.branches"] = "Branches", ["git.console"] = "Konsole", ["git.noRepo"] = "Dieser Ordner ist kein Git-Repository.", ["git.init"] = "Git-Repository erstellen", ["git.initDone"] = "Git-Repository erstellt", ["git.stage"] = "Stagen", ["git.stageAll"] = "Alles stagen", ["git.unstage"] = "Unstagen", ["git.unstageAll"] = "Alles unstagen", ["git.discard"] = "Verwerfen", ["git.discardTitle"] = "Änderungen verwerfen?", ["git.discardText"] = "Alle lokalen Änderungen an „{0}“ gehen verloren.", ["git.commit"] = "Commit", ["git.commitPush"] = "Commit & Push", ["git.amend"] = "Amend", ["git.message"] = "Commit-Nachricht", ["git.committed"] = "Commit erstellt", ["git.push"] = "Push", ["git.pushed"] = "Gepusht", ["git.pull"] = "Pull", ["git.pulled"] = "Aktualisiert", ["git.fetch"] = "Fetch", ["git.checkout"] = "Auschecken", ["git.newBranch"] = "Neuer Branch", ["git.newBranchFrom"] = "Neuer Branch ab „{0}“", ["git.deleteBranch"] = "Branch löschen", ["git.deleteBranchText"] = "Branch „{0}“ löschen?", ["git.forceDelete"] = "Erzwingen (-D)", ["git.merge"] = "In aktuellen Branch mergen", ["git.rebase"] = "Aktuellen Branch darauf rebasen", ["git.stash"] = "Stash", ["git.stashText"] = "Alle Änderungen (inkl. neuer Dateien) beiseitelegen. Optionaler Name:", ["git.unstash"] = "Stash anwenden", ["git.stashes"] = "Stashes", ["git.revert"] = "Commit rückgängig machen (revert)", ["git.reset"] = "Reset", ["git.resetSoft"] = "Branch hierhin zurücksetzen (Änderungen behalten)", ["git.resetHard"] = "Hart zurücksetzen", ["git.resetHardText"] = "Branch hart auf {0} zurücksetzen? Lokale Änderungen gehen verloren.", ["git.cherryPick"] = "Cherry-Pick", ["git.tag"] = "Tag erstellen", ["git.remote"] = "Remote (origin)", ["git.remoteText"] = "URL des Remote-Repositorys:", ["git.copyHash"] = "Hash kopieren", ["git.noChanges"] = "Keine Änderungen – alles committet.", ["git.ahead"] = "{0} zum Pushen", ["git.behind"] = "{0} zum Pullen", ["git.noUpstream"] = "kein Upstream", ["git.local"] = "Lokal", ["git.remoteBranches"] = "Remote", ["git.selectFile"] = "Datei wählen, um den Diff zu sehen.", ["git.openFile"] = "Datei öffnen", ["git.refresh"] = "Aktualisieren", ["git.forcePush"] = "Force-Push (with lease)", ["git.history"] = "Verlauf", ["action.delete"] = "Löschen", ["cmd.copyPath"] = "Pfad kopieren", ["upd.check"] = "Nach Updates suchen …", ["upd.title"] = "Blazor Studio {0} ist verfügbar", ["upd.current"] = "Installiert: {0}", ["upd.checking"] = "Suche nach Updates …", ["upd.upToDate"] = "Blazor Studio ist aktuell", ["upd.noNotes"] = "Keine Release-Notes.", ["upd.downloading"] = "Lade herunter … {0} %", ["upd.starting"] = "Starte Installation – Blazor Studio startet gleich neu …", ["upd.manualHint"] = "Diese Instanz wurde nicht über das Setup installiert – lade das Setup herunter und führe es aus.", ["upd.releasePage"] = "Release-Seite", ["upd.skip"] = "Diese Version überspringen", ["upd.later"] = "Später", ["upd.install"] = "Jetzt aktualisieren", ["upd.download"] = "Setup herunterladen", ["upd.checkFailed"] = "Prüfung fehlgeschlagen:", ["upd.unsavedTitle"] = "Ungespeicherte Änderungen", ["upd.unsavedText"] = "Für das Update wird Blazor Studio neu gestartet. Änderungen vorher speichern?", ["upd.saveAndUpdate"] = "Speichern & aktualisieren", ["upd.pill"] = "Update {0}", ["upd.pillTitle"] = "Neue Version verfügbar – klicken zum Aktualisieren", ["build.Build"] = "Build", ["build.Rebuild"] = "Rebuild", ["build.Clean"] = "Clean", ["build.CleanBuild"] = "Clean & Build", ["build.Restore"] = "NuGet-Pakete wiederherstellen", ["build.solution"] = "Solution erstellen", ["build.rebuildSolution"] = "Solution neu erstellen (Rebuild)", ["build.cleanSolution"] = "Solution bereinigen (Clean)", ["build.cleanBuildSolution"] = "Solution: Clean & Build", ["build.restoreSolution"] = "Solution: NuGet wiederherstellen", ["build.rebuildProject"] = "Projekt neu erstellen (Rebuild)", ["build.cleanProject"] = "Projekt bereinigen (Clean)", ["build.ok"] = "{0} erfolgreich: {1}", ["build.failed"] = "{0} fehlgeschlagen: {1}", ["sol.add"] = "Hinzufügen", ["sol.newProject"] = "Neues Projekt", ["sol.newProjectText"] = "Erstellt ein Projekt mit dotnet new und fügt es der Solution hinzu.", ["sol.addExisting"] = "Vorhandenes Projekt", ["sol.editSolution"] = "Solution-Datei bearbeiten", ["sol.reloadSolution"] = "Solution neu laden", ["sol.reloadProject"] = "Projekt neu laden", ["sol.unloadProject"] = "Projekt entladen", ["sol.removeProject"] = "Aus Solution entfernen", ["sol.removeProjectText"] = "„{0}“ aus der Solution entfernen? Die Dateien bleiben auf der Festplatte.", ["sol.unloadedTag"] = "(entladen)", ["sol.unloaded"] = "„{0}“ entladen", ["sol.reloaded"] = "„{0}“ neu geladen", ["sol.cantUnloadStartup"] = "Das einzige Projekt kann nicht entladen werden.", ["sol.projectReference"] = "Projektverweis", ["sol.removeReference"] = "Verweis entfernen", ["sol.removePackage"] = "Paket entfernen", ["sol.removePackageText"] = "Paket „{0}“ aus „{1}“ entfernen?", ["sol.newFolder"] = "Neuer Ordner", ["sol.folderName"] = "Ordnername", ["sol.rename"] = "Umbenennen", ["sol.renameHint"] = "Komponente umbenannt – Verwendungen wie <AlterName /> bitte anpassen.", ["sol.delete"] = "Löschen", ["sol.toRecycleBin"] = "In den Papierkorb", ["sol.deleteFileText"] = "„{0}“ in den Papierkorb verschieben?", ["sol.deleteFolderText"] = "Ordner „{0}“ mit Inhalt in den Papierkorb verschieben?", ["sol.deleteCompanions"] = "Zugehörige Dateien: {0}", ["sol.deleteFailed"] = "„{0}“ konnte nicht gelöscht werden.", ["sol.copyRelativePath"] = "Relativen Pfad kopieren", ["sol.pathCopied"] = "Pfad kopiert", ["sol.open"] = "Öffnen", ["sol.invalidName"] = "Ungültiger Name.", ["sol.folderExists"] = "Der Ordner „{0}“ existiert bereits.", ["sol.projectCreated"] = "Projekt „{0}“ erstellt", ["sol.tpl.blazor"] = "Blazor Web App", ["sol.tpl.blazorwasm"] = "Blazor WebAssembly", ["sol.tpl.razorclasslib"] = "Razor-Klassenbibliothek", ["sol.tpl.webapi"] = "Web API", ["sol.tpl.classlib"] = "Klassenbibliothek", ["sol.tpl.console"] = "Konsolen-App", ["sol.tpl.worker"] = "Worker Service", ["sol.tpl.xunit"] = "xUnit-Tests", ["sol.tpl.nunit"] = "NUnit-Tests", ["sol.tpl.mstest"] = "MSTest-Tests", ["newFile.title"] = "Neue Datei", ["newFile.search"] = "Vorlage suchen …", ["newFile.preview"] = "Vorschau", ["newFile.namespace"] = "Namespace: {0}", ["newFile.exists"] = "„{0}“ existiert bereits.", ["newFile.g.csharp"] = "C#", ["newFile.g.blazor"] = "Blazor", ["newFile.g.web"] = "Web", ["newFile.g.data"] = "Daten & Konfiguration", ["newFile.g.other"] = "Sonstiges", ["newFile.t.class"] = "Klasse", ["newFile.t.interface"] = "Interface", ["newFile.t.record"] = "Record", ["newFile.t.enum"] = "Enum", ["newFile.t.struct"] = "Struct", ["newFile.t.service"] = "Service (Interface + Klasse)", ["newFile.t.controller"] = "API-Controller", ["newFile.t.dbcontext"] = "EF Core DbContext", ["newFile.t.test"] = "xUnit-Testklasse", ["newFile.t.component"] = "Razor-Komponente", ["newFile.t.page"] = "Razor-Seite (@page)", ["newFile.t.layout"] = "Layout", ["newFile.t.codebehind"] = "Code-Behind (.razor.cs)", ["newFile.t.scopedcss"] = "Scoped CSS (.razor.css)", ["newFile.t.imports"] = "_Imports.razor", ["newFile.t.css"] = "Stylesheet", ["newFile.t.js"] = "JavaScript", ["newFile.t.jsmodule"] = "JS-Modul zur Komponente", ["newFile.t.html"] = "HTML-Seite", ["newFile.t.json"] = "JSON", ["newFile.t.appsettings"] = "appsettings.json", ["newFile.t.xml"] = "XML", ["newFile.t.markdown"] = "Markdown", ["newFile.t.text"] = "Textdatei", ["newFile.t.editorconfig"] = ".editorconfig", ["newFile.t.gitignore"] = ".gitignore", ["find.title"] = "In Dateien suchen", ["find.replaceTitle"] = "In Dateien ersetzen", ["find.placeholder"] = "Suchen in Dateien …", ["find.case"] = "Groß-/Kleinschreibung beachten", ["find.word"] = "Nur ganze Wörter", ["find.regex"] = "Regulärer Ausdruck", ["find.mask"] = "Dateifilter, z. B. *.cs;*.razor;!*.min.js", ["find.maskHint"] = "Mehrere Muster mit ; trennen, ! schließt aus", ["find.scopeSolution"] = "Ganze Solution", ["find.scopeProject"] = "Startprojekt", ["find.replaceToggle"] = "Ersetzen ein-/ausblenden", ["find.replacePlaceholder"] = "Ersetzen durch …", ["find.replaceAll"] = "Alle ersetzen", ["find.regexHint"] = "$1, ${name} für Gruppen", ["find.summary"] = "{0} Treffer in {1} Dateien ({2} durchsucht)", ["find.truncated"] = "nur die ersten 5000", ["find.hint"] = "Suchbegriff eingeben – bin/obj/.git werden übersprungen.", ["find.replaceConfirm"] = "{0} Treffer in {1} Dateien ersetzen?\n„{2}“ → „{3}“\nGeöffnete Dateien werden im Editor geändert (Strg+Z möglich), andere direkt gespeichert.", ["find.replaced"] = "{0} Ersetzungen in {1} Dateien", ["lib.scanFailed"] = "Komponenten-Bibliotheken konnten nicht gelesen werden: {0}", ["live.menuTitle"] = "Echte Darstellung im Designer", ["live.approx"] = "Schnellansicht", ["live.state.Off"] = "Live aus", ["live.state.Starting"] = "Echte Ansicht startet …", ["live.state.Ready"] = "Live", ["live.state.Rebuilding"] = "Aktualisiert …", ["live.state.Failed"] = "Build-Fehler", ["live.interacting"] = "interaktiv", ["live.toggle"] = "Echte Darstellung (App läuft im Designer)", ["live.interact"] = "Mit der App interagieren (Klicks an die App)", ["live.restart"] = "Neu starten", ["live.log"] = "Protokoll", ["live.failedTitle"] = "Echte Darstellung: Das Projekt lässt sich nicht bauen", ["live.failedText"] = "Details im Protokoll – bis dahin siehst du die Schnellansicht.", ["tip.previewStarting"] = "Echte Vorschau startet …", ["tip.previewUnavailable"] = "Keine Live-Vorschau für dieses Element", ["tip.previewOff"] = "Echte Darstellung ist aus", ["tip.moreParams"] = "+{0} weitere Parameter", ["tip.events"] = "{0} Events", ["tip.hint"] = "Klicken zum Einfügen · Ziehen zum Platzieren", ["tip.project"] = "Projekt", ["tip.template"] = "Vorlage", ["action.add"] = "Hinzufügen", ["action.browse"] = "Durchsuchen", ["action.cancel"] = "Abbrechen", ["action.create"] = "Erstellen",
            ["action.done"] = "Fertig", ["action.dontSave"] = "Nicht speichern", ["action.ok"] = "OK", ["action.remove"] = "Entfernen", ["action.save"] = "Speichern",

            ["binding.loop"] = "Schleifenvariable",
            ["block.cards"] = "Karten-Raster (3 Spalten)", ["block.contact"] = "Kontaktformular (EditForm)", ["block.footer"] = "Fußzeile",
            ["block.hero"] = "Hero mit Buttons", ["block.list"] = "Datenliste (@foreach)", ["block.navbar"] = "Navigationsleiste",
            ["blocks.hint"] = "Bausteine auf die Fläche oder in die Ebenen ziehen – oder anklicken, um sie hinter der Auswahl einzufügen.",

            ["bottom.clear"] = "Ausgabe leeren", ["bottom.debug"] = "Debuggen", ["bottom.debuggerMissing"] = "netcoredbg wurde nicht gefunden – Pfad in den Einstellungen hinterlegen.",
            ["bottom.debugIdle"] = "Mit F5 debuggen. Haltepunkte setzt du mit einem Klick links neben die Zeilennummer oder mit F9.",
            ["bottom.noProblems"] = "Keine Probleme gefunden.", ["bottom.output"] = "Ausgabe", ["bottom.outputEmpty"] = "Hier erscheint die Ausgabe von Build, Start und Debugger.",
            ["bottom.problems"] = "Probleme",

            ["canvas.dropHere"] = "Hier ablegen", ["canvas.emptyTitle"] = "Leere Komponente",
            ["canvas.emptyText"] = "Ziehe Elemente aus der Leiste oben hierher oder wähle einen Baustein rechts.", ["canvas.fullWidth"] = "Volle Breite",

            ["cat.layout"] = "Layout", ["cat.text"] = "Text", ["cat.form"] = "Formular", ["cat.media"] = "Medien", ["cat.blazor"] = "Blazor", ["cat.project"] = "Projekt",

            ["cmd.build"] = "Erstellen (Build)", ["cmd.closeProject"] = "Projekt schließen", ["cmd.closeTab"] = "Tab schließen", ["cmd.commands"] = "Befehle",
            ["cmd.continue"] = "Weiter", ["cmd.copy"] = "Kopieren", ["cmd.cut"] = "Ausschneiden", ["cmd.debug"] = "Debuggen", ["cmd.delete"] = "Löschen",
            ["cmd.duplicate"] = "Duplizieren", ["cmd.goToFile"] = "Datei öffnen", ["cmd.moveDown"] = "Nach unten", ["cmd.moveUp"] = "Nach oben",
            ["cmd.newComponent"] = "Neue Komponente", ["cmd.newProject"] = "Neues Projekt", ["cmd.openBrowser"] = "Im Browser öffnen",
            ["cmd.openProject"] = "Projekt öffnen", ["cmd.paste"] = "Einfügen", ["cmd.redo"] = "Wiederholen", ["cmd.rescan"] = "Projekt neu einlesen",
            ["cmd.restart"] = "Neu starten", ["cmd.reveal"] = "Im Explorer zeigen", ["cmd.run"] = "Starten mit Hot Reload", ["cmd.runNoHot"] = "Starten ohne Hot Reload",
            ["cmd.save"] = "Speichern", ["cmd.saveAll"] = "Alle speichern", ["cmd.selectParent"] = "Übergeordnetes Element", ["cmd.settings"] = "Einstellungen",
            ["cmd.stepInto"] = "Einzelschritt", ["cmd.stepOut"] = "Ausführen bis Rücksprung", ["cmd.stepOver"] = "Prozedurschritt", ["cmd.stop"] = "Stoppen",
            ["cmd.toggleBottom"] = "Unteres Panel ein/aus", ["cmd.toggleLanguage"] = "Sprache wechseln (Deutsch/English)", ["cmd.toggleTheme"] = "Hell/Dunkel wechseln",
            ["cmd.undo"] = "Rückgängig", ["cmd.unwrap"] = "Umschließung entfernen", ["cmd.viewCode"] = "Ansicht: Code", ["cmd.viewDesign"] = "Ansicht: Design",
            ["cmd.viewSplit"] = "Ansicht: Split", ["cmd.wrap"] = "In Container packen", ["cmd.wrapFlex"] = "In Flex-Zeile packen", ["cmd.zoomIn"] = "Vergrößern",
            ["cmd.zoomOut"] = "Verkleinern", ["cmd.zoomReset"] = "Zoom zurücksetzen",

            ["debug.addWatch"] = "Ausdruck hinzufügen …", ["debug.callStack"] = "Aufrufliste", ["debug.external"] = "extern",
            ["debug.fallbackRun"] = "Kein Debugger gefunden – die App wird ohne Debugger gestartet. netcoredbg kannst du in den Einstellungen hinterlegen.",
            ["debug.locals"] = "Lokale Variablen", ["debug.missingTitle"] = "Debugger fehlt",
            ["debug.missingText"] = "Zum Debuggen braucht Blazor Studio „netcoredbg“ (kostenlos, MIT-Lizenz, von Samsung). Lade es herunter, entpacke es und hinterlege den Pfad zu netcoredbg.exe in den Einstellungen.",
            ["debug.noDll"] = "Die Ausgabe-DLL wurde nicht gefunden. Ist der Build erfolgreich?", ["debug.runningHint"] = "Läuft … setze einen Haltepunkt, um Variablen zu sehen.",
            ["debug.startFailed"] = "Debugger konnte nicht starten: {0}", ["debug.wasmHint"] = "WebAssembly-Projekte werden im Browser debuggt – Haltepunkte im Server-Code funktionieren trotzdem.",
            ["debug.watch"] = "Überwachen",

            ["design.allStyles"] = "Alle Stile", ["design.border"] = "Rahmen & Effekte", ["design.classes"] = "Klassen", ["design.columns"] = "Spalten",
            ["design.createClass"] = "Klasse anlegen", ["design.extract"] = "Inline-Stile in Klasse verschieben", ["design.extractTo"] = "Als Klasse in {0} speichern",
            ["design.fill"] = "Füllung", ["design.inFile"] = "in", ["design.inFlex"] = "Im Flex-Container", ["design.inlineWins"] = "Inline-Stile überschreiben Regeln für Zustände und Breakpoints. Verschiebe sie zuerst in eine Klasse.",
            ["design.known"] = "bekannt", ["design.layout"] = "Layout", ["design.needsClass"] = "Für Zustände und Breakpoints braucht das Element eine CSS-Klasse.",
            ["design.none"] = "Keiner", ["design.normal"] = "Normal", ["design.noWrap"] = "Nicht umbrechen", ["design.opacity"] = "Deckkraft",
            ["design.property"] = "Eigenschaft", ["design.radius"] = "Eckenradius", ["design.scopeBase"] = "Änderungen gelten für alle Breiten (Inline-Stil)",
            ["design.scopeFrom"] = "Änderungen gelten ab {0}", ["design.shadow"] = "Schatten", ["design.spacing"] = "Abstände & Größe", ["design.state"] = "Zustand",
            ["design.textColor"] = "Textfarbe", ["design.tokens"] = "Design-Tokens", ["design.typography"] = "Typografie", ["design.value"] = "Wert",
            ["design.weight"] = "Stärke", ["design.wrap"] = "Umbrechen",

            ["designer.goToError"] = "Zum Fehler", ["designer.readonly"] = "Designer pausiert", ["designer.readonlyToast"] = "Der Code enthält Fehler – behebe sie, dann kannst du wieder im Designer bearbeiten.",
            ["designer.synced"] = "synchron mit Designer", ["designer.syncPaused"] = "Fehler im Code",

            ["diag.unclosedAttribute"] = "Attribut „{0}“ ist nicht geschlossen.", ["diag.unclosedBlock"] = "Block „{0}“ ist nicht geschlossen.",
            ["diag.unclosedComment"] = "Kommentar „{0}“ ist nicht geschlossen.", ["diag.unclosedTag"] = "Tag <{0}> ist nicht geschlossen.",
            ["diag.unexpectedClose"] = "Unerwartetes </{0}>.",

            ["dlg.pickProject"] = "Projektordner wählen", ["dlg.unsavedOnExit"] = "Es gibt ungespeicherte Änderungen. Vor dem Beenden speichern?",
            ["dlg.unsavedText"] = "Deine Änderungen gehen verloren, wenn du nicht speicherst.", ["dlg.unsavedTitle"] = "Änderungen an {0} speichern?",

            ["doc.addProperty"] = "Property / Feld hinzufügen", ["doc.addPropertyHint"] = "C#-Deklaration, z. B. „[Parameter] public bool IsOpen { get; set; }“ oder „private int count;“.",
            ["doc.code"] = "Code", ["doc.createCodeBehind"] = "Code-Behind {0} anlegen", ["doc.directives"] = "Direktiven", ["doc.empty"] = "Keine Datei geöffnet",
            ["doc.memberStats"] = "{0} Felder/Properties · {1} Methoden · {2} Parameter", ["doc.noRoute"] = "keine Seite (nur Komponente)",
            ["doc.route"] = "Route", ["doc.routeHint"] = "Mit Route wird die Komponente zur Seite, z. B. /products/{id:int}.",
            ["doc.tip"] = "Tipp: Wähle ein Element auf der Fläche, in den Ebenen oder einfach durch Klick in den Code.",

            ["editor.loading"] = "Editor wird geladen …",

            ["events.callbacksOf"] = "Callbacks von {0}", ["events.createRef"] = "Feld anlegen", ["events.directives"] = "Direktiven",
            ["events.goTo"] = "Zur Methode springen", ["events.lambda"] = "Lambda", ["events.newHandler"] = "Handler", ["events.newMethod"] = "Neue Methode",
            ["events.noCallbacks"] = "Diese Komponente hat keine EventCallback-Parameter.", ["events.pickExisting"] = "Vorhandene Methode wählen", ["events.remove"] = "Entfernen",

            ["evgroup.mouse"] = "Maus", ["evgroup.focus"] = "Fokus", ["evgroup.keyboard"] = "Tastatur", ["evgroup.input"] = "Eingabe", ["evgroup.form"] = "Formular",
            ["evgroup.media"] = "Medien", ["evgroup.clipboard"] = "Zwischenablage", ["evgroup.drag"] = "Ziehen", ["evgroup.pointer"] = "Pointer & Touch", ["evgroup.other"] = "Weitere",

            ["feat.binding"] = "Binding", ["feat.bindingText"] = "Parameter und Events direkt an @code und .razor.cs binden.",
            ["feat.debug"] = "Debugger", ["feat.debugText"] = "Haltepunkte, Variablen und Schritte in C#.",
            ["feat.dnd"] = "Drag & Drop", ["feat.dndText"] = "Elemente und Komponenten einfach ziehen.",
            ["feat.responsive"] = "Responsive", ["feat.responsiveText"] = "Breakpoints und Zustände wie :hover.",

            ["files.filter"] = "Dateien filtern …",
            ["insert.hint"] = "Ziehen oder klicken zum Einfügen", ["insert.noProjectComponents"] = "Noch keine eigenen Komponenten",

            ["inspector.addClass"] = "Klasse hinzufügen …", ["inspector.bind"] = "binden", ["inspector.bindHint"] = "An einen Wert aus @code / .razor.cs binden",
            ["inspector.bound"] = "gebunden", ["inspector.component"] = "Komponente", ["inspector.expressionPlaceholder"] = "C#-Ausdruck",
            ["inspector.fragmentHint"] = "Inhalt direkt im Designer in die Komponente ziehen.", ["inspector.in"] = "in {0}",
            ["inspector.makeTwoWay"] = "Zwei-Wege-Binding (@bind-)", ["inspector.memberPlaceholder"] = "Member wählen oder eingeben",
            ["inspector.noMembers"] = "Keine passenden Member gefunden – lege im @code oder in der .razor.cs ein Feld oder eine Property an.",
            ["inspector.noRazor"] = "Öffne eine .razor-Datei, um Elemente zu bearbeiten.", ["inspector.openComponent"] = "Komponente öffnen",
            ["inspector.pageAt"] = "Seite unter {0}", ["inspector.required"] = "Pflicht", ["inspector.reset"] = "Zurücksetzen",
            ["inspector.tabDesign"] = "Design", ["inspector.tabEvents"] = "Events", ["inspector.tabProps"] = "Eigenschaften",

            ["kind.blazor"] = "Blazor-Komponente", ["kind.code"] = "C#-Block", ["kind.component"] = "Komponente", ["kind.control"] = "Kontrollfluss",
            ["kind.element"] = "HTML-Element", ["kind.expression"] = "Razor-Ausdruck", ["kind.text"] = "Text", ["kind.unknownComponent"] = "Unbekannte Komponente",

            ["layers.directives"] = "{0} Direktiven", ["layers.empty"] = "Noch keine Elemente – zieh etwas auf die Fläche.",
            ["layers.members"] = "{0} Member", ["layers.noRazor"] = "Ebenen gibt es nur für .razor-Dateien.",

            ["newComp.codeBehind"] = "Code-Behind (.razor.cs) anlegen", ["newComp.component"] = "Komponente", ["newComp.componentHint"] = "Wiederverwendbarer Baustein",
            ["newComp.folder"] = "Ordner", ["newComp.name"] = "Name", ["newComp.page"] = "Seite", ["newComp.pageHint"] = "Mit eigener Route (@page)",
            ["newComp.route"] = "Route", ["newComp.scopedCss"] = "Scoped CSS (.razor.css) anlegen", ["newComp.text"] = "Komponenten-Namen beginnen mit einem Großbuchstaben.",
            ["newComp.title"] = "Neue Komponente",

            ["newProj.creating"] = "Projekt wird angelegt (dotnet new) …", ["newProj.interactivity"] = "Interaktivität", ["newProj.location"] = "Speicherort",
            ["newProj.name"] = "Projektname", ["newProj.sample"] = "Beispielseiten einfügen", ["newProj.text"] = "Legt ein neues Blazor-Projekt mit dotnet new an und öffnet es.",
            ["newProj.title"] = "Neues Blazor-Projekt", ["newProj.wasm"] = "Blazor WebAssembly", ["newProj.wasmHint"] = "Läuft komplett im Browser",
            ["newProj.web"] = "Blazor Web App", ["newProj.webHint"] = "Server + optional WebAssembly (empfohlen)",

            ["node.addElse"] = "else-Zweig hinzufügen", ["node.condition"] = "Bedingung", ["node.controlHint"] = "C#-Kopf des Blocks.",
            ["node.expressionHint"] = "Wird zur Laufzeit ausgewertet und als Text ausgegeben.", ["node.foreachHint"] = "Die Schleifenvariable steht innen für Bindings zur Verfügung.",
            ["node.ifHint"] = "Der Inhalt wird nur gerendert, wenn die Bedingung true ist.", ["node.loop"] = "Schleife",

            ["palette.commands"] = "Befehl suchen …", ["palette.commandsHint"] = "Befehle", ["palette.files"] = "Datei suchen … ( > Befehle, + Einfügen )",
            ["palette.groupCommands"] = "Befehle", ["palette.groupFiles"] = "Dateien", ["palette.groupInsert"] = "Einfügen", ["palette.insertHint"] = "Element einfügen",
            ["palette.navigate"] = "auswählen", ["palette.nothing"] = "Nichts gefunden.", ["palette.run"] = "ausführen",

            ["props.callbacksInEvents"] = "Callbacks dieser Komponente findest du im Reiter „Events“.", ["props.content"] = "Inhalt",
            ["props.contentHint"] = "Text", ["props.doubleClickHint"] = "Tipp: Doppelklick auf der Fläche bearbeitet den Text direkt.",
            ["props.name"] = "Name", ["props.noParameters"] = "{0} hat keine [Parameter].", ["props.otherAttributes"] = "Weitere Attribute",
            ["props.twoWay"] = "Zwei-Wege-Binding", ["props.unknownComponent"] = "„{0}“ ist im Projekt nicht bekannt – Attribute können trotzdem gesetzt werden.",
            ["props.value"] = "Wert",

            ["run.buildFailed"] = "Build fehlgeschlagen", ["run.building"] = "Wird erstellt …", ["run.buildOk"] = "Build erfolgreich",
            ["run.debugging"] = "Debuggen", ["run.dotnetMissing"] = "Das .NET SDK (dotnet) wurde nicht gefunden.", ["run.exited"] = "Prozess beendet (Code {0})",
            ["run.paused"] = "Angehalten", ["run.start"] = "Starten", ["run.starting"] = "Startet …",

            ["settings.debugger"] = "Debugger (netcoredbg)", ["settings.debuggerHint"] = "Nicht gefunden. Download:", ["settings.fontSize"] = "Schriftgröße Editor",
            ["settings.hotReload"] = "Mit Hot Reload starten", ["settings.language"] = "Sprache", ["settings.minimap"] = "Minimap im Code-Editor",

            ["shadow.small"] = "Klein", ["shadow.soft"] = "Weich", ["shadow.strong"] = "Stark", ["shadow.ring"] = "Fokus-Ring",
            ["side.blocks"] = "Bausteine", ["side.files"] = "Dateien", ["side.layers"] = "Ebenen",

            ["status.cursor"] = "Zeile {0}, Spalte {1}", ["status.parseErrors"] = "Razor-Fehler", ["status.paused"] = "Angehalten · {0}",
            ["status.ready"] = "Bereit", ["status.running"] = "Läuft", ["status.unsaved"] = "ungespeichert",

            ["tb.button"] = "Button", ["tb.card"] = "Karte", ["tb.checkbox"] = "Checkbox", ["tb.container"] = "Container", ["tb.divider"] = "Trennlinie",
            ["tb.foreach"] = "@foreach", ["tb.form"] = "Formular", ["tb.grid"] = "Raster", ["tb.heading1"] = "Überschrift 1", ["tb.heading2"] = "Überschrift 2",
            ["tb.heading3"] = "Überschrift 3", ["tb.icon"] = "Icon", ["tb.if"] = "@if", ["tb.image"] = "Bild", ["tb.input"] = "Eingabe", ["tb.label"] = "Label",
            ["tb.link"] = "Link", ["tb.list"] = "Liste", ["tb.paragraph"] = "Absatz", ["tb.quote"] = "Zitat", ["tb.row"] = "Flex-Zeile", ["tb.section"] = "Abschnitt",
            ["tb.select"] = "Auswahl", ["tb.stack"] = "Stapel", ["tb.textarea"] = "Textbereich", ["tb.video"] = "Video",

            ["theme.accent"] = "Akzentfarbe", ["theme.mode"] = "Modus", ["theme.dark"] = "Dunkel", ["theme.light"] = "Hell", ["theme.system"] = "System",

            ["toast.badComponentName"] = "Komponenten-Namen beginnen mit einem Großbuchstaben und enthalten nur Buchstaben und Ziffern.",
            ["toast.badProjectName"] = "Der Projektname darf nur Buchstaben, Ziffern, Punkt, Binde- und Unterstrich enthalten.",
            ["toast.cannotOpen"] = "{0} kann nicht geöffnet werden.", ["toast.copied"] = "Kopiert", ["toast.created"] = "{0} angelegt",
            ["toast.exists"] = "{0} existiert bereits.", ["toast.fieldCreated"] = "Feld {0} in {1} angelegt", ["toast.folderMissing"] = "Ordner nicht gefunden: {0}",
            ["toast.memberAdded"] = "In {0} eingefügt", ["toast.methodCreated"] = "Methode {0}() in {1} angelegt",
            ["toast.noCsproj"] = "Keine .csproj gefunden – Build, Start und Debugger sind deaktiviert.",
            ["toast.projectFailed"] = "Projekt konnte nicht angelegt werden – Details in der Ausgabe.", ["toast.saved"] = "{0} gespeichert",
            ["toast.saveFailed"] = "{0} konnte nicht gespeichert werden: {1}",

            ["top.appearance"] = "Erscheinungsbild", ["top.search"] = "Datei, Komponente oder Befehl suchen …",
            ["view.code"] = "Code", ["view.design"] = "Design", ["view.live"] = "Live", ["view.liveEmpty"] = "Die App läuft noch nicht.",
            ["view.liveHint"] = "Die laufende App mit Hot Reload direkt hier ansehen", ["view.split"] = "Split",

            ["welcome.lead"] = "Die visuelle IDE für Blazor: Komponenten per Drag & Drop bauen, Stile und Bindings im Inspector setzen, Code mit Vervollständigung schreiben und C# debuggen.",
            ["welcome.newHint"] = "Blazor Web App oder WebAssembly mit dotnet new", ["welcome.noRecent"] = "Noch keine Projekte geöffnet.",
            ["welcome.openHint"] = "Ordner mit einer .csproj wählen", ["welcome.recent"] = "Zuletzt geöffnet",
        },
        ["en"] = new()
        {
            ["cmd.nuget"] = "Manage NuGet packages",
            ["nuget.title"] = "NuGet packages", ["nuget.browse"] = "Browse", ["nuget.installedTab"] = "Installed", ["nuget.updates"] = "Updates",
            ["nuget.searchPlaceholder"] = "Search nuget.org, e.g. MudBlazor …", ["nuget.prerelease"] = "Prerelease", ["nuget.updateAll"] = "Update all",
            ["nuget.refresh"] = "Refresh", ["nuget.verified"] = "Verified owner", ["nuget.by"] = "by {0}", ["nuget.installedBadge"] = "installed",
            ["nuget.noUpdates"] = "All packages are up to date.", ["nuget.noneInstalled"] = "No packages installed yet.", ["nuget.noResults"] = "No packages found.",
            ["nuget.version"] = "Version", ["nuget.latest"] = "latest", ["nuget.working"] = "Working …", ["nuget.install"] = "Install",
            ["nuget.changeTo"] = "Switch to {0}", ["nuget.uninstall"] = "Uninstall", ["nuget.installedVersion"] = "Installed: {0}", ["nuget.description"] = "Description",
            ["nuget.license"] = "License", ["nuget.downloads"] = "Downloads", ["nuget.projectSite"] = "Project site", ["nuget.dependencies"] = "Dependencies",
            ["nuget.pick"] = "Pick a package on the left.", ["nuget.offline"] = "nuget.org is not reachable: {0}",
            ["nuget.confirmRemoveTitle"] = "Uninstall {0}?", ["nuget.confirmRemoveText"] = "The PackageReference will be removed from the .csproj.",
            ["nuget.installed"] = "{0} {1} installed", ["nuget.removed"] = "{0} removed", ["nuget.failed"] = "{0}: operation failed – see the output.",
            ["action.close"] = "Close",
            ["cmd.openSolution"] = "Open solution (.sln, .slnx, .csproj)", ["dlg.solutionFilter"] = "Solutions and projects", ["welcome.solutionHint"] = "Just like in Rider or Visual Studio",
            ["side.solution"] = "Project", ["sol.filter"] = "Filter solution …", ["sol.projectCount"] = "{0} projects", ["sol.setStartup"] = "Set as startup project",
            ["sol.buildProject"] = "Build project", ["sol.editProject"] = "Edit .csproj", ["sol.notFound"] = "not found", ["sol.dependencies"] = "Dependencies",
            ["sol.restoreHint"] = "obj/project.assets.json is missing – build once so packages get resolved.", ["sol.frameworks"] = "Frameworks", ["sol.packages"] = "Packages",
            ["sol.transitive"] = "Transitive packages", ["sol.projects"] = "Projects", ["sol.notInSolution"] = "not in solution", ["sol.assemblies"] = "Assemblies", ["sol.analyzers"] = "Analyzers",
            ["sol.buildOk"] = "{0} built successfully", ["sol.buildFailed"] = "{0}: build failed", ["toast.startupSet"] = "Startup project: {0}",
            ["cmd.ef"] = "Entity Framework Core", ["ef.toolMissing"] = "dotnet-ef not installed", ["ef.checking"] = "checking dotnet-ef …",
            ["ef.migrations"] = "Migrations", ["ef.context"] = "Context", ["ef.scaffold"] = "Scaffolding",
            ["ef.toolMissingText"] = "The dotnet-ef tool is missing. It will be installed globally (dotnet tool install --global dotnet-ef).", ["ef.installTool"] = "Install dotnet-ef",
            ["ef.toolInstallFailed"] = "dotnet-ef could not be installed – see the output.",
            ["ef.designMissing"] = "The startup project {0} lacks Microsoft.EntityFrameworkCore.Design – migrations need it.",
            ["ef.noContexts"] = "No DbContext found", ["ef.startup"] = "Startup project:", ["ef.migrationName"] = "Name of the new migration, e.g. AddProducts",
            ["ef.addMigration"] = "Add migration", ["ef.updateDatabase"] = "Update database", ["ef.removeLast"] = "Remove last",
            ["ef.loading"] = "Loading …", ["ef.noMigrations"] = "No migrations yet.", ["ef.applied"] = "applied", ["ef.notApplied"] = "pending",
            ["ef.migrateTo"] = "Migrate to", ["ef.migrateToHint"] = "Bring the database to this migration (up or down)", ["ef.openFile"] = "Open file",
            ["ef.scriptAll"] = "SQL script (idempotent)", ["ef.checkPending"] = "Check for model changes", ["ef.revertAll"] = "Revert all",
            ["ef.revertAllText"] = "All migrations will be reverted in the database (database update 0). Data may be lost.",
            ["ef.connection"] = "Connection", ["ef.database"] = "Database", ["ef.infoUnavailable"] = "No information – is the project built and the connection configured?",
            ["ef.entities"] = "Entities ({0})", ["ef.createScript"] = "Show CREATE script", ["ef.optimize"] = "Generate compiled model", ["ef.drop"] = "Drop database",
            ["ef.dropText"] = "The database \"{0}\" will be deleted permanently.", ["ef.noContextsText"] = "No DbContext was found in the solution. You can generate one from an existing database.",
            ["ef.scaffoldText"] = "Generates a DbContext and entities from an existing database (reverse engineering).", ["ef.connectionString"] = "Connection string",
            ["ef.targetProject"] = "Target project", ["ef.outputDir"] = "Entities folder", ["ef.contextDir"] = "Context folder", ["ef.contextName"] = "Context name",
            ["ef.tables"] = "Tables", ["ef.tablesHint"] = "empty = all, otherwise comma separated", ["ef.dataAnnotations"] = "Data annotations instead of Fluent API",
            ["ef.noOnConfiguring"] = "Don't store the connection in OnConfiguring", ["ef.force"] = "Overwrite existing files",
            ["ef.providerHint"] = "The target project needs the provider package {0} and Microsoft.EntityFrameworkCore.Design.", ["ef.runScaffold"] = "Run scaffolding",
            ["ef.removeTitle"] = "Remove migration {0}?", ["ef.removeText"] = "The migration files are deleted and the snapshot is reset.",
            ["ef.removeAppliedText"] = "The migration is already applied – it will be reverted in the database first (--force).",
            ["ef.migrationAdded"] = "Migration {0} added", ["ef.migrationRemoved"] = "Migration removed", ["ef.databaseUpdated"] = "Database updated",
            ["ef.databaseDropped"] = "Database dropped", ["ef.optimized"] = "Compiled model generated", ["ef.noPending"] = "No pending model changes",
            ["ef.pending"] = "There are model changes without a migration", ["ef.scaffolded"] = "Scaffolding finished", ["ef.failed"] = "EF command failed – see the output",
            ["menu.file"] = "File", ["menu.edit"] = "Edit", ["menu.view"] = "View", ["menu.project"] = "Project", ["menu.build"] = "Build",
            ["menu.debug"] = "Debug", ["menu.git"] = "Git", ["menu.tools"] = "Tools", ["menu.help"] = "Help",
            ["cmd.helpShortcuts"] = "All commands & shortcuts", ["cmd.helpDocs"] = "Blazor documentation", ["cmd.helpEfDocs"] = "EF Core documentation",
            ["cmd.helpDebugger"] = "Download netcoredbg", ["cmd.helpAbout"] = "About Blazor Studio",
            ["about.text"] = "Visual IDE for Blazor. Runtime: {0} · {1}",
            ["cmd.profiler"] = "Profiler (CPU & memory)", ["prof.title"] = "Profiler", ["prof.attachedTo"] = "Attached to {0}", ["prof.notAttached"] = "Not attached",
            ["prof.process"] = "Process", ["prof.noProcesses"] = "No .NET processes", ["prof.attach"] = "Attach", ["prof.detach"] = "Detach",
            ["prof.startHint"] = "Run the app (without debugger), then the profiler attaches to its process.", ["prof.allocations"] = "Allocations",
            ["prof.hideIdle"] = "Hide waiting", ["prof.hideIdleHint"] = "Don't count threads that only wait (Sleep, Wait, sockets) as CPU load",
            ["prof.recording"] = "Recording … {0} s left", ["prof.stopEarly"] = "Stop now", ["prof.analyzing"] = "Analyzing …", ["prof.record"] = "Record",
            ["prof.empty"] = "Attach to the running app and start a recording. Use the app meanwhile so there is load.",
            ["prof.tabMine"] = "My code", ["prof.tabHotspots"] = "Hotspots", ["prof.tabTree"] = "Call tree", ["prof.tabAllocTypes"] = "Memory by type",
            ["prof.tabAllocSites"] = "Memory by site", ["prof.tabGc"] = "GC & exceptions", ["prof.summary"] = "{0} s · {1} CPU samples · {2} waiting samples",
            ["prof.mineHint"] = "Each sample is attributed to the innermost method of your code that caused it – even if the time was spent in framework code. Click to open the code.",
            ["prof.onlyMine"] = "My code only", ["prof.treeHint"] = "Calls from outside in; your code is highlighted. Double-click opens the method.",
            ["prof.allocSiteHint"] = "Allocations attributed to the innermost method of your code (sampled every ~100 KB). The most frequent types below.",
            ["prof.gcPause"] = "Total pause time", ["prof.gcMaxPause"] = "Longest pause", ["prof.heapAfter"] = "Heap after last GC", ["prof.allocated"] = "Allocated (estimated)",
            ["prof.noExceptions"] = "No exceptions during the recording.", ["prof.sourceNotFound"] = "No source found for this method (framework code).",
            ["prof.method"] = "Method", ["prof.module"] = "Module", ["prof.share"] = "Share", ["prof.noSamples"] = "No CPU samples – was the app busy?",
            ["prof.allocSite"] = "Caused by", ["prof.type"] = "Type", ["prof.samples"] = "Samples", ["prof.bytes"] = "Bytes (estimated)",
            ["prof.noAllocations"] = "No allocations recorded.", ["prof.recordFailed"] = "Recording failed: {0}",
            ["prof.c.cpu-usage"] = "CPU", ["prof.c.working-set"] = "Working set", ["prof.c.gc-heap-size"] = "GC heap", ["prof.c.alloc-rate"] = "Allocation rate",
            ["prof.c.gen-0-gc-count"] = "Gen 0 GCs", ["prof.c.gen-2-gc-count"] = "Gen 2 GCs", ["prof.c.threadpool-thread-count"] = "Thread pool threads", ["prof.c.exception-count"] = "Exceptions",
            ["tb.tabElements"] = "Elements", ["tb.tabProject"] = "Project", ["tb.tabLibraries"] = "Libraries", ["tb.tabLibrariesHint"] = "Components from NuGet packages like MudBlazor, Radzen …",
            ["tb.search"] = "Search toolbox …", ["tb.pages"] = "Pages", ["tb.components"] = "Components", ["tb.scanning"] = "Scanning packages for components …",
            ["tb.noLibraries"] = "No component libraries found. Install e.g. MudBlazor or Radzen.Blazor via NuGet – they will show up here automatically.",
            ["tb.findLibraries"] = "Find libraries", ["tb.componentCount"] = "{0} components",
            ["tb.setupNeeded"] = "{0} is not set up yet (services, CSS/JS, @using, providers).", ["tb.usingNeeded"] = "@using {0} is missing in _Imports.razor.",
            ["tb.setUp"] = "Set up now", ["tb.setupNothing"] = "Already set up", ["tb.setupDone"] = "{0} set up: {1}",
            ["cmd.toggleToolbox"] = "Toggle toolbox",
            ["libcat.layout"] = "Layout", ["libcat.actions"] = "Actions", ["libcat.inputs"] = "Inputs", ["libcat.text"] = "Text", ["libcat.navigation"] = "Navigation",
            ["libcat.data"] = "Data", ["libcat.feedback"] = "Feedback", ["libcat.media"] = "Media", ["libcat.other"] = "Other",
            ["dev.title"] = "Device view", ["dev.desktop"] = "Desktop", ["dev.tablet"] = "Tablet", ["dev.phone"] = "Phone", ["dev.custom"] = "Breakpoint",
            ["dev.rotate"] = "Portrait / landscape", ["dev.landscape"] = "landscape",
            ["cmd.publish"] = "Publish", ["pub.title"] = "Publish", ["pub.noProfiles"] = "No publish profiles yet. Create one below.",
            ["pub.new"] = "New profile", ["pub.t.Folder"] = "Folder", ["pub.t.Zip"] = "ZIP archive", ["pub.t.Container"] = "Docker container", ["pub.t.WebDeploy"] = "IIS (Web Deploy)", ["pub.t.Other"] = "Other",
            ["pub.name"] = "Profile name", ["pub.target"] = "Target", ["pub.configuration"] = "Configuration", ["pub.framework"] = "Target framework", ["pub.fromProject"] = "from project",
            ["pub.runtime"] = "Target runtime", ["pub.portable"] = "Portable (any)", ["pub.mode"] = "Deployment", ["pub.fdd"] = "Framework-dependent", ["pub.scd"] = "Self-contained",
            ["pub.fddHint"] = "Needs .NET installed on the target – small", ["pub.scdHint"] = "Ships .NET with the app – larger, runs without install",
            ["pub.output"] = "Output folder", ["pub.deleteExisting"] = "Delete existing files first", ["pub.repository"] = "Image name", ["pub.registry"] = "Registry",
            ["pub.registryHint"] = "empty = local Docker, e.g. ghcr.io", ["pub.containerHint"] = "Uses the .NET SDK's built-in container support (dotnet publish -t:PublishContainer) – no Dockerfile needed.",
            ["pub.server"] = "Server (Web Deploy)", ["pub.site"] = "IIS site / app", ["pub.user"] = "User", ["pub.passwordHint"] = "The password is not stored; you'll be asked when publishing.",
            ["pub.singleFile"] = "Single file", ["pub.trim"] = "Trim", ["pub.trimHint"] = "Removes unused code – test first, reflection may break",
            ["pub.openFolder"] = "Open folder", ["pub.save"] = "Save profile", ["pub.publish"] = "Publish", ["pub.pick"] = "Pick a profile on the left or create a new one.",
            ["pub.saved"] = "{0} saved", ["pub.deleteTitle"] = "Delete profile {0}?", ["pub.passwordTitle"] = "Web Deploy password", ["pub.passwordText"] = "User {0} on {1}",
            ["pub.failed"] = "Publish failed – see the output.", ["pub.zipDone"] = "{0} created ({1}) in {2} s", ["pub.done"] = "{0} files ({1}) published in {2} s",
            ["pub.doneGeneric"] = "Published in {0} s",
            ["inspector.search"] = "Search properties, styles, events …", ["inspector.noResults"] = "Nothing found for “{0}”", ["action.clear"] = "Clear",
            ["live.layout"] = "Layout", ["live.layoutHint"] = "Show the page inside its layout (MainLayout, navigation …) or on its own",
            ["lib.layoutRepaired"] = "{0} repaired: library providers were inside the @code block and are now in the right place.",
            ["nav.noSymbol"] = "No symbol at this position", ["nav.external"] = "{0} is defined in a library: {1}", ["nav.noImpl"] = "No implementations of {0} found", ["nav.usages"] = "Usages of {0}", ["nav.implementations"] = "Implementations of {0}", ["nav.definitions"] = "Definitions of {0}", ["nav.rename"] = "Rename", ["nav.renameText"] = "New name for “{0}” (all usages in the solution are updated):", ["nav.renamed"] = "{0} → {1}: {2} places in {3} files", ["nav.renameInvalid"] = "“{0}” is not a valid name", ["nav.renameMetadata"] = "{0} comes from a library and can't be renamed", ["nav.goToDef"] = "Go to definition", ["nav.findUsages"] = "Find usages", ["nav.goToImpl"] = "Go to implementation", ["find.navSummary"] = "{0} results in {1} files",
            ["settings.scheme"] = "Editor color scheme", ["events.actions"] = "Actions", ["nuget.project"] = "Project", ["strip.toolbox"] = "Toolbox", ["strip.layers"] = "Layers", ["strip.solution"] = "Solution", ["strip.commit"] = "Commit", ["bottom.terminal"] = "Terminal", ["bottom.build"] = "Build", ["bottom.nuget"] = "NuGet", ["bottom.git"] = "Git", ["term.new"] = "New terminal", ["term.interrupt"] = "Interrupt (Ctrl+C)", ["term.clear"] = "Clear", ["term.running"] = "running …", ["term.openHere"] = "Open terminal here", ["git.title"] = "Git", ["git.changes"] = "Changes", ["git.staged"] = "Staged", ["git.unversioned"] = "Unversioned", ["git.log"] = "Log", ["git.branches"] = "Branches", ["git.console"] = "Console", ["git.noRepo"] = "This folder is not a Git repository.", ["git.init"] = "Create Git repository", ["git.initDone"] = "Git repository created", ["git.stage"] = "Stage", ["git.stageAll"] = "Stage all", ["git.unstage"] = "Unstage", ["git.unstageAll"] = "Unstage all", ["git.discard"] = "Discard", ["git.discardTitle"] = "Discard changes?", ["git.discardText"] = "All local changes to \"{0}\" will be lost.", ["git.commit"] = "Commit", ["git.commitPush"] = "Commit & Push", ["git.amend"] = "Amend", ["git.message"] = "Commit message", ["git.committed"] = "Commit created", ["git.push"] = "Push", ["git.pushed"] = "Pushed", ["git.pull"] = "Pull", ["git.pulled"] = "Updated", ["git.fetch"] = "Fetch", ["git.checkout"] = "Checkout", ["git.newBranch"] = "New branch", ["git.newBranchFrom"] = "New branch from \"{0}\"", ["git.deleteBranch"] = "Delete branch", ["git.deleteBranchText"] = "Delete branch \"{0}\"?", ["git.forceDelete"] = "Force (-D)", ["git.merge"] = "Merge into current branch", ["git.rebase"] = "Rebase current branch onto this", ["git.stash"] = "Stash", ["git.stashText"] = "Put all changes (incl. new files) aside. Optional name:", ["git.unstash"] = "Apply stash", ["git.stashes"] = "Stashes", ["git.revert"] = "Revert commit", ["git.reset"] = "Reset", ["git.resetSoft"] = "Reset branch here (keep changes)", ["git.resetHard"] = "Hard reset", ["git.resetHardText"] = "Hard reset branch to {0}? Local changes will be lost.", ["git.cherryPick"] = "Cherry-pick", ["git.tag"] = "Create tag", ["git.remote"] = "Remote (origin)", ["git.remoteText"] = "Remote repository URL:", ["git.copyHash"] = "Copy hash", ["git.noChanges"] = "No changes – everything committed.", ["git.ahead"] = "{0} to push", ["git.behind"] = "{0} to pull", ["git.noUpstream"] = "no upstream", ["git.local"] = "Local", ["git.remoteBranches"] = "Remote", ["git.selectFile"] = "Select a file to see its diff.", ["git.openFile"] = "Open file", ["git.refresh"] = "Refresh", ["git.forcePush"] = "Force push (with lease)", ["git.history"] = "History", ["action.delete"] = "Delete", ["cmd.copyPath"] = "Copy path", ["upd.check"] = "Check for updates …", ["upd.title"] = "Blazor Studio {0} is available", ["upd.current"] = "Installed: {0}", ["upd.checking"] = "Checking for updates …", ["upd.upToDate"] = "Blazor Studio is up to date", ["upd.noNotes"] = "No release notes.", ["upd.downloading"] = "Downloading … {0} %", ["upd.starting"] = "Starting installation – Blazor Studio will restart …", ["upd.manualHint"] = "This instance was not installed with the setup – download and run the setup.", ["upd.releasePage"] = "Release page", ["upd.skip"] = "Skip this version", ["upd.later"] = "Later", ["upd.install"] = "Update now", ["upd.download"] = "Download setup", ["upd.checkFailed"] = "Check failed:", ["upd.unsavedTitle"] = "Unsaved changes", ["upd.unsavedText"] = "Blazor Studio restarts for the update. Save your changes first?", ["upd.saveAndUpdate"] = "Save & update", ["upd.pill"] = "Update {0}", ["upd.pillTitle"] = "New version available – click to update", ["build.Build"] = "Build", ["build.Rebuild"] = "Rebuild", ["build.Clean"] = "Clean", ["build.CleanBuild"] = "Clean & Build", ["build.Restore"] = "Restore NuGet packages", ["build.solution"] = "Build solution", ["build.rebuildSolution"] = "Rebuild solution", ["build.cleanSolution"] = "Clean solution", ["build.cleanBuildSolution"] = "Solution: Clean & Build", ["build.restoreSolution"] = "Solution: restore NuGet", ["build.rebuildProject"] = "Rebuild project", ["build.cleanProject"] = "Clean project", ["build.ok"] = "{0} succeeded: {1}", ["build.failed"] = "{0} failed: {1}", ["sol.add"] = "Add", ["sol.newProject"] = "New project", ["sol.newProjectText"] = "Creates a project with dotnet new and adds it to the solution.", ["sol.addExisting"] = "Existing project", ["sol.editSolution"] = "Edit solution file", ["sol.reloadSolution"] = "Reload solution", ["sol.reloadProject"] = "Reload project", ["sol.unloadProject"] = "Unload project", ["sol.removeProject"] = "Remove from solution", ["sol.removeProjectText"] = "Remove \"{0}\" from the solution? The files stay on disk.", ["sol.unloadedTag"] = "(unloaded)", ["sol.unloaded"] = "\"{0}\" unloaded", ["sol.reloaded"] = "\"{0}\" reloaded", ["sol.cantUnloadStartup"] = "The only project cannot be unloaded.", ["sol.projectReference"] = "Project reference", ["sol.removeReference"] = "Remove reference", ["sol.removePackage"] = "Remove package", ["sol.removePackageText"] = "Remove package \"{0}\" from \"{1}\"?", ["sol.newFolder"] = "New folder", ["sol.folderName"] = "Folder name", ["sol.rename"] = "Rename", ["sol.renameHint"] = "Component renamed – please update usages like <OldName />.", ["sol.delete"] = "Delete", ["sol.toRecycleBin"] = "Move to recycle bin", ["sol.deleteFileText"] = "Move \"{0}\" to the recycle bin?", ["sol.deleteFolderText"] = "Move folder \"{0}\" with its contents to the recycle bin?", ["sol.deleteCompanions"] = "Related files: {0}", ["sol.deleteFailed"] = "\"{0}\" could not be deleted.", ["sol.copyRelativePath"] = "Copy relative path", ["sol.pathCopied"] = "Path copied", ["sol.open"] = "Open", ["sol.invalidName"] = "Invalid name.", ["sol.folderExists"] = "The folder \"{0}\" already exists.", ["sol.projectCreated"] = "Project \"{0}\" created", ["sol.tpl.blazor"] = "Blazor Web App", ["sol.tpl.blazorwasm"] = "Blazor WebAssembly", ["sol.tpl.razorclasslib"] = "Razor class library", ["sol.tpl.webapi"] = "Web API", ["sol.tpl.classlib"] = "Class library", ["sol.tpl.console"] = "Console app", ["sol.tpl.worker"] = "Worker service", ["sol.tpl.xunit"] = "xUnit tests", ["sol.tpl.nunit"] = "NUnit tests", ["sol.tpl.mstest"] = "MSTest tests", ["newFile.title"] = "New file", ["newFile.search"] = "Search templates …", ["newFile.preview"] = "Preview", ["newFile.namespace"] = "Namespace: {0}", ["newFile.exists"] = "\"{0}\" already exists.", ["newFile.g.csharp"] = "C#", ["newFile.g.blazor"] = "Blazor", ["newFile.g.web"] = "Web", ["newFile.g.data"] = "Data & configuration", ["newFile.g.other"] = "Other", ["newFile.t.class"] = "Class", ["newFile.t.interface"] = "Interface", ["newFile.t.record"] = "Record", ["newFile.t.enum"] = "Enum", ["newFile.t.struct"] = "Struct", ["newFile.t.service"] = "Service (interface + class)", ["newFile.t.controller"] = "API controller", ["newFile.t.dbcontext"] = "EF Core DbContext", ["newFile.t.test"] = "xUnit test class", ["newFile.t.component"] = "Razor component", ["newFile.t.page"] = "Razor page (@page)", ["newFile.t.layout"] = "Layout", ["newFile.t.codebehind"] = "Code-behind (.razor.cs)", ["newFile.t.scopedcss"] = "Scoped CSS (.razor.css)", ["newFile.t.imports"] = "_Imports.razor", ["newFile.t.css"] = "Stylesheet", ["newFile.t.js"] = "JavaScript", ["newFile.t.jsmodule"] = "Component JS module", ["newFile.t.html"] = "HTML page", ["newFile.t.json"] = "JSON", ["newFile.t.appsettings"] = "appsettings.json", ["newFile.t.xml"] = "XML", ["newFile.t.markdown"] = "Markdown", ["newFile.t.text"] = "Text file", ["newFile.t.editorconfig"] = ".editorconfig", ["newFile.t.gitignore"] = ".gitignore", ["find.title"] = "Find in files", ["find.replaceTitle"] = "Replace in files", ["find.placeholder"] = "Find in files …", ["find.case"] = "Match case", ["find.word"] = "Whole words only", ["find.regex"] = "Regular expression", ["find.mask"] = "File mask, e.g. *.cs;*.razor;!*.min.js", ["find.maskHint"] = "Separate patterns with ;, ! excludes", ["find.scopeSolution"] = "Whole solution", ["find.scopeProject"] = "Startup project", ["find.replaceToggle"] = "Toggle replace", ["find.replacePlaceholder"] = "Replace with …", ["find.replaceAll"] = "Replace all", ["find.regexHint"] = "$1, ${name} for groups", ["find.summary"] = "{0} matches in {1} files ({2} searched)", ["find.truncated"] = "first 5000 only", ["find.hint"] = "Type to search – bin/obj/.git are skipped.", ["find.replaceConfirm"] = "Replace {0} matches in {1} files?\n\"{2}\" → \"{3}\"\nOpen files are changed in the editor (Ctrl+Z works), others are saved directly.", ["find.replaced"] = "{0} replacements in {1} files", ["lib.scanFailed"] = "Component libraries could not be read: {0}", ["live.menuTitle"] = "Real rendering in the designer", ["live.approx"] = "Quick view", ["live.state.Off"] = "Live off", ["live.state.Starting"] = "Starting real view …", ["live.state.Ready"] = "Live", ["live.state.Rebuilding"] = "Updating …", ["live.state.Failed"] = "Build error", ["live.interacting"] = "interactive", ["live.toggle"] = "Real rendering (app runs inside the designer)", ["live.interact"] = "Interact with the app (clicks go to the app)", ["live.restart"] = "Restart", ["live.log"] = "Log", ["live.failedTitle"] = "Real rendering: the project does not build", ["live.failedText"] = "See the log – meanwhile you see the quick view.", ["tip.previewStarting"] = "Starting real preview …", ["tip.previewUnavailable"] = "No live preview for this element", ["tip.previewOff"] = "Real rendering is off", ["tip.moreParams"] = "+{0} more parameters", ["tip.events"] = "{0} events", ["tip.hint"] = "Click to insert · drag to place", ["tip.project"] = "Project", ["tip.template"] = "Template", ["action.add"] = "Add", ["action.browse"] = "Browse", ["action.cancel"] = "Cancel", ["action.create"] = "Create",
            ["action.done"] = "Done", ["action.dontSave"] = "Don't save", ["action.ok"] = "OK", ["action.remove"] = "Remove", ["action.save"] = "Save",

            ["binding.loop"] = "loop variable",
            ["block.cards"] = "Card grid (3 columns)", ["block.contact"] = "Contact form (EditForm)", ["block.footer"] = "Footer",
            ["block.hero"] = "Hero with buttons", ["block.list"] = "Data list (@foreach)", ["block.navbar"] = "Navigation bar",
            ["blocks.hint"] = "Drag blocks onto the canvas or into the layers – or click to insert after the selection.",

            ["bottom.clear"] = "Clear output", ["bottom.debug"] = "Debug", ["bottom.debuggerMissing"] = "netcoredbg was not found – set its path in Settings.",
            ["bottom.debugIdle"] = "Press F5 to debug. Set breakpoints by clicking left of the line number or with F9.",
            ["bottom.noProblems"] = "No problems found.", ["bottom.output"] = "Output", ["bottom.outputEmpty"] = "Build, run and debugger output appears here.",
            ["bottom.problems"] = "Problems",

            ["canvas.dropHere"] = "Drop here", ["canvas.emptyTitle"] = "Empty component",
            ["canvas.emptyText"] = "Drag elements from the bar above or pick a block on the right.", ["canvas.fullWidth"] = "Full width",

            ["cat.layout"] = "Layout", ["cat.text"] = "Text", ["cat.form"] = "Form", ["cat.media"] = "Media", ["cat.blazor"] = "Blazor", ["cat.project"] = "Project",

            ["cmd.build"] = "Build", ["cmd.closeProject"] = "Close project", ["cmd.closeTab"] = "Close tab", ["cmd.commands"] = "Commands",
            ["cmd.continue"] = "Continue", ["cmd.copy"] = "Copy", ["cmd.cut"] = "Cut", ["cmd.debug"] = "Debug", ["cmd.delete"] = "Delete",
            ["cmd.duplicate"] = "Duplicate", ["cmd.goToFile"] = "Go to file", ["cmd.moveDown"] = "Move down", ["cmd.moveUp"] = "Move up",
            ["cmd.newComponent"] = "New component", ["cmd.newProject"] = "New project", ["cmd.openBrowser"] = "Open in browser",
            ["cmd.openProject"] = "Open project", ["cmd.paste"] = "Paste", ["cmd.redo"] = "Redo", ["cmd.rescan"] = "Rescan project",
            ["cmd.restart"] = "Restart", ["cmd.reveal"] = "Reveal in Explorer", ["cmd.run"] = "Run with Hot Reload", ["cmd.runNoHot"] = "Run without Hot Reload",
            ["cmd.save"] = "Save", ["cmd.saveAll"] = "Save all", ["cmd.selectParent"] = "Select parent", ["cmd.settings"] = "Settings",
            ["cmd.stepInto"] = "Step into", ["cmd.stepOut"] = "Step out", ["cmd.stepOver"] = "Step over", ["cmd.stop"] = "Stop",
            ["cmd.toggleBottom"] = "Toggle bottom panel", ["cmd.toggleLanguage"] = "Switch language (Deutsch/English)", ["cmd.toggleTheme"] = "Toggle light/dark",
            ["cmd.undo"] = "Undo", ["cmd.unwrap"] = "Unwrap", ["cmd.viewCode"] = "View: Code", ["cmd.viewDesign"] = "View: Design",
            ["cmd.viewSplit"] = "View: Split", ["cmd.wrap"] = "Wrap in container", ["cmd.wrapFlex"] = "Wrap in flex row", ["cmd.zoomIn"] = "Zoom in",
            ["cmd.zoomOut"] = "Zoom out", ["cmd.zoomReset"] = "Reset zoom",

            ["debug.addWatch"] = "Add expression …", ["debug.callStack"] = "Call stack", ["debug.external"] = "external",
            ["debug.fallbackRun"] = "No debugger found – starting the app without debugging. You can set up netcoredbg in Settings.",
            ["debug.locals"] = "Locals", ["debug.missingTitle"] = "Debugger missing",
            ["debug.missingText"] = "Blazor Studio uses \"netcoredbg\" for debugging (free, MIT license, by Samsung). Download and extract it, then set the path to netcoredbg.exe in Settings.",
            ["debug.noDll"] = "The output DLL was not found. Did the build succeed?", ["debug.runningHint"] = "Running … set a breakpoint to see variables.",
            ["debug.startFailed"] = "Debugger failed to start: {0}", ["debug.wasmHint"] = "WebAssembly projects are debugged in the browser – breakpoints in server code still work.",
            ["debug.watch"] = "Watch",

            ["design.allStyles"] = "All styles", ["design.border"] = "Border & effects", ["design.classes"] = "Classes", ["design.columns"] = "Columns",
            ["design.createClass"] = "Create class", ["design.extract"] = "Move inline styles to class", ["design.extractTo"] = "Save as class in {0}",
            ["design.fill"] = "Fill", ["design.inFile"] = "in", ["design.inFlex"] = "Inside flex container", ["design.inlineWins"] = "Inline styles override rules for states and breakpoints. Move them into a class first.",
            ["design.known"] = "known", ["design.layout"] = "Layout", ["design.needsClass"] = "States and breakpoints need the element to have a CSS class.",
            ["design.none"] = "None", ["design.normal"] = "Normal", ["design.noWrap"] = "No wrap", ["design.opacity"] = "Opacity",
            ["design.property"] = "Property", ["design.radius"] = "Corner radius", ["design.scopeBase"] = "Changes apply to all widths (inline style)",
            ["design.scopeFrom"] = "Changes apply from {0}", ["design.shadow"] = "Shadow", ["design.spacing"] = "Spacing & size", ["design.state"] = "State",
            ["design.textColor"] = "Text color", ["design.tokens"] = "Design tokens", ["design.typography"] = "Typography", ["design.value"] = "Value",
            ["design.weight"] = "Weight", ["design.wrap"] = "Wrap",

            ["designer.goToError"] = "Go to error", ["designer.readonly"] = "Designer paused", ["designer.readonlyToast"] = "The code has errors – fix them to continue editing in the designer.",
            ["designer.synced"] = "synced with designer", ["designer.syncPaused"] = "code has errors",

            ["diag.unclosedAttribute"] = "Attribute \"{0}\" is not closed.", ["diag.unclosedBlock"] = "Block \"{0}\" is not closed.",
            ["diag.unclosedComment"] = "Comment \"{0}\" is not closed.", ["diag.unclosedTag"] = "Tag <{0}> is not closed.",
            ["diag.unexpectedClose"] = "Unexpected </{0}>.",

            ["dlg.pickProject"] = "Choose project folder", ["dlg.unsavedOnExit"] = "There are unsaved changes. Save before exiting?",
            ["dlg.unsavedText"] = "Your changes will be lost if you don't save them.", ["dlg.unsavedTitle"] = "Save changes to {0}?",

            ["doc.addProperty"] = "Add property / field", ["doc.addPropertyHint"] = "C# declaration, e.g. \"[Parameter] public bool IsOpen { get; set; }\" or \"private int count;\".",
            ["doc.code"] = "Code", ["doc.createCodeBehind"] = "Create code-behind {0}", ["doc.directives"] = "Directives", ["doc.empty"] = "No file open",
            ["doc.memberStats"] = "{0} fields/properties · {1} methods · {2} parameters", ["doc.noRoute"] = "no page (component only)",
            ["doc.route"] = "Route", ["doc.routeHint"] = "A route turns the component into a page, e.g. /products/{id:int}.",
            ["doc.tip"] = "Tip: select an element on the canvas, in the layers or simply by clicking into the code.",

            ["editor.loading"] = "Loading editor …",

            ["events.callbacksOf"] = "Callbacks of {0}", ["events.createRef"] = "Create field", ["events.directives"] = "Directives",
            ["events.goTo"] = "Go to method", ["events.lambda"] = "Lambda", ["events.newHandler"] = "Handler", ["events.newMethod"] = "New method",
            ["events.noCallbacks"] = "This component has no EventCallback parameters.", ["events.pickExisting"] = "Pick existing method", ["events.remove"] = "Remove",

            ["evgroup.mouse"] = "Mouse", ["evgroup.focus"] = "Focus", ["evgroup.keyboard"] = "Keyboard", ["evgroup.input"] = "Input", ["evgroup.form"] = "Form",
            ["evgroup.media"] = "Media", ["evgroup.clipboard"] = "Clipboard", ["evgroup.drag"] = "Drag", ["evgroup.pointer"] = "Pointer & touch", ["evgroup.other"] = "Other",

            ["feat.binding"] = "Binding", ["feat.bindingText"] = "Bind parameters and events to @code and .razor.cs.",
            ["feat.debug"] = "Debugger", ["feat.debugText"] = "Breakpoints, variables and stepping in C#.",
            ["feat.dnd"] = "Drag & drop", ["feat.dndText"] = "Just drag elements and components.",
            ["feat.responsive"] = "Responsive", ["feat.responsiveText"] = "Breakpoints and states like :hover.",

            ["files.filter"] = "Filter files …",
            ["insert.hint"] = "Drag or click to insert", ["insert.noProjectComponents"] = "No custom components yet",

            ["inspector.addClass"] = "Add class …", ["inspector.bind"] = "bind", ["inspector.bindHint"] = "Bind to a value from @code / .razor.cs",
            ["inspector.bound"] = "bound", ["inspector.component"] = "Component", ["inspector.expressionPlaceholder"] = "C# expression",
            ["inspector.fragmentHint"] = "Drag content into the component in the designer.", ["inspector.in"] = "in {0}",
            ["inspector.makeTwoWay"] = "Two-way binding (@bind-)", ["inspector.memberPlaceholder"] = "Pick or type a member",
            ["inspector.noMembers"] = "No matching members – add a field or property in @code or the .razor.cs.",
            ["inspector.noRazor"] = "Open a .razor file to edit elements.", ["inspector.openComponent"] = "Open component",
            ["inspector.pageAt"] = "Page at {0}", ["inspector.required"] = "required", ["inspector.reset"] = "Reset",
            ["inspector.tabDesign"] = "Design", ["inspector.tabEvents"] = "Events", ["inspector.tabProps"] = "Properties",

            ["kind.blazor"] = "Blazor component", ["kind.code"] = "C# block", ["kind.component"] = "Component", ["kind.control"] = "Control flow",
            ["kind.element"] = "HTML element", ["kind.expression"] = "Razor expression", ["kind.text"] = "Text", ["kind.unknownComponent"] = "Unknown component",

            ["layers.directives"] = "{0} directives", ["layers.empty"] = "No elements yet – drag something onto the canvas.",
            ["layers.members"] = "{0} members", ["layers.noRazor"] = "Layers are available for .razor files.",

            ["newComp.codeBehind"] = "Create code-behind (.razor.cs)", ["newComp.component"] = "Component", ["newComp.componentHint"] = "Reusable building block",
            ["newComp.folder"] = "Folder", ["newComp.name"] = "Name", ["newComp.page"] = "Page", ["newComp.pageHint"] = "With its own route (@page)",
            ["newComp.route"] = "Route", ["newComp.scopedCss"] = "Create scoped CSS (.razor.css)", ["newComp.text"] = "Component names start with an uppercase letter.",
            ["newComp.title"] = "New component",

            ["newProj.creating"] = "Creating project (dotnet new) …", ["newProj.interactivity"] = "Interactivity", ["newProj.location"] = "Location",
            ["newProj.name"] = "Project name", ["newProj.sample"] = "Include sample pages", ["newProj.text"] = "Creates a new Blazor project with dotnet new and opens it.",
            ["newProj.title"] = "New Blazor project", ["newProj.wasm"] = "Blazor WebAssembly", ["newProj.wasmHint"] = "Runs entirely in the browser",
            ["newProj.web"] = "Blazor Web App", ["newProj.webHint"] = "Server + optional WebAssembly (recommended)",

            ["node.addElse"] = "Add else branch", ["node.condition"] = "Condition", ["node.controlHint"] = "C# header of the block.",
            ["node.expressionHint"] = "Evaluated at runtime and rendered as text.", ["node.foreachHint"] = "The loop variable is available inside for bindings.",
            ["node.ifHint"] = "The content renders only when the condition is true.", ["node.loop"] = "Loop",

            ["palette.commands"] = "Search commands …", ["palette.commandsHint"] = "commands", ["palette.files"] = "Search files … ( > commands, + insert )",
            ["palette.groupCommands"] = "Commands", ["palette.groupFiles"] = "Files", ["palette.groupInsert"] = "Insert", ["palette.insertHint"] = "insert element",
            ["palette.navigate"] = "select", ["palette.nothing"] = "Nothing found.", ["palette.run"] = "run",

            ["props.callbacksInEvents"] = "Callbacks of this component are in the \"Events\" tab.", ["props.content"] = "Content",
            ["props.contentHint"] = "text", ["props.doubleClickHint"] = "Tip: double-click on the canvas to edit text inline.",
            ["props.name"] = "Name", ["props.noParameters"] = "{0} has no [Parameter]s.", ["props.otherAttributes"] = "Other attributes",
            ["props.twoWay"] = "two-way binding", ["props.unknownComponent"] = "\"{0}\" is unknown in this project – you can still set attributes.",
            ["props.value"] = "Value",

            ["run.buildFailed"] = "Build failed", ["run.building"] = "Building …", ["run.buildOk"] = "Build succeeded",
            ["run.debugging"] = "Debugging", ["run.dotnetMissing"] = "The .NET SDK (dotnet) was not found.", ["run.exited"] = "Process exited (code {0})",
            ["run.paused"] = "Paused", ["run.start"] = "Run", ["run.starting"] = "Starting …",

            ["settings.debugger"] = "Debugger (netcoredbg)", ["settings.debuggerHint"] = "Not found. Download:", ["settings.fontSize"] = "Editor font size",
            ["settings.hotReload"] = "Run with Hot Reload", ["settings.language"] = "Language", ["settings.minimap"] = "Minimap in code editor",

            ["shadow.small"] = "Small", ["shadow.soft"] = "Soft", ["shadow.strong"] = "Strong", ["shadow.ring"] = "Focus ring",
            ["side.blocks"] = "Blocks", ["side.files"] = "Files", ["side.layers"] = "Layers",

            ["status.cursor"] = "Ln {0}, Col {1}", ["status.parseErrors"] = "Razor errors", ["status.paused"] = "Paused · {0}",
            ["status.ready"] = "Ready", ["status.running"] = "Running", ["status.unsaved"] = "unsaved",

            ["tb.button"] = "Button", ["tb.card"] = "Card", ["tb.checkbox"] = "Checkbox", ["tb.container"] = "Container", ["tb.divider"] = "Divider",
            ["tb.foreach"] = "@foreach", ["tb.form"] = "Form", ["tb.grid"] = "Grid", ["tb.heading1"] = "Heading 1", ["tb.heading2"] = "Heading 2",
            ["tb.heading3"] = "Heading 3", ["tb.icon"] = "Icon", ["tb.if"] = "@if", ["tb.image"] = "Image", ["tb.input"] = "Input", ["tb.label"] = "Label",
            ["tb.link"] = "Link", ["tb.list"] = "List", ["tb.paragraph"] = "Paragraph", ["tb.quote"] = "Quote", ["tb.row"] = "Flex row", ["tb.section"] = "Section",
            ["tb.select"] = "Select", ["tb.stack"] = "Stack", ["tb.textarea"] = "Text area", ["tb.video"] = "Video",

            ["theme.accent"] = "Accent color", ["theme.mode"] = "Mode", ["theme.dark"] = "Dark", ["theme.light"] = "Light", ["theme.system"] = "System",

            ["toast.badComponentName"] = "Component names start with an uppercase letter and contain only letters and digits.",
            ["toast.badProjectName"] = "Project names may only contain letters, digits, dot, dash and underscore.",
            ["toast.cannotOpen"] = "{0} cannot be opened.", ["toast.copied"] = "Copied", ["toast.created"] = "{0} created",
            ["toast.exists"] = "{0} already exists.", ["toast.fieldCreated"] = "Field {0} created in {1}", ["toast.folderMissing"] = "Folder not found: {0}",
            ["toast.memberAdded"] = "Added to {0}", ["toast.methodCreated"] = "Method {0}() created in {1}",
            ["toast.noCsproj"] = "No .csproj found – build, run and debugger are disabled.",
            ["toast.projectFailed"] = "The project could not be created – see the output.", ["toast.saved"] = "{0} saved",
            ["toast.saveFailed"] = "{0} could not be saved: {1}",

            ["top.appearance"] = "Appearance", ["top.search"] = "Search files, components or commands …",
            ["view.code"] = "Code", ["view.design"] = "Design", ["view.live"] = "Live", ["view.liveEmpty"] = "The app is not running yet.",
            ["view.liveHint"] = "Watch the running app with Hot Reload right here", ["view.split"] = "Split",

            ["welcome.lead"] = "The visual IDE for Blazor: build components with drag & drop, set styles and bindings in the inspector, write code with completion and debug C#.",
            ["welcome.newHint"] = "Blazor Web App or WebAssembly via dotnet new", ["welcome.noRecent"] = "No projects opened yet.",
            ["welcome.openHint"] = "Choose a folder containing a .csproj", ["welcome.recent"] = "Recent",
        },
    };
}
