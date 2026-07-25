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
    [int[]]$RevitVersions = @(2025, 2026, 2027)
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

function Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function OK([string]$msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "FAILED  $msg" -ForegroundColor Red; exit 1 }

# -- Version ------------------------------------------------------------------

Step "Checking version consistency"
node "$root/scripts/check-version.mjs"
if ($LASTEXITCODE -ne 0) { Fail "Version strings are out of sync. Fix them and retry." }

$pkg = Get-Content "$root/src/McpServer/package.json" | ConvertFrom-Json
$version = $pkg.version
OK "Version: $version"

$releaseDir = "$root/release/RevitMCPServer-v$version"
if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }

# -- TypeScript ----------------------------------------------------------------

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

# -- C# -----------------------------------------------------------------------

if (-not $SkipTests) {
    Step "C#: tests"
    dotnet test "$root/src/RevitAddin.Tests/RevitMCPAddin.Tests.csproj" `
        -p:DeployToRevit=false --logger "console;verbosity=quiet"
    if ($LASTEXITCODE -ne 0) { Fail "C# tests failed" }
    OK "All C# tests passed"
}

# -- Assemble ONE bundle for all versions --------------------------------------
# Layout:
#   <bundle>/install.ps1, uninstall.ps1, INSTALL.txt
#   <bundle>/addin/<ver>/{RevitMCPAddin.dll, RevitMCP.Core.dll, RevitMCPAddin.addin}
#   <bundle>/mcp-server/{dist/*.js, package.json, package-lock.json}   (shared)
# install.ps1 auto-detects which Revit versions are installed and deploys each.

$pkgDir = "$root/release/RevitMCPServer-v$version"
if (Test-Path $pkgDir) { Remove-Item $pkgDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$pkgDir/mcp-server/dist" | Out-Null

# Shared MCP server: every emitted runtime module, not just the entrypoint -
# index.js imports ./revitClient.js and ./recipes.js at run time. (dist/*.js is
# top-level only, so dist/__tests__/ is correctly excluded.)
Copy-Item "$root/src/McpServer/dist/*.js"         "$pkgDir/mcp-server/dist/" -Force
Copy-Item "$root/src/McpServer/package.json"      "$pkgDir/mcp-server/"      -Force
Copy-Item "$root/src/McpServer/package-lock.json" "$pkgDir/mcp-server/"      -Force
Copy-Item "$root/scripts/install.ps1"             "$pkgDir/" -Force
Copy-Item "$root/scripts/uninstall.ps1"           "$pkgDir/" -Force

foreach ($rv in $RevitVersions) {
    Step "C# build for Revit $rv"
    $buildDir = "$root/release/.build-R$rv"
    dotnet build "$root/src/RevitAddin/RevitMCPAddin.csproj" `
        -p:RevitVersion=$rv `
        -p:DeployToRevit=false `
        -p:OutputPath="$buildDir" `
        -c Release
    if ($LASTEXITCODE -ne 0) { Fail "C# build for R$rv failed" }

    # RevitMCP.Core.dll carries the command kernel - the addin type-loads against
    # it, so shipping RevitMCPAddin.dll alone yields a FileNotFoundException on
    # Revit start-up. Each version gets its own subfolder (net8 for 2025/2026,
    # net10 for 2027, built against version-specific Revit reference assemblies).
    $addinDir = "$pkgDir/addin/$rv"
    New-Item -ItemType Directory -Force -Path $addinDir | Out-Null
    Copy-Item "$buildDir/RevitMCPAddin.dll" "$addinDir/" -Force
    Copy-Item "$buildDir/RevitMCP.Core.dll" "$addinDir/" -Force
    Copy-Item "$root/src/RevitAddin/RevitMCPAddin.addin" "$addinDir/" -Force
    # AutoAudit panel: WebView2 managed assemblies + native loader. The loader
    # may land top-level (CopyLocalLockFileAssemblies) or under runtimes/.
    Copy-Item "$buildDir/Microsoft.Web.WebView2.Core.dll" "$addinDir/" -Force
    Copy-Item "$buildDir/Microsoft.Web.WebView2.Wpf.dll"  "$addinDir/" -Force
    if (Test-Path "$buildDir/WebView2Loader.dll") {
        Copy-Item "$buildDir/WebView2Loader.dll" "$addinDir/" -Force
    } elseif (Test-Path "$buildDir/runtimes/win-x64/native/WebView2Loader.dll") {
        Copy-Item "$buildDir/runtimes/win-x64/native/WebView2Loader.dll" "$addinDir/" -Force
    }
    OK "C# R$rv staged into addin/$rv/ (incl. WebView2)"
}

# Quick-start readme
$quickStart = @"
RevitMCPServer v$version - Revit 2025 / 2026 / 2027
===================================================

INSTALL (one step, all detected Revit versions):
  1. Close Revit.
  2. Right-click install.ps1 -> Run with PowerShell
     (or in a PowerShell window:  .\install.ps1)
  3. Restart Revit, then restart Claude Desktop.

The installer auto-detects which Revit versions you have, deploys the add-in to
each, installs the MCP server to %LOCALAPPDATA%\RevitMCPServer, and adds a
"revit-<ver>" entry to your Claude Desktop config (backing it up first, leaving
your other MCP servers untouched).

REQUIREMENTS
  - Revit 2025, 2026, or 2027
  - Node.js 18+ in PATH   (only for the Claude bridge; the add-in works without it)
  - Claude Desktop        (optional; add-in is usable over HTTP without it)

MCP CLIENTS (default: Claude Desktop)
  .\install.ps1 -Client codex            configure OpenAI Codex CLI instead
  .\install.ps1 -Client claude,gemini,cursor,codex   all of them at once
  (gemini -> ~/.gemini/settings.json, cursor -> ~/.cursor/mcp.json,
   codex -> ~/.codex/config.toml, claude -> %APPDATA%\Claude\...)

OPTIONS
  .\install.ps1 -RevitVersions 2027      only a specific version
  .\install.ps1 -NoClientConfig          don't touch any client config
  .\uninstall.ps1                        remove everything

Full docs: https://github.com/KenLP/RevitMCPServer
"@
$quickStart | Out-File "$pkgDir/INSTALL.txt" -Encoding utf8

# -- Artifact completeness gate ------------------------------------------------
# Assert every runtime dependency is in the bundle BEFORE zipping. A missing file
# here ships a package that dies on first load - exactly what happened at 0.8.15
# when only RevitMCPAddin.dll and index.js were copied.

$required = @(
    "install.ps1", "uninstall.ps1", "INSTALL.txt",
    "mcp-server/dist/index.js",
    "mcp-server/dist/revitClient.js",
    "mcp-server/dist/recipes.js",
    "mcp-server/package.json",
    "mcp-server/package-lock.json"
)
foreach ($rv in $RevitVersions) {
    $required += "addin/$rv/RevitMCPAddin.dll"
    $required += "addin/$rv/RevitMCP.Core.dll"
    $required += "addin/$rv/RevitMCPAddin.addin"
    $required += "addin/$rv/Microsoft.Web.WebView2.Core.dll"
    $required += "addin/$rv/Microsoft.Web.WebView2.Wpf.dll"
    $required += "addin/$rv/WebView2Loader.dll"
}
$missing = @($required | Where-Object { -not (Test-Path (Join-Path $pkgDir $_)) })
if ($missing.Count -gt 0) { Fail "Bundle is missing required file(s): $($missing -join ', ')" }

# Every relative import in the emitted JS must resolve inside the package.
$distDir = Join-Path $pkgDir "mcp-server/dist"
foreach ($js in Get-ChildItem "$distDir/*.js") {
    foreach ($m in [regex]::Matches((Get-Content $js.FullName -Raw), 'from\s+"(\.\/[^"]+)"')) {
        $target = Join-Path $distDir ($m.Groups[1].Value -replace '^\./', '')
        if (-not (Test-Path $target)) {
            Fail "Bundle: $($js.Name) imports '$($m.Groups[1].Value)' which is not in the package"
        }
    }
}
OK "Bundle contents verified ($($required.Count) required files, imports resolve)"

# -- Create ZIP ----------------------------------------------------------------
$zipPath = "$root/release/RevitMCPServer-v$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$pkgDir/*" -DestinationPath $zipPath
OK "Created $zipPath"

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
"$hash  RevitMCPServer-v$version.zip" | Out-File "$zipPath.sha256" -Encoding ascii
OK "SHA-256: $hash"

# -- Cleanup build intermediates -----------------------------------------------
Remove-Item "$root/release/.build-R*" -Recurse -Force -ErrorAction SilentlyContinue

Step "Done"
Write-Host ""
Write-Host "Release bundle in: $root/release/" -ForegroundColor Yellow
Get-ChildItem "$root/release/RevitMCPServer-v$version.zip" | ForEach-Object {
    Write-Host "  $($_.Name)  ($([math]::Round($_.Length/1KB)) KB)" -ForegroundColor White
}
