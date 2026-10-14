<#
  Baut Blazor Studio als Release (self-contained, win-x64) und daraus das Setup.

    .\build-installer.ps1                 # Version aus der .csproj
    .\build-installer.ps1 -Version 1.2.0  # Version überschreiben
    .\build-installer.ps1 -SkipLogo       # Logo nicht neu rendern

  Ergebnis: dist\BlazorStudio-Setup-<Version>.exe (+ dist\app mit der portablen App)
            dist\BlazorStudio-Agent-<Version>.zip (IIS-Agent für die Server, liegt auch im Setup unter agent\)
#>
param(
    [string]$Version,
    [switch]$SkipLogo
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$app = Join-Path $dist 'app'
$stage = Join-Path $dist 'stage'
$appProj = Join-Path $root 'src\BlazorStudio.App\BlazorStudio.App.csproj'
$setupProj = Join-Path $root 'installer\BlazorStudio.Setup\BlazorStudio.Setup.csproj'
$agentProj = Join-Path $root 'src\BlazorStudio.Agent\BlazorStudio.Agent.csproj'
$agentOut = Join-Path $dist 'agent'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

if (-not $Version) {
    $Version = ([xml](Get-Content $appProj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { $Version = '1.0.0' }
}

if (-not $SkipLogo) {
    Step 'Logo rendern'
    python (Join-Path $root 'branding\render.py')
    if ($LASTEXITCODE -ne 0) { throw 'Logo-Rendering fehlgeschlagen' }
    Copy-Item (Join-Path $root 'branding\logo.ico') (Join-Path $root 'src\BlazorStudio.App\logo.ico') -Force
    Copy-Item (Join-Path $root 'branding\logo.svg') (Join-Path $root 'src\BlazorStudio.App\wwwroot\img\logo.svg') -Force
}

Step "App veröffentlichen ($Version, win-x64, self-contained)"
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet publish $appProj -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishReadyToRun=false -o $app -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish fehlgeschlagen' }
# Native Tracing-Bibliotheken für fremde Architekturen werden auf x64 nicht gebraucht
foreach ($d in 'arm64', 'x86') { $p = Join-Path $app $d; if (Test-Path $p) { Remove-Item $p -Recurse -Force } }
Get-ChildItem $app -Filter *.pdb -Recurse | Remove-Item -Force

Step "IIS-Agent veröffentlichen ($Version, win-x64, self-contained)"
if (Test-Path $agentOut) { Remove-Item $agentOut -Recurse -Force }
dotnet publish $agentProj -c Release -r win-x64 --self-contained true -p:Version=$Version -o $agentOut -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Agent-Publish fehlgeschlagen' }
Get-ChildItem $agentOut -Filter *.pdb -Recurse | Remove-Item -Force
$agentZip = Join-Path $dist "BlazorStudio-Agent-$Version.zip"
if (Test-Path $agentZip) { Remove-Item $agentZip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($agentOut, $agentZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
# Mit Studio ausliefern: „IIS-Server → Agent-Paket“ zeigt auf diese Datei
$appAgent = Join-Path $app 'agent'
New-Item -ItemType Directory -Force $appAgent | Out-Null
Copy-Item $agentZip $appAgent -Force
Remove-Item $agentOut -Recurse -Force

Step 'Uninstaller bauen'
dotnet build $setupProj -c Release -p:SetupKind=Uninstall -p:Version=$Version -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller-Build fehlgeschlagen' }

Step 'Nutzlast packen'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
[System.IO.Compression.ZipFile]::CreateFromDirectory($app, (Join-Path $stage 'payload.zip'), [System.IO.Compression.CompressionLevel]::Optimal, $false)
Copy-Item (Join-Path $root 'installer\BlazorStudio.Setup\bin\Uninstall\Uninstall.exe') $stage

Step 'Setup bauen'
dotnet build $setupProj -c Release -p:SetupKind=Setup -p:Version=$Version "-p:PayloadDir=$stage" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Setup-Build fehlgeschlagen' }
$out = Join-Path $dist "BlazorStudio-Setup-$Version.exe"
Copy-Item (Join-Path $root 'installer\BlazorStudio.Setup\bin\Setup\BlazorStudio-Setup.exe') $out -Force
Remove-Item $stage -Recurse -Force

$size = (Get-Item $out).Length / 1MB
Write-Host ("`nFertig: {0} ({1:N1} MB)" -f $out, $size) -ForegroundColor Green
Write-Host ("       {0} ({1:N1} MB)" -f $agentZip, ((Get-Item $agentZip).Length / 1MB)) -ForegroundColor Green
