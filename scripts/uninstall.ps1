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
