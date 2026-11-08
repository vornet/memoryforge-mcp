param(
    [Parameter(Mandatory)][ValidateSet('win-x64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Runtime,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $repo
$output = Join-Path $repo "artifacts/publish/$Runtime"
$packages = Join-Path $repo 'artifacts/packages'
New-Item -ItemType Directory -Force -Path $output, $packages | Out-Null
dotnet publish src/MemoryForge.Server -c Release -r $Runtime --self-contained true -o $output `
    -p:Version=$Version -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
Copy-Item -LiteralPath README.md, LICENSE -Destination $output
Copy-Item -LiteralPath docs -Destination $output -Recurse -Force
$executable = if ($Runtime.StartsWith('win')) { 'memoryforge.exe' } else { 'memoryforge' }
$command = if ($Runtime.StartsWith('win')) { 'C:/Program Files/MemoryForge/memoryforge.exe' } elseif ($Runtime.StartsWith('osx')) { '/usr/local/bin/memoryforge' } else { '/usr/bin/memoryforge' }
Copy-Item -LiteralPath THIRD-PARTY-NOTICES.txt -Destination $output
$assets = Get-Content src/MemoryForge.Server/obj/project.assets.json -Raw | ConvertFrom-Json -AsHashtable
$runtimePackage = $assets.libraries.GetEnumerator() | Where-Object { $_.Key.StartsWith("Microsoft.NETCore.App.Runtime.$Runtime/", [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
if ($runtimePackage) {
    $packageRoot = @($assets.packageFolders.Keys)[0]
    $runtimeRoot = Join-Path $packageRoot $runtimePackage.Value.path
    foreach ($notice in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
        $noticePath = Join-Path $runtimeRoot $notice
        if (Test-Path -LiteralPath $noticePath) {
            Add-Content -LiteralPath "$output/THIRD-PARTY-NOTICES.txt" -Value "`nBundled runtime $($runtimePackage.Key): $notice`n"
            Get-Content -LiteralPath $noticePath -Raw | Add-Content -LiteralPath "$output/THIRD-PARTY-NOTICES.txt"
        }
    }
}
@{ mcpServers = @{ memoryforge = @{ command = $command; args = @() } } } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'mcp-config.json') -Encoding utf8
if ($Runtime.StartsWith('win')) {
    dotnet tool restore
    dotnet wix build packaging/windows/Package.wxs -arch x64 -pdbtype none -d "Version=$Version" -d "PublishDir=$output" -o "$packages/memoryforge-$Version-$Runtime.msi"
    Compress-Archive -Path "$output/*" -DestinationPath "$packages/memoryforge-$Version-$Runtime.zip" -Force
} else {
    chmod +x "$output/$executable"
    if ($Runtime.StartsWith('osx')) {
        codesign --force --sign - --options runtime --entitlements packaging/macos/server.entitlements "$output/$executable"
    }
    tar -czf "$packages/memoryforge-$Version-$Runtime.tar.gz" -C $output .
    $staging = Join-Path $repo "artifacts/staging/$Runtime"
    $library = if ($Runtime.StartsWith('osx')) { 'usr/local/lib/memoryforge' } else { 'usr/lib/memoryforge' }
    $bin = if ($Runtime.StartsWith('osx')) { 'usr/local/bin' } else { 'usr/bin' }
    New-Item -ItemType Directory -Force -Path "$staging/$library", "$staging/$bin" | Out-Null
    Copy-Item -Path "$output/*" -Destination "$staging/$library" -Recurse -Force
    $linkTarget = if ($Runtime.StartsWith('osx')) { '../lib/memoryforge/memoryforge' } else { '../lib/memoryforge/memoryforge' }
    ln -sf $linkTarget "$staging/$bin/memoryforge"
    if ($Runtime.StartsWith('osx')) {
        pkgbuild --root $staging --identifier us.vorn.memoryforge --version $Version --install-location / "$packages/memoryforge-$Version-$Runtime.pkg"
    } else {
        $architecture = if ($Runtime.EndsWith('arm64')) { 'arm64' } else { 'amd64' }
        New-Item -ItemType Directory -Force -Path "$staging/DEBIAN" | Out-Null
        @"
Package: memoryforge-mcp
Version: $Version
Section: devel
Priority: optional
Architecture: $architecture
Maintainer: vornet <vorn@vorn.us>
Depends: libc6 (>= 2.35), libgcc-s1, libstdc++6, libicu74 | libicu72 | libicu70, libssl3, zlib1g
Description: Agent-first local process memory tools over MCP
 .NET self-contained server providing scans, patterns, reads and guarded writes.
"@ | Set-Content -LiteralPath "$staging/DEBIAN/control" -Encoding utf8NoBOM
        chmod -R go-w $staging
        dpkg-deb --root-owner-group --build $staging "$packages/memoryforge-$Version-$Runtime.deb"
    }
}
Write-Output "Packaged MemoryForge $Version for $Runtime"
