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
