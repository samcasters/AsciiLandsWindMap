using System.Globalization;
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
    private readonly HttpClient _debugHttp;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _gameTimer;
    private List<RegionRow> _regions = [];
    private Dictionary<string, RegionRow> _byId = new(StringComparer.Ordinal);
    private Dictionary<string, List<ObservationRow>> _observations = new(StringComparer.Ordinal);
    private Dictionary<string, ObservationRow> _syncWind = new(StringComparer.Ordinal);
    private Dictionary<string, WindSample> _arrows = new(StringComparer.Ordinal);
    private PlayerMark? _player;
    private string _realm = "eu";
    private string _worldId = "frontier-v2";
    private ClockSettings _clock;
    private MapMode _mode = MapMode.Day;
    private int _viewMinute = WindRules.DayViewMinute;
    private bool _ready;
    private bool _syncing;
    private int _syncVersion;
    private int _gameVersion;
    private int _syncSeconds = 5;
    private bool _gameBusy;
    private ServerClock? _serverClock;
    private double? _formulaHour;
    private SeaWind.SeaField? _seaField;
    private readonly Dictionary<string, SeaWindArrow> _seaScratch = new(StringComparer.Ordinal);
    private int _seaPicture = int.MinValue;
    private int _seaMinute = -1;
    private DateTime _seaNotBefore = DateTime.MinValue;
    private int _seaSavedMinute = -1;
    private DateTime _seaSavedUtc = DateTime.MinValue;
    private string? _trackedZone;
    private bool _trackedEdge;
    private string? _trackedWeather;

    public MainWindow()
    {
        InitializeComponent();
        _database = new WindDatabase(WindDatabase.DefaultDatabasePath());
        if (_database.CreatedNew)
            StatusText.Text = "Created a new wind database next to the app.";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AsciiLandsWindMap/1.0");
        _debugHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        _syncSeconds = SyncPace.ParseOrClamp(_database.GetSetting("sync_seconds"), 5);
        SyncSecondsBox.Text = _syncSeconds.ToString(CultureInfo.InvariantCulture);
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
        _gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_syncSeconds) };
        _gameTimer.Tick += async (_, _) => await PollGameAsync();
        LoadFromDatabase();
        _ready = true;
        RealmBox.SelectedItem = RealmBox.Items.Cast<RealmItem>().First(item => item.Id == _realm);
        ApplyMode(MapMode.Day);
        Loaded += async (_, _) => await SyncAsync();
        Closed += (_, _) =>
        {
            _syncVersion++;
            _gameVersion++;
            _timer.Stop();
            _gameTimer.Stop();
            _http.Dispose();
            _debugHttp.Dispose();
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
        _syncWind = map.SyncWind;
        _seaField = _regions.Count == 0 ? null : SeaWind.SeaField.Build(_regions);
        _seaPicture = int.MinValue;
        _seaMinute = -1;
        Map.SetContent(_regions, new Dictionary<string, WindSample>(StringComparer.Ordinal), IsNight());
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
            Map.SetFrame(_arrows, false, _player);
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
        foreach (var (id, sync) in _syncWind)
        {
            if (_arrows.ContainsKey(id) || WindRules.WindowOf(sync.GameMinute) != window)
                continue;
            _arrows[id] = new WindSample(sync.DirectionDeg, sync.Strength, 1, false);
        }
        Map.SetFrame(_arrows, IsNight(), _player);
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
        if (GameSyncButton.IsChecked == true && _serverClock is ServerClock clock)
        {
            DriftSea(clock);
            return;
        }

        if (GameSyncButton.IsChecked == true && _formulaHour is double formulaHour)
        {
            DriftFormula(formulaHour);
            return;
        }

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

    private void DriftSea(ServerClock clock)
    {
        if (SeaWind.Hour(clock, DateTime.UtcNow) is not double hour || _regions.Count == 0)
        {
            UpdateClockText();
            return;
        }

        var minute = SeaWind.MinuteOf(hour);
        var minuteChanged = minute != _viewMinute;
        if (minuteChanged)
        {
            _viewMinute = minute;
            _clock = new ClockSettings(DateTime.UtcNow, minute, null);
            _database.SaveClock(_clock);
        }

        UpdateClockText();
        if (minuteChanged)
        {
            App.ApplyTheme(IsNight());
            PaintModes();
            Tile.UpdateMapTime(_viewMinute);
        }

        if (DateTime.UtcNow < _seaNotBefore && !minuteChanged)
            return;
        _seaNotBefore = DateTime.UtcNow.AddSeconds(2);
        var elapsed = Math.Max(0, (DateTime.UtcNow - clock.ReadUtc).TotalMilliseconds);
        _seaScratch.Clear();
        Field().Fill(_seaScratch, clock.ServerTimeMs + elapsed, hour, _trackedZone, _trackedEdge, _trackedWeather);
        ApplySea(_seaScratch, minute, minuteChanged, refresh: true);
    }

    private void DriftFormula(double hour)
    {
        if (_regions.Count == 0 || DateTime.UtcNow < _seaNotBefore)
            return;
        _seaNotBefore = DateTime.UtcNow.AddSeconds(2);
        var minute = SeaWind.MinuteOf(hour);
        _seaScratch.Clear();
        Field().Fill(_seaScratch, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hour, null, false, null);
        ApplySea(_seaScratch, minute, false, refresh: true);
    }

    private SeaWind.SeaField Field() => _seaField ??= SeaWind.SeaField.Build(_regions);

    private void ApplySea(IReadOnlyDictionary<string, SeaWindArrow> arrows, int minute, bool force, bool refresh)
    {
        var picture = ArrowPicture.HashSea(_regions, arrows);
        var windowChanged = _seaMinute >= 0 && WindRules.WindowOf(minute) != WindRules.WindowOf(_seaMinute);
        if (!force && picture == _seaPicture && !windowChanged)
            return;
        _seaPicture = picture;
        _seaMinute = minute;
        _syncWind = ToSyncRows(arrows, minute);
        if (refresh)
            RefreshArrows();
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
        StyleSync(GameSyncButton.IsChecked == true);
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
        _database.SaveTileMeta(request.RegionId, request.Note, request.Tags, request.Caption, request.Icon);
        if (_byId.TryGetValue(request.RegionId, out var region))
        {
            region.Note = request.Note.Trim();
            region.Caption = TileIcons.CleanCaption(request.Caption);
            region.Icon = TileIcons.Canonical(request.Icon) ?? "";
            region.Tags = request.Tags.ToList();
            StatusText.Text = $"Saved note, description, and mark for {region.Name}.";
            if (Map.SelectedId == region.Id)
                HoverText.Text = Describe(region);
        }
        Map.NoteChanged();
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
        if (!string.IsNullOrWhiteSpace(region.Caption))
            text += " · " + region.Caption.Trim();
        var mark = TileIcons.LabelOf(region.Icon);
        if (mark.Length > 0)
            text += " · mark " + mark;
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
        var hasSync = (snapshot.Tiles ?? []).Any(tile => (tile.Observations ?? []).Any(IsSyncReading));
        IReadOnlySet<string> replace = new HashSet<string>(StringComparer.Ordinal);
        if (analysis.Conflicts.Count > 0)
        {
            var window = new ImportWindow(analysis) { Owner = this };
            if (window.ShowDialog() != true)
                return;
            replace = window.ReplaceIds;
        }
        else if (analysis.Adds.Count == 0 && !hasSync)
        {
            StatusText.Text = analysis.Unchanged == 0
                ? "That file has no wind to import." + UnknownSuffix(analysis.UnknownIds.Count)
                : $"Nothing new. {analysis.Unchanged} tile{(analysis.Unchanged == 1 ? "" : "s")} already match." + UnknownSuffix(analysis.UnknownIds.Count);
            return;
        }

        var result = _database.ApplyImport(snapshot, replace);
        LoadFromDatabase();
        StatusText.Text = $"Imported {result.Added} new, updated {result.Replaced}, kept {result.Kept}."
            + (hasSync ? " Calculated sea wind was stored apart from hand readings." : "")
            + UnknownSuffix(result.Unknown);
    }

    private static bool IsSyncReading(ReadingSnapshot reading) =>
        string.Equals(reading.Source, "sync", StringComparison.OrdinalIgnoreCase);

    private async void GameSyncToggled(object sender, RoutedEventArgs e)
    {
        if (GameSyncButton.IsChecked == true)
        {
            ApplySyncSeconds();
            StyleSync(true);
            _gameTimer.Start();
            await PollGameAsync();
            return;
        }

        _gameTimer.Stop();
        _gameVersion++;
        StyleSync(false);
        SyncStatus.Text = "";
        StatusText.Text = "Sync is off. The last wind and location stay on the map.";
    }

    private void SyncSecondsChanged(object sender, RoutedEventArgs e) => ApplySyncSeconds();

    private void SyncSecondsKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        ApplySyncSeconds();
        e.Handled = true;
    }

    private void ApplySyncSeconds()
    {
        _syncSeconds = SyncPace.ParseOrClamp(SyncSecondsBox.Text, _syncSeconds);
        SyncSecondsBox.Text = _syncSeconds.ToString(CultureInfo.InvariantCulture);
        _database.SetSetting("sync_seconds", SyncSecondsBox.Text);
        _gameTimer.Interval = TimeSpan.FromSeconds(_syncSeconds);
    }

    private void StyleSync(bool on)
    {
        GameSyncButton.Background = (System.Windows.Media.Brush)FindResource(on ? "AccentBrush" : "PanelBrush");
        GameSyncButton.Foreground = on
            ? System.Windows.Media.Brushes.White
            : (System.Windows.Media.Brush)FindResource("TextBrush");
    }

    private async Task PollGameAsync()
    {
        if (_gameBusy || GameSyncButton.IsChecked != true)
            return;
        _gameBusy = true;
        var version = _gameVersion;
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var game = await GameBrowser.ReadAsync(_debugHttp, cancel.Token);
            if (version != _gameVersion || GameSyncButton.IsChecked != true)
                return;
            ApplyGame(game);
        }
        catch (Exception)
        {
            if (version == _gameVersion && GameSyncButton.IsChecked == true)
                StatusText.Text = "Sync could not read the game this time.";
        }
        finally
        {
            _gameBusy = false;
        }
    }

    private void ApplyGame(GameSnapshot game)
    {
        if (game.Detail == "missing")
        {
            StatusText.Text = "Open https://asciilands.io/play in Edge or Chrome.";
            return;
        }
        if (game.Detail == "no-tab")
        {
            StatusText.Text = "https://asciilands.io/play is not open. Nothing was changed.";
            return;
        }
        if (game.Detail == "unreadable")
        {
            StatusText.Text = "The play page is open, but its data could not be read. Nothing was changed.";
            return;
        }
        if (game.Detail == "debug-port")
        {
            SyncStatus.Text = "Click the Asciilands game tab.";
            StatusText.Text = "The game window is open, but Sync cannot read that tab yet. Click the Asciilands tab so the play page is in front.";
            return;
        }

        var zoneId = game.ZoneId;
        if (string.IsNullOrEmpty(zoneId))
            zoneId = RegionMatch.Find(_regions, game.Title, game.NearbyNames);

        var place = game.Title.Trim();
        var unknown = false;
        if (!string.IsNullOrEmpty(zoneId))
        {
            if (_byId.TryGetValue(zoneId, out var region))
            {
                place = region.Name;
                _player = new PlayerMark(region.Q, region.R, region.Biome.Equals("ocean", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                _player = null;
                unknown = true;
                if (place.Length == 0)
                    place = zoneId;
            }
        }

        double liveGoing = 0;
        var windRead = !game.WindHidden && GameHud.TryWindFrom(game.WindLabel, game.WindGlyph, out liveGoing, out _);
        var seaNote = game.FromScreen
            ? "This tile is from the game window. Chrome blocks the server clock on a normal profile, so the other sea tiles are left as they are."
            : "The sea map is waiting on the game clock.";
        string? mismatch = null;
        int? minute = game.ClockMinute;

        if (game.Clock is ServerClock clock && SeaWind.Hour(clock, DateTime.UtcNow) is double hour)
        {
            _serverClock = clock;
            _trackedZone = zoneId;
            _trackedEdge = game.PlayerEdge;
            _trackedWeather = game.ForcedWeather;
            minute = SeaWind.MinuteOf(hour);
            _formulaHour = null;
            var elapsed = Math.Max(0, (DateTime.UtcNow - clock.ReadUtc).TotalMilliseconds);
            _seaScratch.Clear();
            Field().Fill(_seaScratch, clock.ServerTimeMs + elapsed, hour, _trackedZone, _trackedEdge, _trackedWeather);
            RememberSea(minute.Value, _seaScratch);
            ApplySea(_seaScratch, minute.Value, true, refresh: false);
            _seaNotBefore = DateTime.UtcNow.AddSeconds(2);
            seaNote = $"Sea wind filled for {_seaScratch.Count} ocean tiles.";
            if (windRead && !string.IsNullOrEmpty(zoneId) && _seaScratch.TryGetValue(zoneId, out var calculated))
            {
                var liveFrom = CompassText.Point(liveGoing + 180);
                var calcFrom = CompassText.Point(calculated.DirectionDeg + 180);
                if (!liveFrom.Equals(calcFrom, StringComparison.OrdinalIgnoreCase))
                    mismatch = $"On-screen wind is from {liveFrom}; the formula says from {calcFrom}.";
            }
        }
        else if (minute is int hud && _regions.Count > 0)
        {
            var filled = FillSeaFromClock(hud, zoneId, windRead, liveGoing);
            seaNote = filled.Note;
            mismatch = filled.Mismatch;
        }
        else if (_serverClock is ServerClock held && SeaWind.Hour(held, DateTime.UtcNow) is double heldHour)
        {
            minute = SeaWind.MinuteOf(heldHour);
            seaNote = "Using the last game clock. This read had no new server time.";
        }

        if (minute is int view)
        {
            _clock = new ClockSettings(DateTime.UtcNow, view, null);
            _database.SaveClock(_clock);
            ApplyMode(MapMode.Live);
        }
        else
        {
            RefreshArrows();
        }

        if (place.Length == 0)
            place = "location not shown yet";
        if (unknown)
            place += ". That region is not on this map yet. Use Sync map";
        var facing = string.IsNullOrWhiteSpace(game.Facing) ? "" : " · " + game.Facing.Trim();
        var speed = string.IsNullOrWhiteSpace(game.Speed) ? "" : " · " + game.Speed.Trim();
        var time = minute is int shown ? GameClock.Format(shown) : "no time";
        var windNote = windRead ? "On-screen wind was read." : "On-screen wind is hidden.";
        StatusText.Text = $"Game {time} · {place}{facing}{speed}. {windNote} {seaNote}"
            + (mismatch == null ? "" : " " + mismatch);
        SyncStatus.Text = windRead
            ? $"Game {time} · {place}"
            : $"Game {time} · {place}. Wind mark is hidden.";
    }

    private (string Note, string? Mismatch) FillSeaFromClock(int minute, string? zoneId, bool windRead, double liveGoing)
    {
        var hour = minute / 60.0;
        _formulaHour = hour;
        _serverClock = null;
        _trackedZone = null;
        _trackedWeather = null;
        var worldMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _seaScratch.Clear();
        Field().Fill(_seaScratch, worldMs, hour, null, false, null);
        RememberSea(minute, _seaScratch);
        ApplySea(_seaScratch, minute, true, refresh: false);
        _seaNotBefore = DateTime.UtcNow.AddSeconds(2);
        var note = $"Sea wind follows the game clock on {_seaScratch.Count} ocean tiles and keeps moving with the time.";
        if (!windRead || string.IsNullOrEmpty(zoneId) || !_byId.TryGetValue(zoneId, out var standing))
            return (note, null);

        var byCoord = new Dictionary<(int Q, int R), RegionRow>(_regions.Count);
        foreach (var region in _regions)
            byCoord[(region.Q, region.R)] = region;
        var raw = SeaWind.WindAt(standing, byCoord, worldMs, hour, false, null);
        var going = SeaWind.GoingDegrees(raw.Direction);
        var liveFrom = CompassText.Point(liveGoing + 180);
        var calcFrom = CompassText.Point(going + 180);
        if (!liveFrom.Equals(calcFrom, StringComparison.OrdinalIgnoreCase))
            return (note, $"On-screen wind is from {liveFrom}; the formula says from {calcFrom}.");
        return (note + $" This hex agrees: wind from {calcFrom}.", null);
    }

    private void RememberSea(int minute, IReadOnlyDictionary<string, SeaWindArrow> arrows)
    {
        var now = DateTime.UtcNow;
        var windowChanged = _seaSavedMinute >= 0 && WindRules.WindowOf(minute) != WindRules.WindowOf(_seaSavedMinute);
        if (!windowChanged && _seaSavedMinute >= 0 && (now - _seaSavedUtc).TotalSeconds < 30)
            return;
        _database.ReplaceSyncWind(_realm, minute, arrows);
        _seaSavedMinute = minute;
        _seaSavedUtc = now;
    }

    private static Dictionary<string, ObservationRow> ToSyncRows(IReadOnlyDictionary<string, SeaWindArrow> arrows, int minute)
    {
        var utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var rows = new Dictionary<string, ObservationRow>(StringComparer.Ordinal);
        foreach (var (id, arrow) in arrows)
        {
            rows[id] = new ObservationRow
            {
                RegionId = id,
                Realm = "",
                GameMinute = minute,
                DirectionDeg = arrow.DirectionDeg,
                Strength = arrow.Strength,
                RecordedUtc = utc,
            };
        }
        return rows;
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
