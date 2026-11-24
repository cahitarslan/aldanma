using Aldanma.Core.Messages;

namespace Aldanma.Tests;

/// <summary>
/// Regression guard: rule changes must not lower accuracy on the train and holdout splits.
/// Blind splits are deliberately not asserted here; see MEASUREMENT.md.
/// </summary>
public class CorpusTests
{
    private static readonly MessageEngine Engine = MessageEngine.Default();

    private static (int Tp, int Fp, int Tn, int Fn) Measure(string split)
    {
        int tp = 0, fp = 0, tn = 0, fn = 0;
        foreach (var m in TestData.Corpus().Where(m => m.Split == split))
        {
            var warned = Engine.Analyze(m.Text, m.Sender).Level != RiskLevel.NoSigns;
            if (m.Label == "scam") { if (warned) tp++; else fn++; }
            else { if (warned) fp++; else tn++; }
        }
        return (tp, fp, tn, fn);
    }

    [Fact]
    public void Corpus_has_at_least_120_balanced_messages_with_unique_ids()
    {
        var corpus = TestData.Corpus();
        Assert.True(corpus.Count >= 120);
        Assert.InRange(corpus.Count(m => m.Label == "scam") * 100 / corpus.Count, 40, 60);
        Assert.Equal(corpus.Count, corpus.Select(m => m.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("train", 97)]
    [InlineData("holdout", 95)]
    public void Accuracy_does_not_drop_below_threshold(string split, int minPercent)
    {
        var (tp, fp, tn, fn) = Measure(split);
        var total = tp + fp + tn + fn;
        Assert.True((tp + tn) * 100 >= minPercent * total, $"{split}: {tp + tn}/{total} correct (TP {tp}, FP {fp}, TN {tn}, FN {fn})");
    }

    [Fact]
    public void No_legit_message_in_train_or_holdout_is_rated_high()
    {
        var wrong = TestData.Corpus()
            .Where(m => m.Label == "legit" && m.Split is "train" or "holdout")
            .Where(m => Engine.Analyze(m.Text, m.Sender).Level == RiskLevel.High)
            .Select(m => m.Id).ToList();
        Assert.Empty(wrong);
    }
}
