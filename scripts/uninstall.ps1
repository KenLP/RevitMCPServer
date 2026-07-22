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

.PARAMETER KeepClaudeConfig
Do not touch the Claude Desktop config.

.PARAMETER ClaudeConfigPath
Override the Claude config path. Default: %APPDATA%\Claude\claude_desktop_config.json

.PARAMETER ServerInstallDir
Where the MCP server was installed. Default: %LOCALAPPDATA%\RevitMCPServer
#>
param(
    [int[]]$RevitVersions,
    [switch]$KeepClaudeConfig,
    [string]$ClaudeConfigPath = "$env:APPDATA\Claude\claude_desktop_config.json",
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

# -- 3. Clean the Claude Desktop config ----------------------------------------
Info "[3/3] Cleaning Claude Desktop config"
if ($KeepClaudeConfig) {
    Warn "Skipping (-KeepClaudeConfig)."
}
elseif (-not (Test-Path $ClaudeConfigPath)) {
    Warn "No Claude config at $ClaudeConfigPath"
}
else {
    try {
        $raw = Get-Content $ClaudeConfigPath -Raw | ConvertFrom-Json
        $servers = $raw.mcpServers
        if ($servers) {
            $toRemove = @($servers.PSObject.Properties.Name | Where-Object { $_ -match '^revit-\d{4}$' })
            if ($toRemove.Count -gt 0) {
                $backup = "$ClaudeConfigPath.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
                Copy-Item $ClaudeConfigPath $backup -Force
                Good "Backed up -> $backup"
                foreach ($k in $toRemove) { $servers.PSObject.Properties.Remove($k) }
                $raw | ConvertTo-Json -Depth 8 | Set-Content $ClaudeConfigPath -Encoding UTF8
                Good "Removed: $($toRemove -join ', ')"
            }
            else { Warn "No revit-<ver> entries to remove." }
        }
    }
    catch { Warn "Could not parse Claude config - left untouched." }
}
Write-Host ""

$logDir = Join-Path $env:LOCALAPPDATA "RevitMCP\logs"
if (Test-Path $logDir) {
    Write-Host "Diagnostic logs were left in place (metadata only):" -ForegroundColor Yellow
    Write-Host "  $logDir   (delete with: Remove-Item '$logDir' -Recurse -Force)"
}
Write-Host "Uninstall complete. Restart Revit for it to take effect." -ForegroundColor Cyan
