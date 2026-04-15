using System.Text.Json.Nodes;

namespace RevitMCPAddin.Commands;

public sealed class PingCommand : IRevitCommand
{
    public string Name => "ping";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var uiDoc = ctx.App.ActiveUIDocument;
        return new JsonObject
        {
            ["pong"] = true,
            ["hasActiveDocument"] = uiDoc != null,
            ["activeDocumentTitle"] = uiDoc?.Document?.Title,
        };
    }
}
