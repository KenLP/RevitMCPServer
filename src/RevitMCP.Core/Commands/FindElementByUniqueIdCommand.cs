using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Resolve an element from its <c>UniqueId</c> — the identifier ACC / BIM 360 hands
/// out as <c>externalId</c>, and the only one that is stable across documents.
///
/// Why this exists: <c>ElementId</c> is numbered PER DOCUMENT, so an id taken from a
/// clash in a linked model is meaningless — and occasionally *harmful* — when applied
/// to the host. Callers used to derive an ElementId from the UniqueId themselves;
/// every such derivation is a guess. Revit already answers the question exactly via
/// <c>Document.GetElement(string)</c>, so ask Revit instead of guessing.
///
/// Parameters:
///   - uniqueId:    string, required.  45-char "&lt;guid-36&gt;-&lt;8 hex&gt;".
///   - linkId:      long, optional.    Search ONLY inside this RevitLinkInstance.
///   - searchLinks: bool, optional.    When the host has no such element, sweep every
///                                     loaded link. Ignored if linkId is given.
///                                     Default false.
///
/// Returns the element identity plus <c>foundIn</c> ("host" | "link"). Bounding boxes
/// from a link are transformed into host coordinates, matching get_linked_elements.
///
/// NOTE: when foundIn == "link", the returned <c>id</c> is only valid INSIDE that
/// linked document — pass it back with the same <c>linkId</c>, never to a host-document
/// command such as get_element_info.
/// </summary>
public sealed class FindElementByUniqueIdCommand : IRevitCommand
{
    public string Name => "find_element_by_unique_id";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var uniqueId = P.Str(p, "uniqueId");
        var linkId = P.LongOrNull(p, "linkId");
        var searchLinks = P.BoolOr(p, "searchLinks", false);

        // Explicit link: look there and nowhere else, so a hit is unambiguous.
        if (linkId.HasValue)
        {
            var (linkedDoc, instance) = ResolveLink(doc, linkId.Value);
            var el = linkedDoc.GetElement(uniqueId)
                ?? throw new RevitCommandException("not_found",
                    $"No element with uniqueId '{uniqueId}' in link '{instance.Name}'.");
            return Describe(el, instance);
        }

        var hostEl = doc.GetElement(uniqueId);
        if (hostEl != null) return Describe(hostEl, null);

        if (searchLinks)
        {
            foreach (var inst in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance))
                         .Cast<RevitLinkInstance>())
            {
                var ldoc = inst.GetLinkDocument();   // null when the link is unloaded
                if (ldoc == null) continue;
                var found = ldoc.GetElement(uniqueId);
                if (found != null) return Describe(found, inst);
            }

            throw new RevitCommandException("not_found",
                $"No element with uniqueId '{uniqueId}' in the host document or any loaded link.");
        }

        throw new RevitCommandException("not_found",
            $"No element with uniqueId '{uniqueId}' in the host document. " +
            "Pass searchLinks=true to sweep loaded links, or linkId to target one.");
    }

    private static (Document, RevitLinkInstance) ResolveLink(Document doc, long linkId)
    {
        var instance = doc.GetElement(new ElementId(linkId)) as RevitLinkInstance
            ?? throw new RevitCommandException("not_found",
                $"No RevitLinkInstance with id {linkId}.");

        var linkedDoc = instance.GetLinkDocument()
            ?? throw new RevitCommandException("invalid_parameter",
                $"Linked file '{instance.Name}' is not loaded. Load it in Revit first.");

        return (linkedDoc, instance);
    }

    /// <param name="instance">null when the element lives in the host document.</param>
    private static JsonObject Describe(Element element, RevitLinkInstance? instance)
    {
        BoundingBoxXYZ? bbox = null;
        try { bbox = element.get_BoundingBox(null); } catch { }

        JsonObject? bboxObj = null;
        if (bbox != null)
        {
            // A link's element geometry is in the linked document's coordinates; move it
            // into host coordinates so the numbers line up with everything else we emit.
            var transform = instance?.GetTotalTransform() ?? Transform.Identity;
            var a = transform.OfPoint(bbox.Min);
            var b = transform.OfPoint(bbox.Max);
            bboxObj = new JsonObject
            {
                ["min"] = new JsonObject
                {
                    ["x"] = Math.Min(a.X, b.X),
                    ["y"] = Math.Min(a.Y, b.Y),
                    ["z"] = Math.Min(a.Z, b.Z),
                },
                ["max"] = new JsonObject
                {
                    ["x"] = Math.Max(a.X, b.X),
                    ["y"] = Math.Max(a.Y, b.Y),
                    ["z"] = Math.Max(a.Z, b.Z),
                },
            };
        }

        return new JsonObject
        {
            ["foundIn"] = instance is null ? "host" : "link",
            ["linkId"] = instance?.Id.Value,
            ["linkName"] = instance?.Name,
            ["linkedDocTitle"] = instance?.GetLinkDocument()?.Title,
            ["id"] = element.Id.Value,
            ["uniqueId"] = element.UniqueId,
            ["name"] = element.Name,
            ["category"] = element.Category?.Name,
            ["categoryEnum"] = element.Category?.BuiltInCategory.ToString(),
            ["typeId"] = element.GetTypeId()?.Value,
            ["levelId"] = element.LevelId?.Value,
            ["boundingBox"] = bboxObj,
        };
    }
}
