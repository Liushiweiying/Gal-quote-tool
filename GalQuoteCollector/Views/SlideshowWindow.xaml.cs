using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Views;

public partial class SlideshowWindow : Window
{
    private readonly List<Quote> _allQuotes;
    private readonly Dictionary<int, List<Tag>> _tagsByQuote;
    private readonly Dictionary<int, List<QuoteGroup>> _groupsByQuote;
    private readonly Dictionary<int, List<Screenshot>> _screenshotsByQuote;
    private readonly List<QuoteGroup> _availableGroups;
    private readonly List<Tag> _availableTags;
    private readonly Random _random = new();
    private readonly int _mode;
    private bool _loop;

    private List<Quote> _filtered = new();          // currently filtered quotes
    private int[] _order = Array.Empty<int>();      // quote order indices into _filtered
    private int _pos;                       // current quote index in _order
    private int _ssPos;                     // current screenshot index for the current quote
    private bool _ready;
    private bool _isFullscreen;
    private bool _isTopmost;
    private readonly string _chineseFont;
    private readonly string _englishFont;

    public SlideshowWindow(Window owner, List<Quote> quotes,
        Dictionary<int, List<Tag>> tagsByQuote,
        Dictionary<int, List<QuoteGroup>> groupsByQuote,
        Dictionary<int, List<Screenshot>> screenshotsByQuote,
        List<QuoteGroup> availableGroups,
        List<Tag> availableTags,
        int slideshowMode, bool slideshowLoop,
        string chineseFont = "Microsoft YaHei", string englishFont = "Segoe UI")
    {
        InitializeComponent();
        Owner = owner;
        _allQuotes = quotes;
        _tagsByQuote = tagsByQuote;
        _groupsByQuote = groupsByQuote;
        _screenshotsByQuote = screenshotsByQuote;
        _availableGroups = availableGroups;
        _availableTags = availableTags;
        _mode = slideshowMode;
        _loop = slideshowLoop;
        _chineseFont = chineseFont;
        _englishFont = englishFont;

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

        if (GroupFilter.SelectedItem is ComboBoxItem gi && gi.Tag is QuoteGroup g)
            items = items.Where(q => _groupsByQuote.GetValueOrDefault(q.Id, []).Any(gg => gg.Id == g.Id));

        if (TagFilter.SelectedItem is ComboBoxItem ti && ti.Tag is Tag t)
            items = items.Where(q => _tagsByQuote.GetValueOrDefault(q.Id, []).Any(tt => tt.Id == t.Id));

        _filtered = items.ToList();

        if (_mode == 1)
            _order = Enumerable.Range(0, _filtered.Count).OrderBy(_ => _random.Next()).ToArray();
        else
            _order = Enumerable.Range(0, _filtered.Count).ToArray();

        _pos = 0;
        _ssPos = 0;
    }

    private Quote Current => _filtered.Count > 0 ? _filtered[_order[_pos]] : null!;

    private List<Screenshot> CurrentScreenshots =>
        _screenshotsByQuote.GetValueOrDefault(Current?.Id ?? 0, []);

    private string CurrentScreenshotPath
    {
        get
        {
            var screenshots = CurrentScreenshots;
            if (_ssPos >= 0 && _ssPos < screenshots.Count)
                return screenshots[_ssPos].FilePath;
            return Current?.ScreenshotPath ?? "";
        }
    }

    private void ShowCurrent()
    {
        if (_filtered.Count == 0 || _pos >= _order.Length)
        {
            NormalLayout.Visibility = Visibility.Collapsed;
            FullscreenLayout.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        NormalLayout.Visibility = Visibility.Visible;
        if (!_isFullscreen) FullscreenLayout.Visibility = Visibility.Collapsed;

        var quote = Current;
        GameNameText.Text = string.IsNullOrWhiteSpace(quote.GameName) ? "未分类" : quote.GameName;
        QuoteText.Text = quote.Text;

        // Apply Chinese/English font based on text content
        var hasChinese = quote.Text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        var fontName = hasChinese ? _chineseFont : _englishFont;
        var font = new System.Windows.Media.FontFamily(fontName);
        GameNameText.FontFamily = font;
        QuoteText.FontFamily = font;
        FsGameNameText.FontFamily = font;
        FsQuoteText.FontFamily = font;
        FsNotesText.FontFamily = font;
        TagsText.FontFamily = font;

        TagsText.Text = _tagsByQuote.TryGetValue(quote.Id, out var tags) && tags.Count > 0
            ? string.Join("  ", tags.Select(t => $"#{t.Name}")) : "";

        // Show which screenshot within the current quote
        var ssList = CurrentScreenshots;
        if (ssList.Count > 1)
            ProgressText.Text = $"{_pos + 1} / {_filtered.Count}  |  截图 {_ssPos + 1}/{ssList.Count}";
        else
            ProgressText.Text = $"{_pos + 1} / {_filtered.Count}";

        LoopToggle.IsChecked = _loop; // keep the quick toggle in sync
        PrevOrCloseBtn.Content = _pos == 0 && !_loop ? "← 关闭" : "← 上一条";
        NextOrCloseBtn.Content = _pos == _filtered.Count - 1 && !_loop ? "关闭 →" : "下一条 →";

        FsGameNameText.Text = GameNameText.Text;
        FsQuoteText.Text = quote.Text;
        bool hasNotes = !string.IsNullOrWhiteSpace(quote.Notes);
        FsNotesText.Text = quote.Notes;

        FsGameNameText.Visibility = quote.SlideshowShowGameName ? Visibility.Visible : Visibility.Collapsed;
        FsQuoteText.Visibility = quote.SlideshowShowText ? Visibility.Visible : Visibility.Collapsed;
        FsNotesText.Visibility = (hasNotes && quote.SlideshowShowNotes) ? Visibility.Visible : Visibility.Collapsed;
        FsOverlay.Visibility = (quote.SlideshowShowGameName || quote.SlideshowShowText ||
            (hasNotes && quote.SlideshowShowNotes)) ? Visibility.Visible : Visibility.Collapsed;

        // Load screenshot (current index within this quote)
        BitmapImage? bitmap = null;
        var ssPath = CurrentScreenshotPath;
        if (!string.IsNullOrEmpty(ssPath) && File.Exists(ssPath))
        {
            try
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(ssPath);
                bitmap.EndInit();
            }
            catch { }
        }

        ScreenshotImage.Source = bitmap;
        ScreenshotBox.Visibility = bitmap != null ? Visibility.Visible : Visibility.Collapsed;
        FsScreenshotImage.Source = bitmap;
    }

    private void GoPrev()
    {
        var ssList = CurrentScreenshots;
        // If we have more screenshots for this quote, go back one screenshot first
        if (_ssPos > 0)
        {
            _ssPos--;
            ShowCurrent();
            return;
        }

        if (_pos > 0) { _pos--; _ssPos = CurrentScreenshots.Count - 1; ShowCurrent(); }
        else if (_loop && _filtered.Count > 0) { _pos = _filtered.Count - 1; _ssPos = CurrentScreenshots.Count - 1; ShowCurrent(); }
        else { Close(); }
    }

    private void GoNext()
    {
        var ssList = CurrentScreenshots;
        // If we have more screenshots for this quote, show the next one first
        if (_ssPos < ssList.Count - 1)
        {
            _ssPos++;
            ShowCurrent();
            return;
        }

        if (_pos < _filtered.Count - 1) { _pos++; _ssPos = 0; ShowCurrent(); }
        else if (_loop && _filtered.Count > 0) { _pos = 0; _ssPos = 0; ShowCurrent(); }
        else { Close(); }
    }

    private void GoRandom()
    {
        _pos = _random.Next(_filtered.Count);
        var ssList = CurrentScreenshots;
        _ssPos = ssList.Count > 0 ? _random.Next(ssList.Count) : 0;
        ShowCurrent();
    }

    private void OnPrevOrClose(object sender, RoutedEventArgs e) => GoPrev();
    private void OnNextOrClose(object sender, RoutedEventArgs e) => GoNext();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ApplyFilter();
        ShowCurrent();
    }

    private void ToggleSlideshowTopmost()
    {
        _isTopmost = !_isTopmost;
        Topmost = _isTopmost;
    }
    private void OnToggleTopmost(object sender, RoutedEventArgs e) => ToggleSlideshowTopmost();

    /// <summary>Quick loop toggle (keyboard R / top-bar button).</summary>
    private void ToggleLoop()
    {
        _loop = !_loop;
        LoopToggle.IsChecked = _loop;
    }
    private void OnToggleLoop(object sender, RoutedEventArgs e)
    {
        _loop = LoopToggle.IsChecked == true;
    }
    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            WindowState = WindowState.Maximized; ResizeMode = ResizeMode.NoResize; Topmost = true;
            NormalLayout.Visibility = Visibility.Collapsed; FullscreenLayout.Visibility = Visibility.Visible;
        }
        else
        {
            WindowState = WindowState.Normal; ResizeMode = ResizeMode.CanResize; Topmost = _isTopmost;
            NormalLayout.Visibility = Visibility.Visible; FullscreenLayout.Visibility = Visibility.Collapsed;
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
            case Key.Home:          _pos = 0; _ssPos = 0; ShowCurrent(); break;
            case Key.End:           _pos = _filtered.Count - 1; _ssPos = 0; ShowCurrent(); break;
            case Key.R:             ToggleLoop(); break;
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
