using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aldanma.Tests;

public sealed record CorpusMessage(string Id, string Text, string? Sender, string? Label, string? Category, string? Split);

internal static class TestData
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "data", name);

    public static List<CorpusMessage> Read(string name)
    {
        var root = JsonNode.Parse(File.ReadAllText(PathOf(name)))!;
        return root["messages"]!.Deserialize<List<CorpusMessage>>(Json)!;
    }

    public static List<CorpusMessage> Corpus() => Read("corpus.json");

    public static JsonNode ExpectedJs() => JsonNode.Parse(File.ReadAllText(PathOf("expected-js.json")))!;
}
