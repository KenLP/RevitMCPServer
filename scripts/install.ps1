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

.PARAMETER Client
Which MCP client(s) to configure. One or more of: claude, gemini, cursor, codex.
Default: claude. Example: -Client codex,gemini. Each client's config file is
merged in place (backed up first), leaving its other servers untouched.

.PARAMETER NoClientConfig
Install the add-in + server only; don't touch any client config, just print the
snippet. (-NoClaudeConfig is kept as an alias.)

.PARAMETER ServerInstallDir
Where to install the MCP server. Default: %LOCALAPPDATA%\RevitMCPServer

.PARAMETER SkipNpm
Skip the npm install step.

.EXAMPLE
  .\install.ps1                       # auto-detect + configure Claude Desktop
  .\install.ps1 -Client codex         # configure OpenAI Codex CLI instead
  .\install.ps1 -Client claude,gemini,cursor,codex   # all clients at once
  .\install.ps1 -RevitVersions 2027   # just 2027
  .\install.ps1 -NoClientConfig       # add-in + server only, print config
#>
param(
    [int[]]$RevitVersions,
    [switch]$AllVersions,
    [ValidateSet("claude", "gemini", "cursor", "codex")]
    [string[]]$Client = @("claude"),
    [Alias("NoClaudeConfig")]
    [switch]$NoClientConfig,
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

# -- 3. Configure the MCP client(s) --------------------------------------------
# Every client we support wants the same logical entry: run "node <index.js>"
# with REVIT_MCP_VERSION/PORT. Only the file location and the on-disk format
# differ (JSON with an "mcpServers" object for claude/gemini/cursor; TOML with
# "[mcp_servers.NAME]" tables for codex). We merge in place, backing up first
# and leaving every other server the user already configured untouched.

# Windows PowerShell 5.1 ConvertTo-Json collapses a single-element array to a
# scalar ("args": "x" not ["x"]). Re-expand any scalar "args" value; multi-
# element args already serialize as [..] and the regex leaves them alone.
function ConvertTo-ClientJson($obj) {
    $json = $obj | ConvertTo-Json -Depth 8
    return [regex]::Replace($json, '("args":\s*)"([^"]*)"', '$1["$2"]')
}
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

# Client registry: config path + on-disk format.
$CLIENT_TARGETS = @{
    claude = @{ label = "Claude Desktop"; path = "$env:APPDATA\Claude\claude_desktop_config.json"; format = "json" }
    gemini = @{ label = "Gemini CLI";     path = "$env:USERPROFILE\.gemini\settings.json";         format = "json" }
    cursor = @{ label = "Cursor";         path = "$env:USERPROFILE\.cursor\mcp.json";               format = "json" }
    codex  = @{ label = "OpenAI Codex";   path = "$env:USERPROFILE\.codex\config.toml";             format = "toml" }
}

# Build the logical server entries (one per installed Revit version).
$entries = [ordered]@{}
foreach ($ver in $installed) {
    $port = $PORT_BASE + ($ver - 2026)
    $indexArg = if ($serverIndex) { ($serverIndex -replace '\\', '/') } else { "<path-to>/dist/index.js" }
    $entries["revit-$ver"] = [ordered]@{
        command = "node"
        args    = @($indexArg)
        env     = [ordered]@{ REVIT_MCP_VERSION = "$ver"; REVIT_MCP_PORT = "$port" }
    }
}

function Format-JsonSnippet { return ConvertTo-ClientJson ([ordered]@{ mcpServers = $entries }) }
function Format-TomlSnippet {
    $sb = New-Object System.Text.StringBuilder
    foreach ($name in $entries.Keys) {
        $e = $entries[$name]
        $argsToml = (($e.args | ForEach-Object { '"' + $_ + '"' }) -join ', ')
        $envToml  = (($e.env.GetEnumerator() | ForEach-Object { "$($_.Key) = `"$($_.Value)`"" }) -join ', ')
        [void]$sb.AppendLine("[mcp_servers.$name]")
        [void]$sb.AppendLine("command = `"node`"")
        [void]$sb.AppendLine("args = [$argsToml]")
        [void]$sb.AppendLine("env = { $envToml }")
        [void]$sb.AppendLine("")
    }
    return $sb.ToString().TrimEnd()
}

function Set-JsonClientConfig($path) {
    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
    $config = [ordered]@{}
    if (Test-Path $path) {
        Copy-Item $path "$path.bak-$(Get-Date -Format yyyyMMdd-HHmmss)" -Force
        try { $config = ConvertTo-OrderedHashtable (Get-Content $path -Raw | ConvertFrom-Json) }
        catch { Warn "  $path is not valid JSON - left untouched; snippet printed below."; Write-Host (Format-JsonSnippet); return $false }
    }
    if (-not $config.Contains('mcpServers') -or $null -eq $config['mcpServers']) { $config['mcpServers'] = [ordered]@{} }
    elseif ($config['mcpServers'] -isnot [System.Collections.IDictionary]) { $config['mcpServers'] = ConvertTo-OrderedHashtable $config['mcpServers'] }
    foreach ($k in $entries.Keys) { $config['mcpServers'][$k] = $entries[$k] }
    ConvertTo-ClientJson $config | Set-Content $path -Encoding UTF8
    return $true
}

function Set-TomlClientConfig($path) {
    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
    $head = ""
    if (Test-Path $path) {
        Copy-Item $path "$path.bak-$(Get-Date -Format yyyyMMdd-HHmmss)" -Force
        # Strip any existing [mcp_servers.revit-YYYY] blocks (header to next table
        # or EOF), preserving every other table. Then re-append fresh ones.
        $head = [regex]::Replace((Get-Content $path -Raw), '(?ms)^\[mcp_servers\.revit-\d{4}\]\s*.*?(?=^\[|\z)', '').TrimEnd()
    }
    $out = if ($head) { "$head`r`n`r`n" + (Format-TomlSnippet) } else { Format-TomlSnippet }
    ($out.TrimEnd() + "`r`n") | Set-Content $path -Encoding UTF8
    return $true
}

Info "[3/3] Configuring MCP client(s): $($Client -join ', ')"
if ($NoClientConfig -or -not $serverIndex) {
    if ($NoClientConfig) { Warn "Skipping client config (-NoClientConfig)." }
    Write-Host "JSON clients (Claude / Gemini / Cursor) - add under `"mcpServers`":" -ForegroundColor Yellow
    Write-Host (Format-JsonSnippet) -ForegroundColor White
    Write-Host "OpenAI Codex (~/.codex/config.toml):" -ForegroundColor Yellow
    Write-Host (Format-TomlSnippet) -ForegroundColor White
}
else {
    foreach ($c in ($Client | Select-Object -Unique)) {
        $t = $CLIENT_TARGETS[$c]
        $dir = Split-Path $t.path -Parent
        if (-not (Test-Path $dir) -and -not (Test-Path $t.path)) {
            Warn "$($t.label): config folder not found ($dir) - is it installed? Creating it anyway."
        }
        $existed = Test-Path $t.path
        $ok = if ($t.format -eq "toml") { Set-TomlClientConfig $t.path } else { Set-JsonClientConfig $t.path }
        if ($ok) {
            Good "$($t.label): wrote $($installed.Count) entr$(if($installed.Count -eq 1){'y'}else{'ies'}) -> $($t.path)"
            if ($existed) { Good "  (backed up first; your other servers were left unchanged)" }
        }
    }
}
Write-Host ""

# -- Summary -------------------------------------------------------------------
Write-Host "Done." -ForegroundColor Green
Write-Host "  Add-in installed for Revit: $($installed -join ', ')" -ForegroundColor White
if ($serverIndex) { Write-Host "  MCP server: $ServerInstallDir" -ForegroundColor White }
Write-Host "  Client(s) configured: $($Client -join ', ')" -ForegroundColor White
Write-Host "  Next: (re)start Revit, then restart your MCP client." -ForegroundColor White
