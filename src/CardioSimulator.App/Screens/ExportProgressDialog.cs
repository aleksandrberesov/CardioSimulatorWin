using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Data;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Modal status dialog for a long-running content-pack export (ECG data, course packs, and any other
/// <see cref="AppViewModel"/> export that streams entries). It shows a live tally while the pack is
/// written and, because an export can take a while, lets the user interrupt it — but a cancel always
/// goes through an explicit "stop the export?" confirmation so an accidental click doesn't throw away
/// the work in progress.
///
/// <para>The dialog opts out of the app-wide click-outside light dismiss: abandoning the dialog would
/// leave the export running unseen and bypass the confirmation, so the only ways out are the buttons.
/// It drives a single <see cref="ContentDialog"/> through four phases (running → confirm → stopping →
/// done) by reconfiguring the built-in buttons and swapping the visible body, keeping every state in
/// one modal rather than stacking dialogs (WinUI allows only one <see cref="ContentDialog"/> at a
/// time).</para>
/// </summary>
public static class ExportProgressDialog
{
    private enum Phase { Running, Confirm, Stopping, Done }

    /// <summary>
    /// Runs <paramref name="work"/> while showing progress, and returns its <see cref="ExportOutcome"/>.
    /// <paramref name="work"/> receives an <see cref="IProgress{T}"/> that marshals to the UI thread and
    /// a <see cref="CancellationToken"/> that trips when the user confirms cancellation.
    /// </summary>
    public static async Task<ExportOutcome> ShowAsync(
        XamlRoot xamlRoot,
        string title,
        Func<IProgress<ExportProgress>, CancellationToken, Task<ExportOutcome>> work)
    {
        using var cts = new CancellationTokenSource();
        var phase = Phase.Running;
        var outcome = ExportOutcome.Failed;
        var latest = new ExportProgress(0, 0, null);

        // Running / stopping body: spinner + status line + running tally.
        var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        var statusText = new TextBlock
        {
            Text = AppStrings.ExportPreparing,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
        };
        var spinnerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        spinnerRow.Children.Add(ring);
        spinnerRow.Children.Add(statusText);
        var detailText = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var runningView = new StackPanel { Spacing = 10, MinWidth = 340 };
        runningView.Children.Add(spinnerRow);
        runningView.Children.Add(detailText);

        // Confirm body: the "are you sure?" question while the export keeps running behind it.
        var confirmView = new StackPanel { Spacing = 8, MinWidth = 340 };
        confirmView.Children.Add(new TextBlock
        {
            Text = AppStrings.ExportCancelConfirm,
            TextWrapping = TextWrapping.Wrap,
        });

        // Done body: outcome headline + (on success) the final tally.
        var doneText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, FontSize = 15 };
        var doneDetail = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var doneView = new StackPanel { Spacing = 8, MinWidth = 340 };
        doneView.Children.Add(doneText);
        doneView.Children.Add(doneDetail);

        var host = new Grid();
        host.Children.Add(runningView);
        host.Children.Add(confirmView);
        host.Children.Add(doneView);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = host,
            XamlRoot = xamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
            // Seed the running-phase buttons so nothing flashes before Opened runs Render().
            CloseButtonText = AppStrings.CommonCancel,
        };
        // A click outside must not silently abandon a running export; cancelling goes through the
        // confirmation instead. (App-wide light dismiss is opt-out per instance.)
        Controls.DialogLightDismiss.SetIsEnabled(dialog, false);

        void OnThemeChanged() => dialog.RequestedTheme = Theming.AppTheme.Current;
        Theming.AppTheme.Changed += OnThemeChanged;
        dialog.Closed += (_, _) => Theming.AppTheme.Changed -= OnThemeChanged;

        void Render()
        {
            runningView.Visibility = phase is Phase.Running or Phase.Stopping ? Visibility.Visible : Visibility.Collapsed;
            confirmView.Visibility = phase == Phase.Confirm ? Visibility.Visible : Visibility.Collapsed;
            doneView.Visibility = phase == Phase.Done ? Visibility.Visible : Visibility.Collapsed;

            switch (phase)
            {
                case Phase.Running:
                    ring.IsActive = true;
                    dialog.PrimaryButtonText = "";
                    dialog.CloseButtonText = AppStrings.CommonCancel;
                    dialog.DefaultButton = ContentDialogButton.None;
                    break;
                case Phase.Confirm:
                    dialog.PrimaryButtonText = AppStrings.ExportCancelStop;
                    dialog.CloseButtonText = AppStrings.ExportCancelKeep;
                    dialog.DefaultButton = ContentDialogButton.Close; // safe default = keep going
                    break;
                case Phase.Stopping:
                    ring.IsActive = true;
                    statusText.Text = AppStrings.ExportStopping;
                    detailText.Text = "";
                    dialog.PrimaryButtonText = "";
                    dialog.CloseButtonText = ""; // no way out until the worker acknowledges the cancel
                    dialog.DefaultButton = ContentDialogButton.None;
                    break;
                case Phase.Done:
                    dialog.PrimaryButtonText = "";
                    dialog.CloseButtonText = AppStrings.CommonClose;
                    dialog.DefaultButton = ContentDialogButton.Close;
                    break;
            }
        }

        // Primary button only exists in the Confirm phase — it is the destructive "Stop export".
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (phase != Phase.Confirm) return;
            args.Cancel = true;          // keep the dialog open; wait for the worker to wind down
            phase = Phase.Stopping;
            cts.Cancel();
            Render();
        };

        dialog.CloseButtonClick += (_, args) =>
        {
            switch (phase)
            {
                case Phase.Running:          // "Cancel" → ask before actually stopping
                    args.Cancel = true;
                    phase = Phase.Confirm;
                    Render();
                    break;
                case Phase.Confirm:          // "Keep exporting" → back to the live view
                    args.Cancel = true;
                    phase = Phase.Running;
                    UpdateRunning(statusText, detailText, latest);
                    Render();
                    break;
                case Phase.Stopping:         // buttons are hidden here, but guard anyway
                    args.Cancel = true;
                    break;
                    // Phase.Done → let the close proceed.
            }
        };

        dialog.Opened += async (_, _) =>
        {
            Render();

            // Constructed on the UI thread, so Report(...) posts back to it.
            var progress = new Progress<ExportProgress>(p =>
            {
                latest = p;
                if (phase == Phase.Running) UpdateRunning(statusText, detailText, p);
            });

            try { outcome = await work(progress, cts.Token); }
            catch (OperationCanceledException) { outcome = ExportOutcome.Canceled; }
            catch { outcome = ExportOutcome.Failed; }

            phase = Phase.Done;
            doneText.Text = outcome switch
            {
                ExportOutcome.Success => AppStrings.ExportDoneSuccess,
                ExportOutcome.Canceled => AppStrings.ExportDoneCanceled,
                _ => AppStrings.ExportDoneFailed,
            };
            var showTally = outcome == ExportOutcome.Success && latest.EntriesWritten > 0;
            doneDetail.Text = showTally
                ? AppStrings.ExportProgressDetailFormat(latest.EntriesWritten, FormatBytes(latest.BytesWritten))
                : "";
            doneDetail.Visibility = showTally ? Visibility.Visible : Visibility.Collapsed;
            Render();
        };

        await dialog.ShowAsync();
        return outcome;
    }

    private static void UpdateRunning(TextBlock status, TextBlock detail, ExportProgress p)
    {
        if (p.EntriesWritten <= 0)
        {
            status.Text = AppStrings.ExportPreparing;
            detail.Text = "";
            return;
        }
        status.Text = AppStrings.ExportRunning;
        detail.Text = AppStrings.ExportProgressDetailFormat(p.EntriesWritten, FormatBytes(p.BytesWritten));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.#} MB";
        return $"{mb / 1024.0:0.##} GB";
    }
}
