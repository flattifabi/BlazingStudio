# Release & Updates

Blazor Studio aktualisiert sich über **GitHub Releases** von `github.com/flattifabi/BlazingStudio`
(öffentliches Repository, damit die App die Releases ohne Login abrufen kann).

## Neues Release veröffentlichen

1. Version in `src/BlazorStudio.App/BlazorStudio.App.csproj` (`<Version>`) erhöhen und committen.
2. Tag setzen und pushen:

   ```bash
   git tag v1.1.0
   git push origin v1.1.0
   ```

3. Der Workflow `.github/workflows/release.yml` baut auf Windows, führt die Tests aus, erstellt
   `BlazorStudio-Setup-1.1.0.exe` und hängt es an das Release `v1.1.0` (Release-Notes werden aus
   den Commits erzeugt und lassen sich danach auf GitHub bearbeiten).

Alternativ lokal bauen: `powershell -ExecutionPolicy Bypass -File build-installer.ps1 -Version 1.1.0`
und die Datei aus `dist/` manuell an ein Release hängen. Der Dateiname muss mit `BlazorStudio-Setup`
beginnen und auf `.exe` enden, und das Tag muss `v<Version>` heißen.

## So kommt das Update beim Nutzer an

- Beim Start prüft die App höchstens alle 6 Stunden `releases/latest` (Entwürfe und Vorabversionen
  werden ignoriert). „Hilfe → Nach Updates suchen“ prüft sofort.
- Ist eine neuere Version da, erscheint unten in der Statusleiste **„Update x.y.z“**. Der Dialog zeigt
  die Release-Notes mit „Jetzt aktualisieren“, „Später“ und „Diese Version überspringen“.
- „Jetzt aktualisieren“ fragt nach ungespeicherten Änderungen, lädt das Setup, startet es still
  (`/S /Launch /WaitPid=<pid>`) und beendet die App. Das Setup übernimmt Ordner und Verknüpfungs-
  Einstellungen der bisherigen Installation und startet danach die neue Version.
- Läuft die App nicht aus einer Setup-Installation (z. B. aus `bin/`), wird nur der Download angeboten.

## Testen ohne GitHub

Die Umgebungsvariable `BLAZORSTUDIO_UPDATE_FEED` zeigt auf eine JSON-Datei im Format der
GitHub-API (`tag_name`, `body`, `assets[].name/browser_download_url/size`), z. B. über einen lokalen
`python -m http.server`.

## Setup-Schalter

| Schalter | Wirkung |
|---|---|
| `/S` | still installieren bzw. deinstallieren |
| `/D=<Ordner>` | Installationsordner |
| `/NoDesktop` | keine Desktop-Verknüpfung |
| `/Launch` | nach der stillen Installation starten |
| `/WaitPid=<pid>` | vorher warten, bis dieser Prozess beendet ist |
| `Uninstall.exe /S /RemoveSettings` | still deinstallieren und Einstellungen löschen |

## IIS-Agent

Das Release enthält zusätzlich `BlazorStudio-Agent-<Version>.zip` (eigenständig, win-x64). `build-installer.ps1`
legt dieselbe Datei in die App unter `agent\`, damit *IIS-Server → Agent-Paket* sie ohne Download findet.
Auf den Servern: ZIP entpacken und `install-service.ps1` als Administrator ausführen – ein Update erhält
API-Schlüssel und Einstellungen. Details: `src/BlazorStudio.Agent/README.md`.

Lokal testen ohne IIS: `dotnet run --project src/BlazorStudio.Agent` mit `Agent__ApiKey=test` startet die API
(IIS-Aufrufe schlagen außerhalb von Windows/IIS erwartungsgemäß fehl, Ping/Upload/Verlauf funktionieren).
