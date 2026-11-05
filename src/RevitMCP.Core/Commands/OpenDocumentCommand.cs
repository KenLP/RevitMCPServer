using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Open a model and make it the active document — a local/network file or a cloud model by GUIDs.
/// Runs with no document open (never calls RequireDoc), so a caller can launch Revit bare and open
/// the model through the add-in.
///
/// Params (exactly one of path | cloud):
///   - path:     string — .rvt file path
///   - cloud:    { region: "US" | "EMEA" | …, projectGuid, modelGuid } (from get_document_info.cloud)
///   - worksets: "all" (default) | "none" | "last_viewed" — passing a configuration is what keeps
///               Revit's "Opening Worksets" dialog from appearing; there is deliberately no "ask"
///   - detach:   "none" (default) | "preserve" | "discard" — DetachFromCentralOption
///   - audit:    bool, default false
///
/// Idempotent: when the requested model already is the active document nothing happens and the
/// result says alreadyOpen:true; when it is open but not active it is activated (wasOpen:true). Dialogs Revit raises during the open are returned in "dialogs";
/// they are answered only by a rule of revit-mcp-dialog-policy.json, never blind.
/// A large model takes tens of seconds (minutes on a first cloud download) — callers need a long
/// timeout; the command imposes none.
/// </summary>
public sealed class OpenDocumentCommand : IRevitCommand
{
    public string Name => "open_document";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var request = OpenRequest.From(ctx.Parameters);

        var active = ctx.App.ActiveUIDocument?.Document;
        if (active is not null && request.Matches(active))
            return Describe(active, request, alreadyOpen: true, openMs: 0, Array.Empty<DialogEvent>());

        // Open in this session but not active: OpenAndActivateDocument just activates it (measured:
        // ~1.5 s, no reload). Reported as wasOpen so a caller can tell it from a fresh open.
        var wasOpen = ctx.App.Application.Documents.Cast<Document>()
            .Any(d => !d.IsLinked && request.Matches(d));

        if (request.Cloud is not null && !RevitApplication.IsLoggedIn)
            throw new RevitCommandException("not_signed_in",
                "Opening a cloud model needs a signed-in Autodesk account; nobody is signed in to Revit.");

        var options = new OpenOptions
        {
            DetachFromCentralOption = request.Detach switch
            {
                "preserve" => DetachFromCentralOption.DetachAndPreserveWorksets,
                "discard" => DetachFromCentralOption.DetachAndDiscardWorksets,
                _ => DetachFromCentralOption.DoNotDetach,
            },
            Audit = request.Audit,
        };
        options.SetOpenWorksetsConfiguration(new WorksetConfiguration(request.Worksets switch
        {
            "none" => WorksetConfigurationOption.CloseAllWorksets,
            "last_viewed" => WorksetConfigurationOption.OpenLastViewed,
            _ => WorksetConfigurationOption.OpenAllWorksets,
        }));

        var watch = DialogWatch.Start(ctx);
        var sw = Stopwatch.StartNew();
        UIDocument? opened;
        try
        {
            // Inside the try: resolving cloud GUIDs already asks the cloud, and an unknown model
            // throws there (measured: CentralModelMissingException) - that is an open failure too.
            var modelPath = request.Cloud is { } cloud
                ? ModelPathUtils.ConvertCloudGUIDsToCloudPath(cloud.Region, cloud.ProjectGuid, cloud.ModelGuid)
                : ModelPathUtils.ConvertUserVisiblePathToModelPath(request.Path!);
            opened = ctx.App.OpenAndActivateDocument(modelPath, options, false);
        }
        catch (Exception ex) when (ex is not RevitCommandException)
        {
            throw new RevitCommandException("open_failed",
                $"Revit could not open the model ({ex.GetType().Name}): {ex.Message}{watch.ErrorSuffix()}");
        }
        finally
        {
            sw.Stop();
            watch.Dispose();
        }

        var doc = opened?.Document
            ?? throw new RevitCommandException("open_failed", "Revit returned no document for the open request.");
        var result = Describe(doc, request, alreadyOpen: false, openMs: sw.ElapsedMilliseconds, watch.Events);
        result["wasOpen"] = wasOpen;
        return result;
    }

    private static JsonObject Describe(Document doc, OpenRequest request, bool alreadyOpen, long openMs,
        System.Collections.Generic.IReadOnlyList<DialogEvent> dialogs)
    {
        var result = new JsonObject
        {
            ["title"] = doc.Title,
            ["pathName"] = doc.PathName,
            ["isWorkshared"] = doc.IsWorkshared,
            ["isDetached"] = doc.IsDetached,
            ["alreadyOpen"] = alreadyOpen,
            ["openMs"] = openMs,
            ["worksets"] = request.Worksets,
            ["detach"] = request.Detach,
            ["dialogs"] = DialogWatch.ToJson(dialogs),
        };
        var cloud = CloudIds.Of(doc);
        if (cloud is not null) result["cloud"] = cloud;
        return result;
    }

}

/// <summary>Validated params of <c>open_document</c> (pure apart from the file-exists check).</summary>
internal sealed record OpenRequest(string? Path, CloudRequest? Cloud, string Worksets, string Detach, bool Audit)
{
    internal static readonly string[] WorksetChoices = { "all", "none", "last_viewed" };
    internal static readonly string[] DetachChoices = { "none", "preserve", "discard" };

    internal static OpenRequest From(JsonObject p, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var path = P.StrOrNull(p, "path");
        var cloudNode = p["cloud"];
        if ((path is null) == (cloudNode is null))
            throw new RevitCommandException("invalid_parameter", "Give exactly one of 'path' or 'cloud'.");

        var worksets = Choice(p, "worksets", "all", WorksetChoices);
        var detach = Choice(p, "detach", "none", DetachChoices);
        var audit = P.BoolOr(p, "audit", false);

        if (path is not null)
        {
            var full = OutputPath.Full(path, "path");
            if (!fileExists(full))
                throw new RevitCommandException("not_found", $"Model file not found: {full}");
            return new OpenRequest(full, null, worksets, detach, audit);
        }

        if (cloudNode is not JsonObject c)
            throw new RevitCommandException("invalid_parameter", "'cloud' must be an object {region, projectGuid, modelGuid}.");
        var region = P.StrOrNull(c, "region")
            ?? throw new RevitCommandException("invalid_parameter", "'cloud.region' is required (e.g. \"US\" or \"EMEA\").");
        return new OpenRequest(null, new CloudRequest(region.Trim(), Guid(c, "projectGuid"), Guid(c, "modelGuid")),
            worksets, detach, audit);
    }

    /// <summary>
    /// Is <paramref name="doc"/> the model this request names? Cloud: same project + model GUID.
    /// File: same full path (a detached copy has no path, so it never matches and is reopened).
    /// </summary>
    internal bool Matches(Document doc)
    {
        if (Cloud is { } c)
        {
            var ids = CloudIds.Guids(doc);
            return ids is not null && ids.Value.Project == c.ProjectGuid && ids.Value.Model == c.ModelGuid;
        }
        return SamePath(doc.PathName, Path);
    }

    internal static bool SamePath(string? docPath, string? requested)
    {
        if (string.IsNullOrWhiteSpace(docPath) || string.IsNullOrWhiteSpace(requested)) return false;
        try { docPath = System.IO.Path.GetFullPath(docPath); } catch { return false; }
        return string.Equals(docPath, requested, StringComparison.OrdinalIgnoreCase);
    }

    private static string Choice(JsonObject p, string key, string def, string[] allowed)
    {
        var v = P.StrOrNull(p, key)?.Trim().ToLowerInvariant();
        if (v is null) return def;
        if (Array.IndexOf(allowed, v) < 0)
            throw new RevitCommandException("invalid_parameter", $"'{key}' must be one of {string.Join(", ", allowed)}.");
        return v;
    }

    private static Guid Guid(JsonObject c, string key)
    {
        var raw = P.StrOrNull(c, key);
        if (raw is null || !System.Guid.TryParse(raw.Trim(), out var g))
            throw new RevitCommandException("invalid_parameter", $"'cloud.{key}' must be a GUID.");
        return g;
    }
}

internal sealed record CloudRequest(string Region, Guid ProjectGuid, Guid ModelGuid);

/// <summary>The <c>cloud</c> block shared by get_document_info and open_document.</summary>
internal static class CloudIds
{
    internal static (Guid Project, Guid Model)? Guids(Document doc)
    {
        try
        {
            if (!doc.IsModelInCloud) return null;
            var mp = doc.GetCloudModelPath();
            return (mp.GetProjectGUID(), mp.GetModelGUID());
        }
        catch { return null; }
    }

    internal static JsonObject? Of(Document doc)
    {
        try
        {
            if (!doc.IsModelInCloud) return null;
            var mp = doc.GetCloudModelPath();
            return new JsonObject
            {
                ["region"] = mp.Region,
                ["projectGuid"] = mp.GetProjectGUID().ToString(),
                ["modelGuid"] = mp.GetModelGUID().ToString(),
            };
        }
        catch { return null; }
    }
}
