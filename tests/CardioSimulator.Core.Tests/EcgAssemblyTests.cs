using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class EcgAssemblyTests
{
    // ── Slicing ────────────────────────────────────────────────────────────────

    [Fact]
    public void Split_CutsEqualBaselineZeroedParts_ThatConcatenateBackToTheWindow()
    {
        var samples = new int[100];
        for (var i = 0; i < samples.Length; i++) samples[i] = 1024 + i; // baseline 1024

        var parts = EcgAssemblySlicer.Split(samples, baseline: 1024, partCount: 4, windowSamples: 0);

        Assert.NotNull(parts);
        Assert.Equal(4, parts!.Count);
        Assert.All(parts, p => Assert.Equal(25, p.Length)); // equal parts

        // Baseline-zeroed and contiguous: concatenation reproduces (i) for the whole 100-sample window.
        var flat = parts.SelectMany(p => p).ToArray();
        Assert.Equal(Enumerable.Range(0, 100).ToArray(), flat);
    }

    [Fact]
    public void Split_DropsTrailingRemainder_SoAllPartsShareOneLength()
    {
        var samples = Enumerable.Repeat(1024, 103).ToArray(); // 103 / 5 = 20 each, 3 dropped
        var parts = EcgAssemblySlicer.Split(samples, 1024, partCount: 5, windowSamples: 0);

        Assert.NotNull(parts);
        Assert.Equal(5, parts!.Count);
        Assert.All(parts, p => Assert.Equal(20, p.Length));
    }

    [Fact]
    public void Split_HonoursWindow_AndClampsPartCount()
    {
        var samples = Enumerable.Range(0, 1000).Select(i => i + 1024).ToArray();
        var parts = EcgAssemblySlicer.Split(samples, 1024, partCount: 3, windowSamples: 30);

        Assert.NotNull(parts);
        Assert.Equal(3, parts!.Count);
        Assert.All(parts, p => Assert.Equal(10, p.Length)); // window 30 / 3
        Assert.Equal(0, parts[0][0]);   // first window sample, baseline-zeroed
        Assert.Equal(29, parts[2][^1]); // last window sample (index 29)
    }

    [Fact]
    public void Split_ReturnsNull_WhenTooFewSamplesForTheParts()
    {
        Assert.Null(EcgAssemblySlicer.Split(new int[3], 0, partCount: 4));
    }

    // ── AssemblyAttempt (reorder) grading ────────────────────────────────────────

    private static EcgAssembly SampleAssembly(int parts = 4) =>
        new(500,
            Enumerable.Range(0, parts)
                .Select(k => new EcgAssemblyPart(new[] { 0, k, 0 }))
                .ToList(),
            "src", Lead.II);

    [Fact]
    public void Attempt_IncompleteUntilAllSlotsFilled_ThenGradesAllOrNothing()
    {
        var attempt = new AssemblyAttempt(SampleAssembly(), seed: 1);
        Assert.Equal(4, attempt.SlotCount);
        Assert.False(attempt.IsComplete);
        Assert.False(attempt.AllCorrect);

        // Place every part into its correct slot (CorrectIndex).
        foreach (var item in attempt.Palette.ToList())
            attempt.Place(item.CorrectIndex, item);

        Assert.True(attempt.IsComplete);
        Assert.True(attempt.AllCorrect);
    }

    [Fact]
    public void Attempt_WrongOrder_FailsAllOrNothing()
    {
        var attempt = new AssemblyAttempt(SampleAssembly(), seed: 2);
        // Rotate every part one slot forward: a bijection, so all slots fill, but none is in its own slot.
        foreach (var item in attempt.Palette.ToList())
            attempt.Place((item.CorrectIndex + 1) % attempt.SlotCount, item);

        Assert.True(attempt.IsComplete);
        Assert.False(attempt.AllCorrect);
    }

    [Fact]
    public void Attempt_PlacingIntoOccupiedSlot_BumpsPreviousOccupantBackToPool()
    {
        var attempt = new AssemblyAttempt(SampleAssembly(), seed: 3);
        var a = attempt.Palette[0];
        var b = attempt.Palette[1];

        attempt.Place(0, a);
        attempt.Place(0, b); // b displaces a

        Assert.Equal(b.Key, attempt.PlacedAt(0)!.Key);
        Assert.Equal(-1, attempt.SlotOf(a));                    // a is back in the pool
        Assert.Contains(attempt.Available, i => i.Key == a.Key);
        Assert.DoesNotContain(attempt.Available, i => i.Key == b.Key);
    }

    [Fact]
    public void Attempt_MovingPlacedPiece_LeavesItsOldSlotEmpty()
    {
        var attempt = new AssemblyAttempt(SampleAssembly(), seed: 4);
        var item = attempt.Palette[0];

        attempt.Place(0, item);
        attempt.Place(2, item); // move from slot 0 → slot 2

        Assert.Null(attempt.PlacedAt(0));
        Assert.Equal(item.Key, attempt.PlacedAt(2)!.Key);
    }

    [Fact]
    public void Attempt_ClearReturnsPartToPool()
    {
        var attempt = new AssemblyAttempt(SampleAssembly(), seed: 5);
        var item = attempt.Palette[0];

        attempt.Place(1, item);
        Assert.DoesNotContain(attempt.Available, i => i.Key == item.Key);

        attempt.Clear(1);
        Assert.Null(attempt.PlacedAt(1));
        Assert.Contains(attempt.Available, i => i.Key == item.Key);
    }

    [Fact]
    public void Attempt_ShuffleIsDeterministicForSameSeed()
    {
        var a = new AssemblyAttempt(SampleAssembly(6), seed: 42);
        var b = new AssemblyAttempt(SampleAssembly(6), seed: 42);
        Assert.Equal(a.Palette.Select(i => i.Key), b.Palette.Select(i => i.Key));
    }

    // ── JSON round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void TestJson_RoundTrips_AssemblyQuestion()
    {
        var question = new TestQuestion(
            "q1", 1, "Put the trace in order",
            System.Array.Empty<TestOption>(), string.Empty, "Explanation",
            Assemble: SampleAssembly(3));
        var test = new Test("t1", "Assembly test", new[] { question });

        var round = TestJson.Deserialize(TestJson.Serialize(test));

        Assert.NotNull(round);
        var rq = round!.Questions[0];
        Assert.True(rq.IsAssembly);
        Assert.Equal(QuestionKind.AssembleEcg, rq.Kind);
        Assert.Equal("src", rq.Assemble!.SourcePathologyId);
        Assert.Equal(Lead.II, rq.Assemble.SliceLead);
        Assert.Equal(3, rq.Assemble.PartCount);
        Assert.Equal(new[] { 0, 1, 0 }, rq.Assemble.PartList[1].SampleList);
    }
}
