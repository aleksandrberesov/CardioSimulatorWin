namespace CardioSimulator.Core.Domain;

/// <summary>
/// A named snapshot of a comparison layout: which pathologies and which lead to overlay.
/// Persisted as JSON under the <c>${mode}_comparison_presets</c> key.
/// </summary>
public sealed record ComparisonPreset(string Name, IReadOnlyList<string> PathologyIds, Lead Lead);
