namespace CardioSimulator.Core.Domain;

/// <summary>
/// One comparison pane: a pathology shown at a specific lead. Port of the Android
/// <c>ComparisonTarget</c>.
/// </summary>
public sealed record ComparisonTarget(string PathologyId, Lead Lead);
