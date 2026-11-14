using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    private bool _gameBusy;
    private ServerClock? _serverClock;
    private double? _formulaHour;
    private SeaWind.SeaField? _seaField;
    private readonly Dictionary<string, SeaWindArrow> _seaScratch = new(StringComparer.Ordinal);
    private int _seaPicture = int.MinValue;
    private int _seaMinute = -1;
    private int _arrowHour = int.MinValue;
    private DateTime _arrowsAt = DateTime.MinValue;
    private static readonly TimeSpan ArrowRefresh = TimeSpan.FromSeconds(15);
    private WindWindow? _drawnWindow;
    private bool _drawnNight;
    private object? _drawnSync;
    private string? _trackedZone;
    private bool _trackedEdge;
    private string? _trackedWeather;
    private string? _playerZone;
    private readonly Dictionary<string, TileSurvey> _surveys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource> _surveyPictures = new(StringComparer.Ordinal);
    private TravelMode? _gpsMode;
    private string? _gpsDestinationId;
    private RoutePlan? _gpsPlan;
    private bool _gpsPicking;
    private bool _gpsBusy;
    private int _placeEpoch;
    private bool _overlayBusy;
    private bool _overlayOnPage;
    private bool _closing;
    private DateTime _overlayAt = DateTime.MinValue;
    private MinimapFloat? _float;

    public MainWindow()
    {
        InitializeComponent();
        _database = new WindDatabase(WindDatabase.DefaultDatabasePath());
        if (_database.CreatedNew)
            StatusText.Text = "Created a new wind database next to the app.";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AsciiLandsWindMap/1.0");
        _debugHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        _realm = _database.GetSetting("realm") ?? "eu";
        if (!Realms.IsKnown(_realm))
            _realm = "eu";
        _worldId = _database.GetSetting("world_id") ?? "frontier-v2";
        _clock = _database.LoadClock();
        foreach (var realm in Realms.All)
            RealmBox.Items.Add(new RealmItem(realm.Id, realm.Label));
        Map.RegionActivated += (_, region) => OnRegionClicked(region);
        Map.HoverChanged += (_, region) => HoverText.Text = region == null
            ? "Drag to pan. Scroll to zoom. Click a hex."
            : Describe(region);
        Tile.MetaSaveRequested += OnMetaSave;
        Tile.CloseRequested += (_, _) => Map.Select(null);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => TickClock();
        _timer.Start();
        _gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _gameTimer.Tick += async (_, _) => await PollGameAsync();
        LoadFromDatabase();
        _ready = true;
        RealmBox.SelectedItem = RealmBox.Items.Cast<RealmItem>().First(item => item.Id == _realm);
        ApplyMode(MapMode.Day);
        Loaded += async (_, _) =>
        {
            GameSyncButton.IsChecked = true;
            await SyncAsync();
        };
        Closed += (_, _) =>
        {
            _closing = true;
            _syncVersion++;
            _gameVersion++;
            _timer.Stop();
            _gameTimer.Stop();
            if (_float != null)
            {
                _float.Close();
                _float = null;
            }
            if (_overlayOnPage)
            {
                try
                {
                    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    GameBrowser.RemoveOverlayAsync(_debugHttp, cancel.Token).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is HttpRequestException or WebSocketException or JsonException or InvalidOperationException or TaskCanceledException or IOException)
                {
                }
            }
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
        _syncWind = map.SyncWind;
        _seaField = null;
        _seaPicture = int.MinValue;
        _seaMinute = -1;
        _arrowHour = int.MinValue;
        Map.SetContent(_regions, new Dictionary<string, WindSample>(StringComparer.Ordinal), IsNight());
        LoadSurveys();
        RefreshArrows();
        if (_regions.Count > 0)
            EnsureSeaForView(force: true);
        EmptyText.Visibility = _regions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected != null && _byId.TryGetValue(selected, out var region))
            Tile.ShowRegion(region, _viewMinute, ArrowFor(selected));
    }

    private void RefreshArrows()
    {
        if (_mode == MapMode.Live && LiveMinute() is null)
        {
            _arrows = new Dictionary<string, WindSample>(StringComparer.Ordinal);
            _drawnWindow = null;
            _drawnSync = null;
            Map.SetFrame(_arrows, false, _player, exact: true);
            return;
        }

        var window = WindRules.WindowOf(_viewMinute);
        var night = IsNight();
        if (_drawnWindow == window && _drawnNight == night && ReferenceEquals(_drawnSync, _syncWind))
        {
            Map.SetPlayer(_player);
            Tile.UpdateShown(_viewMinute, Tile.RegionId is string shown ? ArrowFor(shown) : null);
            return;
        }

        _drawnWindow = window;
        _drawnNight = night;
        _drawnSync = _syncWind;
        _arrows = new Dictionary<string, WindSample>(StringComparer.Ordinal);
        foreach (var (id, sync) in _syncWind)
        {
            if (WindRules.WindowOf(sync.GameMinute) != window)
                continue;
            _arrows[id] = new WindSample(sync.DirectionDeg, sync.Strength, 1, false);
        }
        Map.SetFrame(_arrows, night, _player, exact: true);
        Tile.UpdateShown(_viewMinute, Tile.RegionId is string shownId ? ArrowFor(shownId) : null);
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
    }

    private void TickClock()
    {
        MaybeOverlay();
        MaybeGpsZone();
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

        var elapsed = Math.Max(0, (DateTime.UtcNow - clock.ReadUtc).TotalMilliseconds);
        PublishSea(clock.ServerTimeMs + elapsed, hour, minute, remember: true, force: false);
    }

    private void DriftFormula(double hour)
    {
        if (_regions.Count == 0)
            return;
        var minute = SeaWind.MinuteOf(hour);
        PublishSea(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hour, minute, remember: true, force: false);
    }

    private void EnsureSeaForView(bool force)
    {
        if (_regions.Count == 0)
            return;
        if (_mode == MapMode.Live && _serverClock is ServerClock clock && SeaWind.Hour(clock, DateTime.UtcNow) is double hour)
        {
            var elapsed = Math.Max(0, (DateTime.UtcNow - clock.ReadUtc).TotalMilliseconds);
            PublishSea(clock.ServerTimeMs + elapsed, hour, SeaWind.MinuteOf(hour), remember: true, force);
            return;
        }
        if (_mode == MapMode.Live && _formulaHour is double formula)
        {
            PublishSea(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), formula, SeaWind.MinuteOf(formula), remember: true, force);
            return;
        }
        var minute = _mode == MapMode.Live && LiveMinute() is int live ? live : _viewMinute;
        PublishSea(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), minute / 60.0, minute, remember: false, force);
    }

    private void PublishSea(double worldMs, double hour, int minute, bool remember, bool force)
    {
        if (_regions.Count == 0)
            return;
        var bucket = SeaWind.HourIndex(hour);
        var now = DateTime.UtcNow;
        if (!force && bucket == _arrowHour && now - _arrowsAt < ArrowRefresh)
            return;
        var hourChanged = bucket != _arrowHour;
        _arrowHour = bucket;
        _arrowsAt = now;
        _seaScratch.Clear();
        Field().Fill(_seaScratch, worldMs, hour, _trackedZone, _trackedEdge, _trackedWeather);
        if (remember && (force || hourChanged))
            RememberSea(minute, _seaScratch);
        ApplySea(_seaScratch, minute, force: true, refresh: true);
    }

    private SeaWind.SeaField Field() => _seaField ??= SeaWind.SeaField.Build(_regions, WaterIds());

    private HashSet<string>? WaterIds()
    {
        HashSet<string>? ids = null;
        foreach (var (id, survey) in _surveys)
        {
            if (survey.OpenSea || survey.Edges != 0)
                (ids ??= new HashSet<string>(StringComparer.Ordinal)).Add(id);
        }
        return ids;
    }

    private void RefreshWaterWind()
    {
        _seaField = null;
        _arrowHour = int.MinValue;
        EnsureSeaForView(force: true);
    }

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
        button.Foreground = on
            ? (System.Windows.Media.Brush)FindResource("InkBrush")
            : (System.Windows.Media.Brush)FindResource("TextBrush");
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
        Tile.ShowRegion(region, _viewMinute, ArrowFor(region.Id));
    }

    private WindSample? ArrowFor(string id) =>
        _arrows.TryGetValue(id, out var sample) ? sample : null;

    private void OnMetaSave(object? sender, TileMetaRequest request)
    {
        _database.SaveTileMeta(request.RegionId, request.Note, request.Tags, request.Caption, request.Icon);
        if (_byId.TryGetValue(request.RegionId, out var region))
        {
            region.Note = request.Note.Trim();
            region.Caption = TileIcons.CleanCaption(request.Caption);
            region.Icon = TileIcons.Canonical(request.Icon) ?? "";
            region.Tags = request.Tags.ToList();
            StatusText.Text = $"Saved description, note, mark, and tags for {region.Name}.";
            if (Map.SelectedId == region.Id)
                HoverText.Text = Describe(region);
        }
        Map.NoteChanged();
        RefreshArrows();
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
            if (sample.Strength <= 0 || sample.Cancelled)
                text += " · no wind arrow";
            else
                text += $" · {CompassText.Mark(sample.Strength)} {CompassText.Going(sample.DirectionDeg)}";
        }
        else
            text += " · no wind arrow";
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

    private void DayClick(object sender, RoutedEventArgs e)
    {
        ApplyMode(MapMode.Day);
        PublishSea(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), WindRules.DayViewMinute / 60.0, WindRules.DayViewMinute, remember: false, force: true);
    }

    private void NightClick(object sender, RoutedEventArgs e)
    {
        ApplyMode(MapMode.Night);
        PublishSea(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), WindRules.NightViewMinute / 60.0, WindRules.NightViewMinute, remember: false, force: true);
    }

    private void LiveClick(object sender, RoutedEventArgs e)
    {
        ApplyMode(MapMode.Live);
        EnsureSeaForView(force: true);
    }

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

    private void NoticeClick(object sender, RoutedEventArgs e)
    {
        var notice = new NoticeWindow(viewOnly: true) { Owner = this };
        notice.ShowDialog();
    }

    private void CreditsClick(object sender, RoutedEventArgs e)
    {
        var credits = new CreditsWindow { Owner = this };
        credits.ShowDialog();
    }

    private void ToggleInfoClick(object sender, RoutedEventArgs e)
    {
        var open = InfoOpen.Visibility != Visibility.Visible;
        InfoOpen.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        InfoClosed.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        InfoColumn.Width = open ? new GridLength(360) : GridLength.Auto;
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var snapshot = _database.Export(_realm, _worldId);
        var dialog = new SaveFileDialog
        {
            Filter = "Tile notes JSON (*.json)|*.json",
            FileName = $"asciilands-tiles-{_realm}.json",
            Title = "Export tile notes",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        MapExchange.Write(dialog.FileName, snapshot);
        StatusText.Text = snapshot.Tiles.Count == 0
            ? "Exported an empty tile file."
            : $"Exported notes for {snapshot.Tiles.Count} tiles in {Realms.Label(_realm)}.";
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Tile notes JSON (*.json)|*.json",
            Title = "Import tile notes",
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
                "Import tile notes",
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
                ? "That file has no tile notes to import." + UnknownSuffix(analysis.UnknownIds.Count)
                : $"Nothing new. {analysis.Unchanged} tile{(analysis.Unchanged == 1 ? "" : "s")} already match." + UnknownSuffix(analysis.UnknownIds.Count);
            return;
        }

        var result = _database.ApplyImport(snapshot, replace);
        LoadFromDatabase();
        StatusText.Text = $"Imported {result.Added} new, updated {result.Replaced}, kept {result.Kept}."
            + UnknownSuffix(result.Unknown);
    }

    private async void GameSyncToggled(object sender, RoutedEventArgs e)
    {
        if (GameSyncButton.IsChecked == true)
        {
            StyleSync(true);
            _gameTimer.Interval = TimeSpan.FromSeconds(5);
            _gameTimer.Start();
            await PollGameAsync();
            return;
        }

        _gameTimer.Stop();
        _gameVersion++;
        StyleSync(false);
        SyncStatus.Text = "";
        StatusText.Text = "Sync is off until you turn it on or open the app again. The last wind and location stay on the map.";
        _ = ClearOverlayAsync();
    }

    private void StyleSync(bool on)
    {
        GameSyncButton.Background = (System.Windows.Media.Brush)FindResource(on ? "AccentBrush" : "PanelBrush");
        GameSyncButton.Foreground = on
            ? (System.Windows.Media.Brush)FindResource("InkBrush")
            : (System.Windows.Media.Brush)FindResource("TextBrush");
    }

    private async Task PollGameAsync()
    {
        if (_gameBusy || GameSyncButton.IsChecked != true)
            return;
        _gameBusy = true;
        var version = _gameVersion;
        var placeEpoch = _placeEpoch;
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var game = await GameBrowser.ReadAsync(_debugHttp, cancel.Token);
            if (version != _gameVersion || GameSyncButton.IsChecked != true)
                return;
            ApplyGame(game, keepPlace: placeEpoch != _placeEpoch);
            if (!game.FromScreen && game.Detail is "ok" or "no-clock" or "visible")
                await ReadStandingChartAsync();
            FollowGps();
            if (OverlayWanted())
                _ = RefreshOverlayAsync();
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

    private void ApplyGame(GameSnapshot game, bool keepPlace = false)
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

        string? zoneId;
        string place;
        var unknown = false;
        if (keepPlace && !string.IsNullOrEmpty(_playerZone))
        {
            zoneId = _playerZone;
            place = _byId.TryGetValue(_playerZone, out var stayed) ? stayed.Name : game.Title.Trim();
        }
        else
        {
            zoneId = ZoneFor(game.Title, game.NearbyNames, game.ZoneId);
            place = game.Title.Trim();
            if (!string.IsNullOrEmpty(zoneId))
            {
                if (_byId.TryGetValue(zoneId, out var region))
                {
                    place = region.Name;
                    _player = new PlayerMark(region.Q, region.R, region.Biome.Equals("ocean", StringComparison.OrdinalIgnoreCase));
                    _playerZone = region.Id;
                }
                else
                {
                    _player = null;
                    _playerZone = null;
                    unknown = true;
                    if (place.Length == 0)
                        place = zoneId;
                }
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
            PublishSea(clock.ServerTimeMs + elapsed, hour, minute.Value, remember: true, force: false);
            seaNote = $"Sea wind refreshes every 15 seconds on {_seaScratch.Count} tiles.";
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
        PublishSea(worldMs, hour, minute, remember: true, force: false);
        var note = $"Sea wind refreshes every 15 seconds on {_seaScratch.Count} tiles.";
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

    private void RememberSea(int minute, IReadOnlyDictionary<string, SeaWindArrow> arrows) =>
        _database.ReplaceSyncWind(_realm, minute, arrows);

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
            return;
        }
        if (e.Key == Key.Escape && _gpsPicking)
        {
            _gpsPicking = false;
            StatusText.Text = "GPS destination cancelled.";
            e.Handled = true;
        }
    }

    private void OnRegionClicked(RegionRow region)
    {
        if (_gpsPicking && _gpsMode is TravelMode mode)
        {
            _gpsPicking = false;
            if (string.IsNullOrEmpty(_playerZone) || !_byId.ContainsKey(_playerZone))
            {
                StatusText.Text = "Sync has not found your hex yet. Open the game, then pick the destination again.";
                return;
            }
            var plan = ComputeRoute(mode, region.Id);
            if (!plan.Found)
            {
                StatusText.Text = plan.Status;
                return;
            }
            _gpsDestinationId = region.Id;
            _gpsPlan = plan;
            PaintRoute();
            StatusText.Text = plan.Status;
            _ = RefreshOverlayAsync();
            return;
        }
        OpenRegion(region);
    }

    private void GpsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new GpsWindow(_gpsPlan != null) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        if (dialog.Choice == GpsChoice.Clear)
        {
            ClearGps();
            StatusText.Text = "GPS route cleared.";
            return;
        }
        if (dialog.Choice is not (GpsChoice.Boat or GpsChoice.Walk))
            return;
        _gpsMode = dialog.Choice == GpsChoice.Boat ? TravelMode.Boat : TravelMode.Walk;
        _gpsPicking = true;
        StatusText.Text = _gpsMode == TravelMode.Boat
            ? "Boat. Click the destination hex. Escape cancels."
            : "Walk. Click the destination hex. Escape cancels.";
    }

    private async void UpdateMapClick(object sender, RoutedEventArgs e)
    {
        if (_closing)
            return;
        UpdateMapButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Reading revealed tiles from the game map…";
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var visited = await GameBrowser.ReadVisitedAsync(_debugHttp, cancel.Token);
            var fromScreen = false;
            if (_closing)
                return;
            if (visited == null)
            {
                StatusText.Text = "Reading the open map…";
                var screen = await Task.Run(GameBrowser.ReadAtlas, cancel.Token);
                if (_closing)
                    return;
                if (screen == null)
                {
                    StatusText.Text = "The play page could not be read. Open it in Edge or Chrome, with the map on screen.";
                    return;
                }
                if (!screen.MapOpen)
                {
                    StatusText.Text = "Open the map in the game with M, then press Update map.";
                    return;
                }
                if (screen.Hits.Count == 0)
                {
                    StatusText.Text = "No revealed tiles are on the open map. Pan the game map and press Update map again.";
                    return;
                }
                var nodes = await WorldGraph.DownloadAsync(_http, cancel.Token);
                if (_closing)
                    return;
                var placement = AtlasPlace.Place(screen.Hits, nodes);
                visited = new VisitedRead
                {
                    MapOpen = true,
                    Tiles = placement.Tiles.Select(tile => new VisitedTile
                    {
                        Id = tile.Id,
                        Name = tile.Name,
                        OpenSea = tile.OpenSea,
                        PngUrl = tile.PngUrl,
                    }).ToList(),
                };
                fromScreen = true;
                foreach (var name in placement.Unresolved)
                    visited.Tiles.Add(new VisitedTile { Name = name });
            }
            if (!visited.MapOpen)
            {
                StatusText.Text = "Open the map in the game with M, then press Update map.";
                return;
            }

            var pending = new List<(string RegionId, bool OpenSea, string Url)>();
            var skipped = 0;
            var unknown = new List<string>();
            foreach (var tile in visited.Tiles)
            {
                if (!SurveyMatch.TryMatch(_regions, tile.Id, tile.Name, out var regionId, out var problem))
                {
                    if (!string.IsNullOrWhiteSpace(problem))
                        unknown.Add(problem);
                    continue;
                }
                if (_surveys.ContainsKey(regionId))
                {
                    skipped++;
                    continue;
                }
                pending.Add((regionId, tile.OpenSea, tile.PngUrl));
            }

            var added = 0;
            var cache = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            var urls = pending.Select(item => item.Url).Where(url => url.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            for (var offset = 0; offset < urls.Count; offset += 4)
            {
                var batch = urls.Skip(offset).Take(4).ToList();
                StatusText.Text = $"Reading tile pictures {Math.Min(offset + batch.Count, urls.Count)} of {urls.Count}…";
                if (fromScreen)
                {
                    foreach (var (url, bytes) in await GameBrowser.DownloadPngsAsync(_http, batch, cancel.Token))
                        cache[url] = bytes;
                }
                else
                {
                    var fetched = await GameBrowser.FetchPngsAsync(_debugHttp, batch, cancel.Token);
                    foreach (var row in fetched)
                    {
                        if (string.IsNullOrWhiteSpace(row.Png))
                        {
                            cache[row.Url] = null;
                            continue;
                        }
                        try
                        {
                            cache[row.Url] = Convert.FromBase64String(row.Png);
                        }
                        catch (FormatException)
                        {
                            cache[row.Url] = null;
                        }
                    }
                }
                if (_closing)
                    return;
            }

            foreach (var item in pending)
            {
                byte[]? png = null;
                if (item.Url.Length > 0)
                    cache.TryGetValue(item.Url, out png);
                if (png == null && !item.OpenSea)
                    continue;
                StoreSurvey(item.RegionId, png, item.OpenSea, paint: false, refreshWind: false);
                added++;
            }
            if (added > 0)
            {
                Map.SetPictures(_surveyPictures);
                RefreshWaterWind();
            }

            var missed = unknown.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            var extra = unknown.Distinct(StringComparer.OrdinalIgnoreCase).Count() - missed.Count;
            var names = missed.Count == 0
                ? ""
                : " Skipped " + string.Join(", ", missed) + (extra > 0 ? $" and {extra} more" : "") + ".";
            var offScreen = fromScreen ? " Tiles off this view stay unread until the map shows them." : "";
            StatusText.Text = $"Update map added {added} tiles and left {skipped} already stored.{names}{offScreen}";
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or JsonException or InvalidOperationException or TaskCanceledException or IOException or InvalidDataException)
        {
            if (!_closing)
                StatusText.Text = "Update map could not finish reading the game.";
        }
        finally
        {
            UpdateMapButton.IsEnabled = true;
        }
    }

    private async Task ReadStandingChartAsync()
    {
        if (_closing || GameSyncButton.IsChecked != true)
            return;
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var chart = await GameBrowser.ReadCurrentChartAsync(_debugHttp, cancel.Token);
            if (_closing || chart == null || GameSyncButton.IsChecked != true)
                return;
            if (!SurveyMatch.TryMatch(_regions, chart.ZoneId, chart.Name, out var regionId, out _))
                return;
            byte[]? png = null;
            if (!string.IsNullOrWhiteSpace(chart.Png))
            {
                try
                {
                    png = Convert.FromBase64String(chart.Png);
                }
                catch (FormatException)
                {
                    png = null;
                }
            }
            if (png == null && (!chart.OpenSea || _surveys.ContainsKey(regionId)))
                return;
            StoreSurvey(regionId, png, chart.OpenSea);
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or JsonException or InvalidOperationException or TaskCanceledException or IOException or FormatException)
        {
        }
    }

    private void LoadSurveys()
    {
        _surveys.Clear();
        _surveyPictures.Clear();
        foreach (var survey in _database.LoadSurveys())
        {
            var profile = ProfileOf(survey.Png, survey.OpenSea, survey.Edges);
            _surveys[survey.RegionId] = new TileSurvey
            {
                RegionId = survey.RegionId,
                Edges = profile.Edges,
                Png = survey.Png,
                OpenSea = survey.OpenSea,
                Touch = profile.Touch,
                Spans = profile.Spans,
            };
            if (survey.Png is { Length: > 0 })
            {
                var image = ChartImage.Bitmap(survey.Png);
                if (image != null)
                    _surveyPictures[survey.RegionId] = image;
            }
        }
        Map.SetPictures(_surveyPictures);
    }

    private void StoreSurvey(string regionId, byte[]? png, bool openSea, bool paint = true, bool refreshWind = true)
    {
        var profile = ProfileOf(png, openSea, 0);
        var edges = profile.Edges;
        var wasWater = _surveys.TryGetValue(regionId, out var existing) && (existing.OpenSea || existing.Edges != 0);
        if (existing != null
            && existing.Edges == edges
            && existing.OpenSea == openSea
            && existing.Touch == profile.Touch
            && existing.Spans == profile.Spans
            && SameBytes(existing.Png, png))
            return;

        _database.SaveSurvey(regionId, edges, png, openSea);
        _surveys[regionId] = new TileSurvey
        {
            RegionId = regionId,
            Edges = edges,
            Png = png,
            OpenSea = openSea,
            Touch = profile.Touch,
            Spans = profile.Spans,
        };
        if (png is { Length: > 0 })
        {
            var image = ChartImage.Bitmap(png);
            if (image != null)
                _surveyPictures[regionId] = image;
        }
        else
            _surveyPictures.Remove(regionId);
        if (paint)
            Map.SetPictures(_surveyPictures);
        if (refreshWind && wasWater != (openSea || edges != 0))
            RefreshWaterWind();
    }

    private static WaterProfile ProfileOf(byte[]? png, bool openSea, byte fallbackEdges)
    {
        if (openSea)
            return WaterProfile.Open;
        if (png is not { Length: > 0 })
            return new WaterProfile(fallbackEdges, 0, 0);
        var pixels = ChartImage.Pixels(png, out var width, out var height, out var stride);
        if (pixels == null)
            return new WaterProfile(fallbackEdges, 0, 0);
        return WaterEdges.FromBgra(width, height, pixels, stride);
    }

    private static bool SameBytes(byte[]? left, byte[]? right)
    {
        if (left == null || right == null)
            return left == null && right == null;
        return left.AsSpan().SequenceEqual(right);
    }

    private RoutePlan ComputeRoute(TravelMode mode, string destinationId)
    {
        var start = _playerZone ?? "";
        var hexes = new List<RouteHex>(_regions.Count);
        foreach (var region in _regions)
        {
            var ocean = region.Biome.Equals("ocean", StringComparison.OrdinalIgnoreCase);
            byte water = 0;
            ulong touch = 0;
            ulong spans = 0;
            if (_surveys.TryGetValue(region.Id, out var survey))
            {
                water = survey.OpenSea ? WaterMask.All : survey.Edges;
                touch = survey.Touch;
                spans = survey.Spans;
            }
            hexes.Add(new RouteHex(region.Id, region.Q, region.R, ocean, water, touch, spans));
        }
        var wind = new Dictionary<string, WindAt>(StringComparer.Ordinal);
        foreach (var (id, sample) in _arrows)
        {
            if (!sample.Cancelled && sample.Strength > 0)
                wind[id] = new WindAt(sample.DirectionDeg, sample.Strength);
        }
        return RouteFinder.Find(hexes, start, destinationId, mode, wind, id => _byId.TryGetValue(id, out var region) ? region.Name : id);
    }

    private string? ZoneFor(string? title, IReadOnlyList<string>? nearby, string? addressed)
    {
        var named = RegionMatch.Find(_regions, title, nearby);
        if (!string.IsNullOrEmpty(named))
            return named;
        return string.IsNullOrEmpty(addressed) ? null : addressed;
    }

    private void MaybeGpsZone()
    {
        if (_closing || _gpsBusy)
            return;
        if (_gpsPlan is not { Found: true } || GameSyncButton.IsChecked != true)
            return;
        _ = PeekGpsZoneAsync();
    }

    private async Task PeekGpsZoneAsync()
    {
        if (_closing || _gpsBusy)
            return;
        if (_gpsPlan is not { Found: true } || GameSyncButton.IsChecked != true)
            return;
        _gpsBusy = true;
        try
        {
            var title = await Task.Run(GameBrowser.PeekZoneTitle);
            if (_closing)
                return;
            var zone = ZoneFor(title, null, null);
            if (string.IsNullOrEmpty(zone))
            {
                zone = await Task.Run(GameBrowser.PeekZoneId);
                if (_closing)
                    return;
            }
            if (string.IsNullOrEmpty(zone) || zone == _playerZone || !_byId.TryGetValue(zone, out var region))
                return;
            _placeEpoch++;
            _player = new PlayerMark(region.Q, region.R, region.Biome.Equals("ocean", StringComparison.OrdinalIgnoreCase));
            _playerZone = region.Id;
            Map.SetPlayer(_player);
            FollowGps();
            if (OverlayWanted())
                _ = RefreshOverlayAsync();
        }
        finally
        {
            _gpsBusy = false;
        }
    }

    private void FollowGps()
    {
        if (_gpsPlan == null || _gpsPlan.Steps.Count == 0 || _gpsMode == null || _gpsDestinationId == null || _playerZone == null)
            return;
        var index = RouteFinder.IndexOnRoute(_gpsPlan.Steps, _playerZone);
        if (index >= 0)
        {
            PaintRoute();
            return;
        }
        var plan = ComputeRoute(_gpsMode.Value, _gpsDestinationId);
        _gpsPlan = plan.Found ? plan : null;
        PaintRoute();
        StatusText.Text = plan.Found
            ? "Left the route. " + plan.Status
            : "Left the route. " + plan.Status;
    }

    private void PaintRoute()
    {
        if (_gpsPlan == null || !_gpsPlan.Found)
        {
            Map.SetRoute(null);
            return;
        }
        var index = _playerZone == null ? -1 : RouteFinder.IndexOnRoute(_gpsPlan.Steps, _playerZone);
        var marks = new List<RouteMark>(_gpsPlan.Steps.Count);
        for (var i = 0; i < _gpsPlan.Steps.Count; i++)
        {
            var step = _gpsPlan.Steps[i];
            marks.Add(new RouteMark(step.RegionId, step.EnterSide, step.ExitSide, Passed: index >= 0 && i < index, Current: i == index));
        }
        Map.SetRoute(marks);
    }

    private void ClearGps()
    {
        _gpsPlan = null;
        _gpsMode = null;
        _gpsDestinationId = null;
        _gpsPicking = false;
        Map.SetRoute(null);
        _ = ClearOverlayAsync();
    }

    private void MaybeOverlay()
    {
        if (_closing || _overlayBusy)
            return;
        if (!OverlayWanted())
        {
            if (_overlayOnPage || _float is { IsVisible: true })
                _ = ClearOverlayAsync();
            return;
        }
        if ((DateTime.UtcNow - _overlayAt).TotalMilliseconds < 900)
            return;
        _ = RefreshOverlayAsync();
    }

    private async Task RefreshOverlayAsync()
    {
        if (!OverlayWanted() || _overlayBusy)
            return;
        _overlayBusy = true;
        _overlayAt = DateTime.UtcNow;
        try
        {
            for (var pass = 0; pass < 2 && OverlayWanted(); pass++)
            {
                var drawnZone = _playerZone;
                var step = CurrentStep();
                var sample = drawnZone != null && _arrows.TryGetValue(drawnZone, out var reading) ? reading : (WindSample?)null;
                var arrow = MinimapOverlay.Arrow(sample);
                var enter = step.EnterSide;
                var exit = step.ExitSide;
                var onPage = false;
                try
                {
                    var script = MinimapOverlay.InstallScript(arrow, enter, exit);
                    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    var json = await GameBrowser.InstallOverlayAsync(_debugHttp, script, cancel.Token);
                    onPage = json != null && json.Contains("true", StringComparison.Ordinal);
                }
                catch (Exception ex) when (ex is HttpRequestException or WebSocketException or JsonException or InvalidOperationException or TaskCanceledException or IOException)
                {
                    onPage = false;
                }
                if (!OverlayWanted())
                    return;
                if (onPage)
                {
                    _overlayOnPage = true;
                    HideFloat();
                    if (_playerZone == drawnZone)
                        return;
                    continue;
                }

                var box = await Task.Run(GameBrowser.MinimapBox);
                if (!OverlayWanted())
                    return;
                _overlayOnPage = false;
                if (_playerZone != drawnZone)
                    continue;
                if (box is GameBrowser.ScreenBox place)
                    ShowFloat(place, arrow, enter, exit);
                else
                    HideFloat();
                return;
            }
        }
        finally
        {
            _overlayBusy = false;
        }
    }

    private bool OverlayWanted() =>
        !_closing && _gpsPlan is { Found: true } && GameSyncButton.IsChecked == true;

    private void ShowFloat(GameBrowser.ScreenBox box, WindArrow arrow, int enter, int exit)
    {
        _float ??= new MinimapFloat();
        _float.ShowOver(this, box.X, box.Y, box.Width, box.Height, arrow, enter, exit);
    }

    private void HideFloat()
    {
        if (_float is { IsVisible: true })
            _float.Hide();
    }

    private RouteStep CurrentStep()
    {
        if (_gpsPlan == null || _playerZone == null)
            return new RouteStep("", -1, -1);
        var index = RouteFinder.IndexOnRoute(_gpsPlan.Steps, _playerZone);
        if (index < 0)
            return new RouteStep(_playerZone, -1, -1);
        var step = _gpsPlan.Steps[index];
        var enter = index == 0 ? -1 : step.EnterSide;
        return step with { EnterSide = enter };
    }

    private async Task ClearOverlayAsync()
    {
        if (_closing)
            return;
        HideFloat();
        if (!_overlayOnPage)
            return;
        _overlayBusy = true;
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await GameBrowser.RemoveOverlayAsync(_debugHttp, cancel.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or JsonException or InvalidOperationException or TaskCanceledException or IOException)
        {
        }
        finally
        {
            _overlayOnPage = false;
            _overlayBusy = false;
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
