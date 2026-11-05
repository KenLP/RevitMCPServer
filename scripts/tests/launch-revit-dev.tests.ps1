# Tests for the pure decision logic of launch-revit-dev.ps1. No Revit, no Pester needed:
#   powershell -NoProfile -File scripts/tests/launch-revit-dev.tests.ps1
# Exits 1 when any assertion fails.
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\launch-revit-dev.lib.ps1")

$script:failed = 0
$script:passed = 0
function Check([string]$name, $actual, $expected) {
    if ("$actual" -ceq "$expected") { $script:passed++ }
    else { $script:failed++; Write-Host "FAIL $name`n  expected: $expected`n  actual:   $actual" }
}
function Throws([string]$name, [scriptblock]$block, [string]$like) {
    try { & $block; $script:failed++; Write-Host "FAIL $name (no exception)" }
    catch { Check $name ($_.Exception.Message -like "*$like*") $true }
}

# --- Get-LaunchState --------------------------------------------------------
function State($o) {
    $p = @{ Exited = $false; Ready = $false; HealthUp = $false; DialogCount = 0; JournalIdleSec = $null; StallSec = 180 }
    foreach ($k in $o.Keys) { $p[$k] = $o[$k] }
    Get-LaunchState @p
}
Check "nothing yet"                    (State @{})                                              "starting"
Check "health answers"                 (State @{ HealthUp = $true })                            "addin_up"
Check "ready wins over health"         (State @{ HealthUp = $true; Ready = $true })             "ready"
Check "exited wins over everything"    (State @{ Exited = $true; Ready = $true; DialogCount = 1 }) "exited"
Check "dialog"                         (State @{ HealthUp = $true; DialogCount = 1 })           "waiting_dialog"
# The case that was misread as a hang: journal silent for minutes BECAUSE a dialog waits.
Check "silent journal + dialog"        (State @{ HealthUp = $true; DialogCount = 1; JournalIdleSec = 600 }) "waiting_dialog"
Check "silent journal, no dialog"      (State @{ HealthUp = $true; JournalIdleSec = 181 })      "stalled"
Check "silent at threshold"            (State @{ JournalIdleSec = 180 })                        "stalled"
Check "below threshold"                (State @{ JournalIdleSec = 179.9 })                      "starting"
Check "journal unknown never stalls"   (State @{ JournalIdleSec = $null })                      "starting"
Check "StallSec 0 disables stall"      (State @{ JournalIdleSec = 9999; StallSec = 0 })         "starting"
Check "ready beats silent journal"     (State @{ Ready = $true; JournalIdleSec = 9999 })        "ready"

# --- ConvertTo-DialogRules --------------------------------------------------
$r = ConvertTo-DialogRules '[{"titleContains":"Opening Worksets","button":"OK"},{"titleContains":"Model Upgrade","action":"ignore"},{"textContains":"sign in","action":"HALT"}]'
Check "rule count"       $r.Count 3
Check "button action"    $r[0].action "button"
Check "button caption"   $r[0].button "OK"
Check "ignore"           $r[1].action "ignore"
Check "halt lower-cased" $r[2].action "halt"
Check "single object accepted" @(ConvertTo-DialogRules '{"titleContains":"X","action":"ignore"}').Count 1
Check "empty spec"       @(ConvertTo-DialogRules "").Count 0
$example = ConvertTo-DialogRules (Join-Path $PSScriptRoot "..\launch-revit-dev.rules.example.json")
Check "example file parses" $example.Count 3
Throws "no condition"     { ConvertTo-DialogRules '[{"button":"OK"}]' } "titleContains"
Throws "button and action" { ConvertTo-DialogRules '[{"titleContains":"X","button":"OK","action":"halt"}]' } "exactly one"
Throws "neither"          { ConvertTo-DialogRules '[{"titleContains":"X"}]' } "exactly one"
Throws "bad action"       { ConvertTo-DialogRules '[{"titleContains":"X","action":"click"}]' } "ignore or halt"
Throws "missing file"     { ConvertTo-DialogRules 'C:\no\such\rules.json' } "neither JSON"

# --- Find-DialogRule --------------------------------------------------------
$rules = ConvertTo-DialogRules '[{"titleContains":"Opening Worksets","button":"OK"},{"titleContains":"Revit","textContains":"closed views","button":"Yes"},{"textContains":"[x]","action":"ignore"}]'
Check "title match, case-insensitive" (Find-DialogRule -Title "opening worksets" -Text "" -Rules $rules).button "OK"
Check "both conditions needed"        (Find-DialogRule -Title "Revit" -Text "other" -Rules $rules) $null
Check "both conditions met"           (Find-DialogRule -Title "Autodesk Revit 2027" -Text "Search Closed Views?" -Rules $rules).button "Yes"
Check "wildcard chars are literal"    (Find-DialogRule -Title "t" -Text "item [x] done" -Rules $rules).action "ignore"
Check "wildcard chars not patterns"   (Find-DialogRule -Title "t" -Text "item x done" -Rules $rules) $null
Check "no rules"                      (Find-DialogRule -Title "a" -Text "b" -Rules @()) $null

# The launcher's exact composition: user rules (several) + defaults. A nested array here once made
# every rule miss, so "Opening Worksets" was reported as unknown instead of clicked.
$composed = @(ConvertTo-DialogRules (Join-Path $PSScriptRoot "..\launch-revit-dev.rules.example.json")) + $DefaultDialogRules
Check "composed: flat list"              $composed.Count 4
Check "composed: Opening Worksets -> OK" (Find-DialogRule -Title "Opening Worksets" -Text "Custom1`nOK`nCancel" -Rules $composed).button "OK"
Check "composed: halt on sign in"        (Find-DialogRule -Title "Autodesk" -Text "Please sign in" -Rules $composed).action "halt"
Check "composed: single rule stays flat" (@(ConvertTo-DialogRules '[{"titleContains":"A","action":"ignore"}]') + $DefaultDialogRules).Count 2
$withDefaults = @($rules) + $DefaultDialogRules
Check "model upgrade ignored by default" (Find-DialogRule -Title "Model Upgrade" -Text "Cancel Upgrade" -Rules $withDefaults).action "ignore"
$mine = @(ConvertTo-DialogRules '[{"titleContains":"Model Upgrade","action":"halt"}]') + $DefaultDialogRules
Check "caller rule wins over default"    (Find-DialogRule -Title "Model Upgrade" -Text "" -Rules $mine).action "halt"

# --- Test-JournalOwner ------------------------------------------------------
$head = "'C 03-Nov-2026 16:22:41.637;   1:< logging main created main queue Session45328_P0MainQueue"
Check "own journal"          (Test-JournalOwner -HeaderText $head -ProcessId 45328) $true
Check "other pid"            (Test-JournalOwner -HeaderText $head -ProcessId 4532) $false
Check "pid prefix not enough" (Test-JournalOwner -HeaderText $head -ProcessId 5328) $false

# --- exit codes stay stable ---------------------------------------------------
Check "legacy codes" ("{0}{1}{2}{3}{4}" -f $LaunchExit.Ready, $LaunchExit.Failed, $LaunchExit.Timeout, $LaunchExit.ForeignAddin, $LaunchExit.NotResponding) "01234"

Write-Host "launch-revit-dev: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 }
