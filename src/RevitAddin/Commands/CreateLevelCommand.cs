using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a new <see cref="Level"/>.
///
/// Parameters:
///   - elevation: number, required.  In user units (default meters).
///   - name:      string, optional.  If supplied, renames the level after creation.
///   - units:     "meters"|"feet"
/// </summary>
public sealed class CreateLevelCommand : IRevitCommand
{
    public string Name => "create_level";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);
        var toFeet = units == "feet" ? 1.0 : P.MetersToFeet;

        var elevation = P.Dbl(p, "elevation") * toFeet;
        var level = Level.Create(doc, elevation);

        var name = P.StrOrNull(p, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            try { level.Name = name; }
            catch (System.Exception ex)
            {
                // Don't fail the whole call just because the name was taken.
                return new JsonObject
                {
                    ["id"] = level.Id.Value,
                    ["elevationFeet"] = elevation,
                    ["name"] = level.Name,
                    ["renameWarning"] = ex.Message,
                };
            }
        }

        return new JsonObject
        {
            ["id"] = level.Id.Value,
            ["elevationFeet"] = elevation,
            ["name"] = level.Name,
        };
    }
}
