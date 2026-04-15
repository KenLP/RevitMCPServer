using System.Text.Json.Nodes;

namespace RevitMCPAddin.Commands;

public sealed class GetVersionCommand : IRevitCommand
{
    public string Name => "get_revit_version";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var a = ctx.App.Application;
        return new JsonObject
        {
            ["versionName"] = a.VersionName,
            ["versionNumber"] = a.VersionNumber,
            ["versionBuild"] = a.VersionBuild,
            ["subVersionNumber"] = a.SubVersionNumber,
            ["language"] = a.Language.ToString(),
            ["username"] = a.Username,
        };
    }
}
