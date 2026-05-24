namespace CardioSimulator.Core.Data;

/// <summary>Baseline-zeroed sample buffer for one lead, ready for the renderer.</summary>
public sealed record Points(IReadOnlyList<float> Values);
