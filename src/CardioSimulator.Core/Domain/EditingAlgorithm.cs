namespace CardioSimulator.Core.Domain;

/// <summary>
/// Smoothing kernels for weighted ECG sample edits — port of the Android
/// <c>EditingAlgorithm</c> enum in <c>ConstructorViewModel.kt</c>. Each kernel produces
/// weight 1.0 at the center and tapers to 0 at <c>|d| = radius</c> with a different
/// shape; callers accumulate weighted contributions in a per-lead float buffer so
/// repeated +/-1 nudges build a genuinely smooth bump (rather than a block of samples
/// moving synchronously where per-call rounding goes over 0.5).
/// </summary>
public enum EditingAlgorithm
{
    Cosine,
    Spline,
    Bezier,
    LOESS,
    MLS,
}
