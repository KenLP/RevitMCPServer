using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Export a Revit view to a PNG image and return it as a base64-encoded string.
/// The image is rendered at the requested DPI and returned in the JSON response.
/// On success the caller receives { imageBase64, mimeType, viewId, viewName, ... }.
/// The MCP TypeScript layer strips imageBase64 and returns it as an MCP Image content block.
///
/// Params:
///   - viewId: long, optional — element id of the view to export.
///             Omit to export the currently active view.
///   - dpi:    int, optional, default 72 (accepted: 36–300, snaps to 72/150/300).
///
/// Notes:
///   - Complex 3D views may be slow to export; the default 30-second timeout applies.
///   - View templates cannot be exported — pass a concrete view id.
/// </summary>
public sealed class GetViewImageCommand : IRevitCommand
{
    public string Name => "get_view_image";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        View view;
        var viewIdNode = p["viewId"];
        if (viewIdNode != null)
        {
            var viewId = new ElementId(viewIdNode.GetValue<long>());
            view = doc.GetElement(viewId) as View
                ?? throw new RevitCommandException("not_found", $"No view with id {viewId.Value}.");
        }
        else
        {
            view = ctx.RequireUIDoc().ActiveView
                ?? throw new RevitCommandException("not_found", "No active view.");
        }

        if (view.IsTemplate)
            throw new RevitCommandException("invalid_parameter",
                "Cannot export a view template — provide a concrete view id.");

        var dpi = Math.Clamp(P.IntOr(p, "dpi", 72), 36, 300);
        var resolution = DpiToResolution(dpi);

        var tempDir = Path.Combine(Path.GetTempPath(), $"revitmcp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                ImageResolution = resolution,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                FilePath = Path.Combine(tempDir, "export"),
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(options);

            // Revit names the output: "export - <ViewType> - <ViewName>.png"
            var pngs = Directory.GetFiles(tempDir, "*.png");
            if (pngs.Length == 0)
                throw new RevitCommandException("command_failed",
                    "Image export produced no output files. The view may be empty or unsupported.");

            var bytes = File.ReadAllBytes(pngs[0]);
            var base64 = Convert.ToBase64String(bytes);

            return new JsonObject
            {
                ["viewId"] = view.Id.Value,
                ["viewName"] = view.Name,
                ["viewType"] = view.ViewType.ToString(),
                ["dpi"] = dpi,
                ["fileSizeBytes"] = bytes.Length,
                ["imageBase64"] = base64,
                ["mimeType"] = "image/png",
            };
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static ImageResolution DpiToResolution(int dpi) => dpi switch
    {
        <= 72  => ImageResolution.DPI_72,
        <= 150 => ImageResolution.DPI_150,
        _      => ImageResolution.DPI_300,
    };
}
