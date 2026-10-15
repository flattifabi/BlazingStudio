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
