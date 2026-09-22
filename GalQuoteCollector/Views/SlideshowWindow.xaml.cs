using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using GalQuoteCollector.Models;
using GalQuoteCollector.Services;

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
    /// <summary>回想显示时的黑边处理：0 原样 / 1 裁掉 / 2 涂白（只影响显示，不改文件）。</summary>
    private int _barsMode;
    /// <summary>模式变化时回调（用于把选择写回设置）。</summary>
    private readonly Action<int>? _barsModeChanged;
    private int _textStyle;
    private double _textOpacity = 0.5;
    private HotkeyConfig? _cfg;
    private Action<HotkeyConfig>? _cfgSaver;

    private List<Quote> _filtered = new();          // currently filtered quotes
    private int[] _order = Array.Empty<int>();      // quote order indices into _filtered
    private int _pos;                       // current quote index in _order
    private int _ssPos;                     // current screenshot index for the current quote
    private bool _ready;
    private bool _isFullscreen;
    private bool _isTopmost;
    private string _chineseFont;
    private string _englishFont;
    private bool _magpieUpscale;
    private string _magpieHotkey;
    private readonly string _magpiePath;
    private bool _magpieActive;
    private bool _magpieBusy; // 正在处理超分开关（防重入：重复发键会把刚开起来的会话停掉）
    private bool _magpieClosing;
    private bool _closed;

    public SlideshowWindow(Window owner, List<Quote> quotes,
        Dictionary<int, List<Tag>> tagsByQuote,
        Dictionary<int, List<QuoteGroup>> groupsByQuote,
        Dictionary<int, List<Screenshot>> screenshotsByQuote,
        List<QuoteGroup> availableGroups,
        List<Tag> availableTags,
        int slideshowMode, bool slideshowLoop,
        string chineseFont = "Microsoft YaHei", string englishFont = "Segoe UI",
        bool magpieUpscale = false, string magpieHotkey = "", string magpiePath = "",
        bool diagnosticsAutoFullscreen = false, int barsMode = 0, Action<int>? barsModeChanged = null,
        HotkeyConfig? cfg = null, Action<HotkeyConfig>? cfgSaver = null)
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
        _barsMode = barsMode;
        if (cfg != null) { _textStyle = cfg.SlideshowTextStyle; _textOpacity = cfg.SlideshowTextOpacity; }
        _barsModeChanged = barsModeChanged;
        _cfg = cfg;
        _cfgSaver = cfgSaver;
        _chineseFont = chineseFont;
        _englishFont = englishFont;
        _magpieUpscale = magpieUpscale;
        _magpieHotkey = magpieHotkey;
        _magpiePath = magpiePath;

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

        if (_magpieUpscale)
        {
            Closing += OnSlideshowClosing;
            MagpieStatusText.Text = "按 F11 全屏后由 Magpie 超分";
        }

        // 诊断用：自动进全屏 → 触发超分 → 若干秒后退出全屏（解除超分）→ 关闭
        if (diagnosticsAutoFullscreen)
        {
            Loaded += async (_, _) =>
            {
                await Task.Delay(1200);
                AppLog.Write("slideshow diagnostics: entering fullscreen");
                if (!_isFullscreen) ToggleFullscreen();
                await Task.Delay(12000);
                AppLog.Write("slideshow diagnostics: leaving fullscreen");
                if (_isFullscreen) ToggleFullscreen();
                await Task.Delay(1200);
                AppLog.Write("slideshow diagnostics: closing");
                if (!_closed) Close();
            };
        }
    }

    // ── 全屏回想时用 Magpie 超分 ──

    /// <summary>
    /// 进入全屏（F11 / 右上角按钮）后才触发 Magpie 的「缩放窗口」热键：超分的目标就是
    /// 全屏的那个回想窗口。Magpie 的钩子按前台窗口决定缩放对象，所以要等本窗口真正激活后再发。
    ///
    /// 热键是开关语义：如果 Magpie 正在缩放别的窗口（比如玩家正在超分游戏），按下去只会
    /// **停掉**那个会话，不会切换过来。所以发完要用日志确认是否真的开始了缩放，没有就再按一次。
    /// </summary>
    private async Task EnableMagpieUpscaleAsync()
    {
        if (!_magpieUpscale || _closed || _magpieActive) return;
        // 防重入：⛶ 按钮 / F11 / 双击可能几乎同时触发两次，
        // 第二次发键会把第一次刚开起来的会话停掉——这正是"要点两次才成功"的元凶之一
        if (_magpieBusy)
        {
            AppLog.Write("slideshow magpie: enable already in progress, skipped duplicate");
            return;
        }
        _magpieBusy = true;
        try
        {
            await EnableMagpieUpscaleCoreAsync();
        }
        finally { _magpieBusy = false; }
    }

    private async Task EnableMagpieUpscaleCoreAsync()
    {
        if (!_magpieUpscale || _closed || _magpieActive) return;

        // 等全屏切换完成、窗口拿到前台
        await Task.Delay(500);
        if (_closed || !_isFullscreen || _magpieActive) return;

        if (!MagpieService.IsRunning())
        {
            MagpieStatusText.Text = "Magpie 未运行，正在尝试启动…";
            var started = await Task.Run(() => MagpieService.TryStart(_magpiePath, out var d) ? (ok: true, detail: d) : (ok: false, detail: d));
            if (!started.ok)
            {
                MagpieStatusText.Text = "Magpie 未运行，已用内置高质量放大";
                AppLog.Write($"slideshow magpie: {started.detail}");
                return;
            }
            await Task.Delay(700);
        }

        if (_closed || !_isFullscreen) return;

        var hotkey = MagpieService.ResolveScaleHotkey(_magpieHotkey, out var source);
        if (!MagpieService.IsValidHotkey(hotkey))
        {
            MagpieStatusText.Text = $"Magpie 热键无效：{hotkey}";
            return;
        }

        Activate();
        WindowState = WindowState.Maximized;
        await EnsureForegroundAsync();
        if (_closed || !_isFullscreen) return;

        // 简化策略（用户要求）：Magpie 在跑就只发热键，不做多余的放大与轮询。
        // 唯一需要的判断是"按之前是不是正在缩放别的窗口"——是的话，第一下只会把它停掉，
        // 必须再补一次才会缩放到我们的全屏窗口（Magpie 热键是开关语义）。
        bool othersWereScaling = MagpieService.IsScalingWindowVisible(out var scaleWinDetail);
        AppLog.Write($"slideshow magpie: scalingWindowBefore={othersWereScaling} {scaleWinDetail}");

        if (!await MagpieService.SendHotkeyAsync(hotkey))
        {
            MagpieStatusText.Text = "Magpie 超分热键发送失败";
            return;
        }

        if (othersWereScaling)
        {
            AppLog.Write("slideshow magpie: stopped the other window's scaling, sending again for this window");
            await Task.Delay(600);
            await EnsureForegroundAsync();
            await MagpieService.SendHotkeyAsync(hotkey);
        }

        _magpieActive = true;
        MagpieStatusText.Text = othersWereScaling
            ? $"Magpie 超分已切到全屏回想：{hotkey}（原窗口的超分已停止）"
            : $"Magpie 超分：{hotkey}（{source}）";
        AppLog.Write($"slideshow magpie: scaling fullscreen window with {hotkey} ({source}), othersWereScaling={othersWereScaling}");
    }

    /// <summary>轮询"Magpie 缩放窗口是否出现/消失"，最多 timeoutMs 毫秒（实时可靠，不依赖日志）。</summary>
    private static async Task<bool> WaitForScalingWindowAsync(bool expectVisible, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (MagpieService.IsScalingWindowVisible(out _) == expectVisible) return true;
            await Task.Delay(200);
        }
        return MagpieService.IsScalingWindowVisible(out _) == expectVisible;
    }

    /// <summary>确保本窗口真的在前台（Magpie 缩放的是当前前台窗口）。</summary>
    private async Task EnsureForegroundAsync()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        for (int i = 0; i < 3; i++)
        {
            if (CaptureService.GetForegroundWindowHandle() == hwnd) return;
            Activate();
            SetForegroundWindow(hwnd);
            await Task.Delay(220);
        }
        if (CaptureService.GetForegroundWindowHandle() != hwnd)
            AppLog.Write("slideshow magpie: 窗口可能不在前台，Magpie 可能缩放了别的窗口");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// 退出全屏时解除超分。Magpie 在源窗口状态改变/销毁时会**自己**结束缩放
    /// （实测：全屏 → 窗口化就会触发「源窗口状态改变 → 缩放结束」），所以先等一下看日志，
    /// 只有它没自己停才补发一次热键——盲目补发会把已经结束的会话又开起来。
    /// </summary>
    private async Task ReleaseMagpieUpscaleAsync()
    {
        if (!_magpieActive) return;
        _magpieActive = false;

        // 用户实测：退出全屏时**不要**再发热键——Magpie 会在源窗口状态改变时自己结束缩放，
        // 补发那一下反而会把缩放重新开到窗口化的回看上（表现为"退出全屏后又被超分一次"）。
        // 所以这里只等一下确认它自己结束了，日志留痕；确实没结束才补发（兜底，正常不会走到）。
        bool ended = await WaitForScalingWindowAsync(false, 4000);
        AppLog.Write(ended
            ? "slideshow magpie: 退出全屏，Magpie 已自行结束缩放（未补发热键）"
            : "slideshow magpie: 缩放窗口仍在，补发一次热键收尾");
        if (ended)
        {
            // Magpie 有时会在源窗口状态变化时**自己**再开一次缩放（实测退出全屏后约 0.1s 又开、1s 后自己结束，
            // 表现为"退出全屏后又被超分闪一下"）。这里等一下，若真又开了就按一次热键收掉。
            // 200ms 轮询、最多 2 秒：它一冒头就立刻收掉（不要再干等一大截，那样反而"超分一会才退出"）
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(200);
                if (!MagpieService.IsScalingWindowVisible(out _)) continue;
                var hk = MagpieService.ResolveScaleHotkey(_magpieHotkey, out _);
                await MagpieService.SendHotkeyAsync(hk);
                AppLog.Write($"slideshow magpie: 收掉 Magpie 自己又开的一次缩放（第 {i + 1} 次检查）");
                break;
            }
            return;
        }

        try
        {
            var hotkey = MagpieService.ResolveScaleHotkey(_magpieHotkey, out _);
            await MagpieService.SendHotkeyAsync(hotkey);
            AppLog.Write($"slideshow magpie: release {hotkey}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"slideshow magpie: release failed: {ex.Message}");
        }
    }

    /// <summary>关闭前解除超分（同样是先看日志，避免多按一次把会话重新开起来）。</summary>
    private async void OnSlideshowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closed = true;
        if (!_magpieActive || _magpieClosing) return;
        // 程序正在退出时不要拦下关闭
        if (Application.Current?.Dispatcher.HasShutdownStarted == true) return;

        e.Cancel = true;
        _magpieClosing = true;
        await ReleaseMagpieUpscaleAsync();
        Close();
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

        // 白底模式：显示时裁掉黑边 / 把黑边涂白（**不修改文件**）
        ApplyTextStyle();
        var shown = bitmap == null ? null : Controls.SlideshowImageBars.Apply(bitmap, _barsMode);
        ScreenshotImage.Source = shown;
        ScreenshotBox.Visibility = shown != null ? Visibility.Visible : Visibility.Collapsed;
        FsScreenshotImage.Source = shown;
        if (FsBackground != null)
            FsBackground.Background = _barsMode == Controls.SlideshowImageBars.ModeNone
                ? System.Windows.Media.Brushes.Black
                : System.Windows.Media.Brushes.White;
    }

    /// <summary>按 B 键 / 点顶栏按钮：原样 → 裁掉黑边 → 黑边涂白 → 原样（只影响显示，不动文件）。</summary>
    private void CycleBarsMode()
    {
        _barsMode = (_barsMode + 1) % 3;
        UpdateBarsModeUi();
        _barsModeChanged?.Invoke(_barsMode);
        ShowCurrent();
    }

    private void UpdateBarsModeUi()
    {
        if (BarsModeBtn == null) return;
        BarsModeBtn.Content = Controls.SlideshowImageBars.ModeName(_barsMode);
        BarsModeBtn.Foreground = _barsMode == Controls.SlideshowImageBars.ModeNone
            ? System.Windows.Media.Brushes.Gray
            : System.Windows.Media.Brushes.SeaGreen;
        ToolTipService.SetToolTip(BarsModeBtn, "黑边处理（只影响显示，不改文件）：" +
            Controls.SlideshowImageBars.ModeName(_barsMode) + "　按 B 切换");
    }

    private void OnBarsMode(object sender, RoutedEventArgs e) => CycleBarsMode();
    /// <summary>文字底：黑底白字 / 白底黑字 + 透明度（只影响显示）。</summary>
    private void ApplyTextStyle()
    {
        try
        {
            var alpha = (byte)Math.Clamp((int)Math.Round(_textOpacity * 255), 0, 255);
            var bg = _textStyle == 1
                ? System.Windows.Media.Color.FromArgb(alpha, 255, 255, 255)
                : System.Windows.Media.Color.FromArgb(alpha, 0, 0, 0);
            var fg = _textStyle == 1
                ? System.Windows.Media.Brushes.Black
                : System.Windows.Media.Brushes.White;
            var brush = new System.Windows.Media.SolidColorBrush(bg);
            if (FsOverlay != null)
            {
                FsOverlay.Background = brush;
                FsQuoteText.Foreground = fg;
                FsGameNameText.Foreground = _textStyle == 1
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC));
                FsNotesText.Foreground = FsGameNameText.Foreground;
            }
            // 窗口化时的文字框也一起换（原来固定浅灰底深色字）
            if (QuoteText != null) QuoteText.Foreground = fg;
        }
        catch { }
    }
    /// <summary>⚙ 按钮：调整回想设置（黑边 / 循环 / 字体 / Magpie 超分），应用后立刻生效并写回设置。</summary>
    private void OnSlideshowSettings(object sender, RoutedEventArgs e)
    {
        if (_cfg == null) return;
        _cfg.SlideshowBarsMode = _barsMode; // 把当前（可能刚按 B 切过的）状态带进对话框
        var dlg = new SlideshowSettingsDialog(this, _cfg);
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        _cfg = dlg.Result;
        _barsMode = Math.Clamp(_cfg.SlideshowBarsMode, 0, 2);
        _loop = _cfg.SlideshowLoop;
        _chineseFont = _cfg.SlideshowChineseFont;
        _englishFont = _cfg.SlideshowEnglishFont;
        _textStyle = _cfg.SlideshowTextStyle;
        _textOpacity = _cfg.SlideshowTextOpacity;
        _magpieUpscale = _cfg.MagpieUpscaleSlideshow;
        _magpieHotkey = _cfg.MagpieScaleHotkey ?? "";
        _cfgSaver?.Invoke(_cfg);
        UpdateBarsModeUi();
        ShowCurrent();
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
            // 超分的目标就是全屏的这个回想窗口
            _ = EnableMagpieUpscaleAsync();
        }
        else
        {
            WindowState = WindowState.Normal; ResizeMode = ResizeMode.CanResize; Topmost = _isTopmost;
            NormalLayout.Visibility = Visibility.Visible; FullscreenLayout.Visibility = Visibility.Collapsed;
            if (Owner != null) { Left = Owner.Left + (Owner.Width - Width) / 2; Top = Owner.Top + (Owner.Height - Height) / 2; }
            _ = ReleaseMagpieUpscaleAsync();
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
            case Key.B:             CycleBarsMode(); break;
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
