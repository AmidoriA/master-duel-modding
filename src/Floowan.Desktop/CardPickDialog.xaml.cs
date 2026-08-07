using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Floowan.Desktop.Localization;

namespace Floowan.Desktop;

public partial class CardPickDialog : Window
{
    public sealed class PickItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public int Id { get; init; }
        public string Label { get; init; } = "";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly ObservableCollection<PickItem> _items;

    public CardPickDialog(Window owner, string title, string intro, IEnumerable<PickItem> items)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        IntroText.Text = intro;
        _items = new ObservableCollection<PickItem>(items);
        CardList.ItemsSource = _items;
        foreach (var item in _items)
            item.PropertyChanged += (_, _) => RefreshCount();

        OkButton.Content = Loc.T("common.confirm");
        CancelButton.Content = Loc.T("common.cancel");
        SelectAllButton.Content = Loc.T("import_export.select_all");
        SelectNoneButton.Content = Loc.T("import_export.select_none");
        RefreshCount();
    }

    public IReadOnlyList<int> SelectedIds =>
        _items.Where(i => i.IsSelected).Select(i => i.Id).ToList();

    private void RefreshCount()
    {
        var selected = _items.Count(i => i.IsSelected);
        CountText.Text = Loc.T("import_export.selected_count", selected, _items.Count);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
            item.IsSelected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
            item.IsSelected = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedIds.Count == 0)
        {
            MessageBox.Show(
                this,
                Loc.T("import_export.select_at_least_one"),
                Loc.T("app.caption"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
