using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AsciiLandsWindMap;

public readonly record struct WindReadingRequest(string RegionId, int GameMinute, double DirectionDeg, int Strength);

public readonly record struct TileMetaRequest(string RegionId, string Note, string Caption, string Icon, IReadOnlyList<string> Tags);

public partial class TilePanel
{
    private readonly Dictionary<string, System.Windows.Controls.Primitives.ToggleButton> _builtIn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, System.Windows.Controls.Primitives.ToggleButton> _icons = new(StringComparer.Ordinal);
    private readonly List<string> _custom = [];
    private string _icon = "";
    private RegionRow? _region;
    private List<ObservationRow> _readings = [];
    private int _mapMinute = WindRules.DayViewMinute;

    public TilePanel()
    {
        InitializeComponent();
        foreach (var (id, label) in TileTags.BuiltIn)
        {
            var toggle = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = label,
                Style = (Style)FindResource("Chip"),
                Tag = id,
            };
            _builtIn[id] = toggle;
            BuiltInWrap.Children.Add(toggle);
        }
        foreach (var (id, label) in TileIcons.All)
        {
            var toggle = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = label,
                Style = (Style)FindResource("Chip"),
                Tag = id,
            };
            toggle.Click += IconClick;
            _icons[id] = toggle;
            IconWrap.Children.Add(toggle);
        }
        Compass.Changed += (_, _) => UpdateCompassCaption();
        ShowHelp();
    }

    public string? RegionId => _region?.Id;

    public event EventHandler<WindReadingRequest>? WindSaveRequested;
    public event EventHandler<TileMetaRequest>? MetaSaveRequested;
    public event EventHandler<long>? ReadingDeleteRequested;

    public void ShowHelp()
    {
        _region = null;
        HelpPanel.Visibility = Visibility.Visible;
        EditorPanel.Visibility = Visibility.Collapsed;
    }

    public void ShowRegion(RegionRow region, IReadOnlyList<ObservationRow> readings, int viewMinute, WindSample? shown)
    {
        _region = region;
        _readings = readings.OrderBy(item => item.GameMinute).ToList();
        HelpPanel.Visibility = Visibility.Collapsed;
        EditorPanel.Visibility = Visibility.Visible;
        NameText.Text = region.Name;
        MetaText.Text = $"{BiomeNames.Pretty(region.Biome)} · {WorldNames.Slug(region.Id)}";
        NoteBox.Text = region.Note;
        CaptionBox.Text = region.Caption;
        SelectIcon(TileIcons.Canonical(region.Icon) ?? "");
        LoadTags(region.Tags);
        UpdateMapTime(viewMinute);
        TimeBox.Text = GameClock.Format(viewMinute);
        var exact = _readings.FirstOrDefault(item => item.GameMinute == viewMinute);
        if (exact != null)
            Compass.SetReading(exact.DirectionDeg, exact.Strength);
        else if (shown is { Cancelled: false, Strength: > 0 } sample)
            Compass.SetReading(sample.DirectionDeg, sample.Strength);
        else
            Compass.SetReading(0, 0);
        ErrorText.Text = shown is { Cancelled: true }
            ? "Saved readings in this window point opposite ways, so the map has no arrow."
            : "";
        UpdateCompassCaption();
        RebuildReadingList();
        UpdateSaveButton();
    }

    public void UpdateMapTime(int minute)
    {
        _mapMinute = minute;
        var text = $"Map is showing {GameClock.Format(minute)} · {WindRules.Phase(minute)} · {WindRules.WindowLabel(WindRules.WindowOf(minute))}";
        MapTimeText.Text = text;
        if (EditorTimeText != null)
            EditorTimeText.Text = text;
    }

    public void SetSuggestedTime(int minute)
    {
        if (_region == null)
            return;
        TimeBox.Text = GameClock.Format(minute);
        UpdateSaveButton();
    }

    public void RefreshReadings(IReadOnlyList<ObservationRow> readings)
    {
        _readings = readings.OrderBy(item => item.GameMinute).ToList();
        RebuildReadingList();
        UpdateSaveButton();
    }

    private void LoadTags(IEnumerable<string> tags)
    {
        foreach (var toggle in _builtIn.Values)
            toggle.IsChecked = false;
        _custom.Clear();
        foreach (var tag in tags)
        {
            var canonical = TileTags.Canonical(tag);
            if (canonical == null)
                continue;
            if (_builtIn.TryGetValue(canonical, out var toggle))
                toggle.IsChecked = true;
            else
                _custom.Add(canonical);
        }
        RebuildCustom();
    }

    private void RebuildCustom()
    {
        CustomWrap.Children.Clear();
        foreach (var tag in _custom)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 6) };
            panel.Children.Add(new TextBlock { Text = tag, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            var remove = new Button { Content = "×", Padding = new Thickness(6, 0, 6, 0), Tag = tag };
            remove.Click += (_, _) =>
            {
                _custom.Remove(tag);
                RebuildCustom();
            };
            panel.Children.Add(remove);
            CustomWrap.Children.Add(panel);
        }
    }

    private void RebuildReadingList()
    {
        ReadingList.Children.Clear();
        if (_readings.Count == 0)
        {
            ReadingList.Children.Add(new TextBlock
            {
                Text = "No readings yet.",
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            });
            return;
        }
        foreach (var reading in _readings)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = $"{TileText.FormatReading(reading.ToLite())} · {WindRules.WindowLabel(WindRules.WindowOf(reading.GameMinute))}",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 0);
            var delete = new Button { Content = "Delete", Tag = reading.Id, Margin = new Thickness(8, 0, 0, 0) };
            delete.Click += (_, _) => ReadingDeleteRequested?.Invoke(this, reading.Id);
            Grid.SetColumn(delete, 1);
            row.Children.Add(label);
            row.Children.Add(delete);
            ReadingList.Children.Add(row);
        }
    }

    private List<string> CollectTags()
    {
        var tags = _builtIn.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).Concat(_custom);
        return TileTags.CanonicalSet(tags);
    }

    private void UpdateCompassCaption()
    {
        if (Compass.Strength <= 0)
        {
            StrengthText.Text = "Calm. No arrow.";
            CompassCaption.Text = "Drag the marker out from the center. The rings are ~, ≈, and ≋.";
            return;
        }
        StrengthText.Text = $"{CompassText.Mark(Compass.Strength)} {CompassText.StrengthWord(Compass.Strength)} · {CompassText.Going(Compass.DirectionDeg)}";
        CompassCaption.Text = $"The game compass would say the wind blows {CompassText.BlowsFrom(Compass.DirectionDeg)}.";
    }

    private void UpdateSaveButton()
    {
        var exists = GameClock.TryParseTime(TimeBox.Text, out var minute) && _readings.Any(item => item.GameMinute == minute);
        SaveWindButton.Content = exists ? "Update wind reading" : "Save wind reading";
    }

    private void SaveWindClick(object sender, RoutedEventArgs e)
    {
        if (_region == null)
            return;
        if (!GameClock.TryParseTime(TimeBox.Text, out var minute))
        {
            ErrorText.Text = "Enter the game time as HH:MM.";
            return;
        }
        if (string.IsNullOrEmpty(ErrorText.Text) || ErrorText.Text.StartsWith("Enter the game time", StringComparison.Ordinal))
            ErrorText.Text = "";
        WindSaveRequested?.Invoke(this, new WindReadingRequest(_region.Id, minute, Compass.DirectionDeg, Compass.Strength));
    }

    private void SaveMetaClick(object sender, RoutedEventArgs e)
    {
        if (_region == null)
            return;
        ErrorText.Text = "";
        var caption = TileIcons.CleanCaption(CaptionBox.Text);
        CaptionBox.Text = caption;
        MetaSaveRequested?.Invoke(this, new TileMetaRequest(_region.Id, NoteBox.Text, caption, _icon, CollectTags()));
    }

    private void SelectIcon(string icon)
    {
        _icon = icon;
        foreach (var pair in _icons)
            pair.Value.IsChecked = pair.Key == icon;
    }

    private void IconClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton toggle || toggle.Tag is not string id)
            return;
        SelectIcon(toggle.IsChecked == true ? id : "");
    }

    private void CalmClick(object sender, RoutedEventArgs e)
    {
        Compass.SetReading(Compass.DirectionDeg, 0);
        UpdateCompassCaption();
    }

    private void AddTagClick(object sender, RoutedEventArgs e) => AddTag();

    private void CustomTagKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddTag();
            e.Handled = true;
        }
    }

    private void AddTag()
    {
        var tag = TileTags.Canonical(CustomTagBox.Text);
        if (tag == null)
            return;
        if (_builtIn.TryGetValue(tag, out var toggle))
            toggle.IsChecked = true;
        else if (_custom.All(item => !string.Equals(item, tag, StringComparison.OrdinalIgnoreCase)))
        {
            _custom.Add(tag);
            RebuildCustom();
        }
        CustomTagBox.Clear();
    }

    private void TimeBoxChanged(object sender, TextChangedEventArgs e) => UpdateSaveButton();
}
