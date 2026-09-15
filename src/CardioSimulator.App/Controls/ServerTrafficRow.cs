using System.Globalization;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
using CardioSimulator.Core.Network;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Immutable display row for <see cref="ServerTrafficView"/>'s virtualized list: one <see cref="TcpTrafficEntry"/>
/// pre-formatted for the compiled <c>x:Bind</c> item template. The localized cells (event names, the direction
/// tooltip) are captured at construction, so after a language switch the view re-creates its rows instead of
/// mutating them. Rows are built on the UI thread only (they hold brushes).
/// </summary>
public sealed class ServerTrafficRow
{
    /// <summary>Sent frames: a mid blue that reads on both the light and the dark card (theme-invariant).</summary>
    public static readonly Windows.UI.Color SentColor = new() { A = 0xFF, R = 0x3B, G = 0x82, B = 0xF6 };

    // Shared, theme-invariant category brushes. Lazily created on first use, which is always on the UI thread
    // (rows are only constructed there), so every row reuses four brushes instead of allocating its own.
    private static SolidColorBrush? _sentBrush;
    private static SolidColorBrush? _receivedBrush;
    private static SolidColorBrush? _eventBrush;
    private static SolidColorBrush? _errorBrush;

    public ServerTrafficRow(TcpTrafficEntry entry)
    {
        Entry = entry;
        TimeText = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        DirectionGlyph = entry.Direction switch
        {
            TcpTrafficDirection.Outgoing => "→",
            TcpTrafficDirection.Incoming => "←",
            _ => "•",
        };
        DirectionTooltip = DirectionLabel(entry.Direction);
        TypeText = TypeLabel(entry);
        Summary = entry.Summary ?? string.Empty;
        // Events carry no bytes on the wire, so their size cell stays blank rather than reading "0 B".
        SizeText = entry.Direction == TcpTrafficDirection.Event ? string.Empty : TcpTrafficLog.FormatBytes(entry.ByteCount);
        AccentBrush = AccentBrushFor(entry);
    }

    /// <summary>The underlying log entry (full payload preview, correlation id, …) for the details pane.</summary>
    public TcpTrafficEntry Entry { get; }

    public long Sequence => Entry.Sequence;

    public bool IsError => Entry.IsError;

    /// <summary>Local wall-clock time, <c>HH:mm:ss.fff</c>.</summary>
    public string TimeText { get; }

    /// <summary><c>→</c> sent, <c>←</c> received, <c>•</c> connection event — plain text, present on Windows 10.</summary>
    public string DirectionGlyph { get; }

    public string DirectionTooltip { get; }

    /// <summary>The wire token (<c>query</c>, <c>OK</c>, …), or the localized event name for events.</summary>
    public string TypeText { get; }

    public string Summary { get; }

    /// <summary>Human byte size of the frame; blank for events.</summary>
    public string SizeText { get; }

    /// <summary>Direction glyph + type colour: blue sent, green received, gray event, red for any error.</summary>
    public Brush AccentBrush { get; }

    /// <summary>Case-insensitive substring match over the wire token, the localized type, the summary and the
    /// correlation id — so pasting an id from a JSON status reply also finds the query / rhythm that carried it.</summary>
    public bool MatchesSearch(string query) =>
        string.IsNullOrEmpty(query)
        || (Entry.Kind?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        || TypeText.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Summary.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (Entry.CorrelationId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>What a screen reader announces for the list item (the ListViewItem peer names itself from the data
    /// item) and what a default text copy yields: time, direction, type, summary, size.</summary>
    public override string ToString() => $"{TimeText} {DirectionTooltip} {TypeText} {Summary} {SizeText}".Trim();

    /// <summary>Localized "App → server" / "Server → app" / "Connection event".</summary>
    public static string DirectionLabel(TcpTrafficDirection direction) => direction switch
    {
        TcpTrafficDirection.Outgoing => AppStrings.ServerLogDirSent,
        TcpTrafficDirection.Incoming => AppStrings.ServerLogDirReceived,
        _ => AppStrings.ServerLogDirEvent,
    };

    private static SolidColorBrush AccentBrushFor(TcpTrafficEntry entry)
    {
        if (entry.IsError) return _errorBrush ??= new SolidColorBrush(AppTheme.NegativeColor);
        return entry.Direction switch
        {
            TcpTrafficDirection.Outgoing => _sentBrush ??= new SolidColorBrush(SentColor),
            TcpTrafficDirection.Incoming => _receivedBrush ??= new SolidColorBrush(AppTheme.PositiveColor),
            _ => _eventBrush ??= new SolidColorBrush(AppTheme.AppTextSecondaryColor),
        };
    }

    private static string TypeLabel(TcpTrafficEntry entry)
    {
        if (entry.Direction != TcpTrafficDirection.Event) return entry.Kind ?? string.Empty;
        var name = AppStrings.ServerLogEventName(entry.Event);
        return string.IsNullOrEmpty(name) ? entry.Kind ?? string.Empty : name;
    }
}
