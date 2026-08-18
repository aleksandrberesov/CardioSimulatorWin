namespace CardioSimulator.App.ViewModels;

/// <summary>
/// How a content-pack export finished. Distinguishing <see cref="Canceled"/> from <see cref="Failed"/>
/// lets the status dialog show "cancelled by you" rather than an error when the user stops the export.
/// </summary>
public enum ExportOutcome
{
    Success,
    Failed,
    Canceled,
}
