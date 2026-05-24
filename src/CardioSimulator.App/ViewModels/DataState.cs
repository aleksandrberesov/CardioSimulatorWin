namespace CardioSimulator.App.ViewModels;

/// <summary>
/// High-level state of the user-controlled ECG dataset, mirroring the Android
/// <c>DataState</c>. Lifecycle: NotConfigured → Loading → (Ready | Error).
/// Re-picking a ZIP cycles back through Loading.
/// </summary>
public abstract record DataState
{
    public sealed record NotConfigured : DataState;

    public sealed record Loading : DataState;

    public sealed record Ready(int PathologyCount) : DataState;

    public sealed record Error(ErrorReason Reason) : DataState;

    public enum ErrorReason
    {
        Unreadable,
        Empty,
        BadManifest,
    }
}
