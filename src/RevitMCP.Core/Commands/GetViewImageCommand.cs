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
///   - pixelSize: int, optional, default 512 (clamped 128-4096) - the WIDTH of the
///               exported image in pixels; height follows the view's aspect ratio.
///   - dpi:    int, optional, default 72 (accepted: 36–300, snaps to 72/150/300).
///             METADATA ONLY. With ExportRange.SetOfViews, ImageResolution writes the
///             print DPI into the PNG and does NOT change how many pixels come out -
///             that is PixelSize. Kept for compatibility; use pixelSize for resolution.
///
/// Notes:
///   - Complex 3D views may be slow to export; the default 30-second timeout applies.
///     A large pixelSize on a Fine-detail 3D view can exceed it - drop the size or
///     the detail level rather than assuming the command hung.
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
            var viewId = new ElementId(P.LongFrom(viewIdNode, "viewId"));
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
        // Default stays 512 - the value Revit already used - so consumers parsing the
        // old small images keep getting exactly what they got before.
        var pixelSize = Math.Clamp(P.IntOr(p, "pixelSize", 512), 128, 4096);

        var tempDir = Path.Combine(Path.GetTempPath(), $"revitmcp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                ImageResolution = resolution,
                // PixelSize is what actually sizes the output. Revit's default is 512,
                // which is why every export came back 512 wide however the DPI was set.
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = pixelSize,
                FitDirection = FitDirectionType.Horizontal,   // pixelSize = width
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
            var (width, height) = ReadPngSize(bytes);

            return new JsonObject
            {
                ["viewId"] = view.Id.Value,
                ["viewName"] = view.Name,
                ["viewType"] = view.ViewType.ToString(),
                // Reported so a caller never has to decode the image to learn its size,
                // and can see at a glance whether pixelSize was honoured.
                ["width"] = width,
                ["height"] = height,
                ["pixelSize"] = pixelSize,
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

    /// <summary>
    /// Width/height from the PNG IHDR chunk: 8-byte signature, then a 4-byte length and
    /// the "IHDR" tag, so width sits at offset 16 and height at 20, both big-endian.
    /// Returns (0,0) rather than throwing if the file is somehow not a PNG - the image
    /// itself is still worth returning.
    /// </summary>
    private static (int Width, int Height) ReadPngSize(byte[] bytes)
    {
        if (bytes.Length < 24) return (0, 0);
        static int BE(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
        return (BE(bytes, 16), BE(bytes, 20));
    }

    private static ImageResolution DpiToResolution(int dpi) => dpi switch
    {
        <= 72  => ImageResolution.DPI_72,
        <= 150 => ImageResolution.DPI_150,
        _      => ImageResolution.DPI_300,
    };
}
