using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Collects the dialogs Revit raises during one command. With an application-level policy its handler
/// is already subscribed (and answers matching dialogs); without one, a record-only monitor listens
/// for the duration of the command and answers nothing.
/// </summary>
internal sealed class DialogWatch : IDisposable
{
    private readonly CommandContext _ctx;
    private readonly DialogMonitor _monitor;
    private readonly bool _ownSubscription;
    private readonly DialogMonitor.CaptureScope _scope;

    private DialogWatch(CommandContext ctx)
    {
        _ctx = ctx;
        _monitor = ctx.Dialogs is { Enabled: true } shared ? shared : new DialogMonitor(DialogPolicySettings.RecordOnly);
        _ownSubscription = !ReferenceEquals(_monitor, ctx.Dialogs);
        if (_ownSubscription) ctx.App.DialogBoxShowing += _monitor.OnDialogBoxShowing;
        _scope = _monitor.Capture();
    }

    internal static DialogWatch Start(CommandContext ctx) => new(ctx);

    /// <summary>The dialogs seen so far; still readable after <see cref="Dispose"/>.</summary>
    internal IReadOnlyList<DialogEvent> Events => _scope.Events;

    internal JsonArray EventsJson() => ToJson(Events);

    /// <summary>" Dialogs during the attempt: […]" for an error message, or empty when there were none.</summary>
    internal string ErrorSuffix()
    {
        var dialogs = EventsJson();
        return dialogs.Count > 0 ? $" Dialogs during the attempt: {dialogs.ToJsonString()}" : "";
    }

    internal static JsonArray ToJson(IReadOnlyList<DialogEvent> events) =>
        new(events.Select(e => (JsonNode?)new JsonObject
        {
            ["dialogId"] = e.DialogId,
            ["kind"] = e.Kind,
            ["message"] = e.Message,
            ["result"] = e.Result,
            ["overridden"] = e.Overridden,
        }).ToArray());

    public void Dispose()
    {
        _scope.Dispose();
        if (_ownSubscription)
        {
            try { _ctx.App.DialogBoxShowing -= _monitor.OnDialogBoxShowing; } catch { }
        }
    }
}
