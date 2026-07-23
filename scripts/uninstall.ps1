<#
.SYNOPSIS
Remove RevitMCPServer - add-in, MCP server, and Claude config entries.

.DESCRIPTION
Mirrors install.ps1. With no arguments it removes the add-in from every Revit
version it finds it installed in, deletes the MCP server folder, and removes the
"revit-<ver>" entries from the Claude Desktop config (backing it up first).
Diagnostic logs are left in place and their location is reported.

.PARAMETER RevitVersions
Explicit list. Default: every supported version that has the add-in installed.

.PARAMETER Client
Which client config(s) to clean. One or more of: claude, gemini, cursor, codex.
Default: all four. Only the "revit-<ver>" entries are removed from each.

.PARAMETER KeepClientConfig
Do not touch any client config. (-KeepClaudeConfig is kept as an alias.)

.PARAMETER ServerInstallDir
Where the MCP server was installed. Default: %LOCALAPPDATA%\RevitMCPServer
#>
param(
    [int[]]$RevitVersions,
    [ValidateSet("claude", "gemini", "cursor", "codex")]
    [string[]]$Client = @("claude", "gemini", "cursor", "codex"),
    [Alias("KeepClaudeConfig")]
    [switch]$KeepClientConfig,
    [string]$ServerInstallDir = "$env:LOCALAPPDATA\RevitMCPServer"
)

$SUPPORTED = @(2025, 2026, 2027)
function Info($m) { Write-Host $m -ForegroundColor Cyan }
function Good($m) { Write-Host "  OK  $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  !!  $m" -ForegroundColor Yellow }

$versions = if ($RevitVersions) { $RevitVersions } else { $SUPPORTED }

# -- 1. Remove the add-in ------------------------------------------------------
Info "[1/3] Removing the Revit add-in"
$removedAny = $false
foreach ($ver in $versions) {
    $dir = "$env:APPDATA\Autodesk\Revit\Addins\$ver"
    $targets = @(
        "RevitMCPAddin.dll", "RevitMCPAddin.pdb",
        "RevitMCP.Core.dll", "RevitMCP.Core.pdb",
        "RevitMCPAddin.addin", "revit-mcp-token.txt"
    ) | ForEach-Object { Join-Path $dir $_ }
    $n = 0
    foreach ($t in $targets) { if (Test-Path $t) { Remove-Item $t -Force; $n++ } }
    if ($n -gt 0) { Good "Revit ${ver}: removed $n file(s) from $dir"; $removedAny = $true }
}
if (-not $removedAny) { Warn "No installed add-in found." }
Write-Host ""

# -- 2. Remove the MCP server --------------------------------------------------
Info "[2/3] Removing the MCP server"
if (Test-Path $ServerInstallDir) {
    Remove-Item $ServerInstallDir -Recurse -Force
    Good "Deleted $ServerInstallDir"
}
else { Warn "No server folder at $ServerInstallDir" }
Write-Host ""

# -- 3. Clean the MCP client config(s) -----------------------------------------
$CLIENT_TARGETS = @{
    claude = @{ label = "Claude Desktop"; path = "$env:APPDATA\Claude\claude_desktop_config.json"; format = "json" }
    gemini = @{ label = "Gemini CLI";     path = "$env:USERPROFILE\.gemini\settings.json";         format = "json" }
    cursor = @{ label = "Cursor";         path = "$env:USERPROFILE\.cursor\mcp.json";               format = "json" }
    codex  = @{ label = "OpenAI Codex";   path = "$env:USERPROFILE\.codex\config.toml";             format = "toml" }
}
Info "[3/3] Cleaning MCP client config(s): $($Client -join ', ')"
if ($KeepClientConfig) { Warn "Skipping (-KeepClientConfig)." }
else {
    foreach ($c in ($Client | Select-Object -Unique)) {
        $t = $CLIENT_TARGETS[$c]
        if (-not (Test-Path $t.path)) { continue }
        try {
            if ($t.format -eq "toml") {
                $raw = Get-Content $t.path -Raw
                $stripped = [regex]::Replace($raw, '(?ms)^\[mcp_servers\.revit-\d{4}\]\s*.*?(?=^\[|\z)', '')
                if ($stripped -ne $raw) {
                    Copy-Item $t.path "$($t.path).bak-$(Get-Date -Format yyyyMMdd-HHmmss)" -Force
                    ($stripped.TrimEnd() + "`r`n") | Set-Content $t.path -Encoding UTF8
                    Good "$($t.label): removed revit-* tables"
                }
            }
            else {
                $raw = Get-Content $t.path -Raw | ConvertFrom-Json
                $servers = $raw.mcpServers
                if ($servers) {
                    $toRemove = @($servers.PSObject.Properties.Name | Where-Object { $_ -match '^revit-\d{4}$' })
                    if ($toRemove.Count -gt 0) {
                        Copy-Item $t.path "$($t.path).bak-$(Get-Date -Format yyyyMMdd-HHmmss)" -Force
                        foreach ($k in $toRemove) { $servers.PSObject.Properties.Remove($k) }
                        # PS 5.1 collapses single-element arrays; keep surviving args as arrays.
                        $json = $raw | ConvertTo-Json -Depth 8
                        [regex]::Replace($json, '("args":\s*)"([^"]*)"', '$1["$2"]') | Set-Content $t.path -Encoding UTF8
                        Good "$($t.label): removed $($toRemove -join ', ')"
                    }
                }
            }
        }
        catch { Warn "$($t.label): could not parse config - left untouched." }
    }
}
Write-Host ""

$logDir = Join-Path $env:LOCALAPPDATA "RevitMCP\logs"
if (Test-Path $logDir) {
    Write-Host "Diagnostic logs were left in place (metadata only):" -ForegroundColor Yellow
    Write-Host "  $logDir   (delete with: Remove-Item '$logDir' -Recurse -Force)"
}
Write-Host "Uninstall complete. Restart Revit for it to take effect." -ForegroundColor Cyan
