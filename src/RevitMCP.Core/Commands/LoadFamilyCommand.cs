using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Load a family (.rfa) from disk into the project.
///
/// Params:
///   - filePath:  string, required — absolute path to a .rfa file.
///   - overwrite: bool, default true — overwrite the family (and its parameter
///                values) if it is already loaded.
///
/// Returns: { familyId, name, category, typeCount, types: [{id, name}] }.
/// </summary>
public sealed class LoadFamilyCommand : IRevitCommand
{
    public string Name => "load_family";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var path = P.Str(p, "filePath");
        if (!path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            throw new RevitCommandException("bad_request", "filePath must be a .rfa family file.");
        if (!File.Exists(path))
            throw new RevitCommandException("not_found", $"Family file not found: {path}");

        bool overwrite = P.BoolOr(p, "overwrite", true);
        var opts = new FamilyLoadOptions(overwrite);

        Family? family;
        bool loaded = doc.LoadFamily(path, opts, out family);
        if (!loaded || family is null)
        {
            // LoadFamily returns false when the family was already present and not
            // overwritten — fall back to the existing family of the same name.
            var name = Path.GetFileNameWithoutExtension(path);
            family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new RevitCommandException("command_failed",
                    "LoadFamily returned false and no family of that name is present.");
        }

        var symbolIds = family.GetFamilySymbolIds();
        var types = new JsonArray();
        foreach (var sid in symbolIds)
            if (doc.GetElement(sid) is FamilySymbol fs)
                types.Add(new JsonObject { ["id"] = fs.Id.Value, ["name"] = fs.Name });

        return new JsonObject
        {
            ["familyId"] = family.Id.Value,
            ["name"] = family.Name,
            ["category"] = family.FamilyCategory?.Name,
            ["wasAlreadyLoaded"] = !loaded,
            ["typeCount"] = symbolIds.Count,
            ["types"] = types,
        };
    }

    /// <summary>Overwrite policy for LoadFamily when the family already exists.</summary>
    private sealed class FamilyLoadOptions : IFamilyLoadOptions
    {
        private readonly bool _overwrite;
        public FamilyLoadOptions(bool overwrite) => _overwrite = overwrite;

        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = _overwrite;
            return _overwrite;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
            out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = _overwrite;
            return _overwrite;
        }
    }
}
