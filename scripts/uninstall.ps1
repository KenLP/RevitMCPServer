<#
.SYNOPSIS
Remove RevitMCPAddin from the per-user Revit Addins folder.

.PARAMETER RevitVersion
Target Revit version. Default: 2026.

.EXAMPLE
  .\uninstall.ps1 -RevitVersion 2026
#>
param(
    [ValidateSet("2025", "2026", "2027", "2028")]
    [string]$RevitVersion = "2026"
)

$addinsDir = "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion"
$targets = @(
    (Join-Path $addinsDir "RevitMCPAddin.dll"),
    (Join-Path $addinsDir "RevitMCPAddin.pdb"),
    (Join-Path $addinsDir "RevitMCP.Core.dll"),
    (Join-Path $addinsDir "RevitMCP.Core.pdb"),
    (Join-Path $addinsDir "RevitMCPAddin.addin"),
    (Join-Path $addinsDir "revit-mcp-token.txt")
)

$removed = 0
foreach ($t in $targets) {
    if (Test-Path $t) {
        Remove-Item $t -Force
        Write-Host "Removed: $t" -ForegroundColor Green
        $removed++
    }
}

if ($removed -eq 0) {
    Write-Host "Nothing to remove in $addinsDir" -ForegroundColor Yellow
} else {
    Write-Host ""
    Write-Host "Uninstall complete. Restart Revit $RevitVersion to take effect." -ForegroundColor Cyan
}

# Diagnostic logs are metadata-only and live outside the Addins folder, so they
# are not deleted implicitly — tell the user exactly where they are.
$logDir = Join-Path $env:LOCALAPPDATA "RevitMCP\logs"
if (Test-Path $logDir) {
    Write-Host ""
    Write-Host "Diagnostic logs were left in place (metadata only, no model data):" -ForegroundColor Yellow
    Write-Host "  $logDir"
    Write-Host "  Delete that folder to remove them: Remove-Item '$logDir' -Recurse -Force"
}

# The MCP server itself lives wherever it was extracted/cloned (it is not copied
# into the Addins folder), so removing it is a manual delete of that folder.
Write-Host ""
Write-Host "Note: the MCP server files (mcp-server/ or the cloned repo) are not" -ForegroundColor Yellow
Write-Host "removed by this script — delete that folder manually if no longer needed."
