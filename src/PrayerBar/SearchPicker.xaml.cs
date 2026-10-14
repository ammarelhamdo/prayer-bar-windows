using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PrayerBar;

/// <summary>One row in a <see cref="SearchPicker"/>.</summary>
/// <param name="Value">The underlying object (Country, City, method id, minutes…).</param>
/// <param name="Primary">Text in the current UI language (shown on the left/leading side).</param>
/// <param name="Secondary">Optional hint in the other language, shown dimmed (helps bilingual search).</param>
public sealed record PickerItem(object Value, string Primary, string? Secondary = null)
{
    /// <summary>Folded once up front so filtering thousands of rows per keystroke stays instant.</summary>
    public string SearchKey { get; } = CityCatalog.Fold(Primary + " " + Secondary);
}

/// <summary>
/// Searchable dropdown. Chosen over an editable ComboBox because WPF's editable ComboBox
/// auto-completes and replaces text while typing, which fights with filtering (and with
/// Arabic input). Here the search box and the selection are separate, so neither surprises.
/// </summary>
public partial class SearchPicker : UserControl
{
    private IReadOnlyList<PickerItem> _items = Array.Empty<PickerItem>();
    private PickerItem? _selected;

    /// <summary>Raised when the user commits a different item (not when set from code).</summary>
    public event EventHandler? SelectionChanged;

    public SearchPicker()
    {
        InitializeComponent();
    }

    /// <summary>Show the search box (off for short lists such as notify minutes).</summary>
    public bool Searchable
    {
        get => SearchRow.Visibility == Visibility.Visible;
        set => SearchRow.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public string PlaceholderText { set => Placeholder.Text = value; }
    public string EmptyText { set => Empty.Text = value; }

    public IReadOnlyList<PickerItem> Items
    {
        get => _items;
        set
        {
            _items = value;
            // Keep the same logical selection if it still exists (e.g. after a language switch).
            var keep = _selected == null ? null : _items.FirstOrDefault(i => Equals(i.Value, _selected.Value));
            SetSelected(keep);
            ApplyFilter();
        }
    }

    public PickerItem? SelectedItem => _selected;

    public bool IsOpen => Toggle.IsChecked == true;

    /// <summary>Closes the dropdown without changing the selection.</summary>
    public void Close()
    {
        Toggle.IsChecked = false;
        Toggle.Focus();
    }

    public object? SelectedValue
    {
        get => _selected?.Value;
        set => SetSelected(_items.FirstOrDefault(i => Equals(i.Value, value)));
    }

    private void SetSelected(PickerItem? item)
    {
        _selected = item;
        SelectedText.Text = item?.Primary ?? "";
    }

    private void Commit(PickerItem? item)
    {
        Toggle.IsChecked = false;
        Toggle.Focus();
        if (item == null || Equals(item.Value, _selected?.Value)) return;
        SetSelected(item);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnToggleChecked(object sender, RoutedEventArgs e)
    {
        Search.Text = "";
        ApplyFilter();
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        List.SelectedItem = _selected;
        if (_selected != null) List.ScrollIntoView(_selected);
        // Focus after the popup is laid out, otherwise the focus request is lost.
        Dispatcher.BeginInvoke(() =>
        {
            if (Searchable) Keyboard.Focus(Search);
            else if (List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem ?? (_items.Count > 0 ? _items[0] : null)) is ListBoxItem c)
                c.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = CityCatalog.Fold(Search.Text.Trim());
        IReadOnlyList<PickerItem> filtered = query.Length == 0
            ? _items
            : _items.Where(i => i.SearchKey.Contains(query, StringComparison.Ordinal))
                    // Prefix matches first: typing "is" should offer Istanbul before Kilis.
                    .OrderBy(i => i.SearchKey.StartsWith(query, StringComparison.Ordinal) ? 0 : 1)
                    .ToList();
        List.ItemsSource = filtered;
        List.SelectedItem = filtered.Contains(_selected!) ? _selected : filtered.FirstOrDefault();
        Empty.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Arrow keys move through results while typing; Enter picks, Esc closes.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        int count = List.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                List.SelectedIndex = Math.Min(count - 1, List.SelectedIndex + 1);
                List.ScrollIntoView(List.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                List.SelectedIndex = Math.Max(0, List.SelectedIndex - 1);
                List.ScrollIntoView(List.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                Commit(List.SelectedItem as PickerItem);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Commit(List.SelectedItem as PickerItem); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        // Only commit when the click landed on a row (not on the scrollbar).
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem row)
            Commit(row.DataContext as PickerItem);
    }
}
