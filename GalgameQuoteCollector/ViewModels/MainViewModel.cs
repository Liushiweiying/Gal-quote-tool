using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalgameQuoteCollector.Models;
using GalgameQuoteCollector.Services;
using GalgameQuoteCollector.Views;

namespace GalgameQuoteCollector.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly HotkeyService _hotkeyService;
    private readonly CaptureService _captureService;
    private readonly OcrService _ocrService;
    private readonly GameDetectService _gameDetectService;
    private readonly StorageService _storageService;
    private readonly SettingsService _settingsService;
    private readonly ExportService _exportService = new();
    private readonly Window _window;

    private readonly string _dataDir;
    private readonly string _screenshotDir;
    private bool _isCapturing;
    private UsageTracker? _usageTracker;
    private int _captureDelayMs = 200;

    public MainViewModel(Window window)
    {
        _window = window;
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GalgameQuoteCollector");
        _screenshotDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "GalgameQuoteCollector");
        Directory.CreateDirectory(_dataDir);

        // Migrate screenshots from old location to Pictures on first run
        var oldScreenshotDir = Path.Combine(_dataDir, "screenshots");
        int migratedCount = 0;
        if (Directory.Exists(oldScreenshotDir) && !Directory.Exists(_screenshotDir))
        {
            try
            {
                Directory.CreateDirectory(_screenshotDir);
                foreach (var f in Directory.GetFiles(oldScreenshotDir, "*.png"))
                {
                    File.Copy(f, Path.Combine(_screenshotDir, Path.GetFileName(f)), false);
                    migratedCount++;
                }
            }
            catch { }
        }
        if (migratedCount > 0)
            StatusText = $"已迁移 {migratedCount} 张截图到 {_screenshotDir}";

        _storageService = new StorageService(Path.Combine(_dataDir, "quotes.db"));
        _captureService = new CaptureService(_screenshotDir);
        _ocrService = new OcrService();
        _gameDetectService = new GameDetectService();
        _settingsService = new SettingsService(_dataDir);

        var hotkeyConfig = _settingsService.LoadHotkeyConfig();
        _captureDelayMs = hotkeyConfig.CaptureDelayMs;
        _gameDetectService.SetRules(hotkeyConfig.GameNameRules);

        // Apply custom font
        if (!string.IsNullOrWhiteSpace(hotkeyConfig.FontFamily))
        {
            try { _window.FontFamily = new System.Windows.Media.FontFamily(hotkeyConfig.FontFamily); }
            catch { }
        }

        _hotkeyService = new HotkeyService(hotkeyConfig.ToModifiers(), hotkeyConfig.VirtualKey);
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;

        // Usage tracker
        if (hotkeyConfig.EnableUsageTracking)
        {
            _usageTracker = new UsageTracker(_dataDir, _gameDetectService);
            _usageTracker.Start();
        }

        StatusText = $"热键: {_hotkeyService.CurrentHotkeyDisplay}   |   正在加载...";
        _ = LoadQuotesAsync();
    }

    [ObservableProperty]
    private ObservableCollection<Quote> _quotes = new();

    [ObservableProperty]
    private Quote? _selectedQuote;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private bool _ocrAvailable = true;

    [ObservableProperty]
    private int _sortMode = 0; // 0=时间, 1=游戏名

    [ObservableProperty]
    private bool _isTopmost = false;

    [ObservableProperty]
    private string _editGameName = string.Empty;

    [ObservableProperty]
    private string _editWindowTitle = string.Empty;

    [ObservableProperty]
    private string _editNotes = string.Empty;

    [ObservableProperty]
    private string _editText = string.Empty;

    [ObservableProperty]
    private DateTime _editCapturedAt = DateTime.Now;

    // ── Tags ──
    [ObservableProperty]
    private ObservableCollection<Tag> _currentTags = new();

    [ObservableProperty]
    private ObservableCollection<Tag> _availableTags = new();

    [ObservableProperty]
    private string _newTagText = string.Empty;

    // ── Groups ──
    [ObservableProperty]
    private ObservableCollection<QuoteGroup> _currentGroups = new();

    [ObservableProperty]
    private ObservableCollection<QuoteGroup> _availableGroups = new();

    [ObservableProperty]
    private string _newGroupText = string.Empty;

    [ObservableProperty]
    private int _selectedGroupFilter = 0; // 0 = all

    [ObservableProperty]
    private ObservableCollection<Tag> _availableTagsForFilter = new();

    [ObservableProperty]
    private int _selectedTagFilter = 0; // 0 = all

    [ObservableProperty]
    private ObservableCollection<FilterItem> _availableGamesForFilter = new();

    [ObservableProperty]
    private int _selectedGameFilter = 0; // 0 = all

    private List<Quote> _allQuotes = new();

    partial void OnSelectedQuoteChanged(Quote? value)
    {
        if (value != null)
        {
            EditText = value.Text;
            EditGameName = value.GameName;
            EditWindowTitle = value.WindowTitle;
            EditNotes = value.Notes;
            EditCapturedAt = value.CapturedAt;
            RefreshCurrentTags();
            RefreshCurrentGroups();
        }
    }

    partial void OnSelectedGroupFilterChanged(int value)
    {
        RefreshQuotes();
    }

    partial void OnSelectedTagFilterChanged(int value)
    {
        RefreshQuotes();
    }

    partial void OnSelectedGameFilterChanged(int value)
    {
        RefreshQuotes();
    }

    partial void OnSearchTextChanged(string value)
    {
        RefreshQuotes();
    }

    partial void OnSortModeChanged(int value)
    {
        RefreshQuotes();
    }

    // ── Capture ──

    [RelayCommand]
    private async Task Capture()
    {
        if (_isCapturing) return;
        _isCapturing = true;

        try
        {
            StatusText = "采集...";
            // Save game window handle BEFORE minimizing
            var gameHwnd = CaptureService.GetForegroundWindowHandle();
            var windowTitle = CaptureService.GetWindowTitle(gameHwnd);

            _window.WindowState = WindowState.Minimized;
            await Task.Delay(_captureDelayMs);

            if (string.IsNullOrWhiteSpace(windowTitle))
            {
                StatusText = "未检测到活动窗口";
                return;
            }

            var gameName = _gameDetectService.DetectGameName(windowTitle);
            var screenshotPath = _captureService.CaptureWindow(gameHwnd);

            var text = await _ocrService.RecognizeTextAsync(screenshotPath);

            if (string.IsNullOrWhiteSpace(text))
                text = "[未识别到文字]";

            var quote = new Quote
            {
                Text = text,
                GameName = gameName,
                WindowTitle = windowTitle,
                ScreenshotPath = screenshotPath,
                CapturedAt = DateTime.Now
            };

            _storageService.InsertQuote(quote);
            _allQuotes.Insert(0, quote);
            RefreshQuotes();

            // Toast notification (top-right corner)
            var toastGame = string.IsNullOrWhiteSpace(quote.GameName) ? "未分类" : quote.GameName;
            var preview = quote.PreviewText.Length > 40 ? quote.PreviewText[..40] + "..." : quote.PreviewText;
            var toast = new Views.ToastWindow(toastGame, preview, 3000, () =>
            {
                _window.WindowState = WindowState.Normal;
                _window.Activate();
            });
            toast.Show();

            StatusText = $"已采集: {quote.PreviewText}";
        }
        catch (Exception ex)
        {
            StatusText = $"采集失败: {ex.Message}";
        }
        finally
        {
            _isCapturing = false;
        }
    }

    // ── Tags ──

    [RelayCommand]
    private void AddTag()
    {
        if (SelectedQuote == null || string.IsNullOrWhiteSpace(NewTagText))
            return;

        var name = NewTagText.Trim();
        var tag = _storageService.AddTag(name);
        _storageService.AddTagToQuote(SelectedQuote.Id, tag.Id);

        // Refresh
        RefreshAvailableTags();
        RefreshCurrentTags();
        NewTagText = string.Empty;
        StatusText = $"已添加标签: {name}";
    }

    [RelayCommand]
    private void RemoveTagFromQuote(Tag tag)
    {
        if (SelectedQuote == null) return;

        _storageService.RemoveTagFromQuote(SelectedQuote.Id, tag.Id);
        RefreshCurrentTags();
    }

    private void RefreshCurrentTags()
    {
        if (SelectedQuote == null)
        {
            CurrentTags.Clear();
            return;
        }
        var tags = _storageService.GetTagsForQuote(SelectedQuote.Id);
        CurrentTags = new ObservableCollection<Tag>(tags);
    }

    private void RefreshAvailableTags()
    {
        var tags = _storageService.GetAllTags();
        AvailableTags = new ObservableCollection<Tag>(tags);
    }

    // ── Groups ──

    private void RefreshCurrentGroups()
    {
        if (SelectedQuote == null) { CurrentGroups.Clear(); return; }
        var groups = _storageService.GetGroupsForQuote(SelectedQuote.Id);
        CurrentGroups = new ObservableCollection<QuoteGroup>(groups);
    }

    private void RefreshAvailableGroups()
    {
        var groups = _storageService.GetAllGroups();
        var list = new List<QuoteGroup> { new() { Id = 0, Name = "全部" } };
        list.AddRange(groups);
        AvailableGroups = new ObservableCollection<QuoteGroup>(list);
    }

    private void RefreshAvailableTagsForFilter()
    {
        var tags = _storageService.GetAllTags();
        var list = new List<Tag> { new() { Id = 0, Name = "全部" } };
        list.AddRange(tags);
        AvailableTagsForFilter = new ObservableCollection<Tag>(list);
    }

    private void RefreshAvailableGamesForFilter()
    {
        var games = _allQuotes.Select(q => q.GameName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        var list = new List<FilterItem> { new() { Id = 0, Name = "全部" } };
        int id = 1;
        foreach (var g in games)
            list.Add(new FilterItem { Id = id++, Name = g });
        AvailableGamesForFilter = new ObservableCollection<FilterItem>(list);
    }

    [RelayCommand]
    private void AddGroup()
    {
        if (string.IsNullOrWhiteSpace(NewGroupText)) return;
        _storageService.AddGroup(NewGroupText.Trim());
        NewGroupText = string.Empty;
        RefreshAvailableGroups();
        StatusText = $"已创建分组";
    }

    [RelayCommand]
    private void DeleteGroup(QuoteGroup group)
    {
        if (group.Id == 0) return; // skip "全部"
        var result = MessageBox.Show($"确定删除分组「{group.Name}」？（不影响其中的语录）", "确认删除",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        _storageService.DeleteGroup(group.Id);
        if (SelectedGroupFilter == group.Id) SelectedGroupFilter = 0;
        RefreshAvailableGroups();
        RefreshCurrentGroups();
        RefreshQuotes();
    }

    [RelayCommand]
    private void ToggleQuoteGroup(QuoteGroup group)
    {
        if (SelectedQuote == null || group.Id == 0) return;

        var currentGroups = _storageService.GetGroupsForQuote(SelectedQuote.Id);
        var isInGroup = currentGroups.Any(g => g.Id == group.Id);

        if (isInGroup)
            _storageService.RemoveQuoteFromGroup(SelectedQuote.Id, group.Id);
        else
            _storageService.AddQuoteToGroup(SelectedQuote.Id, group.Id);

        RefreshCurrentGroups();
    }

    [RelayCommand]
    private void ExportGroup()
    {
        if (SelectedGroupFilter <= 0)
        {
            MessageBox.Show("请先在工具栏选择一个分组", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var groupQuoteIds = _storageService.GetQuoteIdsInGroup(SelectedGroupFilter).ToHashSet();
        var filtered = _allQuotes.Where(q => groupQuoteIds.Contains(q.Id)).ToList();

        if (filtered.Count == 0)
        {
            MessageBox.Show("该分组下没有语录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var groupName = _storageService.GetAllGroups().First(g => g.Id == SelectedGroupFilter).Name;
        DoExport(filtered, $"{groupName}-语录");
    }

    // ── Slideshow ──

    [RelayCommand]
    private void OpenSlideshow()
    {
        var quotes = Quotes.ToList();
        if (quotes.Count == 0)
        {
            MessageBox.Show("没有语录可回想", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var tagsByQuote = new Dictionary<int, List<Tag>>();
        var groupsByQuote = new Dictionary<int, List<QuoteGroup>>();
        foreach (var q in quotes)
        {
            tagsByQuote[q.Id] = _storageService.GetTagsForQuote(q.Id);
            groupsByQuote[q.Id] = _storageService.GetGroupsForQuote(q.Id);
        }

        var slideshowMode = _settingsService.LoadHotkeyConfig().SlideshowMode;
        var allGroups = _storageService.GetAllGroups();
        var allTags = _storageService.GetAllTags();

        var win = new Views.SlideshowWindow(_window, quotes, tagsByQuote,
            groupsByQuote, allGroups, allTags, slideshowMode);
        win.ShowDialog();
    }

    // ── Export ──

    [RelayCommand]
    private void Export()
    {
        if (_allQuotes.Count == 0)
        {
            MessageBox.Show("没有可导出的语录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DoExport(_allQuotes);
    }

    [RelayCommand]
    private void ExportSelected()
    {
        if (SelectedQuote == null)
        {
            MessageBox.Show("请先选择一条语录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DoExport([SelectedQuote]);
    }

    private void DoExport(List<Quote> quotes, string? suggestedName = null)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown 文件 (*.md)|*.md|JSON 文件 (*.json)|*.json",
            DefaultExt = ".md",
            FileName = suggestedName ?? $"galgame-quotes_{DateTime.Now:yyyy-MM-dd}"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            // Build tags + groups lookup
            var tagsByQuote = new Dictionary<int, List<Tag>>();
            var groupsByQuote = new Dictionary<int, List<QuoteGroup>>();
            foreach (var q in quotes)
            {
                tagsByQuote[q.Id] = _storageService.GetTagsForQuote(q.Id);
                groupsByQuote[q.Id] = _storageService.GetGroupsForQuote(q.Id);
            }

            if (dialog.FilterIndex == 1)
            {
                // Markdown
                var grouped = quotes
                    .GroupBy(q => q.GameName)
                    .ToDictionary(g => g.Key, g =>
                    {
                        return g.Select(q => (q,
                            tagsByQuote.GetValueOrDefault(q.Id, []),
                            groupsByQuote.GetValueOrDefault(q.Id, []))).ToList();
                    });

                var md = _exportService.ToMarkdown(grouped);
                File.WriteAllText(dialog.FileName, md);
            }
            else
            {
                // JSON
                var json = _exportService.ToJson(quotes, tagsByQuote, groupsByQuote);
                File.WriteAllText(dialog.FileName, json);
            }

            StatusText = $"已导出 {quotes.Count} 条语录到 {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Import ──

    [RelayCommand]
    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "支持的文件 (*.md;*.json)|*.md;*.json|Markdown (*.md)|*.md|JSON (*.json)|*.json",
            Title = "导入语录"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var content = File.ReadAllText(dialog.FileName);
            var items = dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? _exportService.ParseJson(content)
                : _exportService.ParseMarkdown(content);

            if (items.Count == 0)
            {
                MessageBox.Show("未找到可导入的语录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int imported = 0;
            foreach (var item in items)
            {
                var quote = new Quote
                {
                    Text = item.Text,
                    GameName = item.GameName,
                    CapturedAt = item.CapturedAt,
                    Notes = item.Notes
                };

                _storageService.InsertQuote(quote);

                foreach (var tagName in item.Tags)
                {
                    var tag = _storageService.AddTag(tagName);
                    _storageService.AddTagToQuote(quote.Id, tag.Id);
                }

                foreach (var groupName in item.Groups)
                {
                    var group = _storageService.AddGroup(groupName);
                    _storageService.AddQuoteToGroup(quote.Id, group.Id);
                }

                _allQuotes.Insert(0, quote);
                imported++;
            }

            RefreshQuotes();
            RefreshAvailableTags();
            RefreshAvailableGroups();
            StatusText = $"已导入 {imported} 条语录";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Settings ──

    [RelayCommand]
    private void OpenSettings()
    {
        var currentConfig = _settingsService.LoadHotkeyConfig();
        var dialog = new SettingsWindow(_window, currentConfig, _hotkeyService.CurrentHotkeyDisplay);

        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            var newConfig = dialog.Result;
            _captureDelayMs = newConfig.CaptureDelayMs;

            if (_hotkeyService.UpdateHotkey(newConfig.ToModifiers(), newConfig.VirtualKey))
            {
                _settingsService.SaveHotkeyConfig(newConfig);
                _gameDetectService.SetRules(newConfig.GameNameRules);
                if (!string.IsNullOrWhiteSpace(newConfig.FontFamily))
                {
                    try { _window.FontFamily = new System.Windows.Media.FontFamily(newConfig.FontFamily); }
                    catch { }
                }
                var (autoOk, autoMsg) = TrySetAutoStart(newConfig.AutoStart);
                StatusText = $"热键已更改为: {_hotkeyService.CurrentHotkeyDisplay}";
                if (!autoOk) StatusText += $" | 自启失败: {autoMsg}";
            }
            else
            {
                _settingsService.SaveHotkeyConfig(newConfig);
                _gameDetectService.SetRules(newConfig.GameNameRules);
                if (!string.IsNullOrWhiteSpace(newConfig.FontFamily))
                {
                    try { _window.FontFamily = new System.Windows.Media.FontFamily(newConfig.FontFamily); }
                    catch { }
                }
                var (autoOk, autoMsg) = TrySetAutoStart(newConfig.AutoStart);
                StatusText = autoOk ? $"自启: {autoMsg}" : $"自启失败: {autoMsg}";
                MessageBox.Show("热键注册失败，请选择其他组合键", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void ReapplyRulesToAllQuotes()
    {
        int updated = 0;
        foreach (var q in _allQuotes)
        {
            var src = string.IsNullOrWhiteSpace(q.WindowTitle) ? q.GameName : q.WindowTitle;
            var det = _gameDetectService.DetectGameName(src);
            if (!string.IsNullOrWhiteSpace(det) && det != q.GameName)
            { q.GameName = det; _storageService.UpdateQuote(q); updated++; }
        }
        if (updated > 0) { RefreshQuotes(); StatusText = $"已应用规则，更新了 {updated} 条"; }
    }

    private void ToggleUsageTracking(bool enable)
    {
        if (enable && _usageTracker == null)
        { _usageTracker = new UsageTracker(_dataDir, _gameDetectService); _usageTracker.Start(); }
        else if (!enable && _usageTracker != null)
        { _usageTracker.Stop(); _usageTracker.Dispose(); _usageTracker = null; }
    }

    private static (bool ok, string detail) TrySetAutoStart(bool enable)
    {
        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

        try
        {
            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    return (false, "无法获取程序路径");

                // Write a VBScript to startup folder — no admin rights needed, no console flash
                var vbsPath = Path.Combine(startupFolder, "GalgameQuoteCollector.vbs");
                var vbsContent = $"CreateObject(\"WScript.Shell\").Run \"\"\"{exePath}\"\" --minimized\", 0, False";
                File.WriteAllText(vbsPath, vbsContent);

                if (!File.Exists(vbsPath))
                    return (false, "无法创建启动脚本");

                return (true, $"已创建启动脚本: {vbsPath}");
            }
            else
            {
                // Remove VBS
                var vbsPath = Path.Combine(startupFolder, "GalgameQuoteCollector.vbs");
                if (File.Exists(vbsPath))
                    File.Delete(vbsPath);

                // Also clean up old shortcut if exists
                var lnkPath = Path.Combine(startupFolder, "GalgameQuoteCollector.lnk");
                if (File.Exists(lnkPath))
                    File.Delete(lnkPath);

                return (true, "已移除开机自启");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Edit ──

    [RelayCommand]
    private void SaveEdit()
    {
        if (SelectedQuote == null) return;

        SelectedQuote.Text = EditText;
        SelectedQuote.GameName = EditGameName;
        SelectedQuote.WindowTitle = EditWindowTitle;
        SelectedQuote.Notes = EditNotes;
        SelectedQuote.CapturedAt = EditCapturedAt;
        _storageService.UpdateQuote(SelectedQuote);

        RefreshQuotes();
        StatusText = "已保存";
    }

    [RelayCommand]
    private void DeleteQuote()
    {
        if (SelectedQuote == null) return;

        var result = MessageBox.Show("确定要删除这条语录吗？", "确认删除",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        var quoteToDelete = SelectedQuote;
        _storageService.DeleteQuote(quoteToDelete.Id);

        // Delete screenshot — check both old (db path) and new (Pictures) locations
        var pathsToTry = new[] { quoteToDelete.ScreenshotPath };
        if (!string.IsNullOrEmpty(quoteToDelete.ScreenshotPath))
        {
            var fileName = Path.GetFileName(quoteToDelete.ScreenshotPath);
            var newPath = Path.Combine(_screenshotDir, fileName);
            if (newPath != quoteToDelete.ScreenshotPath)
                pathsToTry = [quoteToDelete.ScreenshotPath, newPath];
        }
        foreach (var p in pathsToTry)
        {
            if (File.Exists(p))
            {
                try { File.Delete(p); }
                catch { }
            }
        }

        _allQuotes.Remove(quoteToDelete);
        SelectedQuote = null;
        RefreshQuotes();

        StatusText = "已删除";
    }

    [RelayCommand]
    private void OpenScreenshot()
    {
        if (SelectedQuote == null || string.IsNullOrEmpty(SelectedQuote.ScreenshotPath))
            return;

        if (File.Exists(SelectedQuote.ScreenshotPath))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = SelectedQuote.ScreenshotPath,
                UseShellExecute = true
            });
        }
    }

    [RelayCommand]
    private void CleanupScreenshots()
    {
        var screenshotDir = _screenshotDir;
        if (!Directory.Exists(screenshotDir))
        {
            StatusText = "没有截图目录需要清理";
            return;
        }

        var allQuotes = _storageService.GetAllQuotes();
        var referencedPaths = allQuotes
            .Select(q => q.ScreenshotPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetFullPath(p))
            .ToHashSet();

        var files = Directory.GetFiles(screenshotDir, "*.png");
        var orphaned = files.Where(f => !referencedPaths.Contains(Path.GetFullPath(f))).ToList();

        if (orphaned.Count == 0)
        {
            StatusText = "没有未引用的截图";
            return;
        }

        var result = MessageBox.Show($"发现 {orphaned.Count} 张无引用的截图，确定删除？\n\n（首张: {Path.GetFileName(orphaned[0])}）",
            "清理截图", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        int deleted = 0;
        foreach (var f in orphaned)
        {
            try { File.Delete(f); deleted++; }
            catch { }
        }

        StatusText = $"已清理 {deleted} 张无引用截图";
    }

    [RelayCommand]
    private void MigrateScreenshots()
    {
        var oldDir = Path.Combine(_dataDir, "screenshots");
        if (!Directory.Exists(oldDir))
        {
            StatusText = "没有旧截图需要迁移";
            return;
        }

        try
        {
            Directory.CreateDirectory(_screenshotDir);
            int count = 0;
            foreach (var f in Directory.GetFiles(oldDir, "*.png"))
            {
                var dest = Path.Combine(_screenshotDir, Path.GetFileName(f));
                if (!File.Exists(dest))
                {
                    File.Copy(f, dest, false);
                    count++;
                }
            }
            StatusText = $"已迁移 {count} 张截图到 {_screenshotDir}";
        }
        catch (Exception ex)
        {
            StatusText = $"迁移失败: {ex.Message}";
        }
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        await Capture();
    }

    private async Task LoadQuotesAsync()
    {
        await Task.Run(() =>
        {
            var quotes = _storageService.GetAllQuotes();
            int updated = 0;
            foreach (var q in quotes)
            {
                var source = !string.IsNullOrWhiteSpace(q.WindowTitle) ? q.WindowTitle : q.GameName;
                var detected = _gameDetectService.DetectGameName(source);
                if (!string.IsNullOrWhiteSpace(detected) && detected != q.GameName)
                {
                    q.GameName = detected;
                    _storageService.UpdateQuote(q);
                    updated++;
                }
            }

            // Switch back to UI thread to update bindings
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _allQuotes = quotes;
                RefreshQuotes();
                OcrAvailable = _ocrService.IsAvailable;
                RefreshAvailableTags();
                RefreshAvailableGroups();
                RefreshAvailableTagsForFilter();
                RefreshAvailableGamesForFilter();

                StatusText = updated > 0
                    ? $"已应用规则，更新了 {updated} 条语录的游戏名"
                    : $"热键: {_hotkeyService.CurrentHotkeyDisplay}   |   共 {_allQuotes.Count} 条语录";
            });
        });
    }

    partial void OnIsTopmostChanged(bool value)
    {
        if (_window != null)
            _window.Topmost = value;
    }

    [RelayCommand]
    private void ToggleTopmost()
    {
        IsTopmost = !IsTopmost;
    }

    [RelayCommand]
    private void OpenUsageStats()
    {
        if (_usageTracker == null)
        {
            MessageBox.Show("使用时长记录已关闭\n请在 设置 → 常规 中开启", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var data = _usageTracker.GetData();
        var win = new Views.UsageStatsWindow(_window, data);
        win.ShowDialog();
        _usageTracker.Save();
    }

    [RelayCommand]
    private void ShowStats()
    {
        var tags = _storageService.GetAllTags();
        var groups = _storageService.GetAllGroups();
        var win = new Views.StatsWindow(_window, _storageService, _allQuotes, tags, groups);
        win.ShowDialog();
    }

    private void RefreshQuotes()
    {
        IEnumerable<Quote> source = _allQuotes;

        // Filter by group
        if (SelectedGroupFilter > 0)
        {
            var groupIds = _storageService.GetQuoteIdsInGroup(SelectedGroupFilter).ToHashSet();
            source = source.Where(q => groupIds.Contains(q.Id));
        }

        // Filter by tag
        if (SelectedTagFilter > 0)
        {
            source = source.Where(q =>
                _storageService.GetTagsForQuote(q.Id).Any(t => t.Id == SelectedTagFilter));
        }

        // Filter by game
        if (SelectedGameFilter > 0)
        {
            var gameName = AvailableGamesForFilter
                .FirstOrDefault(f => f.Id == SelectedGameFilter)?.Name;
            if (gameName != null)
                source = source.Where(q => q.GameName == gameName);
        }

        // Filter by text
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            source = source.Where(q =>
                q.Text.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || q.GameName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        // Sort
        source = SortMode switch
        {
            1 => source.OrderBy(q => string.IsNullOrEmpty(q.GameName) ? "~" : q.GameName)
                       .ThenByDescending(q => q.CapturedAt),
            _ => source.OrderByDescending(q => q.CapturedAt)
        };

        Quotes = new ObservableCollection<Quote>(source.ToList());
    }
}

