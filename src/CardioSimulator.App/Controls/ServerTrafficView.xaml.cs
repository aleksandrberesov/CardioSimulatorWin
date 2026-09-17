using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Network;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using TcpState = CardioSimulator.Core.Network.TcpConnectionState;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Content of the "Server message log" window (<see cref="ServerTrafficWindow"/>): a live,
/// filterable view of <see cref="AppViewModel.TcpTraffic"/> — every frame sent to / received from the monitor
/// server plus connection events — with a connection header, a toolbar, a virtualized list and a details pane.
///
/// <para>Real-time path: the log raises <see cref="TcpTrafficLog.EntryAdded"/> on the recording (socket) thread;
/// the handler only enqueues into a <see cref="ConcurrentQueue{T}"/> and schedules at most one pending
/// <c>DispatcherQueue.TryEnqueue</c> drain, which appends the whole batch on the UI thread. No DispatcherQueueTimer — those don't tick while a
/// ContentDialog (e.g. Settings) is up. <see cref="TcpTrafficLog.Cleared"/> travels through the same queue as a
/// <c>null</c> marker, so a clear lands exactly between the entries recorded before and after it.</para>
///
/// <para>Built once: language and theme changes mutate the existing controls in place (see the persistent-element
/// reparent crash notes); the window calls <see cref="Detach"/> when it closes.</para>
/// </summary>
public sealed partial class ServerTrafficView : UserControl
{
    private enum TrafficFilter { All, Sent, Received, Events, Errors }

    /// <summary>Above this many rows appended (or trimmed) at once, the visible list is rebuilt with one collection reset
    /// instead of a change notification per row: an un-pause, or a drain after a UI-thread stall, can carry up to the
    /// log's capacity, and thousands of single Add / RemoveAt(0) notifications would freeze the shared UI thread.</summary>
    private const int BulkRowThreshold = 64;

    private readonly AppViewModel _appVm;
    private readonly TcpTrafficLog _log;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    // Recording thread → UI thread hand-off. A null item is a "log cleared" marker (keeps clear/append order).
    private readonly ConcurrentQueue<TcpTrafficEntry?> _inbox = new();
    private int _drainScheduled; // 1 while a drain is queued on the dispatcher (Interlocked)

    // Every row still held (oldest first, capped at the log's capacity) and the filtered subset the list shows.
    private readonly List<ServerTrafficRow> _allRows = new();
    private ObservableCollection<ServerTrafficRow> _visible = new();

    // Entries that arrived while paused; flushed into the list on un-pause. Capped like the list itself.
    private readonly Queue<TcpTrafficEntry> _pausedEntries = new();

    /// <summary>Highest <see cref="TcpTrafficEntry.Sequence"/> already taken in — dedupes the overlap between the
    /// initial <see cref="TcpTrafficLog.Snapshot"/> and <see cref="TcpTrafficLog.EntryAdded"/> deliveries.</summary>
    private long _lastSequence;

    private TrafficFilter _filter = TrafficFilter.All;
    private string _query = string.Empty;
    private bool _paused;
    private bool _initialized;
    private bool _suppressFilterEvents;
    private volatile bool _detached;

    /// <summary>The payload text shown in the details pane (pretty-printed when it was JSON) — what "Copy message" copies.</summary>
    private string _detailsBody = string.Empty;

    public ServerTrafficView(AppViewModel appVm)
    {
        _appVm = appVm;
        _log = appVm.TcpTraffic;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        InitializeComponent();

        LogList.ItemsSource = _visible;
        _suppressFilterEvents = true;
        FilterBox.SelectedIndex = 0;
        _suppressFilterEvents = false;

        ApplyStrings();
        UpdateStatus();
        ShowDetails(null);

        // Subscribe BEFORE taking the snapshot so nothing recorded in between is missed; anything delivered to
        // both is dropped by the Sequence dedupe. (Clear only ever comes from this window's own button on the UI
        // thread, so it can't interleave with this constructor.)
        _log.EntryAdded += OnEntryAdded;
        _log.Cleared += OnLogCleared;
        LoadSnapshot(_log.Snapshot());
        UpdateStats();

        _appVm.PropertyChanged += OnAppChanged;
        AppStrings.Changed += OnStringsChanged;

        // ScrollIntoView is a no-op before the list has been laid out — scroll to the newest row once shown.
        Loaded += OnFirstLoaded;
        _initialized = true;
    }

    /// <summary>
    /// Unsubscribes every handler this view registered (log events, view-model and language notifications) and
    /// drops any queued entries. Idempotent. Called by <see cref="ServerTrafficWindow"/> when the window closes.
    /// </summary>
    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        _log.EntryAdded -= OnEntryAdded;
        _log.Cleared -= OnLogCleared;
        _appVm.PropertyChanged -= OnAppChanged;
        AppStrings.Changed -= OnStringsChanged;
        Loaded -= OnFirstLoaded;
        while (_inbox.TryDequeue(out _)) { }
        ConnectingRing.IsActive = false;
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        ScrollToEndIfAuto();
    }

    // ── Real-time feed ─────────────────────────────────────────────────────────

    // Recording thread (often a thread-pool socket thread): enqueue and return — never touch UI or block here.
    private void OnEntryAdded(TcpTrafficEntry entry)
    {
        if (_detached) return;
        _inbox.Enqueue(entry);
        ScheduleDrain();
    }

    private void OnLogCleared()
    {
        if (_detached) return;
        _inbox.Enqueue(null);
        ScheduleDrain();
    }

    private void ScheduleDrain()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) != 0) return;
        if (!_dispatcher.TryEnqueue(DrainInbox))
        {
            Interlocked.Exchange(ref _drainScheduled, 0); // dispatcher shutting down — nothing to show anyway
        }
    }

    /// <summary>UI thread: takes in everything queued so far as one batch.</summary>
    private void DrainInbox()
    {
        // Reset the flag first: anything enqueued from here on schedules a fresh drain, so no entry is stranded.
        Interlocked.Exchange(ref _drainScheduled, 0);
        if (_detached) return;

        List<ServerTrafficRow>? batch = null;
        var changed = false;
        while (_inbox.TryDequeue(out var entry))
        {
            changed = true;
            if (entry is null)
            {
                // Log cleared: rows batched before the marker were cleared too.
                batch?.Clear();
                ResetRows();
                continue;
            }
            if (entry.Sequence <= _lastSequence) continue;
            _lastSequence = entry.Sequence;

            if (_paused)
            {
                _pausedEntries.Enqueue(entry);
                while (_pausedEntries.Count > _log.Capacity) _pausedEntries.Dequeue();
                continue;
            }
            (batch ??= new List<ServerTrafficRow>()).Add(new ServerTrafficRow(entry));
        }

        if (!changed) return;
        if (batch is { Count: > 0 }) AppendRows(batch);
        UpdateStats();
        UpdatePendingLabel();
        UpdateEmptyState(); // entries parked while paused change which empty-state text applies
    }

    private void LoadSnapshot(IReadOnlyList<TcpTrafficEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Sequence <= _lastSequence) continue;
            _allRows.Add(new ServerTrafficRow(entry));
            _lastSequence = entry.Sequence;
        }
        TrimToCapacity(); // the visible list is rebuilt right below either way
        RebuildVisible(selectSequence: null);
    }

    /// <summary>Appends rows (oldest first) to the backing list and, when they pass the filter, to the visible list;
    /// then trims both to the log's capacity and auto-scrolls. A bulk append — more than <see cref="BulkRowThreshold"/>
    /// rows added or trimmed — updates only the backing list and rebuilds the visible list with a single reset; small
    /// live drains keep the incremental path, which leaves the scroll position alone.</summary>
    private void AppendRows(IReadOnlyList<ServerTrafficRow> rows)
    {
        // A batch bigger than the cap would be trimmed straight away — skip those rows up front.
        var first = Math.Max(0, rows.Count - _log.Capacity);
        var count = rows.Count - first;
        if (count <= 0) return;

        if (count > BulkRowThreshold || _allRows.Count + count - _log.Capacity > BulkRowThreshold)
        {
            var selected = SelectedSequence;
            for (var i = first; i < rows.Count; i++) _allRows.Add(rows[i]);
            TrimToCapacity();
            RebuildVisible(selected);
            ScrollToEndIfAuto(); // follow the newest rows, as the incremental path does
            return;
        }

        var addedVisible = false;
        for (var i = first; i < rows.Count; i++)
        {
            var row = rows[i];
            _allRows.Add(row);
            if (!Matches(row)) continue;
            _visible.Add(row);
            addedVisible = true;
        }
        if (TrimToCapacity())
        {
            // Can't happen after the bulk check above; rebuilt rather than ever leaving the visible list stale.
            RebuildVisible(SelectedSequence);
            return;
        }
        UpdateEmptyState();
        if (addedVisible) ScrollToEndIfAuto();
    }

    /// <summary>Drops the oldest rows beyond the log's capacity. A small excess is removed from the visible list too, row
    /// by row (the visible rows are an in-order subsequence of the backing list, so a dropped visible row is always at
    /// its head). An excess above <see cref="BulkRowThreshold"/> is dropped from the backing list only and returns
    /// <c>true</c>: the visible list is then stale and the caller rebuilds it once (<see cref="RebuildVisible"/>).</summary>
    private bool TrimToCapacity()
    {
        var excess = _allRows.Count - _log.Capacity;
        if (excess <= 0) return false;
        if (excess > BulkRowThreshold)
        {
            _allRows.RemoveRange(0, excess);
            return true;
        }
        for (var i = 0; i < excess; i++)
        {
            if (_visible.Count > 0 && ReferenceEquals(_visible[0], _allRows[i])) _visible.RemoveAt(0);
        }
        _allRows.RemoveRange(0, excess);
        return false;
    }

    private void ResetRows()
    {
        _allRows.Clear();
        _pausedEntries.Clear();
        _visible.Clear();
        UpdateEmptyState();
        ShowDetails(null);
    }

    /// <summary>Re-applies the filter + search to every held row (O(n), one collection reset rather than n change
    /// notifications) and restores the selection by sequence when it is still visible.</summary>
    private void RebuildVisible(long? selectSequence)
    {
        var matches = new List<ServerTrafficRow>(_allRows.Count);
        ServerTrafficRow? reselect = null;
        foreach (var row in _allRows)
        {
            if (!Matches(row)) continue;
            matches.Add(row);
            if (row.Sequence == selectSequence) reselect = row;
        }

        _visible = new ObservableCollection<ServerTrafficRow>(matches);
        LogList.ItemsSource = _visible;
        UpdateEmptyState();

        if (reselect is not null)
        {
            LogList.SelectedItem = reselect;
            LogList.ScrollIntoView(reselect);
        }
        else
        {
            ShowDetails(null);
            ScrollToEndIfAuto();
        }
    }

    private bool Matches(ServerTrafficRow row)
    {
        var entry = row.Entry;
        var passes = _filter switch
        {
            TrafficFilter.Sent => entry.Direction == TcpTrafficDirection.Outgoing,
            TrafficFilter.Received => entry.Direction == TcpTrafficDirection.Incoming,
            TrafficFilter.Events => entry.Direction == TcpTrafficDirection.Event,
            TrafficFilter.Errors => entry.IsError,
            _ => true,
        };
        return passes && row.MatchesSearch(_query);
    }

    private void ScrollToEndIfAuto()
    {
        if (AutoScrollCheck.IsChecked != true || _paused || _visible.Count == 0) return;
        LogList.ScrollIntoView(_visible[^1]);
    }

    private long? SelectedSequence => (LogList.SelectedItem as ServerTrafficRow)?.Sequence;

    // ── Toolbar ────────────────────────────────────────────────────────────────

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterEvents || !_initialized) return;
        var filter = FilterBox.SelectedIndex switch
        {
            1 => TrafficFilter.Sent,
            2 => TrafficFilter.Received,
            3 => TrafficFilter.Events,
            4 => TrafficFilter.Errors,
            _ => TrafficFilter.All,
        };
        if (filter == _filter) return;
        _filter = filter;
        ResetCopyFeedback();
        RebuildVisible(SelectedSequence);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        var query = (SearchBox.Text ?? string.Empty).Trim();
        if (query == _query) return;
        _query = query;
        ResetCopyFeedback();
        RebuildVisible(SelectedSequence);
    }

    private void OnAutoScrollClick(object sender, RoutedEventArgs e)
    {
        ResetCopyFeedback();
        ScrollToEndIfAuto();
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        ResetCopyFeedback();
        _paused = PauseCheck.IsChecked == true;
        if (!_paused && _pausedEntries.Count > 0)
        {
            var rows = new List<ServerTrafficRow>(_pausedEntries.Count);
            foreach (var entry in _pausedEntries) rows.Add(new ServerTrafficRow(entry));
            _pausedEntries.Clear();
            AppendRows(rows);
        }
        UpdatePendingLabel();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        ResetCopyFeedback();
        // The log raises Cleared synchronously; its marker empties the list on the next drain, after any entry
        // that was recorded (and queued) before the clear.
        _log.Clear();
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        ResetCopyFeedback();
        if (_visible.Count == 0) return;

        var sb = new StringBuilder();
        sb.Append(AppStrings.ServerLogColTime).Append('\t')
          .Append(AppStrings.ServerLogColDirection).Append('\t')
          .Append(AppStrings.ServerLogColType).Append('\t')
          .Append(AppStrings.ServerLogColMessage).Append('\t')
          .Append(AppStrings.ServerLogColSize).Append('\t')
          .Append("id").Append("\r\n"); // the wire field name, not localized — what a query and its reply share
        foreach (var row in _visible)
        {
            sb.Append(row.TimeText).Append('\t')
              .Append(row.DirectionGlyph).Append('\t')
              .Append(row.TypeText).Append('\t')
              .Append(row.Summary).Append('\t') // summaries are already flattened to one line by the log
              .Append(row.SizeText).Append('\t')
              .Append(FlattenCell(row.Entry.CorrelationId)).Append("\r\n");
        }
        if (TrySetClipboard(sb.ToString())) CopiedText.Text = AppStrings.ServerLogCopied;
    }

    private void OnCopyMessageClick(object sender, RoutedEventArgs e)
    {
        ResetCopyFeedback();
        if (_detailsBody.Length == 0) return;
        if (TrySetClipboard(_detailsBody)) MessageCopiedText.Text = AppStrings.ServerLogCopied;
    }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        ResetCopyFeedback();
        _appVm.ToggleTcpConnection();
    }

    /// <summary>Clears the "Copied" confirmations — they last until the next action instead of on a timer.</summary>
    private void ResetCopyFeedback()
    {
        CopiedText.Text = string.Empty;
        MessageCopiedText.Text = string.Empty;
    }

    /// <summary>An id echoed by the server is untrusted text: keep it on one tab-separated cell.</summary>
    private static string FlattenCell(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static bool TrySetClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch
        {
            return false; // clipboard held open by another process — nothing useful to report
        }
        try { Clipboard.Flush(); } catch { /* best effort: keeps the text after the app exits */ }
        return true;
    }

    private void UpdatePendingLabel()
    {
        PendingText.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
        if (_paused) PendingText.Text = AppStrings.ServerLogPausedPending(_pausedEntries.Count);
    }

    private void UpdateStats()
    {
        var stats = _log.Stats;
        StatsText.Text = AppStrings.ServerLogStats(
            stats.SentMessages, TcpTrafficLog.FormatBytes(stats.SentBytes),
            stats.ReceivedMessages, TcpTrafficLog.FormatBytes(stats.ReceivedBytes),
            stats.Errors);
    }

    /// <summary>Text over an empty list, by why it is empty: "no messages yet" only when nothing at all is held (no rows,
    /// nothing parked by a pause); "nothing matches" when rows exist but the filter / search hides every one; and none
    /// when the only entries are parked by a pause — the pending count beside Pause already says so.</summary>
    private void UpdateEmptyState()
    {
        string? text = null;
        if (_visible.Count == 0)
        {
            if (_allRows.Count > 0) text = AppStrings.ServerLogEmptyFiltered;
            else if (_pausedEntries.Count == 0) text = AppStrings.ServerLogEmpty;
        }
        EmptyText.Text = text ?? string.Empty;
        EmptyText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Details pane ───────────────────────────────────────────────────────────

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowDetails(LogList.SelectedItem as ServerTrafficRow);

    private void ShowDetails(ServerTrafficRow? row)
    {
        MessageCopiedText.Text = string.Empty;
        if (row is null)
        {
            _detailsBody = string.Empty;
            DetailsMetaText.Text = AppStrings.ServerLogDetailsPlaceholder;
            TruncatedText.Visibility = Visibility.Collapsed;
            PayloadBox.Text = string.Empty;
            CopyMessageButton.IsEnabled = false;
            return;
        }

        var entry = row.Entry;
        DetailsMetaText.Text = FormatMeta(row);
        _detailsBody = FormatPayload(entry);
        PayloadBox.Text = _detailsBody;
        CopyMessageButton.IsEnabled = _detailsBody.Length > 0;

        if (entry.PayloadTruncated)
        {
            TruncatedText.Text = AppStrings.ServerLogTruncated(
                entry.Payload?.Length ?? 0, TcpTrafficLog.FormatBytes(entry.ByteCount));
            TruncatedText.Visibility = Visibility.Visible;
        }
        else
        {
            TruncatedText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary><c>#42 · 2026-09-15 14:03:12.345 · App → server · query · 96 B · id=…</c></summary>
    private static string FormatMeta(ServerTrafficRow row)
    {
        var entry = row.Entry;
        var parts = new List<string>(6)
        {
            "#" + entry.Sequence.ToString(CultureInfo.InvariantCulture),
            entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            ServerTrafficRow.DirectionLabel(entry.Direction),
        };
        // Events show the localized name with the stable wire/enum token beside it (what testers grep logs for).
        parts.Add(entry.Direction == TcpTrafficDirection.Event && row.TypeText != entry.Kind
            ? $"{row.TypeText} ({entry.Kind})"
            : row.TypeText);
        if (entry.Direction != TcpTrafficDirection.Event) parts.Add(row.SizeText);
        if (!string.IsNullOrEmpty(entry.CorrelationId)) parts.Add("id=" + entry.CorrelationId);
        return string.Join("  ·  ", parts);
    }

    /// <summary>The payload as shown: indented JSON when the preview is complete and parses, otherwise the raw text.
    /// Non-ASCII (e.g. Cyrillic rhythm names) is kept readable rather than <c>\uXXXX</c>-escaped.</summary>
    private static string FormatPayload(TcpTrafficEntry entry)
    {
        var payload = entry.Payload ?? string.Empty;
        if (entry.PayloadTruncated || entry.Direction == TcpTrafficDirection.Event) return payload;

        var trimmed = payload.AsSpan().TrimStart();
        if (trimmed.Length < 2 || (trimmed[0] != '{' && trimmed[0] != '[')) return payload;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                   {
                       Indented = true,
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // display only, never sent anywhere
                   }))
            {
                doc.RootElement.WriteTo(writer);
            }
            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }
        catch
        {
            return payload; // not JSON after all — show it verbatim
        }
    }

    // ── Connection header ──────────────────────────────────────────────────────

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) &&
            e.PropertyName is not (nameof(AppViewModel.TcpConnectionState)
                or nameof(AppViewModel.IsTcpLinkOn)
                or nameof(AppViewModel.ActiveTcpEndpoint)
                or nameof(AppViewModel.TcpIp)
                or nameof(AppViewModel.TcpPort)))
        {
            return;
        }

        // Raised on the UI thread today; marshal anyway so a future off-thread raise can't touch the controls.
        if (_dispatcher.HasThreadAccess)
        {
            if (!_detached) UpdateStatus();
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (!_detached) UpdateStatus();
            });
        }
    }

    /// <summary>Mirrors <c>SettingsContent.UpdateTcpStatus</c>. "Active" is <see cref="AppViewModel.IsTcpLinkOn"/> — the
    /// user has the link switched on — not the socket state: between reconnect attempts the loop sits in Disconnected
    /// for the whole retry delay while still running, so the toggle must read "Disconnect" (it stops the loop) and the
    /// status must not claim the link is off. Connected → green; link on but not connected (connecting or waiting to
    /// retry) → spinner + "Connecting"; link off → magenta error message, else red "Disconnected".</summary>
    private void UpdateStatus()
    {
        Windows.UI.Color color;
        string text;
        var linkOn = _appVm.IsTcpLinkOn;
        var state = _appVm.TcpConnectionState;
        var connecting = false;
        if (state is TcpState.Connected)
        {
            color = Microsoft.UI.Colors.Green;
            text = AppStrings.TcpStatusConnected;
        }
        else if (linkOn)
        {
            connecting = true;
            color = Microsoft.UI.Colors.Gray;
            text = AppStrings.TcpStatusConnecting;
        }
        else if (state is TcpState.Error error)
        {
            color = Microsoft.UI.Colors.Magenta;
            text = $"{AppStrings.TcpStatusError}: {error.Message}";
        }
        else
        {
            color = Microsoft.UI.Colors.Red;
            text = AppStrings.TcpStatusDisconnected;
        }

        ConnectingRing.IsActive = connecting;
        ConnectingRing.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
        StatusDot.Visibility = connecting ? Visibility.Collapsed : Visibility.Visible;
        StatusDot.Fill = new SolidColorBrush(color);
        StatusText.Text = text;
        ToolTipService.SetToolTip(StatusText, text);
        TargetText.Text = BuildTargetText(linkOn);
        ConnectButton.Content = linkOn ? AppStrings.TcpDisconnect : AppStrings.TcpConnect;
    }

    /// <summary>While the link is on, the endpoint the running loop was started with — Settings may already hold an
    /// edited target that is only used on the next connect; otherwise (or if that endpoint is unavailable) the saved one.</summary>
    private string BuildTargetText(bool linkOn)
    {
        if (linkOn && TrySplitEndpoint(_appVm.ActiveTcpEndpoint, out var ip, out var port))
        {
            return AppStrings.ServerLogTarget(ip, port);
        }
        return AppStrings.ServerLogTarget(_appVm.TcpIp, _appVm.TcpPort);
    }

    /// <summary>Splits an <c>"ip:port"</c> endpoint at its last colon.</summary>
    private static bool TrySplitEndpoint(string? endpoint, out string ip, out int port)
    {
        ip = string.Empty;
        port = 0;
        if (string.IsNullOrEmpty(endpoint)) return false;
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || colon == endpoint.Length - 1) return false;
        if (!int.TryParse(endpoint.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port)) return false;
        ip = endpoint[..colon];
        return true;
    }

    // ── Localization ───────────────────────────────────────────────────────────

    private void OnStringsChanged()
    {
        if (_detached) return;
        ApplyStrings();
        UpdateStatus();
        UpdateStats();
        UpdatePendingLabel();

        // Rows capture localized cells (event names, tooltips): re-create them from their entries and re-show the
        // list, keeping the selection (which also re-renders the details pane in the new language).
        var selected = SelectedSequence;
        for (var i = 0; i < _allRows.Count; i++) _allRows[i] = new ServerTrafficRow(_allRows[i].Entry);
        RebuildVisible(selected);
    }

    /// <summary>Sets every label, placeholder and filter item text in place — the controls are never rebuilt.</summary>
    private void ApplyStrings()
    {
        FilterAllItem.Content = AppStrings.ServerLogFilterAll;
        FilterSentItem.Content = AppStrings.ServerLogFilterSent;
        FilterReceivedItem.Content = AppStrings.ServerLogFilterReceived;
        FilterEventsItem.Content = AppStrings.ServerLogFilterEvents;
        FilterErrorsItem.Content = AppStrings.ServerLogFilterErrors;
        // The closed ComboBox displays a copy of the selected item's content: re-select to refresh it.
        var index = FilterBox.SelectedIndex < 0 ? 0 : FilterBox.SelectedIndex;
        _suppressFilterEvents = true;
        FilterBox.SelectedIndex = -1;
        FilterBox.SelectedIndex = index;
        _suppressFilterEvents = false;

        SearchBox.PlaceholderText = AppStrings.ServerLogSearchPlaceholder;
        // Accessible names for Narrator: the filter and payload boxes have no visible label, and a placeholder is no
        // longer announced once the search box holds text.
        AutomationProperties.SetName(FilterBox, AppStrings.ServerLogFilterLabel);
        AutomationProperties.SetName(SearchBox, AppStrings.ServerLogSearchPlaceholder);
        AutomationProperties.SetName(PayloadBox, AppStrings.ServerLogColMessage);
        AutoScrollCheck.Content = AppStrings.ServerLogAutoScroll;
        PauseCheck.Content = AppStrings.ServerLogPause;
        CopyAllButton.Content = AppStrings.ServerLogCopyAll;
        ClearButton.Content = AppStrings.ServerLogClear;
        CopyMessageButton.Content = AppStrings.ServerLogCopyMessage;

        ColTimeText.Text = AppStrings.ServerLogColTime;
        ColDirectionText.Text = AppStrings.ServerLogColDirection;
        ColTypeText.Text = AppStrings.ServerLogColType;
        ColMessageText.Text = AppStrings.ServerLogColMessage;
        ColSizeText.Text = AppStrings.ServerLogColSize;

        UpdateEmptyState();
        if (LogList.SelectedItem is null) DetailsMetaText.Text = AppStrings.ServerLogDetailsPlaceholder;
        ResetCopyFeedback();
    }
}
