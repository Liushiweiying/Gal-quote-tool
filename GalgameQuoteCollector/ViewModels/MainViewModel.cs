using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalgameQuoteCollector.Models;
using GalgameQuoteCollector.Services;
using GalgameQuoteCollector.Views;

namespace GalgameQuoteCollector.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly HotkeyService _hotkeyService;
    private CaptureService _captureService;
    private readonly OcrService _ocrService;
    private readonly GameDetectService _gameDetectService;
    private StorageService _storageService;
    private readonly SettingsService _settingsService;
    private readonly ExportService _exportService = new();
    private readonly Window _window;

    private readonly string _dataDir;
    private string _screenshotDir;
    private bool _isCapturing;
    private UsageTracker? _usageTracker;
    private int _captureDelayMs = 200;
    private bool _hideUnrecognized;
    private string _screenshotFormat = "png";

    public MainViewModel(Window window)
    {
        _window = window;
        _screenshotDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "GalgameQuoteCollector");

        // Always use %LOCALAPPDATA% for data — consistent across versions
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GalgameQuoteCollector");
        Directory.CreateDirectory(_dataDir);

        _storageService = new StorageService(Path.Combine(_dataDir, "quotes.db"));
        _captureService = new CaptureService(_screenshotDir);
        _ocrService = new OcrService();
        _gameDetectService = new GameDetectService();
        _settingsService = new SettingsService(_dataDir);

        var hotkeyConfig = _settingsService.LoadHotkeyConfig();
        _captureDelayMs = hotkeyConfig.CaptureDelayMs;
        _hideUnrecognized = hotkeyConfig.HideUnrecognized;
        _screenshotFormat = hotkeyConfig.ScreenshotFormat;
        _gameDetectService.SetRules(hotkeyConfig.GameNameRules);

        // Custom screenshot directory
        if (!string.IsNullOrWhiteSpace(hotkeyConfig.ScreenshotDirectory))
        {
            _screenshotDir = hotkeyConfig.ScreenshotDirectory;
            Directory.CreateDirectory(_screenshotDir);
            _captureService = new CaptureService(_screenshotDir);
        }

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
    private ObservableCollection<Tag> _unassignedTags = new();

    [ObservableProperty]
    private string _newTagText = string.Empty;

    // ── Groups ──
    [ObservableProperty]
    private ObservableCollection<QuoteGroup> _currentGroups = new();

    [ObservableProperty]
    private ObservableCollection<QuoteGroup> _availableGroups = new();

    [ObservableProperty]
    private ObservableCollection<QuoteGroup> _unassignedGroups = new();

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
            var screenshotPath = _captureService.CaptureWindow(gameHwnd, _screenshotFormat);

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

    [RelayCommand]
    private void ToggleTag(Tag tag)
    {
        if (tag == null) return;

        // Ctrl+click → delete tag itself
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
        {
            var res = MessageBox.Show($"确定删除标签「{tag.Name}」？（不影响语录）", "删除标签",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;
            _storageService.DeleteTag(tag.Id);
            RefreshAvailableTags();
            if (SelectedQuote != null) RefreshCurrentTags();
            return;
        }

        if (SelectedQuote == null) return;
        var current = _storageService.GetTagsForQuote(SelectedQuote.Id);
        if (current.Any(t => t.Id == tag.Id))
            _storageService.RemoveTagFromQuote(SelectedQuote.Id, tag.Id);
        else
            _storageService.AddTagToQuote(SelectedQuote.Id, tag.Id);
        RefreshCurrentTags();
    }

    private void RefreshCurrentTags()
    {
        if (SelectedQuote == null)
        {
            CurrentTags.Clear(); UnassignedTags.Clear();
            return;
        }
        var tags = _storageService.GetTagsForQuote(SelectedQuote.Id);
        CurrentTags = new ObservableCollection<Tag>(tags);

        var all = _storageService.GetAllTags();
        var assignedIds = tags.Select(t => t.Id).ToHashSet();
        UnassignedTags = new ObservableCollection<Tag>(all.Where(t => !assignedIds.Contains(t.Id)));
    }

    private void RefreshAvailableTags()
    {
        var tags = _storageService.GetAllTags();
        AvailableTags = new ObservableCollection<Tag>(tags);
    }

    // ── Groups ──

    private void RefreshCurrentGroups()
    {
        if (SelectedQuote == null) { CurrentGroups.Clear(); UnassignedGroups.Clear(); return; }
        var groups = _storageService.GetGroupsForQuote(SelectedQuote.Id);
        CurrentGroups = new ObservableCollection<QuoteGroup>(groups);

        var all = _storageService.GetAllGroups();
        var assignedIds = groups.Select(g => g.Id).ToHashSet();
        UnassignedGroups = new ObservableCollection<QuoteGroup>(all.Where(g => !assignedIds.Contains(g.Id)));
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
        if (SelectedQuote != null) RefreshCurrentGroups();
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
        if (group == null || group.Id == 0) return;

        // Ctrl+click → delete group itself
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
        {
            var res = MessageBox.Show($"确定删除分组「{group.Name}」？（不影响语录）", "删除分组",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;
            _storageService.DeleteGroup(group.Id);
            if (SelectedGroupFilter == group.Id) SelectedGroupFilter = 0;
            RefreshAvailableGroups();
            if (SelectedQuote != null) RefreshCurrentGroups();
            RefreshQuotes();
            return;
        }

        if (SelectedQuote == null) return;
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

        var cfg = _settingsService.LoadHotkeyConfig();
        var slideshowMode = cfg.SlideshowMode;
        var slideshowLoop = cfg.SlideshowLoop;
        var allGroups = _storageService.GetAllGroups();
        var allTags = _storageService.GetAllTags();

        var win = new Views.SlideshowWindow(_window, quotes, tagsByQuote,
            groupsByQuote, allGroups, allTags, slideshowMode, slideshowLoop);
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

            // Just save new screenshot directory — don't migrate anything
            var newScreenshotDir = newConfig.ScreenshotDirectory;
            if (!string.IsNullOrWhiteSpace(newScreenshotDir) && newScreenshotDir != _screenshotDir)
            {
                _screenshotDir = newScreenshotDir;
                Directory.CreateDirectory(_screenshotDir);
                _captureService = new CaptureService(_screenshotDir);
                StatusText = $"截图目录已改为: {_screenshotDir}";
            }

            if (_hotkeyService.UpdateHotkey(newConfig.ToModifiers(), newConfig.VirtualKey))
            {
                _settingsService.SaveHotkeyConfig(newConfig);
                _gameDetectService.SetRules(newConfig.GameNameRules);
                ReapplyRulesToAllQuotes();
                _hideUnrecognized = newConfig.HideUnrecognized;
                _screenshotFormat = newConfig.ScreenshotFormat;
                ToggleUsageTracking(newConfig.EnableUsageTracking);
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
                ReapplyRulesToAllQuotes();
                _hideUnrecognized = newConfig.HideUnrecognized;
                _screenshotFormat = newConfig.ScreenshotFormat;
                ToggleUsageTracking(newConfig.EnableUsageTracking);
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

    private static string CheckAutoStart()
    {
        try
        {
            var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var vbsPath = Path.Combine(startupFolder, "GalgameQuoteCollector.vbs");
            if (File.Exists(vbsPath)) return "✓ 已开启";
            var lnkPath = Path.Combine(startupFolder, "GalgameQuoteCollector.lnk");
            if (File.Exists(lnkPath)) return "✓ 已开启";
            return "已关闭";
        }
        catch
        {
            return "未知";
        }
    }

    // ── Edit ──

    [RelayCommand]
    private void ToggleSlideshowFlag(string flag)
    {
        if (SelectedQuote == null) return;
        switch (flag)
        {
            case "game": SelectedQuote.SlideshowShowGameName = !SelectedQuote.SlideshowShowGameName; break;
            case "text": SelectedQuote.SlideshowShowText = !SelectedQuote.SlideshowShowText; break;
            case "notes": SelectedQuote.SlideshowShowNotes = !SelectedQuote.SlideshowShowNotes; break;
            default: return;
        }
        _storageService.UpdateQuote(SelectedQuote);
        RefreshQuotes();
        StatusText = "已更新回想显示设置";
    }

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
    /// <summary>Delete without confirmation. Keeps screenshots.</summary>
    public void DeleteQuoteDirect(Quote quote)
    {
        if (quote == null) return;
        _storageService.DeleteQuote(quote.Id);
        _allQuotes.Remove(quote);
        if (SelectedQuote == quote) SelectedQuote = null;
        RefreshQuotes();
        StatusText = "已删除";
    }

    private void DeleteScreenshots(Quote quote)
    {
        if (string.IsNullOrEmpty(quote.ScreenshotPath)) return;
        var fileName = Path.GetFileName(quote.ScreenshotPath);
        var paths = new[] { quote.ScreenshotPath, Path.Combine(_screenshotDir, fileName) };
        foreach (var p in paths)
            if (File.Exists(p)) try { File.Delete(p); } catch { }
    }

    [RelayCommand]
    private void DeleteQuote()
    {
        if (SelectedQuote == null) return;
        var result = MessageBox.Show("确定要删除这条语录吗？", "确认删除",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        // Ask about screenshots
        var hasScreenshots = !string.IsNullOrEmpty(SelectedQuote.ScreenshotPath);
        if (hasScreenshots)
        {
            var fileName = Path.GetFileName(SelectedQuote.ScreenshotPath);
            var paths = new[] { SelectedQuote.ScreenshotPath, Path.Combine(_screenshotDir, fileName) };
            hasScreenshots = paths.Any(p => File.Exists(p));
        }

        if (hasScreenshots)
        {
            var sr = MessageBox.Show("截图文件怎么处理？\n\n「是」= 删除截图文件\n「否」= 保留截图文件", "删除截图？",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (sr == MessageBoxResult.Yes)
                DeleteScreenshots(SelectedQuote);
        }

        DeleteQuoteDirect(SelectedQuote);
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
    private void BackupData()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "备份文件 (*.zip)|*.zip",
            DefaultExt = ".zip",
            FileName = $"galgame-backup_{DateTime.Now:yyyy-MM-dd}"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // Copy files to temp dir to avoid SQLite file lock
            var tempDir = Path.Combine(Path.GetTempPath(), $"galbackup_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "screenshots"));

            foreach (var f in Directory.GetFiles(_dataDir))
                File.Copy(f, Path.Combine(tempDir, Path.GetFileName(f)), true);

            if (Directory.Exists(_screenshotDir))
            {
                foreach (var f in Directory.GetFiles(_screenshotDir, "*.png"))
                    File.Copy(f, Path.Combine(tempDir, "screenshots", Path.GetFileName(f)), true);
            }

            System.IO.Compression.ZipFile.CreateFromDirectory(tempDir, dialog.FileName,
                System.IO.Compression.CompressionLevel.Optimal, true);

            try { Directory.Delete(tempDir, true); } catch { }

            StatusText = $"已备份到: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"备份失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void RestoreData()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "备份文件 (*.zip)|*.zip",
            Title = "选择备份文件恢复"
        };
        if (dialog.ShowDialog() != true) return;

        var result = MessageBox.Show("恢复将覆盖当前所有数据（语录、截图、设置），确定继续？",
            "确认恢复", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            // Extract to a temp directory first
            var tempDir = Path.Combine(Path.GetTempPath(), $"galrestore_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(dialog.FileName, tempDir);

            // Stop usage tracker to release file locks
            _usageTracker?.Stop();

            // Dispose old services
            _storageService.Dispose();

            // Copy files over
            foreach (var f in Directory.GetFiles(tempDir))
            {
                var dest = Path.Combine(_dataDir, Path.GetFileName(f));
                File.Copy(f, dest, true);
            }

            // Restore screenshots subdirectory
            var tempScreenshots = Path.Combine(tempDir, "screenshots");
            if (Directory.Exists(tempScreenshots))
            {
                foreach (var f in Directory.GetFiles(tempScreenshots))
                    File.Copy(f, Path.Combine(_screenshotDir, Path.GetFileName(f)), true);
            }

            // Clean up temp
            try { Directory.Delete(tempDir, true); } catch { }

            // Reinitialize
            _storageService = new StorageService(Path.Combine(_dataDir, "quotes.db"));
            _allQuotes = _storageService.GetAllQuotes();
            RefreshQuotes();
            RefreshAvailableTags();
            RefreshAvailableGroups();

            StatusText = "已从备份恢复";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"恢复失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void RematchScreenshots()
    {
        if (!Directory.Exists(_screenshotDir))
        {
            StatusText = "截图目录不存在";
            return;
        }

        var filesByTime = new Dictionary<string, string>();
        foreach (var f in Directory.GetFiles(_screenshotDir, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (name.Length >= 19)
                filesByTime[name[..19]] = f;
        }

        if (filesByTime.Count == 0)
        {
            StatusText = "截图目录中没有找到 PNG 文件";
            return;
        }

        int matched = 0;
        foreach (var q in _allQuotes)
        {
            if (!string.IsNullOrEmpty(q.ScreenshotPath) && File.Exists(q.ScreenshotPath))
                continue;
            var key = q.CapturedAt.ToString("yyyy-MM-dd_HHmmss");
            if (filesByTime.TryGetValue(key, out var filePath))
            {
                q.ScreenshotPath = filePath;
                _storageService.UpdateQuote(q);
                matched++;
            }
        }

        RefreshQuotes();
        StatusText = matched > 0
            ? $"已关联 {matched} 条语录与截图"
            : "没有需要关联的语录";
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
                // Merge: don't overwrite quotes captured while loading
                if (_allQuotes.Count > 0)
                {
                    var existingIds = _allQuotes.Select(q => q.Id).ToHashSet();
                    foreach (var q in quotes)
                        if (!existingIds.Contains(q.Id))
                            _allQuotes.Add(q);
                }
                else
                {
                    _allQuotes = quotes;
                }
                RefreshQuotes();
                OcrAvailable = _ocrService.IsAvailable;
                RefreshAvailableTags();
                RefreshAvailableGroups();
                RefreshAvailableTagsForFilter();
                RefreshAvailableGamesForFilter();

                var autoStart = CheckAutoStart();
                StatusText = updated > 0
                    ? $"已应用规则，更新了 {updated} 条语录的游戏名 | 自启: {autoStart}"
                    : $"热键: {_hotkeyService.CurrentHotkeyDisplay}   |   共 {_allQuotes.Count} 条语录 | 自启: {autoStart}";
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

    public void SaveUsageDataNow()
    {
        _usageTracker?.Save();
    }

    [RelayCommand]
    private void ShowStats()
    {
        var tags = _storageService.GetAllTags();
        var groups = _storageService.GetAllGroups();
        // Reload quotes from DB for accurate stats
        var allDbQuotes = _storageService.GetAllQuotes();
        var win = new Views.StatsWindow(_window, _storageService, allDbQuotes, tags, groups);
        win.ShowDialog();

        // If DB has more quotes than memory, resync
        if (allDbQuotes.Count != _allQuotes.Count)
        {
            _allQuotes = allDbQuotes;
            RefreshQuotes();
            StatusText = $"已刷新数据，共 {_allQuotes.Count} 条语录";
        }
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

        // Exclude unrecognized from search results only
        if (_hideUnrecognized && !string.IsNullOrWhiteSpace(SearchText))
        {
            source = source.Where(q => !q.Text.Contains("[未识别到文字]"));
        }

        // Sort
        source = SortMode switch
        {
            1 => source.OrderBy(q => string.IsNullOrEmpty(q.GameName) ? "~" : q.GameName)
                       .ThenByDescending(q => q.CapturedAt),
            2 => SortByRelevance(source, SearchText),
            _ => source.OrderByDescending(q => q.CapturedAt)
        };

        var result = source.ToList();
        Quotes = new ObservableCollection<Quote>(result);

        // Update status to show filtered vs total
        int filtered = result.Count;
        int total = _allQuotes.Count;
        if (filtered != total)
            StatusText = $"显示 {filtered} / 共 {total} 条语录";
    }

    private static IOrderedEnumerable<Quote> SortByRelevance(IEnumerable<Quote> source, string searchText)
    {
        return source
            .OrderByDescending(q =>
            {
                if (string.IsNullOrWhiteSpace(searchText)) return 0;
                var kw = searchText.Trim().ToLower();
                if (string.IsNullOrEmpty(kw)) return 0;
                int score = 0;
                if (q.Text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    score += 10;
                if (q.GameName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    score += 5;
                score += CountOccurrences(q.Text, kw);
                return score;
            })
            .ThenByDescending(q => q.CapturedAt);
    }

    private static int CountOccurrences(string text, string keyword)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword)) return 0;
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            idx += keyword.Length;
        }
        return count;
    }
}

