using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class EcgAssemblyTests
{
    // A three-beat marker set on a synthetic buffer; the middle beat is the "cleanest".
    private static List<SignificantPoint> ThreeBeatMarkers()
    {
        var points = new List<SignificantPoint>();
        foreach (var r in new[] { 100, 300, 500 })
        {
            points.Add(new SignificantPoint(r - 40, EcgPointType.P_START));
            points.Add(new SignificantPoint(r - 30, EcgPointType.P_PEAK));
            points.Add(new SignificantPoint(r - 20, EcgPointType.P_END));
            points.Add(new SignificantPoint(r - 10, EcgPointType.QRS_START));
            points.Add(new SignificantPoint(r, EcgPointType.R_PEAK));
            points.Add(new SignificantPoint(r + 10, EcgPointType.QRS_END));
            points.Add(new SignificantPoint(r + 30, EcgPointType.T_START));
            points.Add(new SignificantPoint(r + 60, EcgPointType.T_END));
        }
        return points;
    }

    [Fact]
    public void BestBeat_PicksBeatNearestCenter_AndFramesItInOrder()
    {
        var beat = EcgBeatSlicer.BestBeat(ThreeBeatMarkers(), sampleCount: 600);

        Assert.NotNull(beat);
        var b = beat!.Value;
        Assert.True(b.IsValid);
        // Center of a 600-sample buffer → the beat anchored on R=300.
        Assert.Equal(260, b.PStart);
        Assert.Equal(280, b.PEnd);
        Assert.Equal(290, b.QrsStart);
        Assert.Equal(310, b.QrsEnd);
        Assert.Equal(330, b.TStart);
        Assert.Equal(360, b.TEnd);
    }

    [Fact]
    public void BestBeat_ReturnsNull_WhenTMarkersMissing()
    {
        var points = ThreeBeatMarkers().Where(p => p.Type != EcgPointType.T_START && p.Type != EcgPointType.T_END).ToList();
        Assert.Null(EcgBeatSlicer.BestBeat(points, 600));
    }

    [Fact]
    public void Slice_ProducesBaselineZeroedSegmentsOfExpectedLength()
    {
        var samples = new int[600];
        for (var i = 0; i < samples.Length; i++) samples[i] = 1024 + i % 7; // arbitrary, baseline 1024

        var beat = EcgBeatSlicer.BestBeat(ThreeBeatMarkers(), 600)!.Value;
        var slice = EcgBeatSlicer.Slice(samples, baseline: 1024, beat);

        Assert.NotNull(slice);
        var (p, qrs, t) = slice!.Value;
        Assert.Equal(20, p.Length);   // 280 - 260
        Assert.Equal(20, qrs.Length);  // 310 - 290
        Assert.Equal(30, t.Length);    // 360 - 330
        // Baseline-zeroed: sample 260 was 1024 + 260%7 = 1024 + 1 → 1.
        Assert.Equal(260 % 7, p[0]);
    }

    [Fact]
    public void Slice_ReturnsNull_WhenBeatRunsPastBuffer()
    {
        var beat = new BeatFiducials(0, 10, 10, 20, 20, 30);
        Assert.Null(EcgBeatSlicer.Slice(new int[25], 0, beat));
    }

    // ── AssemblyAttempt grading ───────────────────────────────────────────────

    private static EcgAssembly SampleAssembly()
    {
        EcgAssemblyBlock Block(EcgBlock b) => new(
            b,
            new EcgBlockPiece(b, new[] { 0, 1, 0 }, "target"),
            new List<EcgBlockPiece>
            {
                new(b, new[] { 0, 2, 0 }, "d1"),
                new(b, new[] { 0, 3, 0 }, "d2"),
            });

        return new EcgAssembly(500, new List<EcgAssemblyBlock>
        {
            Block(EcgBlock.P), Block(EcgBlock.QRS), Block(EcgBlock.T),
        }, "target", new[] { "d1", "d2" }, Lead.II);
    }

    [Fact]
    public void Attempt_IncompleteUntilAllSlotsFilled_ThenGradesAllOrNothing()
    {
        var attempt = new AssemblyAttempt(SampleAssembly(), seed: 1);
        Assert.False(attempt.IsComplete);
        Assert.False(attempt.AllCorrect);

        foreach (var block in attempt.Blocks)
        {
            var correct = attempt.Palette(block).First(i => i.IsCorrect);
            attempt.Place(correct);
        }
        Assert.True(attempt.IsComplete);
        Assert.True(attempt.AllCorrect);
    }

    [Fact]
    public void Attempt_OneWrongPiece_FailsAllOrNothing()
    {
        var attempt = new AssemblyAttempt(SampleAssembly(), seed: 2);
        foreach (var block in attempt.Blocks)
        {
            var pick = block == EcgBlock.QRS
                ? attempt.Palette(block).First(i => !i.IsCorrect)
                : attempt.Palette(block).First(i => i.IsCorrect);
            attempt.Place(pick);
        }
        Assert.True(attempt.IsComplete);
        Assert.False(attempt.AllCorrect);
    }

    [Fact]
    public void Attempt_PlacedPieceLeavesAvailablePalette_ClearRestoresIt()
    {
        var attempt = new AssemblyAttempt(SampleAssembly(), seed: 3);
        var item = attempt.Palette(EcgBlock.P).First();

        attempt.Place(item);
        Assert.DoesNotContain(attempt.Available(EcgBlock.P), i => i.Key == item.Key);
        Assert.Equal(item.Key, attempt.Placed(EcgBlock.P)!.Key);

        attempt.Clear(EcgBlock.P);
        Assert.Null(attempt.Placed(EcgBlock.P));
        Assert.Contains(attempt.Available(EcgBlock.P), i => i.Key == item.Key);
    }

    [Fact]
    public void Attempt_ShuffleIsDeterministicForSameSeed()
    {
        var a = new AssemblyAttempt(SampleAssembly(), seed: 42);
        var b = new AssemblyAttempt(SampleAssembly(), seed: 42);
        Assert.Equal(
            a.Palette(EcgBlock.QRS).Select(i => i.Key),
            b.Palette(EcgBlock.QRS).Select(i => i.Key));
    }

    // ── JSON round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void TestJson_RoundTrips_AssemblyQuestion()
    {
        var question = new TestQuestion(
            "q1", 1, "Assemble a normal sinus complex",
            System.Array.Empty<TestOption>(), string.Empty, "Explanation",
            Assemble: SampleAssembly());
        var test = new Test("t1", "Assembly test", new[] { question });

        var round = TestJson.Deserialize(TestJson.Serialize(test));

        Assert.NotNull(round);
        var rq = round!.Questions[0];
        Assert.True(rq.IsAssembly);
        Assert.Equal(QuestionKind.AssembleEcg, rq.Kind);
        Assert.Equal("target", rq.Assemble!.TargetPathologyId);
        Assert.Equal(Lead.II, rq.Assemble.SliceLead);
        Assert.Equal(3, rq.Assemble.BlockList.Count);
        var p = rq.Assemble.Of(EcgBlock.P)!;
        Assert.Equal(new[] { 0, 1, 0 }, p.Correct.SampleList);
        Assert.Equal(2, p.DistractorList.Count);
    }
}
