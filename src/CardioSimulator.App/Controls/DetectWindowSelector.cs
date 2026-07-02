using System;
using CardioSimulator.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Compact ECG analysis-window picker (Full / 1 / 3 / 5 / 10 s). Options longer than the current
/// lead duration are disabled, and a persisted choice that no longer fits falls back to Full. Shared
/// by the Constructor's significant-point panel and the Teaching monitor's measurements column so
/// both scope significant-point work to the same windows.
/// </summary>
public sealed class DetectWindowSelector : ComboBox
{
    private static readonly double?[] Options = { null, 1, 3, 5, 10 };
    private double? _seconds;      // null = Full (whole lead)
    private float _durationSec = -1f;
    private bool _suppress;

    /// <summary>The chosen window in seconds (<c>null</c> = whole lead).</summary>
    public double? WindowSeconds => _seconds;

    /// <summary>Raised when the user picks a different window (not on programmatic <see cref="Configure"/>).</summary>
    public event Action? Changed;

    public DetectWindowSelector()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        FontSize = 12;
        Rebuild();
        SelectionChanged += (_, _) =>
        {
            if (_suppress || SelectedItem is not ComboBoxItem item) return;
            _seconds = item.Tag as double?;
            Changed?.Invoke();
        };
    }

    /// <summary>
    /// Sets the lead length so options longer than the recording get disabled; a too-long selection
    /// falls back to Full. Never raises <see cref="Changed"/>. Cheap to call repeatedly — it only
    /// rebuilds when the duration bucket or the selection actually changes.
    /// </summary>
    public void Configure(float sampleRate, int sampleCount)
    {
        var newDuration = sampleRate > 0 ? sampleCount / sampleRate : 0f;
        var resetNeeded = _seconds is { } s && s > newDuration;
        if (Math.Abs(newDuration - _durationSec) < 0.001f && !resetNeeded) return;
        _durationSec = newDuration;
        if (resetNeeded) _seconds = null;
        Rebuild();
    }

    private void Rebuild()
    {
        _suppress = true;
        Items.Clear();
        var selected = 0;
        for (var i = 0; i < Options.Length; i++)
        {
            var sec = Options[i];
            var enabled = sec is not { } s || s <= _durationSec;
            Items.Add(new ComboBoxItem
            {
                Content = sec is { } v ? AppStrings.EditorDetectWindowSeconds((int)v) : AppStrings.EditorDetectWindowFull,
                Tag = sec,
                IsEnabled = enabled,
            });
            if (sec == _seconds) selected = i;
        }
        SelectedIndex = selected;
        _suppress = false;
    }
}
