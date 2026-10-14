<#
.SYNOPSIS
    Rollt ein Paket über den Blazor Studio Agent auf einen oder mehrere IIS-Server aus.

.DESCRIPTION
    Lädt einen Publish-Ordner (oder eine fertige ZIP-Datei) zum Agent hoch, der die Version ablegt,
    die IIS-Anwendung darauf umschaltet, den App-Pool recycelt und optional einen Health-Check
    ausführt (bei Fehler automatisches Rollback). Mehrere Server werden nacheinander ausgerollt
    (Rolling Deployment); beim ersten Fehler bricht das Skript ab.

    Läuft mit Windows PowerShell 5.1 und PowerShell 7+ (auch auf Linux-/macOS-Build-Agents).
    Exit-Code 0 = alle Server erfolgreich, 1 = Fehler.

.EXAMPLE
    ./Deploy-BlazorStudio.ps1 -Server https://web01:8790 -Package ./publish -Site Shop -Version 1.4.0

.EXAMPLE
    ./Deploy-BlazorStudio.ps1 -Server http://10.0.0.11:8790,http://10.0.0.12:8790 -Package app.zip `
        -Site Shop -CreateSite -Binding 'http://*:8080' -HealthUrl 'http://localhost:8080/health'
#>
[CmdletBinding()]
param(
    # Adresse(n) der Agents, z. B. https://web01:8790
    [Parameter(Mandatory = $true)][string[]]$Server,
    # API-Schlüssel des Agents (Standard: Umgebungsvariable BLAZORSTUDIO_AGENT_KEY)
    [string]$ApiKey = $env:BLAZORSTUDIO_AGENT_KEY,
    # Publish-Ordner oder ZIP-Datei
    [Parameter(Mandatory = $true)][string]$Package,
    # Name der IIS-Site
    [Parameter(Mandatory = $true)][string]$Site,
    # Versionsname (Standard: Build-Nummer der Pipeline bzw. Zeitstempel)
    [string]$Version,
    # Anwendung innerhalb der Site, z. B. /shop
    [string]$AppPath = '/',
    # Wird nach dem Umschalten vom Server aus aufgerufen; Fehler => Rollback
    [string]$HealthUrl,
    [switch]$NoRollback,
    # Site (und Pool) anlegen, falls nicht vorhanden
    [switch]$CreateSite,
    [string]$Binding,
    [string]$Pool,
    # Runtime für neu angelegte Pools: '' = kein verwalteter Code (ASP.NET Core), 'v4.0' = .NET Framework
    [string]$Runtime = '',
    # Gleichnamige, nicht aktive Version ersetzen
    [switch]$Overwrite,
    # Nur hochladen, nicht umschalten
    [switch]$NoActivate,
    [string]$Commit,
    [string]$Notes,
    [string]$Source = 'Pipeline',
    # SHA1-Fingerabdruck eines selbstsignierten Agent-Zertifikats, dem vertraut werden soll
    [string]$CertThumbprint,
    [int]$TimeoutMinutes = 20
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (-not $ApiKey) { throw 'Kein API-Schlüssel: -ApiKey angeben oder BLAZORSTUDIO_AGENT_KEY setzen.' }

# Version/Commit aus gängigen CI-Systemen übernehmen
if (-not $Version) {
    $build = @($env:GITHUB_RUN_NUMBER, $env:BUILD_BUILDNUMBER, $env:CI_PIPELINE_IID, $env:BUILD_NUMBER) | Where-Object { $_ } | Select-Object -First 1
    $Version = if ($build) { "build-$build" } else { Get-Date -Format 'yyyyMMdd.HHmmss' }
}
$Version = ($Version -replace '[^A-Za-z0-9._+\-]', '-').TrimStart('.', '-', '_', '+')
if (-not $Commit) {
    $Commit = @($env:GITHUB_SHA, $env:BUILD_SOURCEVERSION, $env:CI_COMMIT_SHA, $env:GIT_COMMIT) | Where-Object { $_ } | Select-Object -First 1
}

# HTTP-Client mit optionalem Zertifikat-Pinning (funktioniert in PowerShell 5.1 und 7)
if (-not ('BlazorStudioAgentHttp' -as [type])) {
    $code = @'
using System;
using System.Net.Http;
public static class BlazorStudioAgentHttp
{
    public static HttpClient Create(string thumbprint)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(thumbprint))
        {
            var pinned = thumbprint.Replace(" ", "").Replace(":", "").ToUpperInvariant();
            handler.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None ||
                (cert != null && string.Equals(cert.Thumbprint, pinned, StringComparison.OrdinalIgnoreCase));
        }
        var client = new HttpClient(handler);
        client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        return client;
    }
}
'@
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        Add-Type -AssemblyName System.Net.Http
        Add-Type -TypeDefinition $code -ReferencedAssemblies System.Net.Http, System.Security
    }
    else {
        Add-Type -TypeDefinition $code
    }
}

# Paket vorbereiten
$packagePath = (Resolve-Path $Package).Path
$tempZip = $null
if (Test-Path $packagePath -PathType Container) {
    Write-Step "Ordner packen: $packagePath"
    $tempZip = Join-Path ([IO.Path]::GetTempPath()) ("bs-deploy-" + [Guid]::NewGuid().ToString('N') + '.zip')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($packagePath, $tempZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $packagePath = $tempZip
}
$size = (Get-Item $packagePath).Length
Write-Host ("Paket: {0} ({1:N1} MB), Site: {2}{3}, Version: {4}" -f (Split-Path $packagePath -Leaf), ($size / 1MB), $Site, $(if ($AppPath -ne '/') { $AppPath } else { '' }), $Version)

$client = [BlazorStudioAgentHttp]::Create($CertThumbprint)
$client.DefaultRequestHeaders.Add('X-Api-Key', $ApiKey)

function Invoke-Agent([string]$method, [string]$url, $content) {
    $req = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::new($method)), $url
    if ($content) { $req.Content = $content }
    $resp = $client.SendAsync($req).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $resp.IsSuccessStatusCode) {
        $msg = $body
        try { $msg = ($body | ConvertFrom-Json).error } catch { }
        throw ("{0} {1}: {2}" -f [int]$resp.StatusCode, $url, $msg)
    }
    if ($body) { return $body | ConvertFrom-Json }
}

$failed = $false
try {
    foreach ($s in $Server) {
        $base = $s.TrimEnd('/')
        Write-Step "Deployment auf $base"
        $form = New-Object System.Net.Http.MultipartFormDataContent
        $fields = [ordered]@{
            site = $Site; appPath = $AppPath; version = $Version
            createSite = "$([bool]$CreateSite)".ToLower(); rollback = "$(-not $NoRollback)".ToLower()
            overwrite = "$([bool]$Overwrite)".ToLower(); activate = "$(-not $NoActivate)".ToLower()
            runtime = $Runtime; source = $Source
        }
        if ($Binding) { $fields.binding = $Binding }
        if ($Pool) { $fields.pool = $Pool }
        if ($HealthUrl) { $fields.healthUrl = $HealthUrl }
        if ($Commit) { $fields.commit = $Commit }
        if ($Notes) { $fields.notes = $Notes }
        foreach ($k in $fields.Keys) { $form.Add((New-Object System.Net.Http.StringContent ([string]$fields[$k])), $k) }
        $stream = [IO.File]::OpenRead($packagePath)
        try {
            $file = New-Object System.Net.Http.StreamContent $stream
            $file.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/zip')
            $form.Add($file, 'package', (Split-Path $packagePath -Leaf))
            $dep = Invoke-Agent 'POST' "$base/api/deployments" $form
        }
        finally { $stream.Dispose() }

        Write-Host "Deployment $($dep.id) gestartet"
        $seen = 0
        $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
        do {
            Start-Sleep -Seconds 1
            $dep = Invoke-Agent 'GET' "$base/api/deployments/$($dep.id)" $null
            $log = @($dep.log)
            for ($i = $seen; $i -lt $log.Count; $i++) {
                $color = switch ($log[$i].level) { 'error' { 'Red' } 'warn' { 'Yellow' } 'ok' { 'Green' } default { 'Gray' } }
                Write-Host ("  " + $log[$i].text) -ForegroundColor $color
            }
            $seen = $log.Count
            if ((Get-Date) -gt $deadline) { throw "Zeitüberschreitung nach $TimeoutMinutes Minuten" }
        } while ($dep.state -in @('Queued', 'Running'))

        if ($dep.state -ne 'Succeeded') {
            Write-Host "Fehlgeschlagen auf ${base}: $($dep.state) – $($dep.error)" -ForegroundColor Red
            if ($env:GITHUB_ACTIONS) { Write-Host "::error::Deployment auf $base fehlgeschlagen: $($dep.error)" }
            if ($env:TF_BUILD) { Write-Host "##vso[task.logissue type=error]Deployment auf $base fehlgeschlagen: $($dep.error)" }
            $failed = $true
            break
        }
        Write-Host "Version $Version ist auf $base aktiv." -ForegroundColor Green
    }
}
finally {
    $client.Dispose()
    if ($tempZip -and (Test-Path $tempZip)) { Remove-Item $tempZip -Force }
}

if ($failed) { exit 1 }
exit 0
