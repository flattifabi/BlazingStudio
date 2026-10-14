<#
.SYNOPSIS
    Installiert oder aktualisiert den Blazor Studio Agent als Windows-Dienst auf einem IIS-Server.

.DESCRIPTION
    Als Administrator im entpackten Agent-Ordner ausführen. Kopiert den Agent nach -InstallDir,
    erzeugt (einmalig) einen API-Schlüssel, richtet den Dienst „BlazorStudioAgent“ (LocalSystem,
    automatischer Start, Neustart bei Absturz) und eine Firewall-Regel ein.
    Bei einem Update bleiben API-Schlüssel und Einstellungen erhalten.

.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -Port 8790 -DeployRoot D:\Deployments
    .\install-service.ps1 -Https -SelfSigned           # HTTPS mit selbstsigniertem Zertifikat
    .\install-service.ps1 -Https -CertSubject web01.firma.local
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'BlazorStudio Agent'),
    [int]$Port = 0,
    [string]$DeployRoot,
    [switch]$Https,
    # Zertifikat aus LocalMachine\My (Antragsteller ohne „CN=“)
    [string]$CertSubject,
    # Selbstsigniertes Zertifikat für diesen Server erzeugen
    [switch]$SelfSigned,
    # Vorgegebener Schlüssel statt eines erzeugten
    [string]$ApiKey,
    # Neuen Schlüssel erzeugen (alter wird ungültig)
    [switch]$NewApiKey,
    [int]$KeepVersions = 0,
    [switch]$NoFirewall
)

$ErrorActionPreference = 'Stop'
$serviceName = 'BlazorStudioAgent'
$generated = $false
function Step($t) { Write-Host "==> $t" -ForegroundColor Cyan }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Bitte als Administrator ausführen.' }

if (-not (Get-Service W3SVC -ErrorAction SilentlyContinue)) {
    Write-Warning 'IIS (W3SVC) ist nicht installiert. Installieren z. B. mit:  Install-WindowsFeature Web-Server -IncludeManagementTools'
    Write-Warning 'Für ASP.NET Core zusätzlich das „ASP.NET Core Hosting Bundle“ installieren.'
}

# --- vorhandene Einstellungen übernehmen ---------------------------------------------------
$settingsFile = Join-Path $InstallDir 'appsettings.json'
$old = $null
if (Test-Path $settingsFile) {
    try { $old = Get-Content $settingsFile -Raw | ConvertFrom-Json } catch { Write-Warning "appsettings.json nicht lesbar – wird neu angelegt." }
}
function OldValue($path, $fallback) {
    $o = $old
    foreach ($p in $path.Split('.')) { if ($null -eq $o) { break }; $o = $o.$p }
    if ($null -ne $o -and "$o" -ne '') { return $o } else { return $fallback }
}

# --- Dienst stoppen ------------------------------------------------------------------------
$svc = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') {
    Step 'Dienst anhalten'
    Stop-Service $serviceName -Force
    $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

# --- Dateien kopieren -----------------------------------------------------------------------
$source = $PSScriptRoot
if ((Resolve-Path $source).Path.TrimEnd('\') -ne [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')) {
    Step "Dateien nach $InstallDir kopieren"
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Get-ChildItem $source -Force | Where-Object { $_.Name -ne 'appsettings.json' } | ForEach-Object {
        Copy-Item $_.FullName -Destination $InstallDir -Recurse -Force
    }
    if (-not $old) { Copy-Item (Join-Path $source 'appsettings.json') $settingsFile -Force }
}

# --- Einstellungen --------------------------------------------------------------------------
Step 'Einstellungen schreiben'
if (-not $ApiKey) {
    $ApiKey = if ($NewApiKey) { $null } else { OldValue 'Agent.ApiKey' $null }
    if (-not $ApiKey) {
        $bytes = New-Object byte[] 32
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $ApiKey = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $generated = $true
    }
}
if (-not $DeployRoot) { $DeployRoot = OldValue 'Agent.DeployRoot' 'C:\inetpub\deployments' }
if ($Port -le 0) {
    $oldUrl = OldValue 'Kestrel.Endpoints.Http.Url' (OldValue 'Kestrel.Endpoints.Https.Url' '')
    $Port = if ($oldUrl -match ':(\d+)$') { [int]$Matches[1] } else { 8790 }
}
if ($KeepVersions -le 0) { $KeepVersions = [int](OldValue 'Agent.KeepVersions' 5) }
if (-not $Https -and -not $CertSubject -and -not $SelfSigned -and (OldValue 'Kestrel.Endpoints.Https.Url' $null)) {
    $Https = $true
    $CertSubject = OldValue 'Kestrel.Endpoints.Https.Certificate.Subject' $null
}

$thumbprint = $null
if ($SelfSigned) {
    $Https = $true
    $dns = @($env:COMPUTERNAME)
    try { $fqdn = [Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName; if ($fqdn -and $fqdn -ne $env:COMPUTERNAME) { $dns += $fqdn } } catch { }
    Step "Selbstsigniertes Zertifikat für $($dns -join ', ') erzeugen"
    $cert = New-SelfSignedCertificate -DnsName $dns -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(5) -FriendlyName 'Blazor Studio Agent'
    $CertSubject = $dns[0]
    $thumbprint = $cert.Thumbprint
}
elseif ($Https) {
    if (-not $CertSubject) { throw '-Https braucht -CertSubject <Name> oder -SelfSigned.' }
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq "CN=$CertSubject" -and $_.NotAfter -gt (Get-Date) } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) { throw "Kein gültiges Zertifikat mit CN=$CertSubject in LocalMachine\My gefunden." }
    $thumbprint = $cert.Thumbprint
}

$endpoint = if ($Https) {
    @{ Https = @{ Url = "https://*:$Port"; Certificate = @{ Subject = $CertSubject; Store = 'My'; Location = 'LocalMachine'; AllowInvalid = [bool]$SelfSigned } } }
} else {
    @{ Http = @{ Url = "http://*:$Port" } }
}
$settings = [ordered]@{
    Agent = [ordered]@{
        ApiKey = $ApiKey
        DeployRoot = $DeployRoot
        DataDirectory = (OldValue 'Agent.DataDirectory' '')
        KeepVersions = $KeepVersions
        MaxPackageMegabytes = [int](OldValue 'Agent.MaxPackageMegabytes' 2048)
        HealthCheckSeconds = [int](OldValue 'Agent.HealthCheckSeconds' 60)
    }
    Kestrel = @{ Endpoints = $endpoint }
    Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning' }; EventLog = @{ LogLevel = @{ Default = 'Warning' } } }
}
$settings | ConvertTo-Json -Depth 10 | Set-Content $settingsFile -Encoding UTF8
New-Item -ItemType Directory -Force $DeployRoot | Out-Null

# Schlüssel nur für Administratoren und SYSTEM lesbar
$acl = New-Object Security.AccessControl.FileSecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($id in 'BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM') {
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'Allow')))
}
Set-Acl $settingsFile $acl

# --- Dienst --------------------------------------------------------------------------------
$exe = Join-Path $InstallDir 'BlazorStudio.Agent.exe'
if (-not $svc) {
    Step 'Dienst einrichten'
    New-Service -Name $serviceName -BinaryPathName "`"$exe`"" -DisplayName 'Blazor Studio Agent' -StartupType Automatic `
        -Description 'Deployment und Verwaltung von IIS-Sites und App-Pools für Blazor Studio und Build-Pipelines.' | Out-Null
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null
}
else {
    & sc.exe config $serviceName binPath= "`"$exe`"" | Out-Null
}

if (-not $NoFirewall) {
    Step "Firewall-Port $Port freigeben"
    Get-NetFirewallRule -Name 'BlazorStudioAgent' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name 'BlazorStudioAgent' -DisplayName "Blazor Studio Agent ($Port)" -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Domain, Private | Out-Null
}

Step 'Dienst starten'
Start-Service $serviceName
Start-Sleep -Seconds 2
$scheme = if ($Https) { 'https' } else { 'http' }
$url = "${scheme}://$($env:COMPUTERNAME):$Port"
try {
    if ($Https) { [Net.ServicePointManager]::ServerCertificateValidationCallback = { $true } }
    $ping = Invoke-RestMethod "${scheme}://localhost:$Port/api/ping" -TimeoutSec 10
    Write-Host "Agent $($ping.version) läuft auf $($ping.machineName)." -ForegroundColor Green
}
catch {
    Write-Warning "Agent antwortet noch nicht: $($_.Exception.Message) – Ereignisanzeige (Anwendung) prüfen."
}

Write-Host ''
Write-Host '  In Blazor Studio unter „Extras → IIS-Server“ eintragen:' -ForegroundColor White
Write-Host "    Adresse:      $($env:COMPUTERNAME)  (Port $Port, $($scheme.ToUpper()))"
Write-Host "    API-Schlüssel: $ApiKey" -ForegroundColor Yellow
if ($thumbprint) { Write-Host "    Zertifikat:   $thumbprint" }
if (-not $generated -and $old) { Write-Host '    (bisheriger Schlüssel wurde beibehalten – neuen erzeugen mit -NewApiKey)' -ForegroundColor DarkGray }
Write-Host ''
Write-Host "  Pipeline: Deploy-BlazorStudio.ps1 -Server $url -ApiKey <Schlüssel> -Package <Ordner|zip> -Site <Name>"
