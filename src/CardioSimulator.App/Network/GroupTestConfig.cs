using System;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.Network;

/// <summary>
/// Describes how a Group session builds each participant's test — the classroom equivalent of the
/// Individual «Формирование теста» setup, so the two flows offer the <b>same test configuration</b>.
/// Produced by the shared Quick-Test setup panel (ready test / generate over the course themes) and
/// consumed by <see cref="GroupTestServer"/>, which calls <see cref="Next"/> once per registrant.
/// </summary>
/// <remarks>
/// A «ready test» setup returns a copy of the same authored test to everyone (the factory yields the
/// shared, read-only <see cref="Test"/> instance); a «generate» setup draws a fresh, individually
/// randomized test each time from the chosen types / count / time / difficulty / theme. <see cref="Next"/>
/// is invoked on the server's background threads, so the factory must be self-contained (no UI access).
/// </remarks>
public sealed class GroupTestConfig
{
    private readonly Func<Test?> _factory;

    public GroupTestConfig(Func<Test?> factory) => _factory = factory;

    /// <summary>Builds the next participant's test (a shared fixed test, or a fresh generated draw).
    /// May return null if the underlying bank can no longer satisfy the setup.</summary>
    public Test? Next() => _factory();
}
