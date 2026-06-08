using System.Collections.Generic;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// A named snapshot of a comparison layout: a per-pane map of (pathology, lead) targets.
/// Persisted as JSON under the <c>${mode}_comparison_presets</c> key. Port of the Android
/// <c>ComparisonPreset</c>.
/// </summary>
public sealed record ComparisonPreset(string Name, IReadOnlyDictionary<int, ComparisonTarget> Targets);
