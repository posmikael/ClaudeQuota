# Builds the macOS app from Windows (or any OS): dist\ClaudeQuota-macOS-arm64.tar.gz and -x64.tar.gz,
# each holding "Claude Quota.app" (self-contained single-file binary, no Dock icon).
# Usage: .\build-mac.ps1 [-Version 2.0.0]
# A .tar.gz, not a .zip: it keeps the executable bit that a zip made on Windows loses.
# The app is unsigned; see README for the one-time xattr/codesign step on the Mac.
param([string]$Version = '2.0.0')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

foreach ($arch in 'arm64', 'x64') {
    $publish = Join-Path $root "publish\osx-$arch"
    dotnet publish (Join-Path $root 'Quota.csproj') -c Release -r "osx-$arch" --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:Version=$Version -o $publish
    if ($LASTEXITCODE) { throw "dotnet publish osx-$arch failed" }

    # Claude Quota.app/Contents/{Info.plist, MacOS/ClaudeQuota, Resources/app.icns}
    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Claude Quota</string>
  <key>CFBundleDisplayName</key><string>Claude Quota</string>
  <key>CFBundleIdentifier</key><string>com.claudequota.app</string>
  <key>CFBundleVersion</key><string>$Version</string>
  <key>CFBundleShortVersionString</key><string>$Version</string>
  <key>CFBundleExecutable</key><string>ClaudeQuota</string>
  <key>CFBundleIconFile</key><string>app.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"@
    $files = @(
        @{ Path = 'Contents/Info.plist'; Bytes = [Text.Encoding]::UTF8.GetBytes($plist); Exec = $false }
        @{ Path = 'Contents/MacOS/ClaudeQuota'; Bytes = [IO.File]::ReadAllBytes((Join-Path $publish 'ClaudeQuota')); Exec = $true }
        @{ Path = 'Contents/Resources/app.icns'; Bytes = [IO.File]::ReadAllBytes((Join-Path $root 'app.icns')); Exec = $false }
    )

    $out = Join-Path $dist "ClaudeQuota-macOS-$arch.tar.gz"
    $fs = [IO.File]::Create($out)
    $gz = [IO.Compression.GZipStream]::new($fs, [IO.Compression.CompressionLevel]::Optimal)
    $tar = [Formats.Tar.TarWriter]::new($gz, [Formats.Tar.TarEntryFormat]::Pax, $false)
    $dirMode = [IO.UnixFileMode]493   # 0755
    foreach ($dir in 'Claude Quota.app/', 'Claude Quota.app/Contents/', 'Claude Quota.app/Contents/MacOS/', 'Claude Quota.app/Contents/Resources/') {
        $e = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::Directory, $dir); $e.Mode = $dirMode; $tar.WriteEntry($e)
    }
    foreach ($f in $files) {
        $e = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, "Claude Quota.app/$($f.Path)")
        $e.Mode = if ($f.Exec) { $dirMode } else { [IO.UnixFileMode]420 }   # 0755 / 0644
        $e.DataStream = [IO.MemoryStream]::new($f.Bytes)
        $tar.WriteEntry($e)
    }
    $tar.Dispose(); $gz.Dispose(); $fs.Dispose()
    Get-Item $out | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
}
