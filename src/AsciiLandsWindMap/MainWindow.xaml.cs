using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AsciiLandsWindMap;

public partial class MainWindow : Window
{
    private readonly WindDatabase _database;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer;
    private List<RegionRow> _regions = [];
    private Dictionary<string, RegionRow> _byId = new(StringComparer.Ordinal);
    private Dictionary<string, List<ObservationRow>> _observations = new(StringComparer.Ordinal);
    private Dictionary<string, WindSample> _arrows = new(StringComparer.Ordinal);
    private string _realm = "eu";
    private string _worldId = "frontier-v2";
    private ClockSettings _clock;
    private MapMode _mode = MapMode.Day;
    private int _viewMinute = WindRules.DayViewMinute;
    private bool _ready;
    private bool _syncing;
    private int _syncVersion;

    public MainWindow()
    {
        InitializeComponent();
        _database = new WindDatabase(WindDatabase.DefaultDatabasePath());
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AsciiLandsWindMap/1.0");
        _realm = _database.GetSetting("realm") ?? "eu";
        if (!Realms.IsKnown(_realm))
            _realm = "eu";
        _worldId = _database.GetSetting("world_id") ?? "frontier-v2";
        _clock = _database.LoadClock();
        foreach (var realm in Realms.All)
            RealmBox.Items.Add(new RealmItem(realm.Id, realm.Label));
        Map.RegionActivated += (_, region) => OpenRegion(region);
        Map.HoverChanged += (_, region) => HoverText.Text = region == null
            ? "Drag to pan. Scroll to zoom. Click a hex."
            : Describe(region);
        Tile.WindSaveRequested += OnWindSave;
        Tile.MetaSaveRequested += OnMetaSave;
        Tile.ReadingDeleteRequested += OnDeleteReading;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => TickClock();
        _timer.Start();
        LoadFromDatabase();
        _ready = true;
        RealmBox.SelectedItem = RealmBox.Items.Cast<RealmItem>().First(item => item.Id == _realm);
        ApplyMode(MapMode.Day);
        Loaded += async (_, _) => await SyncAsync();
        Closed += (_, _) =>
        {
            _syncVersion++;
            _timer.Stop();
            _http.Dispose();
            _database.Dispose();
        };
    }

    private void LoadFromDatabase()
    {
        var selected = Tile.RegionId;
        var map = _database.LoadMap(_realm);
        _regions = map.Regions;
        _byId = _regions.ToDictionary(region => region.Id, StringComparer.Ordinal);
        _observations = map.Observations;
        RefreshArrows();
        EmptyText.Visibility = _regions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected != null && _byId.TryGetValue(selected, out var region))
            Tile.ShowRegion(region, ReadingsFor(selected), _viewMinute, ArrowFor(selected));
    }

    private void RefreshArrows()
    {
        if (_mode == MapMode.Live && LiveMinute() is null)
        {
            _arrows = new Dictionary<string, WindSample>(StringComparer.Ordinal);
            Map.SetContent(_regions, _arrows, false);
            return;
        }

        var window = WindRules.WindowOf(_viewMinute);
        _arrows = new Dictionary<string, WindSample>(StringComparer.Ordinal);
        foreach (var (id, list) in _observations)
        {
            var inWindow = list
                .Where(item => WindRules.WindowOf(item.GameMinute) == window)
                .Select(item => item.ToLite())
                .ToList();
            var sample = WindRules.Average(inWindow);
            if (sample != null)
                _arrows[id] = sample.Value;
        }
        Map.SetContent(_regions, _arrows, IsNight());
    }

    private void ApplyMode(MapMode mode)
    {
        _mode = mode;
        if (mode == MapMode.Day)
            _viewMinute = WindRules.DayViewMinute;
        else if (mode == MapMode.Night)
            _viewMinute = WindRules.NightViewMinute;
        else if (LiveMinute() is int live)
            _viewMinute = live;

        UpdateClockText();
        PaintModes();
        App.ApplyTheme(IsNight());
        RefreshArrows();
        Tile.UpdateMapTime(_viewMinute);
        if (mode != MapMode.Live)
            Tile.SetSuggestedTime(_viewMinute);
    }

    private void TickClock()
    {
        if (_mode != MapMode.Live)
            return;
        var live = LiveMinute();
        UpdateClockText();
        if (live is not int minute || minute == _viewMinute)
            return;
        _viewMinute = minute;
        App.ApplyTheme(IsNight());
        PaintModes();
        RefreshArrows();
        Tile.UpdateMapTime(_viewMinute);
    }

    private int? LiveMinute() =>
        GameClock.CurrentMinute(DateTime.UtcNow, _clock.AnchorUtc, _clock.AnchorMinute, _clock.DayLengthMinutes);

    private bool IsNight() => _mode switch
    {
        MapMode.Day => false,
        MapMode.Night => true,
        _ => LiveMinute() is int minute && WindRules.IsDarkPhase(minute),
    };

    private void UpdateClockText()
    {
        if (_mode == MapMode.Live && LiveMinute() is null)
        {
            ClockText.Text = "Live · set the game clock";
            return;
        }

        var prefix = _mode switch
        {
            MapMode.Live when _clock.DayLengthMinutes is null => "Held",
            MapMode.Live => "Live",
            _ => "Viewing",
        };
        var text = $"{prefix} {GameClock.Format(_viewMinute)} · {WindRules.Phase(_viewMinute)} · {WindRules.WindowLabel(WindRules.WindowOf(_viewMinute))}";
        if (_mode == MapMode.Live && _clock.DayLengthMinutes is null)
            text += " · clock held";
        ClockText.Text = text;
    }

    private void PaintModes()
    {
        StyleMode(DayButton, _mode == MapMode.Day);
        StyleMode(NightButton, _mode == MapMode.Night);
        StyleMode(LiveButton, _mode == MapMode.Live);
    }

    private void StyleMode(System.Windows.Controls.Button button, bool on)
    {
        button.Background = (System.Windows.Media.Brush)FindResource(on ? "AccentBrush" : "PanelBrush");
        button.Foreground = on ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)FindResource("TextBrush");
    }

    private async void SyncClick(object sender, RoutedEventArgs e) => await SyncAsync();

    private async Task SyncAsync()
    {
        if (_syncing)
            return;
        _syncing = true;
        var version = ++_syncVersion;
        SyncButton.IsEnabled = false;
        var hadRegions = _regions.Count > 0;
        StatusText.Text = "Downloading the map…";
        if (!hadRegions)
            EmptyText.Text = "Downloading the Asciilands map…";
        try
        {
            var world = await WorldClient.DownloadAsync(_http, CancellationToken.None);
            if (!IsLoaded || version != _syncVersion)
                return;
            var result = _database.ApplyWorld(world.Nodes, world.WorldId);
            if (!string.IsNullOrWhiteSpace(world.WorldId))
                _worldId = world.WorldId;
            LoadFromDatabase();
            StatusText.Text = result.Added == 0 && result.Renamed == 0
                ? $"Map is current. {result.Total:N0} regions."
                : $"Map updated. {result.Added:N0} new, {result.Renamed:N0} renamed. {result.Total:N0} regions.";
        }
        catch (Exception ex)
        {
            if (!IsLoaded || version != _syncVersion)
                return;
            var reason = ex is HttpRequestException or TaskCanceledException
                ? "Check your connection and press Sync map."
                : ex is InvalidDataException
                    ? ex.Message
                    : "Press Sync map to try again.";
            StatusText.Text = hadRegions
                ? "Offline. Showing the saved map. " + reason
                : "Could not download the map. " + reason;
            if (!hadRegions)
                EmptyText.Text = "No saved map yet. Press Sync map when you are online.";
        }
        finally
        {
            if (version == _syncVersion)
            {
                _syncing = false;
                SyncButton.IsEnabled = true;
                EmptyText.Visibility = _regions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void OpenRegion(RegionRow region)
    {
        SearchPopup.IsOpen = false;
        Map.Select(region.Id);
        Tile.ShowRegion(region, ReadingsFor(region.Id), _viewMinute, ArrowFor(region.Id));
    }

    private List<ObservationRow> ReadingsFor(string id) =>
        _observations.TryGetValue(id, out var list) ? list : [];

    private WindSample? ArrowFor(string id) =>
        _arrows.TryGetValue(id, out var sample) ? sample : null;

    private void OnWindSave(object? sender, WindReadingRequest request)
    {
        var saved = _database.SaveObservation(request.RegionId, _realm, request.GameMinute, request.DirectionDeg, request.Strength);
        if (!_observations.TryGetValue(request.RegionId, out var list))
            _observations[request.RegionId] = list = [];
        var index = list.FindIndex(item => item.GameMinute == saved.GameMinute);
        if (index >= 0)
            list[index] = saved;
        else
            list.Add(saved);
        list.Sort((a, b) => a.GameMinute.CompareTo(b.GameMinute));
        RefreshArrows();
        Tile.RefreshReadings(list);
        var name = _byId.TryGetValue(request.RegionId, out var region) ? region.Name : "tile";
        StatusText.Text = $"Saved {name} at {GameClock.Format(saved.GameMinute)}.";
    }

    private void OnMetaSave(object? sender, TileMetaRequest request)
    {
        _database.SaveTileMeta(request.RegionId, request.Note, request.Tags);
        if (_byId.TryGetValue(request.RegionId, out var region))
        {
            region.Note = request.Note.Trim();
            region.Tags = request.Tags.ToList();
            StatusText.Text = $"Saved note and tags for {region.Name}.";
            if (Map.SelectedId == region.Id)
                HoverText.Text = Describe(region);
        }
        RefreshArrows();
    }

    private void OnDeleteReading(object? sender, long id)
    {
        _database.DeleteObservation(id);
        foreach (var list in _observations.Values)
            list.RemoveAll(item => item.Id == id);
        RefreshArrows();
        if (Tile.RegionId is string regionId)
            Tile.RefreshReadings(ReadingsFor(regionId));
        StatusText.Text = "Reading deleted.";
    }

    private string Describe(RegionRow region)
    {
        var text = $"{region.Name} · {BiomeNames.Pretty(region.Biome)} · {WorldNames.Slug(region.Id)}";
        if (region.Tags.Count > 0)
            text += " · " + string.Join(", ", region.Tags.Select(TagLabel));
        if (_arrows.TryGetValue(region.Id, out var sample))
        {
            if (sample.Cancelled)
                text += " · readings in this window point opposite ways";
            else if (sample.Strength <= 0)
                text += " · calm";
            else
                text += $" · {CompassText.Mark(sample.Strength)} {CompassText.Going(sample.DirectionDeg)} · {sample.Count} reading{(sample.Count == 1 ? "" : "s")}";
        }
        else
            text += " · no wind for this time";
        if (!string.IsNullOrWhiteSpace(region.Note))
        {
            var note = region.Note.ReplaceLineEndings(" ");
            if (note.Length > 80)
                note = note[..80] + "…";
            text += " · " + note;
        }
        return text;
    }

    private static string TagLabel(string tag)
    {
        foreach (var built in TileTags.BuiltIn)
        {
            if (built.Id == tag)
                return built.Label;
        }
        return tag;
    }

    private void DayClick(object sender, RoutedEventArgs e) => ApplyMode(MapMode.Day);

    private void NightClick(object sender, RoutedEventArgs e) => ApplyMode(MapMode.Night);

    private void LiveClick(object sender, RoutedEventArgs e) => ApplyMode(MapMode.Live);

    private void FitClick(object sender, RoutedEventArgs e) => Map.FitAll();

    private void MatchClockClick(object sender, RoutedEventArgs e)
    {
        var current = _mode == MapMode.Live ? LiveMinute() ?? _viewMinute : _viewMinute;
        var dialog = new ClockWindow(current, _clock.DayLengthMinutes) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        _clock = new ClockSettings(DateTime.UtcNow, dialog.Minute, dialog.DayLengthMinutes);
        _database.SaveClock(_clock);
        ApplyMode(MapMode.Live);
        StatusText.Text = dialog.DayLengthMinutes is null
            ? "Clock held. Enter a day length later if you want it to run."
            : "Live clock is running from the time you entered.";
    }

    private void RealmChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_ready || RealmBox.SelectedItem is not RealmItem item || item.Id == _realm)
            return;
        _realm = item.Id;
        _database.SetSetting("realm", _realm);
        LoadFromDatabase();
        StatusText.Text = $"Showing {item.Label} wind.";
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var snapshot = _database.Export(_realm, _worldId);
        var dialog = new SaveFileDialog
        {
            Filter = "Wind map JSON (*.json)|*.json",
            FileName = $"asciilands-wind-{_realm}.json",
            Title = "Export wind map",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        MapExchange.Write(dialog.FileName, snapshot);
        StatusText.Text = snapshot.Tiles.Count == 0
            ? "Exported an empty wind map."
            : $"Exported {snapshot.Tiles.Count} tiles for {Realms.Label(_realm)}.";
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Wind map JSON (*.json)|*.json",
            Title = "Import a wind map",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        MapSnapshot snapshot;
        try
        {
            snapshot = MapExchange.Read(dialog.FileName);
        }
        catch (MapFileException ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        if (!string.Equals(snapshot.Realm, _realm, StringComparison.Ordinal))
        {
            var answer = MessageBox.Show(this,
                $"This file is for {Realms.Label(snapshot.Realm)}. Import it there and switch to that realm?",
                "Import wind map",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;
            _realm = snapshot.Realm;
            _database.SetSetting("realm", _realm);
            _ready = false;
            RealmBox.SelectedItem = RealmBox.Items.Cast<RealmItem>().First(item => item.Id == _realm);
            _ready = true;
            LoadFromDatabase();
        }

        var analysis = _database.Analyze(snapshot);
        IReadOnlySet<string> replace = new HashSet<string>(StringComparer.Ordinal);
        if (analysis.Conflicts.Count > 0)
        {
            var window = new ImportWindow(analysis) { Owner = this };
            if (window.ShowDialog() != true)
                return;
            replace = window.ReplaceIds;
        }
        else if (analysis.Adds.Count == 0)
        {
            StatusText.Text = analysis.Unchanged == 0
                ? "That file has no wind to import." + UnknownSuffix(analysis.UnknownIds.Count)
                : $"Nothing new. {analysis.Unchanged} tile{(analysis.Unchanged == 1 ? "" : "s")} already match." + UnknownSuffix(analysis.UnknownIds.Count);
            return;
        }

        var result = _database.ApplyImport(snapshot, replace);
        LoadFromDatabase();
        StatusText.Text = $"Imported {result.Added} new, updated {result.Replaced}, kept {result.Kept}."
            + UnknownSuffix(result.Unknown);
    }

    private static string UnknownSuffix(int count) => count == 0
        ? ""
        : $" Skipped {count} region{(count == 1 ? "" : "s")} that are not on the map yet.";

    private void SearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        var matches = RegionSearch.Search(_regions, SearchBox.Text);
        SearchList.ItemsSource = matches;
        SearchPopup.IsOpen = matches.Count > 0 && SearchBox.IsKeyboardFocused;
    }

    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (RegionSearch.Search(_regions, SearchBox.Text).FirstOrDefault() is RegionRow region)
                GoTo(region);
            else
                StatusText.Text = "No region with that name.";
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && SearchList.Items.Count > 0)
        {
            SearchPopup.IsOpen = true;
            SearchList.Focus();
            SearchList.SelectedIndex = 0;
            e.Handled = true;
        }
    }

    private void SearchListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchList.SelectedItem is RegionRow region)
        {
            GoTo(region);
            e.Handled = true;
        }
    }

    private void SearchListClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchList.SelectedItem is RegionRow region)
            GoTo(region);
    }

    private void GoTo(RegionRow region)
    {
        SearchPopup.IsOpen = false;
        Map.Select(region.Id);
        Map.CenterOn(region.Q, region.R);
        OpenRegion(region);
    }

    private void MapPreviewDown(object sender, MouseButtonEventArgs e) => SearchPopup.IsOpen = false;

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private enum MapMode
    {
        Day,
        Night,
        Live,
    }

    private sealed record RealmItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}
