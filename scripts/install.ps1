<#
.SYNOPSIS
One-shot installer for RevitMCPServer - Revit add-in + MCP server, all versions.

.DESCRIPTION
Extract the release bundle and run this. With no arguments it:
  1. Auto-detects which Revit versions (2025/2026/2027) are installed and
     deploys the matching add-in (RevitMCPAddin.dll + RevitMCP.Core.dll +
     manifest) into each %APPDATA%\Autodesk\Revit\Addins\<ver>\ folder.
  2. Copies the MCP server to a stable location (%LOCALAPPDATA%\RevitMCPServer)
     and runs npm install there, so the extracted folder can be deleted after.
  3. Merges a "revit-<ver>" entry per version into your Claude Desktop config
     (backing it up first, leaving every other MCP server untouched).

Close Revit before running so the add-in DLL is not locked.

.PARAMETER RevitVersions
Explicit list, e.g. -RevitVersions 2026,2027. Default: auto-detect installed.

.PARAMETER AllVersions
Install for every version present in the bundle, skip auto-detection.

.PARAMETER NoClaudeConfig
Do not touch the Claude Desktop config; just print the snippet.

.PARAMETER ClaudeConfigPath
Override the Claude config path (used for testing). Default:
%APPDATA%\Claude\claude_desktop_config.json

.PARAMETER ServerInstallDir
Where to install the MCP server. Default: %LOCALAPPDATA%\RevitMCPServer

.PARAMETER SkipNpm
Skip the npm install step.

.EXAMPLE
  .\install.ps1                       # auto-detect + full setup
  .\install.ps1 -RevitVersions 2027   # just 2027
  .\install.ps1 -NoClaudeConfig       # add-in + server only, print config
#>
param(
    [int[]]$RevitVersions,
    [switch]$AllVersions,
    [switch]$NoClaudeConfig,
    [string]$ClaudeConfigPath = "$env:APPDATA\Claude\claude_desktop_config.json",
    [string]$ServerInstallDir = "$env:LOCALAPPDATA\RevitMCPServer",
    [switch]$SkipNpm
)

$ErrorActionPreference = "Stop"
$SUPPORTED = @(2025, 2026, 2027)
$PORT_BASE = 7891   # 2026 -> 7891, so port = 7891 + (year - 2026)

function Info($m) { Write-Host $m -ForegroundColor Cyan }
function Good($m) { Write-Host "  OK  $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  !!  $m" -ForegroundColor Yellow }

# -- Resolve the add-in source directory for a given version -------------------
# Bundle layout:  <root>\addin\<ver>\{RevitMCPAddin.dll, RevitMCP.Core.dll, .addin}
# Flat layout:    <root>\{RevitMCPAddin.dll, ...}         (a single-version zip)
# Dev layout:     <repo>\src\RevitAddin\bin\Release\...   (after a local build)
function Get-AddinSource([int]$ver) {
    $candidates = @(
        (Join-Path $PSScriptRoot "addin\$ver"),
        $PSScriptRoot,
        (Join-Path $PSScriptRoot "..\src\RevitAddin\bin\Release"),
        (Join-Path $PSScriptRoot "..\src\RevitAddin\bin\Debug")
    )
    foreach ($c in $candidates) {
        if ((Test-Path (Join-Path $c "RevitMCPAddin.dll")) -and
            (Test-Path (Join-Path $c "RevitMCP.Core.dll"))) {
            return (Resolve-Path $c).Path
        }
    }
    return $null
}

function Get-AddinManifest([string]$src) {
    $m = Join-Path $src "RevitMCPAddin.addin"
    if (Test-Path $m) { return $m }
    $m = Join-Path $PSScriptRoot "..\src\RevitAddin\RevitMCPAddin.addin"
    if (Test-Path $m) { return (Resolve-Path $m).Path }
    return $null
}

# -- Decide which versions to install ------------------------------------------
function Test-RevitInstalled([int]$ver) {
    return (Test-Path "$env:ProgramFiles\Autodesk\Revit $ver\Revit.exe")
}

$targets = @()
if ($RevitVersions) {
    $targets = $RevitVersions | Where-Object { $SUPPORTED -contains $_ }
}
elseif ($AllVersions) {
    $targets = $SUPPORTED | Where-Object { Get-AddinSource $_ }
}
else {
    Info "Detecting installed Revit versions..."
    $targets = $SUPPORTED | Where-Object { (Test-RevitInstalled $_) -and (Get-AddinSource $_) }
    if (-not $targets) {
        Warn "No installed Revit detected. Falling back to every version in the bundle."
        $targets = $SUPPORTED | Where-Object { Get-AddinSource $_ }
    }
}

if (-not $targets) {
    Write-Error "Nothing to install: no add-in binaries found for any supported Revit version. Run this from an extracted release bundle."
    exit 1
}
Info "Will install for Revit: $($targets -join ', ')"
Write-Host ""

# -- 1. Deploy the add-in for each target version ------------------------------
Info "[1/3] Deploying the Revit add-in"
$installed = @()
foreach ($ver in $targets) {
    $src = Get-AddinSource $ver
    if (-not $src) { Warn "Revit ${ver}: no binaries in bundle - skipped."; continue }
    $manifest = Get-AddinManifest $src
    if (-not $manifest) { Warn "Revit ${ver}: RevitMCPAddin.addin not found - skipped."; continue }

    $dest = "$env:APPDATA\Autodesk\Revit\Addins\$ver"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    try {
        Copy-Item (Join-Path $src "RevitMCPAddin.dll") $dest -Force
        Copy-Item (Join-Path $src "RevitMCP.Core.dll") $dest -Force
        Copy-Item $manifest $dest -Force
        Good "Revit ${ver}: -> $dest"
        $installed += $ver
    }
    catch {
        Warn "Revit ${ver}: copy failed ($($_.Exception.Message)). Is Revit still open?"
    }
}
if (-not $installed) { Write-Error "No add-in was installed. Aborting."; exit 1 }
Write-Host ""

# -- 2. Install the MCP server to a stable location ----------------------------
Info "[2/3] Installing the MCP server"
$serverSrc = Join-Path $PSScriptRoot "mcp-server"
$serverIndex = $null
if (Test-Path $serverSrc) {
    New-Item -ItemType Directory -Force -Path $ServerInstallDir | Out-Null
    Copy-Item "$serverSrc\*" $ServerInstallDir -Recurse -Force
    $serverIndex = Join-Path $ServerInstallDir "dist\index.js"
    Good "Server files -> $ServerInstallDir"

    $node = Get-Command node -ErrorAction SilentlyContinue
    if (-not $node) {
        Warn "Node.js not found on PATH. The add-in is installed and works, but the"
        Warn "Claude bridge needs Node 18+. Install from https://nodejs.org then re-run"
        Warn "with -SkipNpm=`$false, or run 'npm install --production' in $ServerInstallDir."
    }
    elseif (-not $SkipNpm) {
        Info "  Running npm install --production ..."
        Push-Location $ServerInstallDir
        try { npm install --production --silent 2>&1 | Out-Null } finally { Pop-Location }
        if ($LASTEXITCODE -eq 0) { Good "npm dependencies installed" }
        else { Warn "npm install failed - run it manually in $ServerInstallDir" }
    }
}
else {
    Warn "No mcp-server/ folder in the bundle - add-in installed, but the Claude bridge is not set up."
}
Write-Host ""

# -- 3. Merge into the Claude Desktop config -----------------------------------
function ConvertTo-OrderedHashtable($obj) {
    if ($obj -is [System.Management.Automation.PSCustomObject]) {
        $h = [ordered]@{}
        foreach ($p in $obj.PSObject.Properties) { $h[$p.Name] = ConvertTo-OrderedHashtable $p.Value }
        return $h
    }
    elseif ($obj -is [System.Collections.IEnumerable] -and $obj -isnot [string]) {
        return @($obj | ForEach-Object { ConvertTo-OrderedHashtable $_ })
    }
    return $obj
}

Info "[3/3] Configuring Claude Desktop"
$configSnippet = [ordered]@{}
foreach ($ver in $installed) {
    $port = $PORT_BASE + ($ver - 2026)
    $args = if ($serverIndex) { @(($serverIndex -replace '\\', '/')) } else { @("<path-to>/dist/index.js") }
    $configSnippet["revit-$ver"] = [ordered]@{
        command = "node"
        args    = $args
        env     = [ordered]@{ REVIT_MCP_VERSION = "$ver"; REVIT_MCP_PORT = "$port" }
    }
}

if ($NoClaudeConfig -or -not $serverIndex) {
    if ($NoClaudeConfig) { Warn "Skipping Claude config (-NoClaudeConfig)." }
    Write-Host "Add these entries under `"mcpServers`" in your Claude Desktop config:" -ForegroundColor Yellow
    Write-Host (([ordered]@{ mcpServers = $configSnippet } | ConvertTo-Json -Depth 8)) -ForegroundColor White
}
else {
    $claudeDir = Split-Path $ClaudeConfigPath -Parent
    if (-not (Test-Path $claudeDir)) {
        Warn "Claude Desktop config folder not found ($claudeDir) - Claude Desktop may not be installed."
        Write-Host "When you install Claude Desktop, add these under `"mcpServers`":" -ForegroundColor Yellow
        Write-Host (([ordered]@{ mcpServers = $configSnippet } | ConvertTo-Json -Depth 8)) -ForegroundColor White
    }
    else {
        # Load existing config (or start fresh), preserving everything already there.
        $config = [ordered]@{}
        if (Test-Path $ClaudeConfigPath) {
            $backup = "$ClaudeConfigPath.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
            Copy-Item $ClaudeConfigPath $backup -Force
            Good "Backed up existing config -> $backup"
            try {
                $config = ConvertTo-OrderedHashtable (Get-Content $ClaudeConfigPath -Raw | ConvertFrom-Json)
            }
            catch {
                Warn "Existing config is not valid JSON - leaving it untouched and printing the snippet instead."
                Write-Host (([ordered]@{ mcpServers = $configSnippet } | ConvertTo-Json -Depth 8)) -ForegroundColor White
                $config = $null
            }
        }
        if ($null -ne $config) {
            if (-not $config.Contains('mcpServers') -or $null -eq $config['mcpServers']) {
                $config['mcpServers'] = [ordered]@{}
            }
            elseif ($config['mcpServers'] -isnot [System.Collections.IDictionary]) {
                $config['mcpServers'] = ConvertTo-OrderedHashtable $config['mcpServers']
            }
            foreach ($key in $configSnippet.Keys) { $config['mcpServers'][$key] = $configSnippet[$key] }
            $config | ConvertTo-Json -Depth 8 | Set-Content $ClaudeConfigPath -Encoding UTF8
            Good "Wrote $($installed.Count) server entr$(if($installed.Count -eq 1){'y'}else{'ies'}) to $ClaudeConfigPath"
            Good "Other MCP servers in the file were left unchanged."
        }
    }
}
Write-Host ""

# -- Summary -------------------------------------------------------------------
Write-Host "Done." -ForegroundColor Green
Write-Host "  Add-in installed for Revit: $($installed -join ', ')" -ForegroundColor White
if ($serverIndex) { Write-Host "  MCP server: $ServerInstallDir" -ForegroundColor White }
Write-Host "  Next: (re)start Revit, then restart Claude Desktop." -ForegroundColor White
