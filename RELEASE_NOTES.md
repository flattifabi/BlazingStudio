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
