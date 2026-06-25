#requires -Version 5.1
<#
.SYNOPSIS
  Live-Revit smoke test for the Revit MCP addin. Drives the real HTTP surface of a
  RUNNING Revit + addin and asserts behaviour that unit tests (which mock the Revit
  API) cannot cover: read surface, dry-run vs real writes, batch, P1 limits and
  observability.

.DESCRIPTION
  Requires Revit to be open with the addin loaded and a document open. Read tests are
  always safe. The one real-write test creates a uniquely-named Level and deletes it
  again (self-cleaning), so the open model is left as found; skip it with -NoWrites.

  Golden mode: -Snapshot writes a small JSON fingerprint of the current model;
  -Golden <file> compares the current model against a saved fingerprint and fails on
  drift. Use this against a FIXED fixture .rvt for regression detection.

.EXAMPLE
  pwsh scripts/smoke-test.ps1 -Version 2027
  pwsh scripts/smoke-test.ps1 -Version 2027 -Snapshot fixture.json   # capture golden
  pwsh scripts/smoke-test.ps1 -Version 2027 -Golden  fixture.json    # compare to golden
#>
[CmdletBinding()]
param(
    [int]$Version = 2027,
    [int]$Port = 0,
    [switch]$NoWrites,
    [string]$Snapshot,
    [string]$Golden
)

$ErrorActionPreference = 'Stop'
if ($Port -eq 0) { $Port = 7890 + ($Version - 2025) }  # 2025->7890, 2026->7891, 2027->7892
$base = "http://127.0.0.1:$Port"

$tokenPath = "$env:APPDATA\Autodesk\Revit\Addins\$Version\revit-mcp-token.txt"
if (-not (Test-Path $tokenPath)) { Write-Error "Token file not found: $tokenPath (is Revit $Version running?)"; exit 2 }
$token = (Get-Content $tokenPath -Raw).Trim()
$headers = @{ Authorization = "Bearer $token" }

$script:pass = 0; $script:fail = 0; $script:failures = @()

function Invoke-Mcp {
    param([string]$Command, [hashtable]$Params = @{}, [switch]$DryRun)
    $body = @{ command = $Command; params = $Params }
    if ($DryRun) { $body.dryRun = $true }
    Invoke-RestMethod -Uri "$base/mcp" -Method Post -Headers $headers `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 10)
}

function Test-Case {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        $script:pass++
        Write-Host "  PASS  $Name" -ForegroundColor Green
    } catch {
        $script:fail++
        $script:failures += $Name
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        Write-Host "        $($_.Exception.Message)" -ForegroundColor DarkGray
    }
}

function Assert { param($Cond, [string]$Msg) if (-not $Cond) { throw $Msg } }

# Returns the HTTP status code from a failed Invoke-RestMethod (for negative tests).
function Get-ErrorStatus { param($ErrorRecord)
    $resp = $ErrorRecord.Exception.Response
    if ($resp) { return [int]$resp.StatusCode }
    return 0
}

Write-Host "Revit MCP smoke test - R$Version @ $base" -ForegroundColor Cyan
Write-Host ("=" * 56)

# ── 1. Connectivity ──────────────────────────────────────────────────────────
Write-Host "`n[1] Connectivity"
Test-Case "ping reports active document" {
    $r = Invoke-Mcp ping
    Assert ($r.ok -eq $true) "ping not ok"
    Assert ($r.data.hasActiveDocument -eq $true) "no active document - open a model first"
}
Test-Case "health endpoint (no auth) returns version" {
    $r = Invoke-RestMethod -Uri "$base/health"
    Assert ($r.ok -eq $true) "health not ok"
    Assert ([string]::IsNullOrEmpty($r.version) -eq $false) "no version"
}
Test-Case "commands endpoint lists >= 79 commands" {
    $r = Invoke-RestMethod -Uri "$base/commands" -Headers $headers
    Assert ($r.data.count -ge 79) "only $($r.data.count) commands"
}
Test-Case "stats endpoint returns counters" {
    $r = Invoke-RestMethod -Uri "$base/stats" -Headers $headers
    Assert ($null -ne $r.data.totalRequests) "no totalRequests"
}

# ── 2. Read surface ──────────────────────────────────────────────────────────
Write-Host "`n[2] Read surface"
Test-Case "get_document_info has title"      { Assert ((Invoke-Mcp get_document_info).data.title) "no title" }
Test-Case "list_levels ok"                   { Assert ((Invoke-Mcp list_levels).ok -eq $true) "not ok" }
Test-Case "list_categories ok"               { Assert ((Invoke-Mcp list_categories).ok -eq $true) "not ok" }
Test-Case "get_views ok"                      { Assert ((Invoke-Mcp get_views).ok -eq $true) "not ok" }
Test-Case "get_model_health has scorecard"   {
    $r = Invoke-Mcp get_model_health
    Assert ($r.data.scorecard.grade) "no grade"
}
Test-Case "get_worksets has isWorkshared"    {
    $r = Invoke-Mcp get_worksets
    Assert ($null -ne $r.data.isWorkshared) "no isWorkshared"
}
Test-Case "list_elements pagination (total/hasMore/nextOffset, distinct pages)" {
    $p1 = Invoke-Mcp list_elements -Params @{ limit = 5; offset = 0 }
    Assert ($p1.ok -eq $true) "page1 not ok"
    Assert ($null -ne $p1.data.total) "no total field"
    if ($p1.data.total -gt 5) {
        Assert ($p1.data.hasMore -eq $true) "hasMore should be true when total>5"
        Assert ($p1.data.nextOffset -eq 5) "nextOffset=$($p1.data.nextOffset)"
        $p2 = Invoke-Mcp list_elements -Params @{ limit = 5; offset = 5 }
        Assert ($p1.data.elements[0].id -ne $p2.data.elements[0].id) "pages overlap (same first id)"
    }
}
$sched = (Invoke-Mcp get_views).data.views | Where-Object { $_.viewType -eq 'Schedule' } | Select-Object -First 1
if ($sched) {
    Test-Case "get_schedule_data reads rendered cells" {
        $r = Invoke-Mcp get_schedule_data -Params @{ scheduleId = [long]$sched.id; limit = 5 }
        Assert ($r.ok -eq $true) "not ok: $($r.error.message)"
        Assert ($r.data.totalColumns -gt 0) "no columns ($($r.data.totalColumns))"
    }
} else {
    Write-Host "  SKIP  get_schedule_data (no schedule in model)" -ForegroundColor Yellow
}

# ── 3. Observability (P1) ────────────────────────────────────────────────────
Write-Host "`n[3] Observability"
Test-Case "response carries a server-minted X-Request-Id" {
    $r = Invoke-WebRequest -UseBasicParsing -Uri "$base/mcp" -Method Post -Headers $headers `
        -ContentType 'application/json' -Body (@{command='ping';params=@{}} | ConvertTo-Json)
    Assert (-not [string]::IsNullOrEmpty($r.Headers['X-Request-Id'])) "no X-Request-Id header"
}
Test-Case "client-supplied X-Request-Id is echoed" {
    $h2 = $headers.Clone(); $h2['X-Request-Id'] = 'smoke-trace-001'
    $r = Invoke-WebRequest -UseBasicParsing -Uri "$base/mcp" -Method Post -Headers $h2 `
        -ContentType 'application/json' -Body (@{command='ping';params=@{}} | ConvertTo-Json)
    Assert ($r.Headers['X-Request-Id'] -eq 'smoke-trace-001') "id not echoed: $($r.Headers['X-Request-Id'])"
}
Test-Case "stats totalRequests increases after a call" {
    $before = (Invoke-RestMethod -Uri "$base/stats" -Headers $headers).data.totalRequests
    Invoke-Mcp ping | Out-Null
    $after = (Invoke-RestMethod -Uri "$base/stats" -Headers $headers).data.totalRequests
    Assert ($after -gt $before) "counter did not advance ($before -> $after)"
}

# ── 4. Dry-run write (no mutation) ───────────────────────────────────────────
Write-Host "`n[4] Dry-run write"
Test-Case "create_level dryRun does not commit" {
    $r = Invoke-Mcp create_level -Params @{ elevation = 333.0; name = "MCP_SMOKE_DRYRUN" } -DryRun
    Assert ($r.ok -eq $true) "not ok"
    Assert ($r.committed -eq $false) "dry-run reported committed=true"
}

# ── 5. Real write round-trip (self-cleaning) ─────────────────────────────────
if (-not $NoWrites) {
    Write-Host "`n[5] Real write round-trip (create -> verify -> delete)"
    $createdId = $null
    Test-Case "create_level -> get_element_info -> delete -> gone" {
        $name = "MCP_SMOKE_" + [DateTime]::Now.Ticks
        $c = Invoke-Mcp create_level -Params @{ elevation = 333.0; name = $name }
        Assert ($c.ok -eq $true) "create failed"
        $script:createdId = [long]$c.data.id
        Assert ($script:createdId -gt 0) "no id returned"

        $info = Invoke-Mcp get_element_info -Params @{ id = $script:createdId }
        Assert ($info.ok -eq $true) "created level not found by get_element_info"

        $del = Invoke-Mcp delete_elements -Params @{ ids = @($script:createdId) }
        Assert ($del.ok -eq $true) "delete failed"
        $script:createdId = $null

        $gone = $false
        try { $g = Invoke-Mcp get_element_info -Params @{ id = [long]$c.data.id }; $gone = ($g.ok -eq $false) }
        catch { $gone = $true }  # 404 throws
        Assert $gone "level still present after delete"
    }
    # Safety net: if the test threw mid-way, delete the orphan level.
    if ($script:createdId) {
        try { Invoke-Mcp delete_elements -Params @{ ids = @($script:createdId) } | Out-Null
              Write-Host "  (cleaned up orphan level $script:createdId)" -ForegroundColor Yellow } catch { }
    }
} else {
    Write-Host "`n[5] Real write round-trip  (skipped: -NoWrites)" -ForegroundColor Yellow
}

# ── 6. Batch ─────────────────────────────────────────────────────────────────
Write-Host "`n[6] Batch"
Test-Case "read-only batch of 2 pings returns count=2" {
    $body = @{ steps = @(@{command='ping';params=@{}}, @{command='ping';params=@{}}) } | ConvertTo-Json -Depth 6
    $r = Invoke-RestMethod -Uri "$base/mcp/batch" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    # Read-only batch nests its summary under data; write batch puts count at top level.
    $count = if ($null -ne $r.data.count) { $r.data.count } else { $r.count }
    Assert ($count -eq 2) "count=$count"
}

# ── 7. Limits (P1) ───────────────────────────────────────────────────────────
Write-Host "`n[7] Limits"
Test-Case "batch > 200 steps -> 400 too_many_steps" {
    $steps = 1..201 | ForEach-Object { @{ command = 'ping'; params = @{} } }
    try {
        Invoke-RestMethod -Uri "$base/mcp/batch" -Method Post -Headers $headers `
            -ContentType 'application/json' -Body (@{ steps = $steps } | ConvertTo-Json -Depth 6)
        throw "expected rejection but call succeeded"
    } catch {
        $status = Get-ErrorStatus $_
        Assert ($status -eq 400) "expected HTTP 400, got $status"
    }
}
Test-Case "body > 1MB -> 413 payload_too_large" {
    $big = 'x' * 1100000
    try {
        Invoke-RestMethod -Uri "$base/mcp" -Method Post -Headers $headers `
            -ContentType 'application/json' -Body (@{ command = 'ping'; params = @{ junk = $big } } | ConvertTo-Json)
        throw "expected rejection but call succeeded"
    } catch {
        $status = Get-ErrorStatus $_
        Assert ($status -eq 413) "expected HTTP 413, got $status"
    }
}

# ── 8. Golden snapshot / compare ─────────────────────────────────────────────
function Get-Fingerprint {
    $doc    = Invoke-Mcp get_document_info
    $levels = Invoke-Mcp list_levels
    $cats   = Invoke-Mcp list_categories
    $health = Invoke-Mcp get_model_health
    [ordered]@{
        title          = $doc.data.title
        levelCount     = $levels.data.count
        categoryCount  = $cats.data.count
        elementTotal   = $health.data.elements.total
        warningTotal   = $health.data.warnings.total
        healthGrade    = $health.data.scorecard.grade
    }
}

if ($Snapshot) {
    Write-Host "`n[8] Snapshot -> $Snapshot"
    (Get-Fingerprint | ConvertTo-Json) | Set-Content -Path $Snapshot -Encoding utf8
    Write-Host "  Saved fingerprint." -ForegroundColor Green
}
elseif ($Golden) {
    Write-Host "`n[8] Golden compare vs $Golden"
    if (-not (Test-Path $Golden)) { Write-Error "Golden file not found: $Golden"; exit 2 }
    $expected = Get-Content $Golden -Raw | ConvertFrom-Json
    $actual   = Get-Fingerprint
    foreach ($k in $actual.Keys) {
        Test-Case "golden:$k matches ($($expected.$k))" {
            Assert ("$($actual[$k])" -eq "$($expected.$k)") "expected '$($expected.$k)', got '$($actual[$k])'"
        }
    }
}

# ── 9. Dimensions & spot elevations (dry-run; skip if model lacks elements) ──
Write-Host "`n[9] Dimensions & spot elevations (dry-run)"

$grids = (Invoke-Mcp find_elements -Params @{ category = "OST_Grids"; limit = 5 }).data.elements
if ($grids.Count -ge 2) {
    Test-Case "create_aligned_dimension dryRun (grid+grid)" {
        $g0 = [long]$grids[0].id; $g1 = [long]$grids[1].id
        $r = Invoke-Mcp create_aligned_dimension -DryRun -Params @{
            references = @(@{ elementId = $g0 }, @{ elementId = $g1 })
            line = @{ start = @{ x = -20; y = 0; z = 0 }; end = @{ x = 20; y = 0; z = 0 } }
        }
        Assert ($r.ok -eq $true) "not ok: $($r.error.message)"
        Assert ($r.data.dimensionId -gt 0) "no dimensionId"
        Assert ($r.data.value -gt 0) "dimension value <= 0 ($($r.data.value))"
    }
} else {
    Write-Host "  SKIP  create_aligned_dimension (fewer than 2 grids in model)" -ForegroundColor Yellow
}

# create_spot_elevation is hidden from the MCP surface (ReferenceIntersector raycast
# finds no face on a temporary 3D view); not smoke-tested until a reliable approach lands.

# ── Summary ──────────────────────────────────────────────────────────────────
Write-Host "`n$("=" * 56)"
$total = $script:pass + $script:fail
Write-Host "Result: $script:pass/$total passed" -ForegroundColor ($(if ($script:fail -eq 0) { 'Green' } else { 'Red' }))
if ($script:fail -gt 0) {
    Write-Host "Failed: $($script:failures -join ', ')" -ForegroundColor Red
    exit 1
}
exit 0
