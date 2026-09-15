using System.ComponentModel;
using System.Runtime.InteropServices;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Security;
using CardioSimulator.App.Theming;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using AppRole = CardioSimulator.Core.Domain.AppRole;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The administrator "Server message log": a separate top-level window hosting a <see cref="ServerTrafficView"/>
/// over <see cref="AppViewModel.TcpTraffic"/>, so a tester can watch the TCP server conversation live while using
/// the app. Single instance, opened from Settings (Admin role, Full edition). Unlike the in-window popups it is its
/// own XAML root, so it follows the app theme and language by itself; it closes with the main window (otherwise the
/// process would stay alive) and as soon as the role leaves Admin.
///
/// <para>Never open during a protected Test / Examination / OSKE attempt: activating this window would deactivate the
/// main one (the exam guard then ends the attempt), and the log shows the rhythm ids and names being streamed. It
/// refuses to open while <see cref="ExamSecurityGuard.IsProtectionActive"/> and closes itself when protection turns on.</para>
/// </summary>
public static class ServerTrafficWindow
{
    /// <summary>Initial size in DIPs — scaled by the window's DPI on the main window's monitor, clamped to its work area.</summary>
    private const int DefaultWidth = 1100;
    private const int DefaultHeight = 720;

    /// <summary>Minimum size in DIPs, scaled the same way: below this the one-row toolbar starts to clip.</summary>
    private const int MinimumWidth = 860;
    private const int MinimumHeight = 520;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private static Window? _window;
    private static ServerTrafficView? _view;
    private static AppViewModel? _appVm;
    private static Window? _mainWindow;
    private static ExamSecurityGuard? _securityGuard;

    /// <summary>True while the log window is open.</summary>
    public static bool IsOpen => _window is not null;

    /// <summary>The main window's exam guard; null before the shell exists.</summary>
    private static ExamSecurityGuard? CurrentSecurityGuard => (App.MainWindow as MainWindow)?.SecurityGuard;

    /// <summary>Opens the single log window (or activates it if already open). Does nothing while an attempt is under
    /// exam protection (Settings disables its button then; this covers every other path, including bring-to-front).</summary>
    public static void ShowOrActivate(AppViewModel appVm)
    {
        if (CurrentSecurityGuard is { IsProtectionActive: true }) return;

        if (_window is not null)
        {
            BringToFront(_window);
            return;
        }
        // Administrator tool: the Settings button is hidden for User, and a User must never reach it another way.
        if (appVm.Role != AppRole.Admin) return;

        // Built once; the view mutates its controls in place for language/theme changes afterwards. A new window
        // is a new XAML root and does NOT inherit the theme the app sets on the main window's root — pin it.
        var view = new ServerTrafficView(appVm) { RequestedTheme = AppTheme.Current };
        var window = new Window { Content = view, Title = BuildTitle() };

        _window = window;
        _view = view;
        _appVm = appVm;
        _mainWindow = App.MainWindow;
        _securityGuard = CurrentSecurityGuard;

        ApplyTitleBarTheme(window);
        PlaceWindow(window, _mainWindow);
        TrySetIcon(window);

        window.Closed += OnWindowClosed;
        appVm.PropertyChanged += OnAppChanged;
        AppTheme.Changed += OnThemeChanged;
        AppStrings.Changed += OnStringsChanged;
        if (_mainWindow is not null) _mainWindow.Closed += OnMainWindowClosed;
        if (_securityGuard is not null) _securityGuard.ProtectionChanged += OnProtectionChanged;

        window.Activate();
    }

    /// <summary>Closes the log window if it is open, releasing every subscription first.</summary>
    public static void CloseIfOpen()
    {
        var window = _window;
        if (window is null) return;
        Detach();
        try { window.Close(); }
        catch { /* already closing */ }
    }

    private static void OnWindowClosed(object sender, WindowEventArgs args) => Detach();

    // The log window is a top-level window of its own: left open, it would keep the process alive after the
    // user closes the app.
    private static void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        if (args.Handled) return; // the close was cancelled — the app stays up, so does the log
        CloseIfOpen();
    }

    private static void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppViewModel.Role)) return;
        var window = _window;
        if (window is null) return;
        if (window.DispatcherQueue.HasThreadAccess) CloseIfNotAdmin();
        else window.DispatcherQueue.TryEnqueue(CloseIfNotAdmin);
    }

    private static void CloseIfNotAdmin()
    {
        if (_appVm is { Role: not AppRole.Admin }) CloseIfOpen();
    }

    // An attempt just came under exam protection: close before it can be seen or clicked into (either would expose
    // the streamed rhythm or deactivate the main window and end the attempt). Raised on the UI thread; marshalled anyway.
    private static void OnProtectionChanged(bool active)
    {
        if (!active) return;
        var window = _window;
        if (window is null) return;
        if (window.DispatcherQueue.HasThreadAccess) CloseIfOpen();
        else window.DispatcherQueue.TryEnqueue(CloseIfOpen);
    }

    // RequestedTheme is a one-shot seed: re-apply it (the view's {ThemeResource} brushes then recolour live) and
    // keep the system caption bar in step.
    private static void OnThemeChanged()
    {
        if (_window is null || _view is null) return;
        _view.RequestedTheme = AppTheme.Current;
        ApplyTitleBarTheme(_window);
    }

    private static void OnStringsChanged()
    {
        if (_window is not null) _window.Title = BuildTitle();
    }

    /// <summary>Unsubscribes every handler registered at open (window, view-model, theme, language, main window, exam
    /// guard, and the view's own log/view-model/language handlers). Idempotent.</summary>
    private static void Detach()
    {
        var window = _window;
        if (window is null) return;

        window.Closed -= OnWindowClosed;
        if (_appVm is not null) _appVm.PropertyChanged -= OnAppChanged;
        AppTheme.Changed -= OnThemeChanged;
        AppStrings.Changed -= OnStringsChanged;
        if (_mainWindow is not null) _mainWindow.Closed -= OnMainWindowClosed;
        if (_securityGuard is not null) _securityGuard.ProtectionChanged -= OnProtectionChanged;
        _view?.Detach();

        _window = null;
        _view = null;
        _appVm = null;
        _mainWindow = null;
        _securityGuard = null;
    }

    private static string BuildTitle() => $"{AppStrings.ServerLogTitle} — {BuildInfo.Name}";

    private static void BringToFront(Window window)
    {
        try
        {
            if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            {
                presenter.Restore();
            }
        }
        catch { /* activation below still brings it forward */ }
        window.Activate();
    }

    private static void ApplyTitleBarTheme(Window window)
    {
        try
        {
            window.AppWindow.TitleBar.PreferredTheme = AppTheme.IsDark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        }
        catch
        {
            // Title-bar theming unsupported on this OS/runtime — keep the system caption colours.
        }
    }

    /// <summary>Centres the window on the main window's monitor work area at <see cref="DefaultWidth"/>×<see cref="DefaultHeight"/>
    /// DIPs and sets its minimum size. AppWindow sizes are physical pixels, so this runs in two steps: first move the
    /// window fully inside the target work area (landing on a monitor of another DPI rescales it there), then read the
    /// log window's OWN DPI and size it. Sizing by the main window's DPI in a single move would let that DPI change
    /// rescale an already-scaled size (too small or too large, and no longer centred).</summary>
    private static void PlaceWindow(Window window, Window? owner)
    {
        RectInt32 work;
        double scale;
        try
        {
            var anchor = owner ?? window;
            work = DisplayArea.GetFromWindowId(anchor.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

            // Step 1: onto the target monitor (the middle half of its work area).
            window.AppWindow.MoveAndResize(new RectInt32(
                work.X + work.Width / 4,
                work.Y + work.Height / 4,
                Math.Max(1, work.Width / 2),
                Math.Max(1, work.Height / 2)));

            // Step 2: the DPI the window now has there → final size, centred and clamped to the work area.
            var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
            scale = dpi > 0 ? dpi / 96.0 : 1.0;
            var width = Math.Min((int)Math.Round(DefaultWidth * scale), work.Width);
            var height = Math.Min((int)Math.Round(DefaultHeight * scale), work.Height);
            window.AppWindow.MoveAndResize(new RectInt32(
                work.X + (work.Width - width) / 2,
                work.Y + (work.Height - height) / 2,
                width,
                height));
        }
        catch
        {
            try { window.AppWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight)); }
            catch { /* keep the OS default placement */ }
            return;
        }

        TrySetMinimumSize(window, scale, work);
    }

    /// <summary><see cref="MinimumWidth"/>×<see cref="MinimumHeight"/> DIPs at the window's DPI (the presenter's preferred
    /// minimum is in pixels), never more than the work area.</summary>
    private static void TrySetMinimumSize(Window window, double scale, RectInt32 work)
    {
        try
        {
            if (window.AppWindow.Presenter is not OverlappedPresenter presenter) return;
            presenter.PreferredMinimumWidth = Math.Min((int)Math.Round(MinimumWidth * scale), work.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)Math.Round(MinimumHeight * scale), work.Height);
        }
        catch
        {
            // Unsupported on this runtime — the window just has no minimum size.
        }
    }

    /// <summary>Uses the exe's icon file when it was deployed next to the app; it isn't copied to the output by
    /// default, in which case the window simply keeps the default caption icon.</summary>
    private static void TrySetIcon(Window window)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(path)) window.AppWindow.SetIcon(path);
        }
        catch
        {
            // cosmetic only
        }
    }
}
