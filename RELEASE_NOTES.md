## Neu: Zertifikate im Griff (win-acme, IIS, nginx, Dienste)
- Im IIS-Fenster neuer Reiter **Zertifikate**: alle Zertifikate des Servers in einer Liste – aus dem Windows-Zertifikatsspeicher (mit den IIS-Bindungen, die sie nutzen), aus der **nginx**-Konfiguration (`ssl_certificate`, inkl. `include`), aus **Diensten mit PFX-Datei** (`Kestrel:Certificates` in appsettings.json) und aus den **win-acme-Erneuerungen**. Mit „alle Server“ alle Server in einer Übersicht.
- Pro Zertifikat: Namen, Aussteller, gültig bis (Tage übrig), Laufzeit, wo es verwendet wird, zugehörige win-acme-Erneuerung mit letztem Lauf, Fehler und Fälligkeit.
- **Ausgeliefert = aktuell?** Der Agent prüft, welches Zertifikat IIS und nginx tatsächlich ausliefern. Ein nicht neu geladenes nginx oder ein Dienst mit altem Zertifikat wird als „alt ausgeliefert“ markiert.
- **Firmenrichtlinie** (Standard: höchstens 30 Tage gültig, Erneuerung 7 Tage vor Ablauf). Studio markiert, was abweicht. **„Richtlinie auf Server anwenden“** stellt win-acme passend ein: `RenewalDays` und `RenewalMinimumValidDays`, optional `DefaultValidDays` (Laufzeit bei der eigenen ACME-CA anfordern). Die settings.json wird vorher gesichert, auf Wunsch auf allen Servern.
- **Jetzt erneuern** (einzeln oder alle fälligen) mit Live-Protokoll von win-acme; **nginx neu laden**, **Dienst neu starten**, **Erneuerung entfernen**.
- **Neues Zertifikat** über win-acme direkt aus Studio: Hostnamen, Prüfung (HTTP selbst, Webroot oder eigene Argumente), Ziele kombinierbar: Windows-Speicher plus https-Bindungen einer IIS-Site, **PEM-Dateien für nginx** (danach automatisch nginx neu laden), **PFX mit Passwort** für einen Dienst (danach automatisch Dienst neu starten). Der win-acme-Aufruf wird vorab angezeigt; das PFX-Passwort geht nur an win-acme auf dem Server.
- Die Folgeaktionen richtet der Agent als Skript-Installation in win-acme ein (`Cert-Hook.ps1`). Sie laufen deshalb auch bei den automatischen Erneuerungen der geplanten Aufgabe.
- **Agent 1.14.0 nötig.** Neue optionale Agent-Einstellungen: `WinAcmePath`, `NginxPath`, `CertificateFiles`.

## Neu: Speicheranalyse von Apps auf dem Server
- Im IIS-Fenster gibt es bei laufenden **App-Pools** und **Diensten** den Knopf **„Speicher analysieren“** (Puls-Symbol). Der Agent erstellt eine Kopie des Prozesses – die App wird dabei nur Millisekunden angehalten – und wertet den .NET-Heap direkt auf dem Server aus (Microsoft ClrMD). Es wird kein Dump übertragen.
- **Woher kommt der Speicher?** Verwalteter Heap vs. Working Set, Aufteilung auf Gen 0/1/2, Large Object Heap und Pinned Heap, Fragmentierung, und eine Tabelle aller Typen nach Größe und Anzahl (filterbar).
- **Was steht drin?** Typ anklicken → Instanzen (größte zuerst, mit Vorschau) → Objekt mit allen Feldern und Werten (Strings, Zahlen, DateTime, Guid, Enums, Structs), Listen/Arrays elementweise, Referenzen zum Weiterklicken mit Brotkrumen-Navigation.
- **„Wer hält das fest?“** zeigt die Referenzpfade von der GC-Wurzel (statisches Feld, Handle, lokale Variable) bis zum Objekt – die Ursache von Speicherlecks. „Erreichbare Größe“ zeigt, wie viel an einem Objekt hängt.
- **Snapshots vergleichen:** zwei Snapshots desselben Prozesses (z. B. vor und nach einem Lasttest) – sortiert nach Zuwachs pro Typ.
- ASP.NET Core „out-of-process“ hinter IIS: Der Agent analysiert automatisch den App-Prozess unter w3wp.exe.
- **Agent 1.13.0 nötig.**

## Neu: Claude Code im Web – ohne Installation
- Im Claude-Panel: **„Ohne Installation: Claude Code im Web“** (auch im Zahnrad-Menü; ohne installiertes Claude Code direkt sichtbar). Claude Code im Web läuft in der Cloud mit deinem Claude-Abo – keine Installation, keine API-Kosten – und arbeitet auf deinem GitHub-Repository.
- Auftrag eintippen → **„Auftrag kopieren & öffnen“**: Der Auftrag kommt mit Repository, Branch und aktiver Datei in die Zwischenablage, claude.ai/code öffnet sich im Browser – dort Repository wählen und einfügen.
- Hinweis, wenn lokale Änderungen noch nicht auf GitHub sind (Claude im Web sieht nur Gepushtes).
- **„Ergebnis holen“**: holt die Branches von GitHub und listet die von Claude (`claude/…`) zum Auschecken.

## Neu: Claude im IDE (mit deinem Claude-Abo)
- Rechte Seitenleiste → **Claude** (oder Tools → Claude): Chat mit Claude direkt zum geöffneten Projekt. Claude liest Code, ändert Dateien und führt erlaubte Befehle aus; geänderte Dateien lädt der Editor automatisch neu. Antworten erscheinen live, Werkzeugaufrufe (Datei gelesen/geändert, Befehl) als aufklappbare Zeilen – Dateinamen öffnen die Datei.
- Blazor Studio nutzt dafür **Claude Code** von Anthropic, das auf deinem Rechner installiert ist. Die **Anmeldung machst du selbst in Claude Code** (Knopf „Anmelden“ öffnet ein Konsolenfenster mit `claude auth login`) – mit deinem **Claude-Abo (Pro/Max)** oder einem API-Schlüssel. Blazor Studio sieht und speichert keine Zugangsdaten. Ist Claude Code noch nicht installiert, startet „Claude Code installieren“ das offizielle Skript von Anthropic.
- Berechtigungen: „Nur planen“, „Dateien ändern“ (Standard) oder „Alles erlauben“. Befehle, die nicht erlaubt sind, lehnt Claude ab – danach „Erlauben und weitermachen“. Erlaubte Befehle (z. B. `Bash(dotnet build *)`) lassen sich verwalten. Modell wählbar (Standard, Opus, Sonnet, Haiku).
- Der Chat merkt sich den Verlauf (Sitzung wird fortgesetzt), „Neuer Chat“ beginnt neu. Für das volle interaktive Claude: Menü → „Claude im Konsolenfenster öffnen“ (im Projektordner).
- Ungespeicherte Dateien werden vor jeder Anfrage gespeichert.

## Neu: Deployment als Windows-Dienst
- Im Bereitstellen-Reiter der IIS-Server gibt es jetzt **Ziel: IIS-Site | Windows-Dienst**. Für Worker/Hintergrunddienste: Dienstname, Programm (leer = die .exe der App wird automatisch gefunden, framework-abhängig ohne .exe läuft sie über dotnet), Argumente, Konto (LocalSystem, LocalService, NetworkService oder Domänenkonto), Starttyp.
- Gibt es den Dienst noch nicht, legt der Agent ihn an (mit automatischem Neustart bei Absturz). Sonst: Dienst stoppen → **persistente Dateien** mitnehmen → Programmpfad auf die neue Version → starten → prüfen (läuft er nach 8 s noch? optional Health-URL). Schlägt das fehl, läuft automatisch wieder die vorherige Version.
- Neuer Reiter **Dienste**: Zustand, Version, Konto, CPU/RAM; Starten, Stoppen, Neu starten, Entfernen; aufgeklappt Versionen (Aktivieren/Zurückschalten), **appsettings*.json im Editor bearbeiten** (bleibt für künftige Deployments) und persistente Dateien.
- Pipeline: `Deploy-BlazorStudio.ps1 -Service -Site ShopWorker [-Exe Worker.exe] [-Account NetworkService] [-Persist 'data.db']`; im Pipeline-Builder Feld „Ziel“ = service.
- Die App muss als Dienst laufen können: `builder.Services.AddWindowsService()` (Paket Microsoft.Extensions.Hosting.WindowsServices). Fehlt das, meldet der Agent den Fehler 1053 mit diesem Hinweis.
- **Agent 1.11.0 nötig**; Studio und das Skript prüfen das, damit ältere Agents nicht versehentlich in eine IIS-Site gleichen Namens deployen.

## Behoben: Run ließ sich nach Stopp nicht erneut starten
- Nach dem Stoppen blieb Studio manchmal im Zustand „läuft“, bis die IDE neu gestartet wurde: Ein Kindprozess von `dotnet watch` hielt die Ausgabe offen, das Prozessende kam nie an. Jetzt wird nur noch kurz gewartet, und Stopp setzt den Zustand spätestens nach wenigen Sekunden zurück.

## Neu: Fehler im Projektmappen-Explorer rot markiert (wie in Rider)
- Dateien mit Fehlern werden im Projektmappen-Explorer **rot und gewellt unterstrichen** – ebenso alle Ordner darüber, das Projekt und die Projektmappe. Der Tooltip zeigt die Anzahl.
- Grundlage ist eine **lösungsweite Analyse** im Hintergrund (alle .cs-Dateien und die @code-Blöcke der .razor-Dateien, mit dem aktuellen Editor-Text): Benennst du z. B. in einer Datei eine Eigenschaft um, wird jede Datei rot, die sie noch verwendet – auch wenn sie nicht geöffnet ist.
- Die geöffnete Datei wird beim Tippen sofort aktualisiert, die übrigen nach dem Speichern bzw. wenn sich Dateien auf der Platte ändern. Fehler aus dem letzten Build kommen dazu, bis die Datei wieder geändert wird.

## Neu: Persistente Dateien wandern von Version zu Version mit (IIS)
- In **IIS-Server → Sites** eine Anwendung aufklappen → **Persistente Dateien**: z. B. `app.db`, `App_Data` oder `wwwroot/uploads` eintragen.
- Bei jedem Deployment, Zurückschalten und Versionswechsel nimmt der Agent die App kurz offline (`app_offline.htm`), **verschiebt** diese Dateien/Ordner aus der laufenden in die neue Version und schaltet erst dann um. Gleiches Laufwerk → auch große Datenbanken in Millisekunden, keine Kopie.
- SQLite: `-wal`, `-shm` und `-journal` wandern mit; eine Start-Datenbank aus dem Paket wird durch die laufende ersetzt. Fehlt ein Eintrag in der laufenden Version (erstes Deployment), bleibt der Stand aus dem Paket.
- Schlägt etwas fehl (Datei gesperrt, Umschalten, Health-Check mit Rollback), wandern die Dateien zurück und die alte Version läuft mit ihren Daten weiter.
- Aus der Pipeline: `Deploy-BlazorStudio.ps1 -Persist 'app.db','App_Data'` (setzt die Liste auf dem Server), im Pipeline-Builder als Feld „Persistente Dateien“. Ohne `-Persist` gilt die in Studio gepflegte Liste.
- **Agent 1.9.0 nötig.**

## Neu: Docker-Deployment und Menü „Deployment“
- Neues Hauptmenü **Deployment** mit **In Docker bereitstellen …**, **Auf IIS bereitstellen …**, **Docker …**, **IIS-Server …**, Pipeline-Builder und Version ändern. Unter **Tools** bleibt IIS wie bisher.
- **Docker-Hosts:** lokales Docker (Docker Desktop) oder Server per `ssh://benutzer@server` bzw. `tcp://server:2376`, mit Umgebung (Test/Produktion) und Status (Docker-Version, laufende Container).
- **Container:** Liste mit Zustand, Image/Version, Ports, CPU und RAM (alle 5 s aktualisiert); Starten, Stoppen, Neu starten, Löschen, **Logs im Editor**.
- **Bereitstellen:** Projekt wählen, Hosts ankreuzen, Container- und Image-Name, Tag aus der Projektversion (+ Patch/Minor/Major direkt daneben), Ports, Umgebungsvariablen, Volumes, Restart-Policy, Netzwerk, weitere `docker run`-Argumente.
  - Gebaut wird mit dem **Dockerfile** neben der .csproj oder ohne Dockerfile über das **.NET SDK** (`dotnet publish /t:PublishContainer`) – „Auto“ wählt selbst.
  - Ohne Registry wird direkt auf dem Ziel-Host gebaut; mit Registry einmal gebaut, gepusht und auf jedem Host gezogen.
  - Der laufende Container wird gestoppt und als `name-previous` aufgehoben, der neue gestartet und geprüft (läuft er? antwortet `http://host:port/pfad`?). Schlägt die Prüfung fehl, läuft automatisch wieder der vorherige – mit den letzten Log-Zeilen in der Ausgabe.
  - Alte Images werden aufgeräumt (die neuesten 5 bleiben, einstellbar).
- **Zurückschalten mit einem Klick** (↶ am Container) und **Version wechseln**: jedes Image desselben Repositorys auf dem Host auswählen – der Container wird mit denselben Einstellungen neu erstellt (sie stehen als Label am Container).
- **Images:** Liste mit Größe, Alter und welcher Container sie verwendet; nicht verwendete löschen.

## Neu: IIS – Authentifizierung, Konfiguration und Logs pro Site
In **IIS-Server → Sites** eine Anwendung aufklappen:
- **Authentifizierung:** Anonym, Windows, Standard (Basic) und Digest per Häkchen ein-/ausschalten (wird in der applicationHost.config für genau diese Site/Anwendung gesetzt). Ist ein IIS-Modul nicht installiert, steht „nicht installiert“ daneben.
- **Konfiguration:** `web.config`, `appsettings.json` und `appsettings.<Umgebung>.json` mit **Bearbeiten** direkt im Editor von Studio öffnen. **Strg+S** speichert auf dem Server: sofort aktiv (bei appsettings wird der App-Pool recycelt) **und für alle künftigen Deployments** – die eigene Datei wird jedes Mal über die aus dem Paket gelegt (auch beim Zurückschalten auf ältere Versionen). Neue `appsettings.Production.json` usw. lassen sich direkt anlegen. „Eigene Version entfernen“ stellt wieder auf die Datei aus dem Paket um. Ungültiges XML/JSON wird abgelehnt.
- **stdout immer an:** In jeder web.config wird bei jedem Deployment `stdoutLogEnabled="true"` gesetzt. Die Logs landen in einem festen Ordner `<DeployRoot>\<Site>\_logs\` (bleibt über Versionen erhalten, Schreibrecht für IIS_IUSRS wird gesetzt) und lassen sich unter **Logs** im Editor ansehen.
- Remote-Dateien erkennt man im Editor am Server-Symbol im Reiter; der Tooltip zeigt Server, Site und Datei.
- **Agent 1.7.0 nötig** – `BlazorStudio-Agent-1.7.0.zip` entpacken und `install-service.ps1` erneut ausführen.

## Behoben: Strg+. änderte die falsche Datei
- Bei mehreren offenen Reitern wurde die gewählte Schnellaktion (Strg+. / Alt+Enter) im ersten offenen Editor statt in der aktuellen Datei angewendet. Die Aktion merkt sich jetzt ihr Dokument.

## Behoben: Site zeigte nach dem Deployment weiter auf „_initial“
- Ursache: Auf dem Server lief noch ein **älterer Agent** (vor 1.6.2) – und Studio hat das nicht als „veraltet“ markiert, weil nur Haupt- und Nebenversion verglichen wurden.
- Studio prüft jetzt **vor dem Veröffentlichen** die Agent-Version aller Zielserver und bricht mit klarer Meldung ab, wenn ein Agent älter als 1.6.2 ist (statt ein Deployment zu starten, das den Pfad nicht umstellt).
- Die Kennzeichnung „veraltet“ in der Server-Übersicht berücksichtigt jetzt auch die Patch-Version.
- Wird in eine Unteranwendung (z. B. `/app`) deployt, während die Site selbst noch auf `_initial` zeigt, weist das Protokoll darauf hin.
- **Agent aktualisieren:** `BlazorStudio-Agent-1.6.3.zip` auf dem Server entpacken und `install-service.ps1` erneut ausführen (Schlüssel und Einstellungen bleiben). Danach einmal deployen – die Site wird auf den Versionsordner umgeschaltet.

## Neu: Ein Klick zurück auf die vorherige Version
- In **IIS-Server → Sites** hat jede ausgerollte Anwendung jetzt den Knopf **↶ Eine Version zurück** direkt in der Zeile (neben 🚀). Nach Rückfrage wird auf die vorherige Version umgeschaltet und der App-Pool recycelt.
- Ältere Versionen gezielt: Zeile aufklappen → Versionsliste → **Aktivieren** bzw. **Zurückschalten**. Aus der Pipeline geht es per API: `POST /api/sites/<Site>/activate` mit `{ "version": "…", "appPath": "/" }`.

## Behoben: Bestätigungsdialog erschien hinter dem Fenster
- Rückfragen (z. B. „Bereitstellen?“ im IIS-Fenster) liegen jetzt immer über offenen Fenstern wie IIS-Server oder Pipeline-Builder.
- **Esc** schließt nur die Rückfrage, nicht gleich das Fenster darunter.

## Behoben: IIS-Deployment blieb auf „_initial“ stehen
- Wurde beim Bereitstellen eine neue Site angelegt und schlug danach ein Schritt fehl (z. B. ein zweiter Versuch in derselben Minute mit gleichem Versionsnamen), blieb die Site auf dem leeren Ordner `…\_initial` stehen – die App mit ihren DLLs lag zwar auf dem Server, war aber nicht aktiv.
- Neue Sites werden jetzt **erst nach dem Entpacken** angelegt – und zwar direkt auf dem Versionsordner. Den Ordner `_initial` gibt es nicht mehr.
- Eine gleichnamige, nicht aktive Version (Rest eines abgebrochenen Versuchs) wird einfach ersetzt statt mit Fehler abzubrechen.
- Nach dem Umschalten liest der Agent den physischen Pfad aus IIS nach und meldet einen klaren Fehler, falls IIS ihn nicht übernommen hat. Das Protokoll im Verlauf zeigt den gesetzten Pfad.
- **Bitte den Agent auf den Servern aktualisieren:** `BlazorStudio-Agent-1.6.2.zip` entpacken und `install-service.ps1` erneut ausführen (Schlüssel und Einstellungen bleiben). Sites, die noch auf `_initial` stehen, werden beim nächsten Deployment richtig umgeschaltet.

## Neu: Projektversion in Studio ändern
- **Projekt → Version ändern …** und im Kontextmenü eines Projekts im Projektmappen-Explorer: setzt `<Version>` in der `.csproj` (bzw. `<VersionPrefix>`, falls verwendet). Vorschlag ist die nächste Patch-Version.
- Im Bereitstellen-Reiter der IIS-Server steht die **Projektversion** mit **+ Patch / + Minor / + Major** – die Vorschau der Deploy-Version aktualisiert sich sofort.
- Ist die `.csproj` im Editor geöffnet, geht die Änderung über den Editor (Strg+Z möglich); der Rest der Datei bleibt unverändert.
