using System.Text.Json;
using System.Text.Json.Nodes;
using Aldanma.Core.Messages;

namespace Aldanma.Tests;

/// <summary>
/// The C# engine must give the same result as the JS engine for the same rule set.
/// Expected values are produced from the JS engine by "npm run parity" (corpus/expected-js.json).
/// </summary>
public class ParityTests
{
    private static readonly MessageEngine Engine = MessageEngine.Default();

    [Fact]
    public void Expected_file_was_generated_with_the_same_rules_version()
    {
        Assert.Equal(Engine.RulesVersion, TestData.ExpectedJs()["rulesVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Every_message_gives_field_by_field_identical_results_in_CSharp_and_JS()
    {
        var expected = TestData.ExpectedJs()["results"]!.AsObject();
        var inputs = TestData.Corpus().Concat(TestData.Read("parity-extra.json")).ToList();
        Assert.Equal(expected.Count, inputs.Count);

        var diffs = new List<string>();
        foreach (var m in inputs)
        {
            var ours = JsonSerializer.SerializeToNode(Engine.Analyze(m.Text, m.Sender), TestData.Json)!;
            if (!JsonNode.DeepEquals(ours, expected[m.Id]))
                diffs.Add($"{m.Id}\n  C#: {ours.ToJsonString()}\n  JS: {expected[m.Id]!.ToJsonString()}");
        }
        Assert.True(diffs.Count == 0, $"{diffs.Count} messages differ:\n" + string.Join("\n", diffs.Take(5)));
    }
}
