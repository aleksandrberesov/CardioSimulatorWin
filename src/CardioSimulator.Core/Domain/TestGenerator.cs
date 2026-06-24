using System;
using System.Collections.Generic;
using System.Linq;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Builds an ad-hoc <see cref="Test"/> by drawing questions at random from the question bank — the
/// engine behind «Формирование теста» (Individual generates one locally; Group generates one per
/// student). Pure (no IO): the caller supplies the bank snapshot and a <see cref="Random"/>, so it is
/// deterministic under test.
/// </summary>
public static class TestGenerator
{
    /// <summary>The question counts offered in the UI (10 / 20 / 30).</summary>
    public static readonly IReadOnlyList<int> CountOptions = new[] { 10, 20, 30 };

    /// <summary>
    /// Picks up to <paramref name="count"/> distinct questions from <paramref name="bank"/> (optionally
    /// restricted to <paramref name="theme"/>), shuffled, renumbered 1..N. When fewer matching questions
    /// exist than requested, the test simply contains all of them. Returns a test with no questions when
    /// the bank (or theme subset) is empty.
    /// </summary>
    public static Test Generate(IReadOnlyList<TestQuestion> bank, int count, string? theme = null, Random? rng = null)
    {
        rng ??= new Random();
        if (count < 1) count = 1;

        var pool = string.IsNullOrWhiteSpace(theme)
            ? bank.AsEnumerable()
            : bank.Where(q => string.Equals(q.Theme, theme, StringComparison.CurrentCultureIgnoreCase));

        // Fisher–Yates over a copy, then take the first `count`.
        var shuffled = pool.ToList();
        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        var picked = shuffled
            .Take(count)
            .Select((q, i) => q with { Number = i + 1 })
            .ToList();

        return new Test(
            TestId: "gen_" + Guid.NewGuid().ToString("N")[..8],
            Title: TitleFor(picked.Count, theme),
            Questions: picked,
            QuestionTimeSeconds: 0);
    }

    private static string TitleFor(int count, string? theme) =>
        string.IsNullOrWhiteSpace(theme)
            ? $"Тест — {count} вопр."
            : $"Тест — {theme} ({count} вопр.)";
}
