using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using GalgameQuoteCollector.Models;

namespace GalgameQuoteCollector.Views;

public partial class SlideshowWindow : Window
{
    private readonly List<Quote> _quotes;
    private readonly Dictionary<int, List<Tag>> _tagsByQuote;
    private readonly Random _random = new();
    private int _index;
    private bool _isFullscreen;
    private bool _isTopmost;

    public SlideshowWindow(Window owner, List<Quote> quotes, Dictionary<int, List<Tag>> tagsByQuote)
    {
        InitializeComponent();
        Owner = owner;
        _quotes = quotes;
        _tagsByQuote = tagsByQuote;
        _index = 0;

        if (quotes.Count > 0)
            ShowQuote(0);
    }

    private void ShowQuote(int index)
    {
        if (_quotes.Count == 0) return;
        _index = Math.Clamp(index, 0, _quotes.Count - 1);
        var quote = _quotes[_index];

        GameNameText.Text = string.IsNullOrWhiteSpace(quote.GameName) ? "未分类" : quote.GameName;
        QuoteText.Text = quote.Text;

        TagsText.Text = _tagsByQuote.TryGetValue(quote.Id, out var tags) && tags.Count > 0
            ? string.Join("  ", tags.Select(t => $"#{t.Name}")) : "";

        ProgressText.Text = $"{_index + 1} / {_quotes.Count}";

        PrevOrCloseBtn.Content = _index == 0 ? "← 关闭" : "← 上一条";
        NextOrCloseBtn.Content = _index == _quotes.Count - 1 ? "关闭 →" : "下一条 →";

        FsGameNameText.Text = GameNameText.Text;
        bool hasNotes = !string.IsNullOrWhiteSpace(quote.Notes);
        FsNotesText.Text = quote.Notes;
        FsOverlay.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;

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

    private void GoPrev() { if (_index > 0) ShowQuote(_index - 1); else Close(); }
    private void GoNext() { if (_index < _quotes.Count - 1) ShowQuote(_index + 1); else Close(); }
    private void GoRandom() => ShowQuote(_random.Next(_quotes.Count));

    private void OnPrevOrClose(object sender, RoutedEventArgs e) => GoPrev();
    private void OnNextOrClose(object sender, RoutedEventArgs e) => GoNext();

    private void ToggleSlideshowTopmost()
    {
        _isTopmost = !_isTopmost;
        Topmost = _isTopmost;
        TopmostBtn.Background = _isTopmost
            ? System.Windows.Media.Brushes.Green
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0F, 0x34, 0x60));
    }

    private void OnToggleTopmost(object sender, RoutedEventArgs e) => ToggleSlideshowTopmost();
    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            NormalLayout.Visibility = Visibility.Collapsed;
            FullscreenLayout.Visibility = Visibility.Visible;
            FullscreenBtn.Content = "窗口";
        }
        else
        {
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.CanResize;
            Topmost = _isTopmost;
            NormalLayout.Visibility = Visibility.Visible;
            FullscreenLayout.Visibility = Visibility.Collapsed;
            FullscreenBtn.Content = "全屏";
            if (Owner != null)
            {
                Left = Owner.Left + (Owner.Width - Width) / 2;
                Top = Owner.Top + (Owner.Height - Height) / 2;
            }
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
                if (_isFullscreen) ToggleFullscreen();
                else Close();
                break;
            case Key.Home:          ShowQuote(0); break;
            case Key.End:           ShowQuote(_quotes.Count - 1); break;
        }
        e.Handled = true;
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isFullscreen && !e.Handled)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                GoNext();
            else if (e.RightButton == MouseButtonState.Pressed)
                GoPrev();
        }
    }
}
