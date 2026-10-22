<#
  Blazor Studio Agent – Folgeaktionen nach einer Zertifikatserneuerung durch win-acme.
  Wird von win-acme als Installationsschritt „script“ aufgerufen (eingerichtet über Blazor Studio):

    wacs.exe ... --installation script --script "<Agent>\Cert-Hook.ps1" --scriptparameters "-Service 'ShopWorker' -Nginx 'C:\nginx\nginx.exe'"

  -Service   Windows-Dienst neu starten (lädt z. B. die neue PFX-Datei in Kestrel)
  -Nginx     Pfad zu nginx.exe: Konfiguration prüfen und nginx neu laden (übernimmt neue PEM-Dateien)
#>
param(
    [string] $Service,
    [string] $Nginx
)

$ErrorActionPreference = 'Stop'
$failed = $false

if ($Nginx) {
    try {
        $prefix = Split-Path -Parent $Nginx
        Push-Location $prefix
        try {
            $test = & $Nginx -p "$prefix\" -t 2>&1
            if ($LASTEXITCODE -ne 0) { throw "nginx -t fehlgeschlagen: $test" }
            & $Nginx -p "$prefix\" -s reload 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "nginx -s reload fehlgeschlagen (Exit $LASTEXITCODE)" }
            Write-Output "nginx neu geladen ($Nginx)"
        }
        finally { Pop-Location }
    }
    catch {
        Write-Error "nginx: $_" -ErrorAction Continue
        $failed = $true
    }
}

if ($Service) {
    try {
        $svc = Get-Service -Name $Service
        if ($svc.Status -eq 'Running') {
            Restart-Service -Name $Service -Force
            Write-Output "Dienst $Service neu gestartet"
        }
        else {
            Write-Output "Dienst $Service läuft nicht ($($svc.Status)) – kein Neustart"
        }
    }
    catch {
        Write-Error "Dienst ${Service}: $_" -ErrorAction Continue
        $failed = $true
    }
}

if ($failed) { exit 1 }
