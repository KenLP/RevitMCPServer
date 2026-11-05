using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.UI.Events;

namespace RevitMCPAddin;

/// <summary>
/// One rule of <c>revit-mcp-dialog-policy.json</c>: a dialog whose id equals <see cref="DialogId"/>
/// (when set) AND whose message contains <see cref="MessageContains"/> (when set) is answered with
/// <see cref="Result"/> through <c>DialogBoxShowingEventArgs.OverrideResult</c>.
/// </summary>
public sealed record DialogRule(string? DialogId, string? MessageContains, int Result);

/// <summary>Parsed <c>revit-mcp-dialog-policy.json</c>; <see cref="Errors"/> lists entries that were skipped or defaulted.</summary>
public sealed record DialogPolicySettings(
    bool Enabled,
    bool Log,
    string Directory,
    IReadOnlyList<DialogRule> Rules,
    IReadOnlyList<string> Errors)
{
    /// <summary>No policy file: the add-in does not subscribe to DialogBoxShowing at all.</summary>
    public static DialogPolicySettings Disabled { get; } =
        new(false, false, DialogPolicyConfig.DefaultDirectory, Array.Empty<DialogRule>(), Array.Empty<string>());

    /// <summary>Records dialogs, overrides none, writes no file (a command's own scoped capture).</summary>
    public static DialogPolicySettings RecordOnly { get; } =
        new(true, false, DialogPolicyConfig.DefaultDirectory, Array.Empty<DialogRule>(), Array.Empty<string>());
}

/// <summary>
/// Reads <c>%APPDATA%\Autodesk\Revit\Addins\{version}\revit-mcp-dialog-policy.json</c> (next to
/// <c>revit-mcp-audit.json</c>). No file = disabled, and the host must not subscribe to
/// DialogBoxShowing — Revit's dialogs then behave exactly as without the add-in.
/// <code>{ "enabled": true, "log": true, "dir": null,
///   "rules": [ { "dialogId": "TaskDialog_Unresolved_References", "result": 1002 },
///              { "messageContains": "…", "result": 1 } ] }</code>
/// A dialog no rule matches is only recorded, never answered: pressing a button blind can cancel
/// work (the only button of a progress dialog is Cancel).
/// </summary>
public static class DialogPolicyConfig
{
    public const string FileName = "revit-mcp-dialog-policy.json";

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitMCP", "dialogs");

    public static string PathFor(string revitVersion) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Autodesk", "Revit", "Addins", revitVersion, FileName);

    public static DialogPolicySettings Load(string revitVersion)
    {
        var path = PathFor(revitVersion);
        if (!File.Exists(path)) return DialogPolicySettings.Disabled;
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception ex)
        {
            return DialogPolicySettings.Disabled with { Errors = new[] { $"{FileName} could not be read: {ex.Message}" } };
        }
    }

    /// <summary>
    /// Pure parser (unit-tested). Null text = no file = disabled. Any present file enables the
    /// policy unless it says <c>"enabled": false</c>; invalid JSON leaves it disabled (a policy that
    /// cannot be read must not half-apply).
    /// </summary>
    public static DialogPolicySettings Parse(string? json)
    {
        if (json is null) return DialogPolicySettings.Disabled;

        var errors = new List<string>();
        var rules = new List<DialogRule>();
        bool enabled = true, log = true;
        var dir = DefaultDirectory;

        if (string.IsNullOrWhiteSpace(json))
            return new DialogPolicySettings(true, true, dir, rules, errors);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            return DialogPolicySettings.Disabled with
            {
                Errors = new[] { $"{FileName} is not valid JSON ({ex.Message}); dialog policy OFF" },
            };
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return DialogPolicySettings.Disabled with { Errors = new[] { $"{FileName} must be a JSON object; dialog policy OFF" } };

            enabled = Bool(root, "enabled", true, errors);
            log = Bool(root, "log", true, errors);

            if (root.TryGetProperty("dir", out var d) && d.ValueKind != JsonValueKind.Null)
            {
                if (d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString()))
                    dir = Environment.ExpandEnvironmentVariables(d.GetString()!.Trim());
                else errors.Add("\"dir\" must be a non-empty string or null; using the default folder");
            }

            if (root.TryGetProperty("rules", out var arr))
            {
                if (arr.ValueKind != JsonValueKind.Array)
                    errors.Add("\"rules\" must be an array; no rules applied");
                else
                {
                    var i = 0;
                    foreach (var r in arr.EnumerateArray())
                    {
                        var rule = ParseRule(r, i, errors);
                        if (rule is not null) rules.Add(rule);
                        i++;
                    }
                }
            }
        }
        return new DialogPolicySettings(enabled, log, dir, rules, errors);
    }

    private static DialogRule? ParseRule(JsonElement r, int index, List<string> errors)
    {
        if (r.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"rules[{index}] must be an object; skipped");
            return null;
        }
        var id = OptString(r, "dialogId");
        var contains = OptString(r, "messageContains");
        if (id is null && contains is null)
        {
            errors.Add($"rules[{index}] needs \"dialogId\" and/or \"messageContains\"; skipped");
            return null;
        }
        if (!r.TryGetProperty("result", out var res) || res.ValueKind != JsonValueKind.Number || !res.TryGetInt32(out var result))
        {
            errors.Add($"rules[{index}] needs an integer \"result\"; skipped");
            return null;
        }
        return new DialogRule(id, contains, result);
    }

    private static string? OptString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static bool Bool(JsonElement root, string key, bool def, List<string> errors)
    {
        if (!root.TryGetProperty(key, out var v)) return def;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        errors.Add($"\"{key}\" must be true or false; using {def.ToString().ToLowerInvariant()}");
        return def;
    }

    /// <summary>First rule that matches wins; null = no rule (record only).</summary>
    public static int? Match(IReadOnlyList<DialogRule> rules, string? dialogId, string? message)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            if (r.DialogId is not null && !string.Equals(r.DialogId, dialogId, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.MessageContains is not null &&
                (message is null || message.IndexOf(r.MessageContains, StringComparison.OrdinalIgnoreCase) < 0)) continue;
            return i;
        }
        return null;
    }
}

/// <summary>A dialog Revit showed, and what the policy did with it.</summary>
public sealed record DialogEvent(
    long Seq,
    DateTime TimestampUtc,
    string? DialogId,
    string Kind,
    string? Message,
    int? Rule,
    int? Result,
    bool Overridden)
{
    private static readonly int ProcessId = Environment.ProcessId;

    /// <summary><c>pid</c> tells sessions apart in the shared daily file (<c>seq</c> restarts per session).</summary>
    public JsonObject ToJson() => new()
    {
        ["pid"] = ProcessId,
        ["seq"] = Seq,
        ["ts"] = TimestampUtc.ToString("o"),
        ["dialogId"] = DialogId,
        ["kind"] = Kind,
        ["message"] = Message,
        ["rule"] = Rule,
        ["result"] = Result,
        ["overridden"] = Overridden,
    };
}

/// <summary>
/// Sees every DialogBoxShowing it is subscribed to, answers the ones a rule matches, and records
/// all of them: a ring of the most recent (read over HTTP while Revit may still be modal), an
/// optional JSONL file per UTC day (<c>{dir}\dialogs-YYYY-MM-DD.jsonl</c>), and any open
/// <see cref="Capture"/> scope (a command collecting the dialogs raised during its own call).
/// A dialog is recorded when it is SHOWN, so a reader learns which dialog Revit is waiting on.
///
/// Fail-open like the audit log: a file write error lands in <see cref="LastError"/>, never in Revit.
/// </summary>
public sealed class DialogMonitor
{
    public const int RingSize = 200;

    private readonly object _lock = new();
    private readonly LinkedList<DialogEvent> _ring = new();
    private readonly List<List<DialogEvent>> _captures = new();
    private readonly Action<string>? _log;
    private long _seq;

    public DialogMonitor(DialogPolicySettings settings, Action<string>? log = null)
    {
        Settings = settings;
        _log = log;
    }

    public DialogPolicySettings Settings { get; }
    public bool Enabled => Settings.Enabled;
    public string? LastError { get; private set; }

    /// <summary>Revit's event handler. Subscribe it only when <see cref="Enabled"/>.</summary>
    public void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e)
    {
        string kind = "dialog";
        string? message = null;
        try
        {
            switch (e)
            {
                case TaskDialogShowingEventArgs t: kind = "task_dialog"; message = t.Message; break;
                case MessageBoxShowingEventArgs m: kind = "message_box"; message = m.Message; break;
            }
        }
        catch { /* a message we cannot read is not a reason to miss the dialog */ }

        string? id = null;
        try { id = e.DialogId; } catch { }

        var rule = DialogPolicyConfig.Match(Settings.Rules, id, message);
        int? result = rule is int i ? Settings.Rules[i].Result : null;
        var overridden = false;
        if (result is int code)
        {
            try { overridden = e.OverrideResult(code); }
            catch (Exception ex) { _log?.Invoke($"[RevitMCP] OverrideResult({code}) on {id} failed: {ex.Message}"); }
        }
        Record(id, kind, message, rule, result, overridden);
    }

    /// <summary>Stores one event everywhere it belongs; returns it (pure apart from the stores).</summary>
    public DialogEvent Record(string? dialogId, string kind, string? message, int? rule, int? result, bool overridden)
    {
        DialogEvent ev;
        lock (_lock)
        {
            ev = new DialogEvent(++_seq, DateTime.UtcNow, dialogId, kind, message, rule, result, overridden);
            _ring.AddLast(ev);
            while (_ring.Count > RingSize) _ring.RemoveFirst();
            foreach (var c in _captures) c.Add(ev);
        }
        if (Settings.Log) WriteFile(ev);
        return ev;
    }

    /// <summary>Recorded events with <c>seq &gt; since</c>, oldest first.</summary>
    public IReadOnlyList<DialogEvent> Recent(long since = 0)
    {
        lock (_lock) return _ring.Where(e => e.Seq > since).ToList();
    }

    /// <summary>Collects every event recorded until the scope is disposed.</summary>
    public CaptureScope Capture()
    {
        var sink = new List<DialogEvent>();
        lock (_lock) _captures.Add(sink);
        return new CaptureScope(this, sink);
    }

    public sealed class CaptureScope : IDisposable
    {
        private readonly DialogMonitor _owner;
        private readonly List<DialogEvent> _sink;

        internal CaptureScope(DialogMonitor owner, List<DialogEvent> sink) { _owner = owner; _sink = sink; }

        public IReadOnlyList<DialogEvent> Events
        {
            get { lock (_owner._lock) return _sink.ToList(); }
        }

        public void Dispose()
        {
            lock (_owner._lock) _owner._captures.Remove(_sink);
        }
    }

    private void WriteFile(DialogEvent ev)
    {
        try
        {
            var line = ev.ToJson().ToJsonString() + "\n";
            var file = Path.Combine(Settings.Directory, $"dialogs-{ev.TimestampUtc:yyyy-MM-dd}.jsonl");
            lock (_lock)
            {
                Directory.CreateDirectory(Settings.Directory);
                File.AppendAllText(file, line, new UTF8Encoding(false));
            }
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            _log?.Invoke($"[RevitMCP] Dialog log write failed: {LastError}");
        }
    }
}
