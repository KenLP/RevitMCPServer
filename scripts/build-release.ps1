<#
.SYNOPSIS
Build and package versioned release artifacts for RevitMCPServer.

.DESCRIPTION
Runs the full release pipeline:
  1. Version consistency check
  2. TypeScript tests + build
  3. C# tests + build for R2026 and R2027
  4. Packages each into a versioned ZIP under release/

.PARAMETER SkipTests
Skip the test step (faster, use only when tests were just run).

.PARAMETER RevitVersions
Revit versions to build for. Default: 2026, 2027.

.EXAMPLE
  .\scripts\build-release.ps1
  .\scripts\build-release.ps1 -SkipTests
  .\scripts\build-release.ps1 -RevitVersions 2026
#>
param(
    [switch]$SkipTests,
    [int[]]$RevitVersions = @(2026, 2027)
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

function Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function OK([string]$msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "FAILED  $msg" -ForegroundColor Red; exit 1 }

# ── Version ──────────────────────────────────────────────────────────────────

Step "Checking version consistency"
node "$root/scripts/check-version.mjs"
if ($LASTEXITCODE -ne 0) { Fail "Version strings are out of sync. Fix them and retry." }

$pkg = Get-Content "$root/src/McpServer/package.json" | ConvertFrom-Json
$version = $pkg.version
OK "Version: $version"

$releaseDir = "$root/release/RevitMCPServer-v$version"
if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }

# ── TypeScript ────────────────────────────────────────────────────────────────

Step "TypeScript: install dependencies"
Push-Location "$root/src/McpServer"
npm ci
if ($LASTEXITCODE -ne 0) { Fail "npm ci failed" }
OK "npm ci"

if (-not $SkipTests) {
    Step "TypeScript: tests"
    npm test
    if ($LASTEXITCODE -ne 0) { Fail "TypeScript tests failed" }
    OK "All TypeScript tests passed"
}

Step "TypeScript: build"
npm run build
if ($LASTEXITCODE -ne 0) { Fail "TypeScript build failed" }
Pop-Location
OK "TypeScript build complete"

# ── C# ───────────────────────────────────────────────────────────────────────

if (-not $SkipTests) {
    Step "C#: tests"
    dotnet test "$root/src/RevitAddin.Tests/RevitMCPAddin.Tests.csproj" `
        -p:DeployToRevit=false --logger "console;verbosity=quiet"
    if ($LASTEXITCODE -ne 0) { Fail "C# tests failed" }
    OK "All C# tests passed"
}

# ── Package each Revit version ────────────────────────────────────────────────

foreach ($rv in $RevitVersions) {

    Step "C# build for Revit $rv"

    $buildDir = "$root/release/.build-R$rv"
    dotnet build "$root/src/RevitAddin/RevitMCPAddin.csproj" `
        -p:RevitVersion=$rv `
        -p:DeployToRevit=false `
        -p:OutputPath="$buildDir" `
        -c Release
    if ($LASTEXITCODE -ne 0) { Fail "C# build for R$rv failed" }
    OK "C# R$rv build complete"

    # ── Assemble ZIP contents ─────────────────────────────────────────────────

    $pkgDir = "$root/release/RevitMCPServer-v$version-R$rv"
    New-Item -ItemType Directory -Force -Path "$pkgDir/mcp-server/dist" | Out-Null

    # Addin DLL + manifest
    Copy-Item "$buildDir/RevitMCPAddin.dll"   "$pkgDir/" -Force
    Copy-Item "$root/src/RevitAddin/RevitMCPAddin.addin" "$pkgDir/" -Force

    # MCP server (built JS + package metadata for npm install)
    Copy-Item "$root/src/McpServer/dist/index.js"       "$pkgDir/mcp-server/dist/" -Force
    Copy-Item "$root/src/McpServer/package.json"        "$pkgDir/mcp-server/"      -Force
    Copy-Item "$root/src/McpServer/package-lock.json"   "$pkgDir/mcp-server/"      -Force

    # Installer scripts
    Copy-Item "$root/scripts/install.ps1"   "$pkgDir/" -Force
    Copy-Item "$root/scripts/uninstall.ps1" "$pkgDir/" -Force

    # Quick-start readme
    $quickStart = @"
RevitMCPServer v$version for Revit $rv
======================================

INSTALL
  PowerShell:  .\install.ps1 -RevitVersion $rv
  (Run from this folder after extracting the zip)

REQUIREMENTS
  - Revit $rv installed
  - Node.js 18+ in PATH
  - Claude Desktop

UNINSTALL
  PowerShell:  .\uninstall.ps1 -RevitVersion $rv

Full docs: https://github.com/your-org/RevitMCPServer
"@
    $quickStart | Out-File "$pkgDir/INSTALL.txt" -Encoding utf8

    # ── Create ZIP ────────────────────────────────────────────────────────────

    $zipPath = "$root/release/RevitMCPServer-v$version-R$rv.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$pkgDir/*" -DestinationPath $zipPath
    OK "Created $zipPath"

    # SHA-256 checksum alongside the zip
    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
    "$hash  RevitMCPServer-v$version-R$rv.zip" | Out-File "$zipPath.sha256" -Encoding ascii
    OK "SHA-256: $hash"
}

# ── Cleanup build intermediates ───────────────────────────────────────────────

Remove-Item "$root/release/.build-R*" -Recurse -Force -ErrorAction SilentlyContinue

Step "Done"
Write-Host ""
Write-Host "Release artifacts in: $root/release/" -ForegroundColor Yellow
Get-ChildItem "$root/release/*.zip" | ForEach-Object {
    Write-Host "  $($_.Name)  ($([math]::Round($_.Length/1KB)) KB)" -ForegroundColor White
}
