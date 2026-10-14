# Blazor Studio Agent

Windows-Dienst für IIS-Server. Blazor Studio und Build-Pipelines sprechen ihn über eine REST-API an:

- **Status & Auslastung** – CPU, Arbeitsspeicher, Plattenplatz, IIS-Dienst, Verlauf der letzten 3 Minuten
- **App-Pools** – auflisten, starten/stoppen/recyceln, anlegen, ändern (Runtime, Pipeline, Identität, Startmodus,
  Leerlauf, Recycling, CPU-Limit), löschen; Arbeitsprozesse mit CPU/RAM/laufenden Anfragen
- **Sites** – auflisten (Bindungen, Anwendungen, aktive Version), starten/stoppen, anlegen, löschen
- **Deployments mit Versionen** – Paket hochladen → Version ablegen → umschalten → Pool recyceln → Health-Check →
  bei Fehler automatisches Rollback → alte Versionen aufräumen
- **Versionen umschalten** – jede abgelegte Version kann sofort wieder aktiviert werden (Rollback in Sekunden)
- **Verlauf** – wer hat wann welche Version ausgerollt, mit vollständigem Log

## Installation

Voraussetzungen: Windows Server 2016+ / Windows 10+, IIS (`Install-WindowsFeature Web-Server -IncludeManagementTools`),
für ASP.NET-Core-Apps das *ASP.NET Core Hosting Bundle*. Der Agent selbst bringt seine .NET-Runtime mit.

```powershell
# ZIP auf den Server kopieren, entpacken, als Administrator:
.\install-service.ps1                       # HTTP auf Port 8790
.\install-service.ps1 -Https -SelfSigned    # HTTPS mit selbstsigniertem Zertifikat (Fingerabdruck in Studio eintragen)
.\install-service.ps1 -Https -CertSubject web01.firma.local   # vorhandenes Zertifikat
```

Das Skript gibt am Ende **Adresse und API-Schlüssel** aus – beides in Blazor Studio unter
*Extras → IIS-Server → Server hinzufügen* eintragen.

**Update:** neues ZIP entpacken und `install-service.ps1` erneut ausführen – Schlüssel und Einstellungen bleiben.
**Entfernen:** `uninstall-service.ps1` (mit `-RemoveFiles` auch Programmordner und Verlauf).

## Einstellungen (`appsettings.json`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Agent:ApiKey` | – | Pflicht; Header `X-Api-Key` jeder Anfrage |
| `Agent:DeployRoot` | `C:\inetpub\deployments` | Ablage: `<Root>\<Site>\<Version>\` |
| `Agent:KeepVersions` | `5` | so viele Versionen bleiben je Site (aktive + vorherige immer) |
| `Agent:MaxPackageMegabytes` | `2048` | maximale Paketgröße |
| `Agent:HealthCheckSeconds` | `60` | so lange wartet der Health-Check auf eine Antwort < 400 |
| `Kestrel:Endpoints` | `http://*:8790` | Adresse/Port, HTTPS siehe oben |

Nach Änderungen: `Restart-Service BlazorStudioAgent`.

## Sicherheit

- Jede Anfrage außer `GET /api/ping` braucht den API-Schlüssel; `appsettings.json` ist nur für Administratoren lesbar.
- Der Dienst läuft als LocalSystem (nötig für die IIS-Konfiguration). Port nur im internen Netz/VPN freigeben;
  die Firewall-Regel gilt für die Profile *Domäne* und *Privat*.
- Für Verbindungen über fremde Netze HTTPS verwenden. Bei selbstsignierten Zertifikaten trägt Studio bzw. das
  Pipeline-Skript den Fingerabdruck ein und vertraut genau diesem Zertifikat.

## Aus einer Pipeline ausrollen

`Deploy-BlazorStudio.ps1` (liegt im Paket und lässt sich in Studio unter *IIS-Server → Pipeline* speichern) läuft mit
Windows PowerShell 5.1 und PowerShell 7 – also auch auf Linux-Build-Agents:

```powershell
./Deploy-BlazorStudio.ps1 -Server https://web01:8790,https://web02:8790 -ApiKey $env:BLAZORSTUDIO_AGENT_KEY `
    -Package ./publish -Site Shop -Version 1.4.0 -HealthUrl http://localhost/health
```

Mehrere Server werden nacheinander ausgerollt; schlägt einer fehl (Health-Check → Rollback), bricht das Skript mit
Exit-Code 1 ab. Ohne `-Version` wird die Build-Nummer der Pipeline verwendet (GitHub, Azure DevOps, GitLab, Jenkins).
Fertige Vorlagen für GitHub Actions, Azure DevOps und GitLab erzeugt der Pipeline-Builder in Blazor Studio.

## REST-API

| Methode | Pfad | |
|---|---|---|
| GET | `/api/ping` | ohne Schlüssel: Agent-Version, Rechnername |
| GET | `/api/info` | Status, Auslastung, Verlauf |
| GET/POST | `/api/pools` | auflisten / anlegen (`PoolSettings`) |
| PUT/DELETE | `/api/pools/{name}` | ändern / löschen |
| POST | `/api/pools/{name}/start\|stop\|recycle` | |
| GET/POST | `/api/sites` | auflisten / anlegen (`{ name, binding, pool?, physicalPath? }`) |
| DELETE | `/api/sites/{name}?removeFiles=true` | |
| POST | `/api/sites/{name}/start\|stop` | |
| GET | `/api/sites/{name}/versions?app=/` | abgelegte Versionen |
| POST | `/api/sites/{name}/activate` | `{ version, appPath, recycle }` – umschalten/Rollback |
| DELETE | `/api/sites/{name}/versions/{version}?app=/` | Version löschen (nicht die aktive) |
| POST | `/api/deployments` | multipart: `package` (ZIP) + `site`, `version`, `appPath`, `healthUrl`, `rollback`, `createSite`, `binding`, `pool`, `runtime`, `overwrite`, `activate`, `source`, `commit`, `notes` → 202 |
| GET | `/api/deployments?site=&take=` | Verlauf |
| GET | `/api/deployments/{id}` | Stand mit Log (zum Abfragen bis `state` ≠ Queued/Running) |

Beispiel mit curl:

```bash
curl -H "X-Api-Key: $KEY" -F package=@app.zip -F site=Shop -F version=1.4.0 http://web01:8790/api/deployments
```
