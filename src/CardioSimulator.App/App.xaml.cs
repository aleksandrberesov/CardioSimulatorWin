using System;
using System.IO;
using CardioSimulator.App.Data;
using Microsoft.UI.Xaml;

namespace CardioSimulator.App;

public partial class App : Application
{
    /// <summary>The app's main window. Used by dialogs that need an HWND (e.g. file pickers).</summary>
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    /// <summary>
    /// Last-resort logger: append the exception to <c>%LOCALAPPDATA%\CardioSimulator\crash.log</c> so a
    /// spontaneous close leaves a stack trace behind. Best-effort and never throws; does not mark the
    /// exception handled (behavior is otherwise unchanged).
    /// </summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.AppendAllText(
                Path.Combine(AppPaths.Root, "crash.log"),
                $"{DateTimeOffset.Now:o}\n{e.Message}\n{e.Exception}\n\n");
        }
        catch
        {
            // never throw from the unhandled-exception handler
        }
    }
}
