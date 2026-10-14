## Neu: IIS-Server verwalten und ausrollen
Blazor Studio bringt jetzt einen eigenen Dienst für deine Windows-Server mit: den **Blazor Studio Agent** (`BlazorStudio-Agent-<Version>.zip`, auch unter *Extras → IIS-Server → Agent-Paket*). Auf dem Server entpacken, `install-service.ps1` als Administrator ausführen, Adresse und API-Schlüssel in Studio eintragen – fertig.

- **Serverliste mit Status:** Rechnernamen oder IPs unter *Extras → IIS-Server* eintragen, nach Umgebung gruppieren (Test, Staging, Produktion …). Studio prüft alle 30 Sekunden; die Statusleiste zeigt immer „IIS 3/3“ und warnt, wenn einer nicht erreichbar ist oder den Schlüssel ablehnt.
- **Übersicht & Auslastung:** CPU und Arbeitsspeicher live mit Verlauf, Plattenplatz, IIS-Version und -Dienst, gestartete Pools und Sites, Laufzeit.
- **App-Pools:** alle Pools mit Zustand, Runtime, Identität, Arbeitsprozessen, CPU, RAM und laufenden Anfragen. Starten, stoppen, recyceln, anlegen, löschen und bearbeiten (Runtime, Pipeline-Modus, Identität inkl. eigenem Benutzer, Startmodus, Leerlauf, Recycling, Warteschlange, CPU-Limit, 32-Bit).
- **Sites:** alle Sites mit Bindungen (anklickbar), Anwendungen, Pool und aktiver Version. Starten, stoppen, anlegen, löschen.
- **Versionen umschalten:** jedes Deployment wird als eigene Version abgelegt. Ein Klick schaltet auf eine ältere Version zurück („Zurückschalten“) oder wieder vor – in Sekunden, ohne neu zu bauen.
- **Bereitstellen aus Studio:** Projekt und Zielserver wählen → Studio veröffentlicht, packt, lädt hoch, schaltet um und recycelt den Pool. Mehrere Server werden nacheinander ausgerollt. Optional mit **Health-Check und automatischem Rollback**, „Site anlegen, falls nicht vorhanden“ und Versionsvorlage wie `{version}-{date}.{time}`.
- **Verlauf:** wer hat wann welche Version ausgerollt (Studio oder Pipeline, mit Commit), inklusive vollständigem Protokoll.
- **Aus der Pipeline:** `Deploy-BlazorStudio.ps1` rollt aus jeder CI aus (Windows PowerShell 5.1 und PowerShell 7, auch auf Linux-Agents), mit Exit-Code für die Pipeline. Die REST-API des Agents ist im Agent-README beschrieben.
- **Sicherheit:** API-Schlüssel pro Server (in Studio verschlüsselt gespeichert), HTTPS mit eigenem oder selbstsigniertem Zertifikat (Fingerabdruck-Pinning), Schutz gegen manipulierte Pakete.

## Neu: Pipeline-Builder
*Projekt → Pipeline-Builder*: Pipelines zusammenklicken statt YAML schreiben.
- Vorlagen „Build & Test“, „Build → IIS“ und „Staging → Produktion“ (main nach Staging, Tags nach Freigabe in die Produktion).
- Stufen mit Runner, Bedingung (immer, Branch, Tags, Pull Requests), Umgebung und manueller Freigabe; Schritte Restore, Build, Test (mit Ergebnissen und Abdeckung), Publish, **Deploy IIS** und eigene Skripte.
- Erzeugt **GitHub Actions**, **Azure DevOps** oder **GitLab CI** – mit Live-Vorschau des YAML, Hinweisen auf Lücken und Speichern direkt ins Repository (inkl. Deploy-Skript). Server und Site übernimmt er aus deinen IIS-Einstellungen.

## Verbessert
- **Automatisches `@using` wie in Rider:** Ziehst du eine Komponente aus der Toolbox in den Designer, fügst sie ein oder übernimmst sie aus der Vervollständigung im Code-Editor, ergänzt Studio den fehlenden Namespace – nur wenn er nicht schon über `_Imports.razor` (richtig nach Ordnern), `global using`, die Projektdatei oder den eigenen bzw. übergeordneten Namespace sichtbar ist. Gilt auch für Komponenten innerhalb von Vorlagen und für Blazor-eigene Komponenten wie `EditForm`, `NavLink` oder `Virtualize`.
- **Code-Editor reagiert direkter:** keine Animationen mehr beim Scrollen, beim Cursor und bei Hover/Vorschlagsliste.

## Behoben
- Build mit aktuellen .NET-10-SDKs schlug im Eigenschaften-Panel (`@bind:format`/`@bind:after`) fehl.
