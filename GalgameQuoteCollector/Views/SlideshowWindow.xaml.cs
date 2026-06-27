using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using GalgameQuoteCollector.Models;

namespace GalgameQuoteCollector.Views;

public partial class SlideshowWindow : Window
{
    private readonly List<Quote> _allQuotes;         // full unfiltered list
    private readonly Dictionary<int, List<Tag>> _tagsByQuote;
    private readonly Dictionary<int, List<QuoteGroup>> _groupsByQuote;
    private readonly List<QuoteGroup> _availableGroups;
    private readonly List<Tag> _availableTags;
    private readonly Random _random = new();
    private readonly int _mode;  // 0=时间, 1=随机

    private List<Quote> _filtered;  // current filtered list
    private int[] _order;
    private int _pos;
    private bool _ready;
    private bool _isFullscreen;
    private bool _isTopmost;

    public SlideshowWindow(Window owner, List<Quote> quotes,
        Dictionary<int, List<Tag>> tagsByQuote,
        Dictionary<int, List<QuoteGroup>> groupsByQuote,
        List<QuoteGroup> availableGroups,
        List<Tag> availableTags,
        int slideshowMode)
    {
        InitializeComponent();
        Owner = owner;
        _allQuotes = quotes;
        _tagsByQuote = tagsByQuote;
        _groupsByQuote = groupsByQuote;
        _availableGroups = availableGroups;
        _availableTags = availableTags;
        _mode = slideshowMode;

        // Populate group filter
        foreach (var g in availableGroups)
            GroupFilter.Items.Add(new ComboBoxItem { Content = g.Name, Tag = g });
        if (availableTags.Count > 0)
        {
            foreach (var t in availableTags)
                TagFilter.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t });
        }

        ApplyFilter();
        if (_filtered.Count > 0) ShowCurrent();
        _ready = true;
    }

    private void ApplyFilter()
    {
        var items = _allQuotes.AsEnumerable();

        // Group filter
        if (GroupFilter.SelectedItem is ComboBoxItem gi && gi.Tag is QuoteGroup g)
            items = items.Where(q => _groupsByQuote.GetValueOrDefault(q.Id, []).Any(gg => gg.Id == g.Id));

        // Tag filter
        if (TagFilter.SelectedItem is ComboBoxItem ti && ti.Tag is Tag t)
            items = items.Where(q => _tagsByQuote.GetValueOrDefault(q.Id, []).Any(tt => tt.Id == t.Id));

        _filtered = items.ToList();

        if (_mode == 1)
            _order = Enumerable.Range(0, _filtered.Count).OrderBy(_ => _random.Next()).ToArray();
        else
            _order = Enumerable.Range(0, _filtered.Count).ToArray();

        _pos = 0;
    }

    private Quote Current => _filtered.Count > 0 ? _filtered[_order[_pos]] : null!;

    private void ShowCurrent()
    {
        if (_filtered.Count == 0 || _pos >= _order.Length)
        {
            Close();
            return;
        }

        var quote = Current;
        GameNameText.Text = string.IsNullOrWhiteSpace(quote.GameName) ? "未分类" : quote.GameName;
        QuoteText.Text = quote.Text;

        TagsText.Text = _tagsByQuote.TryGetValue(quote.Id, out var tags) && tags.Count > 0
            ? string.Join("  ", tags.Select(t => $"#{t.Name}")) : "";

        ProgressText.Text = $"{_pos + 1} / {_filtered.Count}";
        PrevOrCloseBtn.Content = _pos == 0 ? "← 关闭" : "← 上一条";
        NextOrCloseBtn.Content = _pos == _filtered.Count - 1 ? "关闭 →" : "下一条 →";

        FsGameNameText.Text = GameNameText.Text;
        FsQuoteText.Text = quote.Text;
        bool hasNotes = !string.IsNullOrWhiteSpace(quote.Notes);
        FsNotesText.Text = quote.Notes;

        FsGameNameText.Visibility = quote.SlideshowShowGameName ? Visibility.Visible : Visibility.Collapsed;
        FsQuoteText.Visibility = quote.SlideshowShowText ? Visibility.Visible : Visibility.Collapsed;
        FsNotesText.Visibility = (hasNotes && quote.SlideshowShowNotes) ? Visibility.Visible : Visibility.Collapsed;
        FsOverlay.Visibility = (quote.SlideshowShowGameName || quote.SlideshowShowText ||
            (hasNotes && quote.SlideshowShowNotes)) ? Visibility.Visible : Visibility.Collapsed;

        BitmapImage? bitmap = null;
        if (!string.IsNullOrEmpty(quote.ScreenshotPath) && File.Exists(quote.ScreenshotPath))
        {
            try
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(quote.ScreenshotPath);
                bitmap.EndInit();
            }
            catch { }
        }

        ScreenshotImage.Source = bitmap;
        ScreenshotBox.Visibility = bitmap != null ? Visibility.Visible : Visibility.Collapsed;
        FsScreenshotImage.Source = bitmap;
    }

    private void GoPrev() { if (_pos > 0) { _pos--; ShowCurrent(); } else Close(); }
    private void GoNext() { if (_pos < _filtered.Count - 1) { _pos++; ShowCurrent(); } else Close(); }
    private void GoRandom() { _pos = _random.Next(_filtered.Count); ShowCurrent(); }

    private void OnPrevOrClose(object sender, RoutedEventArgs e) => GoPrev();
    private void OnNextOrClose(object sender, RoutedEventArgs e) => GoNext();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnFilterChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ApplyFilter();
        if (_filtered.Count > 0) ShowCurrent(); else Close();
    }

    private void ToggleSlideshowTopmost()
    {
        _isTopmost = !_isTopmost;
        Topmost = _isTopmost;
        TopmostBtn.Background = _isTopmost
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.LightGray;
    }
    private void OnToggleTopmost(object sender, RoutedEventArgs e) => ToggleSlideshowTopmost();
    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            WindowState = WindowState.Maximized; ResizeMode = ResizeMode.NoResize; Topmost = true;
            NormalLayout.Visibility = Visibility.Collapsed; FullscreenLayout.Visibility = Visibility.Visible;
            FullscreenBtn.Content = "窗口";
        }
        else
        {
            WindowState = WindowState.Normal; ResizeMode = ResizeMode.CanResize; Topmost = _isTopmost;
            NormalLayout.Visibility = Visibility.Visible; FullscreenLayout.Visibility = Visibility.Collapsed;
            FullscreenBtn.Content = "全屏";
            if (Owner != null) { Left = Owner.Left + (Owner.Width - Width) / 2; Top = Owner.Top + (Owner.Height - Height) / 2; }
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:        GoPrev(); break;
            case Key.Right:
            case Key.PageDown:      GoNext(); break;
            case Key.Space:         GoNext(); break;
            case Key.Enter:         GoRandom(); break;
            case Key.F11:           ToggleFullscreen(); break;
            case Key.F2:            ToggleSlideshowTopmost(); break;
            case Key.Escape:
                if (_isFullscreen) ToggleFullscreen(); else Close();
                break;
            case Key.Home:          _pos = 0; ShowCurrent(); break;
            case Key.End:           _pos = _filtered.Count - 1; ShowCurrent(); break;
        }
        e.Handled = true;
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isFullscreen && !e.Handled)
        {
            if (e.LeftButton == MouseButtonState.Pressed) GoNext();
            else if (e.RightButton == MouseButtonState.Pressed) GoPrev();
        }
    }
}
