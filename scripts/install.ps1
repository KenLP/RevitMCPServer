<#
.SYNOPSIS
Install RevitMCPAddin into the per-user Revit Addins folder.

.DESCRIPTION
Copies RevitMCPAddin.dll and RevitMCPAddin.addin to
%APPDATA%\Autodesk\Revit\Addins\<RevitVersion>\

Run this script from the folder that contains RevitMCPAddin.dll, or from the
repo root after running "dotnet build -p:DeployToRevit=false".

.PARAMETER RevitVersion
Target Revit version. Default: 2026.

.PARAMETER DllSource
Directory containing RevitMCPAddin.dll. Default: auto-detected.

.EXAMPLE
  # From an extracted release zip:
  .\install.ps1 -RevitVersion 2026

  # From the repo root after a local build:
  .\scripts\install.ps1 -RevitVersion 2026
#>
param(
    [ValidateSet("2025", "2026", "2027", "2028")]
    [string]$RevitVersion = "2026",

    [string]$DllSource = ""
)

$ErrorActionPreference = "Stop"

# ── Locate DLL ────────────────────────────────────────────────────────────────

if (-not $DllSource) {
    # Candidates: same folder as this script, then common build outputs.
    $candidates = @(
        $PSScriptRoot,
        (Join-Path $PSScriptRoot "../src/RevitAddin/bin/Debug"),
        (Join-Path $PSScriptRoot "../src/RevitAddin/bin/Release")
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "RevitMCPAddin.dll")) {
            $DllSource = (Resolve-Path $c).Path
            break
        }
    }
}

if (-not $DllSource -or -not (Test-Path (Join-Path $DllSource "RevitMCPAddin.dll"))) {
    Write-Error "RevitMCPAddin.dll not found. Pass -DllSource <path> or build first."
    exit 1
}

# RevitMCP.Core.dll carries the command kernel; the addin type-loads against it
# and throws FileNotFoundException on start-up without it.
if (-not (Test-Path (Join-Path $DllSource "RevitMCP.Core.dll"))) {
    Write-Error "RevitMCP.Core.dll not found next to RevitMCPAddin.dll in '$DllSource'. The addin cannot load without it."
    exit 1
}

# Locate addin manifest (same folder, or repo root)
$addinFile = Join-Path $DllSource "RevitMCPAddin.addin"
if (-not (Test-Path $addinFile)) {
    $addinFile = Join-Path $PSScriptRoot "../src/RevitAddin/RevitMCPAddin.addin"
}
if (-not (Test-Path $addinFile)) {
    Write-Error "RevitMCPAddin.addin not found alongside DLL or in src/RevitAddin."
    exit 1
}

# ── Copy to Addins folder ─────────────────────────────────────────────────────

$addinsDir = "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion"
New-Item -ItemType Directory -Force -Path $addinsDir | Out-Null

Copy-Item (Join-Path $DllSource "RevitMCPAddin.dll") -Destination $addinsDir -Force
Copy-Item (Join-Path $DllSource "RevitMCP.Core.dll") -Destination $addinsDir -Force
Copy-Item $addinFile -Destination $addinsDir -Force
Write-Host "Installed addin to: $addinsDir" -ForegroundColor Green

# ── MCP server npm install (if mcp-server/ present next to this script) ───────

$mcpServerDir = Join-Path $PSScriptRoot "mcp-server"
if (Test-Path $mcpServerDir) {
    Write-Host "Running npm install --production in mcp-server/ ..." -ForegroundColor Cyan
    Push-Location $mcpServerDir
    npm install --production
    Pop-Location
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "npm install failed — MCP server may not work until dependencies are installed."
    } else {
        Write-Host "MCP server dependencies installed." -ForegroundColor Green
    }
}

# ── Show Claude Desktop config snippet ────────────────────────────────────────

$portMap = @{ "2025" = 7890; "2026" = 7891; "2027" = 7892; "2028" = 7893 }
$port = $portMap[$RevitVersion]

$indexPath = if (Test-Path (Join-Path $PSScriptRoot "mcp-server/dist/index.js")) {
    (Resolve-Path (Join-Path $PSScriptRoot "mcp-server/dist/index.js")).Path -replace "\\", "/"
} else {
    "C:/path/to/RevitMCPServer/src/McpServer/dist/index.js"
}

Write-Host ""
Write-Host "Add this to your Claude Desktop config (claude_desktop_config.json):" -ForegroundColor Yellow
Write-Host ""
Write-Host @"
{
  "mcpServers": {
    "revit-$RevitVersion": {
      "command": "node",
      "args": ["$indexPath"],
      "env": {
        "REVIT_MCP_VERSION": "$RevitVersion",
        "REVIT_MCP_PORT": "$port"
      }
    }
  }
}
"@ -ForegroundColor White

Write-Host ""
Write-Host "Restart Revit $RevitVersion to activate the addin." -ForegroundColor Cyan
