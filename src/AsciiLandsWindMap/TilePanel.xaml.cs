using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AsciiLandsWindMap;

public readonly record struct TileMetaRequest(string RegionId, string Note, string Caption, string Icon, IReadOnlyList<string> Tags);

public partial class TilePanel
{
    private readonly Dictionary<string, System.Windows.Controls.Primitives.ToggleButton> _builtIn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, System.Windows.Controls.Primitives.ToggleButton> _icons = new(StringComparer.Ordinal);
    private readonly List<string> _custom = [];
    private string _icon = "";
    private RegionRow? _region;
    private bool _editing;

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
        ShowHelp();
    }

    public string? RegionId => _region?.Id;

    public bool IsEditing => _editing;

    public event EventHandler<TileMetaRequest>? MetaSaveRequested;
    public event EventHandler? CloseRequested;

    public void ShowHelp()
    {
        _region = null;
        _editing = false;
        HelpPanel.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
    }

    public void ShowRegion(RegionRow region, int viewMinute, WindSample? shown)
    {
        _region = region;
        _editing = false;
        HelpPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        NameText.Text = region.Name;
        MetaText.Text = $"{BiomeNames.Pretty(region.Biome)} · {WorldNames.Slug(region.Id)}";
        UpdateMapTime(viewMinute);
        WindText.Text = FormatWind(shown);
        ShowRead();
    }

    public void UpdateShown(int minute, WindSample? shown)
    {
        UpdateMapTime(minute);
        if (_region == null)
            return;
        WindText.Text = FormatWind(shown);
    }

    public void UpdateMapTime(int minute)
    {
        var text = $"Map is showing {GameClock.Format(minute)} · {WindRules.Phase(minute)} · {WindRules.WindowLabel(WindRules.WindowOf(minute))}";
        MapTimeText.Text = text;
        if (EditorTimeText != null)
            EditorTimeText.Text = text;
    }

    private void ShowRead()
    {
        if (_region == null)
            return;
        _editing = false;
        CaptionRead.Text = string.IsNullOrWhiteSpace(_region.Caption) ? "No description." : _region.Caption.Trim();
        NoteRead.Text = string.IsNullOrWhiteSpace(_region.Note) ? "No note." : _region.Note.Trim();
        var mark = TileIcons.LabelOf(_region.Icon);
        MarkRead.Text = mark.Length == 0 ? "No mark." : mark;
        TagsRead.Text = _region.Tags.Count == 0
            ? "No tags."
            : string.Join(", ", _region.Tags.Select(TagLabel));
        ReadBody.Visibility = Visibility.Visible;
        ReadBar.Visibility = Visibility.Visible;
        EditBody.Visibility = Visibility.Collapsed;
        EditBar.Visibility = Visibility.Collapsed;
    }

    private static string FormatWind(WindSample? shown)
    {
        if (shown is not { Strength: > 0 } sample || sample.Cancelled)
            return "No wind arrow on this tile.";
        return $"{CompassText.Mark(sample.Strength)} {CompassText.StrengthWord(sample.Strength)} · {CompassText.Going(sample.DirectionDeg)}";
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

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        ShowHelp();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void EditClick(object sender, RoutedEventArgs e)
    {
        if (_region == null)
            return;
        _editing = true;
        NoteBox.Text = _region.Note;
        CaptionBox.Text = _region.Caption;
        SelectIcon(TileIcons.Canonical(_region.Icon) ?? "");
        LoadTags(_region.Tags);
        ErrorText.Text = "";
        ReadBody.Visibility = Visibility.Collapsed;
        ReadBar.Visibility = Visibility.Collapsed;
        EditBody.Visibility = Visibility.Visible;
        EditBar.Visibility = Visibility.Visible;
    }

    private void CancelEditClick(object sender, RoutedEventArgs e) => ShowRead();

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

    private List<string> CollectTags()
    {
        var tags = _builtIn.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).Concat(_custom);
        return TileTags.CanonicalSet(tags);
    }

    private void SaveMetaClick(object sender, RoutedEventArgs e)
    {
        if (_region == null)
            return;
        ErrorText.Text = "";
        var caption = TileIcons.CleanCaption(CaptionBox.Text);
        CaptionBox.Text = caption;
        MetaSaveRequested?.Invoke(this, new TileMetaRequest(_region.Id, NoteBox.Text, caption, _icon, CollectTags()));
        ShowRead();
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
}
