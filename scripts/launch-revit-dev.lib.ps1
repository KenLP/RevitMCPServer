# Pure decision logic for launch-revit-dev.ps1 - no Revit, no windows, no network, so
# scripts/tests/launch-revit-dev.tests.ps1 can run it anywhere. Dot-source it.

# What the launched Revit is doing right now, from the three signals the launcher has: the process
# it started, that process's own journal, and the add-in. Precedence matters: a modal dialog also
# silences the journal (measured: nothing is written while "Opening Worksets" waits), so it must win
# over "stalled", and a silent journal only counts as a stall while nothing is ready yet.
#   starting -> addin_up (/health answers) -> ready (the model is the active document)
#   waiting_dialog: the process shows a modal dialog
#   stalled: journal silent for >= StallSec, no dialog, not ready
#   exited: the process is gone
function Get-LaunchState {
    param(
        [bool]$Exited,
        [bool]$Ready,
        [bool]$HealthUp,
        [int]$DialogCount,
        [Nullable[double]]$JournalIdleSec,
        [int]$StallSec
    )
    if ($Exited) { return "exited" }
    if ($Ready) { return "ready" }
    if ($DialogCount -gt 0) { return "waiting_dialog" }
    if ($null -ne $JournalIdleSec -and $StallSec -gt 0 -and $JournalIdleSec -ge $StallSec) { return "stalled" }
    if ($HealthUp) { return "addin_up" }
    return "starting"
}

# -DialogRules: a JSON array (inline or a file path) of
#   { "titleContains": "...", "textContains": "...", "button": "OK" }    click that button (BM_CLICK)
#   { "titleContains": "...", "action": "ignore" }                       leave it, it is expected (progress windows)
#   { "textContains": "...",  "action": "halt" }                         stop now with exit code 7
# At least one of titleContains / textContains; exactly one of button / action. Matching is
# case-insensitive substring; the first matching rule wins.
function ConvertTo-DialogRules {
    param([string]$Spec)
    if ([string]::IsNullOrWhiteSpace($Spec)) { return @() }
    $json = $Spec
    if ($Spec.TrimStart() -notmatch '^[\[\{]') {
        if (-not (Test-Path -LiteralPath $Spec)) { throw "DialogRules: '$Spec' is neither JSON nor an existing file" }
        $json = Get-Content -LiteralPath $Spec -Raw
    }
    $parsed = $json | ConvertFrom-Json
    $rules = @()
    $i = 0
    foreach ($r in @($parsed)) {
        $title = [string]$r.titleContains
        $text = [string]$r.textContains
        $button = [string]$r.button
        $action = ([string]$r.action).ToLowerInvariant()
        if (-not $title -and -not $text) { throw "DialogRules[$i]: needs titleContains and/or textContains" }
        if ([bool]$button -eq [bool]$action) { throw "DialogRules[$i]: needs exactly one of button / action" }
        if ($action -and $action -notin @("ignore", "halt")) { throw "DialogRules[$i]: action must be ignore or halt" }
        if ($button) { $action = "button" }
        $rules += [pscustomobject]@{ index = $i; titleContains = $title; textContains = $text; button = $button; action = $action }
        $i++
    }
    # Unrolled on purpose: callers write @(ConvertTo-DialogRules ...) + more, and a comma-wrapped
    # array would land there as ONE nested element whose properties are arrays.
    return $rules
}

# Appended after the caller's rules (theirs win). "Model Upgrade" is a progress window (#32770)
# whose only button is "Cancel Upgrade" - it never waits for input, and clicking it would cancel the
# upgrade. Measured on Revit 2027 opening a 2023 model: no DialogBoxShowing event for it either.
$DefaultDialogRules = @(
    [pscustomobject]@{ index = -1; titleContains = "Model Upgrade"; textContains = ""; button = ""; action = "ignore" }
)

function Find-DialogRule {
    param([string]$Title, [string]$Text, [object[]]$Rules)
    foreach ($r in @($Rules)) {
        if (-not $r) { continue }
        # IndexOf, not -like: a dialog title may hold wildcard characters such as [ ].
        if ($r.titleContains -and ([string]$Title).IndexOf($r.titleContains, [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
        if ($r.textContains -and ([string]$Text).IndexOf($r.textContains, [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
        return $r
    }
    return $null
}

# Journal files carry the writing process in a queue name: "... main queue Session<PID>_P0MainQueue"
# (verified on Revit 2027). Several Revit sessions write journals side by side, so the launcher
# picks the one naming its own PID.
function Test-JournalOwner {
    param([string]$HeaderText, [int]$ProcessId)
    return [bool]($HeaderText -match ("\bSession{0}_" -f $ProcessId))
}

# Exit codes (0-4 unchanged from the original launcher).
$LaunchExit = @{
    Ready          = 0
    Failed         = 1   # bad arguments, Revit missing, Revit exited
    Timeout        = 2
    ForeignAddin   = 3   # another add-in's unsigned prompt is waiting for the user
    NotResponding  = 4
    OpenFailed     = 5   # open_document returned an error (or the add-in lacks it)
    UnknownDialog  = 6   # a dialog no rule covers is waiting; nothing was clicked
    HaltDialog     = 7   # a "halt" rule matched
    Stalled        = 8   # journal silent, no dialog, restarts exhausted
}
