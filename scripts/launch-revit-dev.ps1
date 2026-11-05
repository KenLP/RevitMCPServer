#requires -Version 5.1
<#
.SYNOPSIS
  Start Revit for a dev/test session, open a model, and wait until the Revit MCP addin is ready -
  without anyone clicking, and without waiting blind.

.DESCRIPTION
  1. Refuses to start when a Revit of the same version is already running (no duplicate instances
     fighting over the addin port), unless -AllowExisting.
  2. Launches Revit and follows THAT process (by PID) only.
  3. Clicks "Load Once" on the "Security - Unsigned Add-In" prompt ONLY when it names -AddinName and
     points at RevitMCPAddin.dll in this user's Addins folder (BM_CLICK, no foreground needed). A
     prompt for any other addin is left alone and reported (exit 3). Never "Always Load".
  4. Opens -Model:
       -OpenVia addin (default): launch Revit bare, wait for /health, then call the addin's
         open_document {path, worksets, detach}. Passing a workset configuration means Revit never
         shows "Opening Worksets".
       -OpenVia commandline: Revit.exe "<model>" (the original behaviour). A workshared model then
         shows "Opening Worksets", answered by the addin's revit-mcp-dialog-policy.json or by a
         -DialogRules rule.
  5. Classifies every tick: starting -> addin_up -> ready, or waiting_dialog (the process shows a
     modal dialog: title, text and buttons are reported), stalled (its journal has been silent for
     -StallSec with no dialog), exited. A silent journal alone is NOT a hang: Revit writes nothing
     while a dialog waits for input.
  6. Dialogs: -DialogRules (see launch-revit-dev.lib.ps1) click a button, ignore, or halt (exit 7).
     A dialog no rule covers is never clicked: after -DialogGraceSec it is reported (exit 6).
     Built in, after yours: the "Model Upgrade" progress window is ignored (its button cancels).
  7. stalled -> Stop-Process -Id <pid> (never by name: other Revit sessions may belong to the user)
     and relaunch, at most -Restart times; then exit 8.
  8. Prints one JSON line: phase times (tHealthSec, tOpenSec, tReadySec), dialogs, restarts, the
     journal path, and journalTail when it fails.

  /health is answered on the listener thread; /mcp waits for Revit's main thread. Probes of /mcp
  are therefore spaced (>= 10 s) so orphaned calls cannot pile up and trip the addin's in-flight cap.

  Exit codes: 0 ready, 1 failed/exited, 2 timeout, 3 another addin's unsigned prompt, 4 not
  responding > 120 s, 5 open_document failed, 6 unknown dialog, 7 halt rule, 8 stalled.

.PARAMETER WebView2DebugPort
  Opens a Chrome DevTools Protocol port on 127.0.0.1 for every WebView2 inside Revit. Off by default;
  the port dies with the Revit process.

.EXAMPLE
  powershell -File scripts/launch-revit-dev.ps1 -Version 2027 -Model "C:\models\a.rvt"
  powershell -File scripts/launch-revit-dev.ps1 -Model "C:\models\a.rvt" -OpenVia commandline -DialogRules scripts\launch-revit-dev.rules.example.json
  powershell -File scripts/launch-revit-dev.ps1 -Model "C:\models\a.rvt" -StallSec 180 -Restart 1
#>
[CmdletBinding()]
param(
    [int]$Version = 2027,
    [string]$Model,
    [ValidateSet("addin", "commandline")]
    [string]$OpenVia = "addin",
    [ValidateSet("all", "none", "last_viewed")]
    [string]$Worksets = "all",
    [ValidateSet("none", "preserve", "discard")]
    [string]$Detach = "none",
    [string]$AddinName = "Revit MCP Addin",
    [int]$TimeoutSec = 300,
    [int]$StallSec = 180,
    [int]$Restart = 0,
    [string]$DialogRules,
    [int]$DialogGraceSec = 30,
    [int]$WebView2DebugPort = 0,
    [switch]$AllowExisting
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "launch-revit-dev.lib.ps1")

$revitExe = "C:\Program Files\Autodesk\Revit $Version\Revit.exe"
$addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Version"
$expectedDll = Join-Path $addinsDir "RevitMCPAddin.dll"
$tokenPath = Join-Path $addinsDir "revit-mcp-token.txt"
$journalDir = Join-Path $env:LOCALAPPDATA "Autodesk\Revit\Autodesk Revit $Version\Journals"
$port = 7891 + ($Version - 2026)
if ($env:REVIT_MCP_PORT) { $port = [int]$env:REVIT_MCP_PORT }
$base = "http://127.0.0.1:$port"
$t0 = Get-Date

# Result accumulated over every attempt; Out-Result adds the exit code and prints it.
$result = [ordered]@{
    state = "starting"; pid = $null; port = $port; model = $Model; openVia = $OpenVia
    tHealthSec = $null; tOpenSec = $null; openMs = $null; tReadySec = $null; readyAfterSec = $null
    restarts = 0; clicked = @(); dialogs = @(); addinDialogs = @(); journal = $null
    webView2DebugPort = $WebView2DebugPort
}

function Get-JournalTail([int]$lines = 40) {
    if (-not $result.journal) { return $null }
    try {
        $fs = [IO.File]::Open($result.journal, 'Open', 'Read', 'ReadWrite')
        try { $all = (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
        return (($all -split "`r?`n") | Select-Object -Last $lines) -join "`n"
    } catch { return $null }
}

function Out-Result([int]$code, [hashtable]$extra = @{}) {
    foreach ($k in $extra.Keys) { $result[$k] = $extra[$k] }
    $result["exitCode"] = $code
    if ($code -ne 0 -and -not $result.Contains("journalTail")) { $result["journalTail"] = Get-JournalTail }
    $result | ConvertTo-Json -Depth 6 -Compress | Write-Output
    exit $code
}

function Since([datetime]$from) { [math]::Round(((Get-Date) - $from).TotalSeconds, 1) }

if (-not (Test-Path $revitExe)) { Out-Result 1 @{ error = "Revit $Version not found at $revitExe" } }
if ($Model -and -not (Test-Path $Model)) { Out-Result 1 @{ error = "Model not found: $Model" } }
try { $rules = @(ConvertTo-DialogRules $DialogRules) + $DefaultDialogRules } catch { Out-Result 1 @{ error = $_.Exception.Message } }

$existing = Get-Process Revit -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -ieq $revitExe }
if ($existing -and -not $AllowExisting) {
    Out-Result 1 @{ error = "Revit $Version already running"; pids = @($existing.Id) }
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Net.Http
if (-not ("RevitDevWin2" -as [type])) {
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class RevitDevWin2 {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    const uint BM_CLICK = 0x00F5;
    const uint GW_OWNER = 4;

    public static string Text(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }
    public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
    public static IntPtr Owner(IntPtr h) { return GetWindow(h, GW_OWNER); }

    public static IntPtr[] TopWindows(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list.ToArray();
    }

    public static string[] Buttons(IntPtr dlg) {
        var list = new List<string>();
        EnumChildWindows(dlg, (h, l) => { if (Cls(h) == "Button" && IsWindowVisible(h)) { var t = Text(h).Replace("&", ""); if (t.Length > 0) list.Add(t); } return true; }, IntPtr.Zero);
        return list.ToArray();
    }

    public static IntPtr FindButton(IntPtr dlg, string caption) {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(dlg, (h, l) => { if (Cls(h) == "Button" && Text(h).Replace("&", "") == caption) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }

    public static void Click(IntPtr button) { SendMessage(button, BM_CLICK, IntPtr.Zero, IntPtr.Zero); }
}
"@
}

# Revit draws some dialog text (unsigned-addin prompt, task dialogs) in a DirectUIHWND that
# GetWindowText cannot read; UI Automation can. Cached per window: UIA is slow on a busy process.
$uiaCache = @{}
function Get-DialogText([IntPtr]$hwnd) {
    $key = $hwnd.ToInt64()
    if ($uiaCache.ContainsKey($key)) { return $uiaCache[$key] }
    $text = ""
    try {
        $el = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                           [System.Windows.Automation.Condition]::TrueCondition)
        $text = (($all | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join "`n")
    } catch { }
    $uiaCache[$key] = $text
    return $text
}

function Get-UiaButtons([IntPtr]$hwnd) {
    try {
        $el = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        return @($el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
    } catch { return @() }
}

# Win32 button first (works without focus); UIA Invoke for task-dialog buttons that are not windows.
function Invoke-DialogButton([IntPtr]$hwnd, [string]$caption) {
    $btn = [RevitDevWin2]::FindButton($hwnd, $caption)
    if ($btn -ne [IntPtr]::Zero) { [RevitDevWin2]::Click($btn); return $true }
    foreach ($b in Get-UiaButtons $hwnd) {
        if ($b.Current.Name -eq $caption) {
            try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { }
        }
    }
    return $false
}

# Modal dialogs of the process: standard dialog windows (#32770 - Revit's own dialogs and task
# dialogs), or a window owned by a main window that is disabled (WPF modals) AND showing a button -
# Revit also keeps button-less owned helper windows (a WinForms "<guid>Monitor") that are no dialog.
$hasButtons = @{}
function Test-HasButtons([IntPtr]$hwnd) {
    $key = $hwnd.ToInt64()
    if (-not $hasButtons.ContainsKey($key)) {
        $hasButtons[$key] = ([RevitDevWin2]::Buttons($hwnd).Count -gt 0) -or (@(Get-UiaButtons $hwnd).Count -gt 0)
    }
    return $hasButtons[$key]
}
function Get-ModalDialogs($proc) {
    $proc.Refresh()
    $main = $proc.MainWindowHandle
    $mainDisabled = ($main -ne [IntPtr]::Zero) -and -not [RevitDevWin2]::IsWindowEnabled($main)
    $out = @()
    foreach ($h in [RevitDevWin2]::TopWindows([uint32]$proc.Id)) {
        if ($h -eq $main) { continue }
        $cls = [RevitDevWin2]::Cls($h)
        $owned = [RevitDevWin2]::Owner($h) -ne [IntPtr]::Zero
        if ($cls -ne "#32770" -and -not ($owned -and $mainDisabled -and (Test-HasButtons $h))) { continue }
        $out += [pscustomobject]@{ hwnd = $h; title = [RevitDevWin2]::Text($h); cls = $cls }
    }
    return $out
}

function Get-Token {
    try { return (Get-Content $tokenPath -Raw).Trim() } catch { return $null }
}

function Test-Health {
    try { $h = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 3; return [bool]$h.ok } catch { return $false }
}

function Get-AddinDialogs([long]$since) {
    $token = Get-Token
    if (-not $token) { return @() }
    try {
        $r = Invoke-RestMethod -Uri "$base/dialogs?since=$since" -TimeoutSec 3 -Headers @{ Authorization = "Bearer $token" }
        if ($r.ok -and $r.data.enabled) { return @($r.data.dialogs) }
    } catch { }
    return @()
}

# Commandline mode readiness: the model is the active document. Spaced probes (see .DESCRIPTION).
$docProbeEverySec = 10
$lastDocProbe = [datetime]::MinValue
function Test-DocReady {
    if (((Get-Date) - $script:lastDocProbe).TotalSeconds -lt $docProbeEverySec) { return $false }
    $script:lastDocProbe = Get-Date
    try {
        $r = Invoke-RestMethod -Uri "$base/mcp" -Method Post -TimeoutSec 30 -ContentType "application/json" `
            -Headers @{ Authorization = "Bearer $(Get-Token)" } -Body '{"command":"get_document_info","params":{}}'
        return ($r.ok -and $r.data.title -eq [IO.Path]::GetFileNameWithoutExtension($Model))
    } catch { return $false }
}

# Addin mode: open_document runs for as long as the model takes, so it is sent asynchronously and
# the loop keeps watching dialogs and the journal meanwhile.
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromSeconds([math]::Max($TimeoutSec, 60))
function Start-OpenDocument {
    $body = @{ command = "open_document"; params = @{ path = (Resolve-Path $Model).Path; worksets = $Worksets; detach = $Detach } } |
        ConvertTo-Json -Depth 4 -Compress
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$base/mcp")
    $req.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", (Get-Token))
    $req.Content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "application/json")
    return $http.SendAsync($req)
}

function Find-OwnJournal([int]$processId, [datetime]$after) {
    if (-not (Test-Path $journalDir)) { return $null }
    foreach ($f in Get-ChildItem $journalDir -Filter "journal.*.txt" | Where-Object { $_.LastWriteTime -ge $after.AddSeconds(-5) } |
                   Sort-Object LastWriteTime -Descending) {
        try {
            $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
            try {
                $buf = New-Object byte[] 131072
                $n = $fs.Read($buf, 0, $buf.Length)
                $head = [Text.Encoding]::Default.GetString($buf, 0, $n)
            } finally { $fs.Dispose() }
            if (Test-JournalOwner -HeaderText $head -ProcessId $processId) { return $f.FullName }
        } catch { }
    }
    return $null
}

function Start-Revit {
    # Launch through the shell so Revit gets its own console streams (with UseShellExecute=false it
    # inherits ours and floods it with Dynamo/host logs). The WebView2 variable lives only in this
    # PowerShell process's environment, which Revit inherits; it is removed right after launch.
    # WebView2 binds the debug port to loopback (127.0.0.1 / ::1) only.
    if ($WebView2DebugPort -gt 0) {
        $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$WebView2DebugPort"
    }
    $startArgs = @{ FilePath = $revitExe; PassThru = $true }
    if ($Model -and $OpenVia -eq "commandline") { $startArgs.ArgumentList = '"' + $Model + '"' }
    $p = Start-Process @startArgs
    Remove-Item Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -ErrorAction SilentlyContinue
    return $p
}

$deadline = $t0.AddSeconds($TimeoutSec)
$foreign = @()

:attempt while ($true) {
    $proc = Start-Revit
    $started = Get-Date
    $result.pid = $proc.Id
    $result.journal = $null
    $notRespondingSince = $null
    $healthAt = $null
    $openTask = $null
    $openSentAt = $null
    $seen = @{}          # hwnd -> dialog record (first seen, action taken)
    $addinSeq = 0        # /dialogs sequence numbers restart with each Revit process
    $ready = $false

    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
            $result.state = "exited"
            Out-Result $LaunchExit.Failed @{ error = "Revit exited (code $($proc.ExitCode))" }
        }

        # A load that hangs (e.g. a broken Steel Connections LocalDB instance) shows as "Not Responding"
        # for minutes; report (or restart) after 120 s instead of waiting out the whole timeout.
        $proc.Refresh()
        if ($proc.Responding) { $notRespondingSince = $null }
        elseif (-not $notRespondingSince) { $notRespondingSince = Get-Date }
        elseif (((Get-Date) - $notRespondingSince).TotalSeconds -gt 120) {
            if ($result.restarts -lt $Restart) {
                $result.restarts++
                Stop-Process -Id $proc.Id -Force; $null = $proc.WaitForExit(30000)
                continue attempt
            }
            $result.state = "not_responding"
            Out-Result $LaunchExit.NotResponding @{ error = "Revit not responding for 120 s (check the journal)" }
        }

        if (-not $result.journal) { $result.journal = Find-OwnJournal $proc.Id $started }

        # Dialogs of this process.
        $dialogs = @(Get-ModalDialogs $proc)
        $waiting = @()
        foreach ($d in $dialogs) {
            $key = $d.hwnd.ToInt64()
            if ($d.title -like "*Unsigned Add-In*") {
                $text = Get-DialogText $d.hwnd
                $isOurs = ($text -match [regex]::Escape($AddinName)) -and ($text -match [regex]::Escape($expectedDll))
                if ($isOurs) {
                    if (Invoke-DialogButton $d.hwnd "Load Once") {
                        $result.clicked += "Load Once: $AddinName"
                        Start-Sleep -Milliseconds 800
                    }
                } elseif ($foreign -notcontains $text) {
                    # Someone else's addin: never trust it on the user's behalf.
                    $foreign += $text
                    Write-Warning "Unsigned-addin prompt for another addin left for the user:`n$text"
                }
                continue
            }
            if (-not $seen.ContainsKey($key)) {
                $text = Get-DialogText $d.hwnd
                $buttons = @([RevitDevWin2]::Buttons($d.hwnd))
                if ($buttons.Count -eq 0) { $buttons = @(Get-UiaButtons $d.hwnd | ForEach-Object { $_.Current.Name }) }
                $rec = [ordered]@{ title = $d.title; text = $text; buttons = $buttons; atSec = (Since $t0); action = $null; clicked = $null }
                $seen[$key] = $rec
                $rule = Find-DialogRule -Title $d.title -Text $text -Rules $rules
                if ($rule) {
                    $rec.action = $rule.action
                    if ($rule.action -eq "button") {
                        if (Invoke-DialogButton $d.hwnd $rule.button) { $rec.clicked = $rule.button }
                        else { $rec.action = "button_not_found" }
                    }
                }
                $result.dialogs += [pscustomobject]$rec
                if ($rec.action -eq "halt") {
                    $result.state = "waiting_dialog"
                    Out-Result $LaunchExit.HaltDialog @{ error = "halt rule matched dialog '$($d.title)'" }
                }
            }
            $rec = $seen[$key]
            if ($rec.action -ne "ignore") { $waiting += $rec }
        }
        $unknown = @($waiting | Where-Object { -not $_.action -or $_.action -eq "button_not_found" })
        if ($unknown.Count -gt 0 -and ((Since $t0) - $unknown[0].atSec) -ge $DialogGraceSec) {
            $result.state = "waiting_dialog"
            $result.addinDialogs = @(Get-AddinDialogs 0)
            Out-Result $LaunchExit.UnknownDialog @{ error = "dialog '$($unknown[0].title)' waits for input and no rule covers it (nothing clicked)" }
        }

        # The addin.
        if (-not $healthAt -and (Test-Health)) { $healthAt = Get-Date; $result.tHealthSec = Since $t0 }
        if ($healthAt) {
            foreach ($e in Get-AddinDialogs $addinSeq) { $result.addinDialogs += $e; $addinSeq = [math]::Max($addinSeq, [long]$e.seq) }
        }
        if (-not $Model) { $ready = [bool]$healthAt }
        elseif ($OpenVia -eq "commandline") { $ready = $healthAt -and (Test-DocReady) }
        elseif ($healthAt) {
            if (-not $openTask -and (Get-Token)) { $openTask = Start-OpenDocument; $openSentAt = Get-Date }
            if ($openTask -and $openTask.IsCompleted) {
                $result.tOpenSec = Since $openSentAt
                if ($openTask.IsFaulted) {
                    Out-Result $LaunchExit.OpenFailed @{ error = "open_document transport error: $($openTask.Exception.InnerException.Message)" }
                }
                $text = $openTask.Result.Content.ReadAsStringAsync().Result
                $r = $text | ConvertFrom-Json
                if (-not $r.ok) {
                    $msg = "$($r.error.code): $($r.error.message)"
                    if ($r.error.code -eq "unknown_command") { $msg += " - this addin build has no open_document; use -OpenVia commandline" }
                    $result.state = "addin_up"
                    Out-Result $LaunchExit.OpenFailed @{ error = $msg }
                }
                $result.openMs = $r.data.openMs
                $result.openDialogs = @($r.data.dialogs)
                $result.alreadyOpen = $r.data.alreadyOpen
                $ready = $true
            }
        }

        $idle = $null
        if ($result.journal) { try { $idle = ((Get-Date) - (Get-Item $result.journal).LastWriteTime).TotalSeconds } catch { } }
        $result.state = Get-LaunchState -Exited $false -Ready $ready -HealthUp ([bool]$healthAt) `
            -DialogCount $waiting.Count -JournalIdleSec $idle -StallSec $StallSec

        if ($result.state -eq "ready") {
            $result.tReadySec = Since $t0
            $result.readyAfterSec = [int]$result.tReadySec
            Out-Result $LaunchExit.Ready
        }
        if ($result.state -eq "stalled") {
            if ($result.restarts -lt $Restart) {
                $result.restarts++
                $result.journalTailBeforeRestart = Get-JournalTail 15
                Stop-Process -Id $proc.Id -Force; $null = $proc.WaitForExit(30000)
                continue attempt
            }
            Out-Result $LaunchExit.Stalled @{ error = "journal silent for $StallSec s with no dialog and nothing ready" }
        }
        if ($foreign.Count -gt 0 -and ((Get-Date) - $started).TotalSeconds -gt 60) {
            Out-Result $LaunchExit.ForeignAddin @{ error = "blocked on another addin's unsigned prompt"; foreign = $foreign }
        }
        Start-Sleep -Seconds 2
    }
    break
}
Out-Result $LaunchExit.Timeout @{ error = "timeout after $TimeoutSec s (state: $($result.state))" }
