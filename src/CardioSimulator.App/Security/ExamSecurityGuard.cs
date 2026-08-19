using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CardioSimulator.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace CardioSimulator.App.Security;

/// <summary>
/// Manages anti-cheat defense during active Test, Examination, and OSKE attempts:
/// 1. Prevents screen captures via Win32 <c>SetWindowDisplayAffinity</c> (<c>WDA_EXCLUDEFROMCAPTURE</c>).
/// 2. Detects screen switching (loss of window activation) and screenshot key shortcuts (<c>PrintScreen</c>).
/// 3. Instantly terminates active attempts upon violation and shows a warning dialog.
/// </summary>
public sealed class ExamSecurityGuard
{
    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_MONITOR = 0x00000001;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private readonly Window _window;
    private readonly nint _hwnd;
    private bool _isProtectionActive;
    private Action? _onViolationCallback;
    private bool _dialogShowing;

    public ExamSecurityGuard(Window window, nint hwnd)
    {
        _window = window;
        _hwnd = hwnd;
        _window.Activated += OnWindowActivated;
        if (_window.Content is FrameworkElement root)
        {
            root.PreviewKeyDown += OnPreviewKeyDown;
        }
    }

    public bool IsProtectionActive => _isProtectionActive;

    public void UpdateProtectionState(bool isActive, Action? onViolation)
    {
        if (_isProtectionActive == isActive)
        {
            _onViolationCallback = onViolation;
            return;
        }

        _isProtectionActive = isActive;
        _onViolationCallback = onViolation;

        if (_hwnd != nint.Zero)
        {
            var affinity = isActive ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE;
            if (isActive && !SetWindowDisplayAffinity(_hwnd, affinity))
            {
                // Fallback to WDA_MONITOR for older Windows builds if WDA_EXCLUDEFROMCAPTURE fails
                SetWindowDisplayAffinity(_hwnd, WDA_MONITOR);
            }
            else if (!isActive)
            {
                SetWindowDisplayAffinity(_hwnd, WDA_NONE);
            }
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_isProtectionActive && args.WindowActivationState == WindowActivationState.Deactivated)
        {
            TriggerViolation();
        }
    }

    private void OnPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!_isProtectionActive) return;

        // Catch PrintScreen (VirtualKey.Snapshot)
        if (e.Key == VirtualKey.Snapshot)
        {
            e.Handled = true;
            TriggerViolation();
        }
    }

    public void TriggerViolation()
    {
        if (!_isProtectionActive) return;

        var callback = _onViolationCallback;
        UpdateProtectionState(false, null);

        callback?.Invoke();
        _ = ShowSecurityViolationDialogAsync();
    }

    private async Task ShowSecurityViolationDialogAsync()
    {
        if (_dialogShowing) return;
        _dialogShowing = true;
        try
        {
            if (_window.Content?.XamlRoot is { } xamlRoot)
            {
                var dialog = new ContentDialog
                {
                    Title = AppStrings.SecurityViolationTitle,
                    Content = new TextBlock
                    {
                        Text = AppStrings.SecurityViolationMessage,
                        TextWrapping = TextWrapping.Wrap
                    },
                    CloseButtonText = AppStrings.DataSourceClose,
                    XamlRoot = xamlRoot,
                };
                await dialog.ShowAsync();
            }
        }
        catch
        {
            // Ignore if a dialog is already active or XamlRoot is unavailable
        }
        finally
        {
            _dialogShowing = false;
        }
    }
}
