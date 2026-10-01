# Builds dist\ClaudeQuota-Setup.msi: publishes a self-contained single-file exe, then packages it with WiX.
# Usage: .\build-installer.ps1 [-Version 1.0.1]
param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'publish'

if (-not (Test-Path (Join-Path $root 'app.ico'))) { & (Join-Path $root 'installer\make-icon.ps1') }

# Self-contained: the target PC does not need the .NET runtime installed
dotnet publish (Join-Path $root 'Quota.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version -o $publish
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

dotnet build (Join-Path $root 'installer\ClaudeQuota.Installer.wixproj') -c Release `
    -p:Version=$Version -p:PublishDir="$publish\" -o (Join-Path $root 'dist')
if ($LASTEXITCODE) { throw 'installer build failed' }

Get-Item (Join-Path $root 'dist\ClaudeQuota-Setup.msi') | Select-Object FullName, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
