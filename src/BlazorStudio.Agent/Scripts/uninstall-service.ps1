<#
.SYNOPSIS
    Entfernt den Blazor Studio Agent (Dienst und Firewall-Regel).
    Abgelegte Versionen und IIS-Sites bleiben unangetastet.

.EXAMPLE
    .\uninstall-service.ps1
    .\uninstall-service.ps1 -RemoveFiles     # auch Programmordner und Verlauf löschen
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'BlazorStudio Agent'),
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Stop'
$serviceName = 'BlazorStudioAgent'

$svc = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service $serviceName -Force; $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    & sc.exe delete $serviceName | Out-Null
    Write-Host 'Dienst entfernt.'
}
Get-NetFirewallRule -Name 'BlazorStudioAgent' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

if ($RemoveFiles) {
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    $data = Join-Path $env:ProgramData 'BlazorStudio\Agent'
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    Write-Host 'Dateien entfernt (abgelegte Versionen in der Deploy-Ablage bleiben erhalten).'
}
