using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Export a Revit view or sheet to a PDF file on disk.
///
/// Params:
///   - viewId:        long, optional — ElementId of the view/sheet. Defaults to active view.
///   - outputFolder:  string, optional — folder to save the PDF. Defaults to user's Documents folder.
///   - fileName:      string, optional — file name without extension. Defaults to view name + timestamp.
///   - rasterQuality: string, optional — Low|Medium|High|Presentation. Default "Medium".
///   - colorMode:     string, optional — Color|Grayscale|BlackLine. Default "Color".
/// </summary>
public sealed class ExportViewPdfCommand : IRevitCommand
{
    public string Name => "export_view_pdf";
    public bool IsReadOnly => true; // no model changes

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        // Resolve view
        View view;
        var viewIdNode = p["viewId"];
        if (viewIdNode != null)
        {
            view = doc.GetElement(new ElementId(P.LongFrom(viewIdNode, "viewId"))) as View
                ?? throw new RevitCommandException("not_found",
                    $"No view with id {P.LongFrom(viewIdNode, "viewId")}.");
        }
        else
        {
            view = ctx.RequireUIDoc().ActiveView
                ?? throw new RevitCommandException("not_found", "No active view.");
        }

        if (view.IsTemplate)
            throw new RevitCommandException("invalid_parameter",
                "Cannot export a view template — provide a concrete view id.");

        // Resolve output folder
        var outputFolder = P.StrOrNull(p, "outputFolder")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!Directory.Exists(outputFolder))
            Directory.CreateDirectory(outputFolder);

        // File name (no extension)
        var safeViewName = string.Concat(view.Name.Split(Path.GetInvalidFileNameChars()));
        var baseName = P.StrOrNull(p, "fileName")
            ?? $"{safeViewName}_{DateTime.Now:yyyyMMdd_HHmmss}";
        baseName = baseName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? baseName[..^4]
            : baseName;

        var rasterQualityStr = P.StrOrNull(p, "rasterQuality") ?? "Medium";
        var colorModeStr     = P.StrOrNull(p, "colorMode")     ?? "Color";

        var pdfOptions = new PDFExportOptions
        {
            FileName      = baseName,
            Combine       = true,
            RasterQuality = ParseRasterQuality(rasterQualityStr),
            ColorDepth    = ParseColorDepth(colorModeStr),
            ZoomType      = ZoomType.FitToPage,
            PaperFormat   = ExportPaperFormat.Default,
        };

        try
        {
            doc.Export(outputFolder, new List<ElementId> { view.Id }, pdfOptions);
        }
        catch (Exception ex)
        {
            throw new RevitCommandException("command_failed", $"PDF export failed: {ex.Message}");
        }

        var expectedPath = Path.Combine(outputFolder, baseName + ".pdf");
        long? fileSize = File.Exists(expectedPath) ? new FileInfo(expectedPath).Length : null;

        return new JsonObject
        {
            ["viewId"]        = view.Id.Value,
            ["viewName"]      = view.Name,
            ["outputPath"]    = expectedPath,
            ["fileSizeBytes"] = fileSize,
            ["rasterQuality"] = rasterQualityStr,
            ["colorMode"]     = colorModeStr,
            ["changeSummary"] = $"Exported '{view.Name}' to PDF: {expectedPath}",
        };
    }

    private static RasterQualityType ParseRasterQuality(string s) => s.ToLowerInvariant() switch
    {
        "low"          => RasterQualityType.Low,
        "high"         => RasterQualityType.High,
        "presentation" => RasterQualityType.Presentation,
        _              => RasterQualityType.Medium,
    };

    private static ColorDepthType ParseColorDepth(string s) => s.ToLowerInvariant() switch
    {
        "grayscale" or "greyscale" => ColorDepthType.GrayScale,
        "blackline" or "bw"        => ColorDepthType.BlackLine,
        _                          => ColorDepthType.Color,
    };
}
