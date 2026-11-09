using System.ComponentModel;
using System.Windows;

namespace AsciiLandsWindMap;

public partial class ImportWindow : Window
{
    private readonly List<ConflictRow> _rows;

    public ImportWindow(ImportAnalysis analysis)
    {
        InitializeComponent();
        _rows = analysis.Conflicts.Select(conflict => new ConflictRow(conflict)).ToList();
        ConflictList.ItemsSource = _rows;
        var extra = analysis.Adds.Count == 0
            ? ""
            : $" {analysis.Adds.Count} new tile{(analysis.Adds.Count == 1 ? "" : "s")} will be added either way.";
        var unknown = analysis.UnknownIds.Count == 0
            ? ""
            : $" {analysis.UnknownIds.Count} region{(analysis.UnknownIds.Count == 1 ? "" : "s")} in the file are not on your map yet and will be skipped.";
        HeaderText.Text = $"{_rows.Count} tile{(_rows.Count == 1 ? "" : "s")} differ. Choose what to keep.{extra}{unknown}";
    }

    public IReadOnlySet<string> ReplaceIds =>
        _rows.Where(row => row.ChoiceIndex == 1).Select(row => row.Conflict.RegionId).ToHashSet(StringComparer.Ordinal);

    private void KeepAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.ChoiceIndex = 0;
    }

    private void UseAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.ChoiceIndex = 1;
    }

    private void ApplyClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class ConflictRow : INotifyPropertyChanged
    {
        private int _choice;

        public ConflictRow(ImportConflict conflict) => Conflict = conflict;

        public ImportConflict Conflict { get; }
        public string Title => Conflict.DisplayName;
        public string Local => Conflict.LocalSummary;
        public string Theirs => Conflict.TheirSummary;

        public int ChoiceIndex
        {
            get => _choice;
            set
            {
                if (_choice == value)
                    return;
                _choice = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChoiceIndex)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
