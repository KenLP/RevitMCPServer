using System.Text.Json.Nodes;
using RevitMCPAddin.Commands;

namespace HelloPack;

/// <summary>
/// A read-only pack command: echoes its parameters and the open document's title. It uses only the
/// public pack surface — <see cref="IRevitCommand"/>, <see cref="CommandContext"/>, <see cref="P"/>
/// and <see cref="RevitCommandException"/> — which is everything a pack needs.
/// </summary>
public sealed class HelloPackCommand : IRevitCommand
{
    public string Name => "hello_pack";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var greeting = P.StrOrNull(ctx.Parameters, "greeting") ?? "hello";
        if (greeting.Length > 100)
            throw new RevitCommandException("invalid_parameter", "Parameter 'greeting' is longer than 100 characters.");

        return new JsonObject
        {
            ["message"] = $"{greeting} from a command pack",
            ["documentTitle"] = ctx.Doc?.Title,
        };
    }
}
