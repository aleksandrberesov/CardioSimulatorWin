using System;
using System.IO;
using CardioSimulator.App.Data;
using Microsoft.UI.Xaml;

namespace CardioSimulator.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
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
