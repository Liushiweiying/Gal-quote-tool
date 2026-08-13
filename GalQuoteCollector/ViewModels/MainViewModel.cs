using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualBasic.FileIO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalQuoteCollector.Models;
using GalQuoteCollector.Services;
using GalQuoteCollector.Views;

namespace GalQuoteCollector.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // Single source of truth for the version: read from the assembly (csproj <Version>).
    public static string AppVersion =>
        "v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
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
            "GalQuoteCollector");

        // Always use %LOCALAPPDATA% for data. From now on everything is READ and
        // WRITTEN under "GalQuoteCollector"; legacy "GalgameQuoteCollector" data is
        // only READ once (copied below), never written to.
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GalQuoteCollector");

        // Backward compatibility (Galgame -> Gal rename): if the legacy data dir holds
        // a real database and the new dir's DB is still empty, copy the legacy data
        // over so nothing is lost, then use the new dir exclusively. The old dir stays
        // untouched as a backup.
        var oldDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GalgameQuoteCollector");
        var oldDbPath = Path.Combine(oldDataDir, "quotes.db");
        if (File.Exists(oldDbPath) && !DbHasQuotes(Path.Combine(_dataDir, "quotes.db")))
            MigrateDataDir(oldDataDir, _dataDir);

        // Same rename for the default screenshot dir; only applied later when the user
        // has NOT configured a custom screenshot directory.
        var oldScreenshotDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "GalgameQuoteCollector");
        var migrateDefaultScreenshotDir =
            Directory.Exists(oldScreenshotDir)
            && (!Directory.Exists(_screenshotDir) || !Directory.EnumerateFileSystemEntries(_screenshotDir).Any());

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
        // Legacy default dir rename: move screenshots that still live under the old
        // "GalgameQuoteCollector" folder into the new default, updating the DB paths.
        else if (migrateDefaultScreenshotDir)
        {
            MigrateScreenshots(_screenshotDir);
        }

        // Apply custom font
        if (!string.IsNullOrWhiteSpace(hotkeyConfig.FontFamily))
        {
            try { _window.FontFamily = new System.Windows.Media.FontFamily(hotkeyConfig.FontFamily); }
            catch { }
        }

        _hotkeyService = new HotkeyService(
            hotkeyConfig.ToModifiers(), hotkeyConfig.VirtualKey,
            hotkeyConfig.ToAddModifiers(), hotkeyConfig.AddShotVirtualKey);
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.HotkeyPressedAdd += OnHotkeyPressedAdd;

        // Usage tracker
        if (hotkeyConfig.EnableUsageTracking)
        {
            _usageTracker = new UsageTracker(_dataDir, _gameDetectService);
            _usageTracker.Start();
        }

        StatusText = $"热键: {_hotkeyService.CurrentHotkeyDisplay}   |   正在加载...";
        _ = LoadQuotesAsync();

        RunTranslucentTbFixIfNeeded();
    }

    /// <summary>
    /// When the option is enabled and we were launched by the auto-start path (--minimized),
    /// wait for TranslucentTB to be running, then relaunch its exe once. A second launch makes
    /// TranslucentTB show its "已在运行" dialog and re-apply the taskbar transparency, repairing
    /// the case where it sometimes fails to take effect at boot.
    /// </summary>
    private async void RunTranslucentTbFixIfNeeded()
    {
        if (!Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            return; // only at boot via auto-start
        if (!_settingsService.LoadHotkeyConfig().EnableTranslucentTbFix)
            return;

        System.Diagnostics.Process? proc = null;
        for (int i = 0; i < 60 && proc == null; i++)
        {
            var list = System.Diagnostics.Process.GetProcessesByName("TranslucentTB");
            if (list.Length > 0) proc = list[0];
            else await Task.Delay(1000);
        }
        if (proc == null) return;

        try
        {
            var exePath = proc.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath));
        }
        catch
        {
            // Non-fatal: if TranslucentTB is gone or inaccessible, nothing to repair
        }
    }

    [ObservableProperty]
    private ObservableCollection<Quote> _quotes = new();

    [ObservableProperty]
    private Quote? _selectedQuote;

    [ObservableProperty]
    private ObservableCollection<Screenshot> _currentScreenshots = new();

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
    private DateTime _editCapturedAtDate = DateTime.Now;

    [ObservableProperty]
    private string _editCapturedAtTimeString = "00:00:00";

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
    private ObservableCollection<int> _selectedGroupFilters = new();

    [ObservableProperty]
    private bool _groupFilterExclude;

    [ObservableProperty]
    private ObservableCollection<Tag> _availableTagsForFilter = new();

    [ObservableProperty]
    private ObservableCollection<int> _selectedTagFilters = new();

    [ObservableProperty]
    private bool _tagFilterExclude;

    [ObservableProperty]
    private ObservableCollection<FilterItem> _availableGamesForFilter = new();

    [ObservableProperty]
    private ObservableCollection<int> _selectedGameFilters = new();

    [ObservableProperty]
    private bool _gameFilterExclude;

    [ObservableProperty]
    private bool _isGridView;

    private List<Quote> _allQuotes = new();

    partial void OnSelectedQuoteChanged(Quote? value)
    {
        if (value != null)
        {
            EditText = value.Text;
            EditGameName = value.GameName;
            EditWindowTitle = value.WindowTitle;
            EditNotes = value.Notes;
            EditCapturedAtDate = value.CapturedAt.Date;
            EditCapturedAtTimeString = value.CapturedAt.ToString("HH:mm:ss");
            RefreshCurrentTags();
            RefreshCurrentGroups();
            RefreshCurrentScreenshots();
        }
    }

    partial void OnGroupFilterExcludeChanged(bool value) { RefreshQuotes(); }
    partial void OnTagFilterExcludeChanged(bool value) { RefreshQuotes(); }
    partial void OnGameFilterExcludeChanged(bool value) { RefreshQuotes(); }

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
        bool restoreWindow = true; // restored on failure; success stays minimized (toast restores)

        try
        {
            StatusText = "采集...";
            // Save game window handle BEFORE minimizing
            var gameHwnd = CaptureService.GetForegroundWindowHandle();
            var windowTitle = CaptureService.GetWindowTitle(gameHwnd);

            _window.WindowState = WindowState.Minimized;
            // Minimum 100ms: anything shorter risks capturing our own window
            await Task.Delay(Math.Max(100, _captureDelayMs));

            if (string.IsNullOrWhiteSpace(windowTitle))
            {
                StatusText = "未检测到活动窗口";
                return;
            }

            var gameName = _gameDetectService.DetectGameName(windowTitle);
            var screenshotPath = _captureService.CaptureWindow(gameHwnd, _screenshotFormat,
                forceFullscreen: !string.IsNullOrWhiteSpace(gameName));

            var ocrConfig = _settingsService.LoadHotkeyConfig();
            var text = ocrConfig.OcrEngine switch
            {
                "local" => await _ocrService.RecognizeLocalTextAsync(
                    screenshotPath, ocrConfig.LocalOcrUrl, ocrConfig.LocalOcrModel),
                "rapid" => await _ocrService.RecognizeRapidTextAsync(
                    screenshotPath, ocrConfig.RapidOcrPython),
                _ => await _ocrService.RecognizeTextAsync(screenshotPath)
            };

            if (string.IsNullOrWhiteSpace(text))
                text = "[未识别到文字]";

            var quote = new Quote
            {
                Text = text,
                GameName = gameName,
                WindowTitle = windowTitle,
                ScreenshotPath = screenshotPath,
                CapturedAt = DateTime.Now,
                // Smart defaults for slideshow display
                SlideshowShowText = text != "[未识别到文字]",
                SlideshowShowNotes = false,  // new quotes have no notes yet
                SlideshowShowGameName = text != "[未识别到文字]"
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

            restoreWindow = false;
            StatusText = $"已采集: {quote.PreviewText}";
        }
        catch (Exception ex)
        {
            StatusText = $"采集失败: {ex.Message}";
        }
        finally
        {
            if (restoreWindow) _window.WindowState = WindowState.Normal;
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
        var list = new List<QuoteGroup> { new() { Id = -1, Name = "未分组" } };
        list.AddRange(groups);
        AvailableGroups = new ObservableCollection<QuoteGroup>(list);
    }

    private void RefreshAvailableTagsForFilter()
    {
        var tags = _storageService.GetAllTags();
        var list = new List<Tag> { new() { Id = -1, Name = "无标签" } };
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
        var list = new List<FilterItem> { new() { Id = -1, Name = "未分类" } };
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
        SelectedGroupFilters.Remove(group.Id);
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
            SelectedGroupFilters.Remove(group.Id);
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
        if (SelectedGroupFilters.Count == 0)
        {
            MessageBox.Show("请先在···菜单的分组筛选中选择至少一个分组", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var groupQuoteIds = new HashSet<int>();
        foreach (var gid in SelectedGroupFilters.Where(id => id > 0))
            foreach (var qid in _storageService.GetQuoteIdsInGroup(gid))
                groupQuoteIds.Add(qid);

        // "未分组" (Id == -1) selected → include quotes not in any group
        if (SelectedGroupFilters.Contains(-1))
            foreach (var q in _allQuotes.Where(q => _storageService.GetGroupsForQuote(q.Id).Count == 0))
                groupQuoteIds.Add(q.Id);

        var filtered = GroupFilterExclude
            ? _allQuotes.Where(q => !groupQuoteIds.Contains(q.Id)).ToList()
            : _allQuotes.Where(q => groupQuoteIds.Contains(q.Id)).ToList();

        if (filtered.Count == 0)
        {
            MessageBox.Show("该分组下没有语录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DoExport(filtered, "分组语录");
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
        var screenshotsByQuote = new Dictionary<int, List<Screenshot>>();
        foreach (var q in quotes)
        {
            tagsByQuote[q.Id] = _storageService.GetTagsForQuote(q.Id);
            groupsByQuote[q.Id] = _storageService.GetGroupsForQuote(q.Id);
            screenshotsByQuote[q.Id] = _storageService.GetScreenshots(q.Id);
        }

        var cfg = _settingsService.LoadHotkeyConfig();
        var slideshowMode = cfg.SlideshowMode;
        var slideshowLoop = cfg.SlideshowLoop;
        var allGroups = _storageService.GetAllGroups();
        var allTags = _storageService.GetAllTags();

        var win = new Views.SlideshowWindow(_window, quotes, tagsByQuote,
            groupsByQuote, screenshotsByQuote, allGroups, allTags, slideshowMode, slideshowLoop,
            cfg.SlideshowChineseFont, cfg.SlideshowEnglishFont);
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
            FileName = suggestedName ?? $"gal-quotes_{DateTime.Now:yyyy-MM-dd}"
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

    // ── Bundled export/import (quotes + screenshots in a ZIP) ──

    [RelayCommand]
    private void ExportBundled()
    {
        if (_allQuotes.Count == 0)
        {
            MessageBox.Show("没有可导出的语录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "压缩包 (*.zip)|*.zip",
            DefaultExt = ".zip",
            FileName = $"gal-quotes_{DateTime.Now:yyyy-MM-dd}"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"galexport_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var screenshotsDir = Path.Combine(tempDir, "screenshots");
            Directory.CreateDirectory(screenshotsDir);

            // Build tags + groups lookup
            var tagsByQuote = new Dictionary<int, List<Tag>>();
            var groupsByQuote = new Dictionary<int, List<QuoteGroup>>();
            foreach (var q in _allQuotes)
            {
                tagsByQuote[q.Id] = _storageService.GetTagsForQuote(q.Id);
                groupsByQuote[q.Id] = _storageService.GetGroupsForQuote(q.Id);
            }

            // Copy all screenshots into the temp dir and update paths (keep original Quote objects)
            var exportQuotes = new List<Quote>();
            var screenshotsByQuote = new Dictionary<int, List<string>>();
            foreach (var q in _allQuotes)
            {
                var clone = new Quote
                {
                    Id = q.Id, Text = q.Text, GameName = q.GameName,
                    ScreenshotPath = q.ScreenshotPath,
                    CapturedAt = q.CapturedAt, Notes = q.Notes,
                    WindowTitle = q.WindowTitle,
                    SlideshowShowGameName = q.SlideshowShowGameName,
                    SlideshowShowText = q.SlideshowShowText,
                    SlideshowShowNotes = q.SlideshowShowNotes
                };

                var ssList = _storageService.GetScreenshots(q.Id);
                var relPaths = new List<string>();
                for (int i = 0; i < ssList.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(ssList[i].FilePath) || !File.Exists(ssList[i].FilePath)) continue;
                    var ssName = $"{q.Id}_{i}_{Path.GetFileName(ssList[i].FilePath)}";
                    File.Copy(ssList[i].FilePath, Path.Combine(screenshotsDir, ssName), true);
                    var rel = $"screenshots/{ssName}";
                    relPaths.Add(rel);
                    if (i == 0) clone.ScreenshotPath = rel; // first screenshot for list thumbnail
                }
                if (relPaths.Count > 0)
                    screenshotsByQuote[q.Id] = relPaths;

                exportQuotes.Add(clone);
            }

            var json = _exportService.ToJson(exportQuotes, tagsByQuote, groupsByQuote, screenshotsByQuote);
            File.WriteAllText(Path.Combine(tempDir, "quotes.json"), json);

            // Include settings and usage data
            foreach (var fn in new[] { "settings.json", "usage.json" })
            {
                var src = Path.Combine(_dataDir, fn);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(tempDir, fn));
            }

            var zipPath = dialog.FileName;
            if (File.Exists(zipPath)) File.Delete(zipPath);
            System.IO.Compression.ZipFile.CreateFromDirectory(tempDir, zipPath);

            try { Directory.Delete(tempDir, true); } catch { }
            StatusText = $"已导出 {_allQuotes.Count} 条语录及截图到 {zipPath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ImportBundled()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "压缩包 (*.zip)|*.zip",
            Title = "导入语录压缩包"
        };
        if (dialog.ShowDialog() != true) return;

        string? tempDir = null;
        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), $"galimport_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(dialog.FileName, tempDir);

            var jsonPath = Path.Combine(tempDir, "quotes.json");
            if (!File.Exists(jsonPath))
            {
                MessageBox.Show("压缩包中未找到 quotes.json", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var content = File.ReadAllText(jsonPath);
            var items = _exportService.ParseJson(content);

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

                // Import screenshots if bundled (all of them; "screenshots" array, fall back to single "screenshot")
                var ssDests = new List<string>();
                var ssSrcs = item.Screenshots.Count > 0
                    ? item.Screenshots
                    : (!string.IsNullOrWhiteSpace(item.Screenshot) ? new List<string> { item.Screenshot } : []);
                foreach (var relPath in ssSrcs)
                {
                    // Reject path traversal / absolute paths that escape the extraction dir
                    var srcPath = Path.Combine(tempDir, relPath);
                    if (!IsUnderDir(srcPath, tempDir)) continue;
                    if (!File.Exists(srcPath)) continue;
                    var ext = Path.GetExtension(srcPath);
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff");
                    var destPath = Path.Combine(_screenshotDir, $"{timestamp}_{ssDests.Count}_import{ext}");
                    File.Copy(srcPath, destPath);
                    ssDests.Add(destPath);
                }

                if (ssDests.Count > 0)
                    quote.ScreenshotPath = ssDests[0];

                _storageService.InsertQuote(quote);

                // InsertQuote already created the Screenshots row for the first file; add the rest
                for (int i = 1; i < ssDests.Count; i++)
                    _storageService.AddScreenshot(quote.Id, ssDests[i], i);

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

            // Restore settings and usage data if bundled — only with explicit confirmation,
            // since silently overwriting the local config would lose data.
            bool hasExtra = File.Exists(Path.Combine(tempDir, "settings.json"))
                || File.Exists(Path.Combine(tempDir, "usage.json"));
            if (hasExtra)
            {
                bool restoreExtra = MessageBox.Show(
                    "压缩包中还包含 settings.json / usage.json，是否同时恢复这些设置与使用记录？\n" +
                    "（选择「否」将只导入语录和截图，不覆盖当前配置）",
                    "恢复设置", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                if (restoreExtra)
                {
                    foreach (var fn in new[] { "settings.json", "usage.json" })
                    {
                        var src = Path.Combine(tempDir, fn);
                        var dest = Path.Combine(_dataDir, fn);
                        if (File.Exists(src))
                            File.Copy(src, dest, true);
                    }
                }
            }

            RefreshQuotes();
            RefreshAvailableTags();
            RefreshAvailableGroups();
            StatusText = $"已导入 {imported} 条语录及截图";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (tempDir != null) try { Directory.Delete(tempDir, true); } catch { }
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

            var newScreenshotDir = newConfig.ScreenshotDirectory;
            if (!string.IsNullOrWhiteSpace(newScreenshotDir)
                && !PathsEqual(newScreenshotDir, _screenshotDir))
            {
                Directory.CreateDirectory(newScreenshotDir);
                // Move existing screenshots to the new directory, then recycle-bin the originals
                MigrateScreenshots(newScreenshotDir);
                _screenshotDir = newScreenshotDir;
                _captureService = new CaptureService(_screenshotDir);
                StatusText = $"截图目录已改为: {_screenshotDir}";
            }

            _hotkeyService.UpdateAddHotkey(newConfig.ToAddModifiers(), newConfig.AddShotVirtualKey);

            // UpdateHotkey returns false when the primary collides with the add-screenshot
            // hotkey — the two must stay distinct so a single press can't fire both actions.
            bool conflict = !_hotkeyService.UpdateHotkey(newConfig.ToModifiers(), newConfig.VirtualKey);
            var (autoOk, autoMsg) = ApplySettings(newConfig);

            if (conflict)
            {
                MessageBox.Show("采集热键与补拍热键冲突，请选择其他组合键", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText = autoOk ? $"自启: {autoMsg}" : $"自启失败: {autoMsg}";
            }
            else
            {
                StatusText = $"热键已更改为: {_hotkeyService.CurrentHotkeyDisplay}";
                if (!autoOk) StatusText += $" | 自启失败: {autoMsg}";
            }
        }
    }

    /// <summary>Apply a confirmed settings config: persist, re-apply game-name rules and UI-affecting options.</summary>
    private (bool ok, string msg) ApplySettings(HotkeyConfig cfg)
    {
        _settingsService.SaveHotkeyConfig(cfg);
        _gameDetectService.SetRules(cfg.GameNameRules);
        ReapplyRulesToAllQuotes();
        _hideUnrecognized = cfg.HideUnrecognized;
        _screenshotFormat = cfg.ScreenshotFormat;
        ToggleUsageTracking(cfg.EnableUsageTracking);
        if (!string.IsNullOrWhiteSpace(cfg.FontFamily))
        {
            try { _window.FontFamily = new System.Windows.Media.FontFamily(cfg.FontFamily); }
            catch { }
        }
        return TrySetAutoStart(cfg.AutoStart);
    }

    /// <summary>
    /// Copy every referenced screenshot into the new directory and update the DB paths.
    /// Originals are only sent to the recycle bin after every copy exists and the DB is
    /// updated, so a mid-way failure never loses data.
    /// </summary>
    private void MigrateScreenshots(string newDir)
    {
        if (string.IsNullOrWhiteSpace(newDir)) return;
        Directory.CreateDirectory(newDir);

        // Collect every screenshot location: DB rows first, legacy field as fallback
        var entries = new List<(int QuoteId, string FilePath, int? ScreenshotId)>();
        foreach (var quote in _allQuotes)
        {
            var rows = _storageService.GetScreenshots(quote.Id);
            if (rows.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(quote.ScreenshotPath))
                    entries.Add((quote.Id, quote.ScreenshotPath, null));
            }
            else
            {
                foreach (var ss in rows)
                    entries.Add((quote.Id, ss.FilePath, ss.Id));
            }
        }

        var migrated = new List<(string OldPath, string NewPath)>();
        var failed = new List<string>();
        int skipped = 0;

        // Pass 1: copy to the new directory and update DB paths (originals are kept for now)
        foreach (var (quoteId, filePath, ssId) in entries)
        {
            if (!File.Exists(filePath)) { failed.Add($"{Path.GetFileName(filePath)} (文件不存在)"); continue; }
            if (IsUnderDir(filePath, newDir)) { skipped++; continue; }

            var dest = Path.Combine(newDir, Path.GetFileName(filePath));
            if (File.Exists(dest))
            {
                var ext = Path.GetExtension(filePath);
                var stem = Path.GetFileNameWithoutExtension(filePath);
                dest = Path.Combine(newDir, $"{stem}_{DateTime.Now:yyyyMMddHHmmssfff}_{migrated.Count}{ext}");
            }

            try
            {
                File.Copy(filePath, dest);
                if (!File.Exists(dest)) { failed.Add($"{Path.GetFileName(filePath)} (复制后不存在)"); continue; }

                if (ssId is int id)
                    _storageService.UpdateScreenshotPath(id, dest);
                else
                    _storageService.AddScreenshot(quoteId, dest, _storageService.GetNextScreenshotOrder(quoteId));

                migrated.Add((filePath, dest));
            }
            catch (Exception ex)
            {
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                failed.Add($"{Path.GetFileName(filePath)} ({ex.Message})");
            }
        }

        // Re-sync the legacy ScreenshotPath field to each quote's first screenshot
        foreach (var quote in _allQuotes)
        {
            var first = _storageService.GetScreenshots(quote.Id).FirstOrDefault();
            quote.ScreenshotPath = first?.FilePath ?? "";
        }

        // Pass 2: only after every copy exists and the DB is updated, recycle-bin the originals
        foreach (var (oldPath, _) in migrated)
        {
            try
            {
                if (File.Exists(oldPath))
                    FileSystem.DeleteFile(oldPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            catch { }
        }

        RefreshQuotes();

        var msg = $"已迁移 {migrated.Count} 张截图到新目录";
        if (skipped > 0) msg += $"，{skipped} 张本就在新目录";
        if (failed.Count > 0) msg += $"\n{failed.Count} 张失败：\n{string.Join("\n", failed.Take(5))}";
        MessageBox.Show(msg, "截图迁移", MessageBoxButton.OK,
            failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    /// <summary>
    /// One-time copy of the legacy "GalgameQuoteCollector" data dir into the new
    /// "GalQuoteCollector" dir. The old dir is left untouched as a backup; from this
    /// point on the app reads and writes only the new dir.
    /// </summary>
    private static void MigrateDataDir(string oldDir, string newDir)
    {
        try { Directory.CreateDirectory(newDir); }
        catch { return; }

        foreach (var file in Directory.GetFiles(oldDir, "*", System.IO.SearchOption.AllDirectories))
        {
            try
            {
                var dest = Path.Combine(newDir, Path.GetRelativePath(oldDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                // Overwrite: this only runs when the new DB has no quotes yet, so any
                // schema-only/empty leftovers in the new dir are safely replaced.
                File.Copy(file, dest, true);
            }
            catch
            {
                // Non-fatal: a single unreadable file must not abort the migration.
            }
        }
    }

    /// <summary>True when the quotes.db at <paramref name="dbPath"/> contains at least
    /// one quote row. Used to decide whether the new data dir still needs the legacy
    /// data migrated into it.</summary>
    private static bool DbHasQuotes(string dbPath)
    {
        try
        {
            if (!File.Exists(dbPath)) return false;
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Quotes";
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
        catch
        {
            return false; // unreadable/absent file ⇒ treat as needing migration
        }
    }

    private static bool IsUnderDir(string path, string dir)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
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

                // Clean up stale VBS/lnk from ALL previous naming generations
                // (GalgameQuoteCollector → GalQuoteCollector → Gal-quote-tool)
                foreach (var name in new[] { "GalQuoteCollector.vbs", "GalgameQuoteCollector.vbs",
                                             "GalQuoteCollector.lnk", "GalgameQuoteCollector.lnk" })
                {
                    var stalePath = Path.Combine(startupFolder, name);
                    if (File.Exists(stalePath)) File.Delete(stalePath);
                }

                // Write a VBScript to startup folder — no admin rights needed, no console flash
                var vbsPath = Path.Combine(startupFolder, "Gal-quote-tool.vbs");
                var vbsContent = $"CreateObject(\"WScript.Shell\").Run \"\"\"{exePath}\"\" --minimized\", 0, False";
                File.WriteAllText(vbsPath, vbsContent);

                if (!File.Exists(vbsPath))
                    return (false, "无法创建启动脚本");

                return (true, $"已创建启动脚本: {vbsPath}");
            }
            else
            {
                // Remove VBS (all naming generations)
                foreach (var name in new[] { "Gal-quote-tool.vbs", "GalQuoteCollector.vbs", "GalgameQuoteCollector.vbs" })
                {
                    var path = Path.Combine(startupFolder, name);
                    if (File.Exists(path)) File.Delete(path);
                }
                // Also clean up old shortcuts if exist
                foreach (var name in new[] { "Gal-quote-tool.lnk", "GalQuoteCollector.lnk", "GalgameQuoteCollector.lnk" })
                {
                    var path = Path.Combine(startupFolder, name);
                    if (File.Exists(path)) File.Delete(path);
                }

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
            foreach (var name in new[] { "Gal-quote-tool.vbs", "GalQuoteCollector.vbs", "GalgameQuoteCollector.vbs",
                                         "Gal-quote-tool.lnk", "GalQuoteCollector.lnk", "GalgameQuoteCollector.lnk" })
            {
                if (File.Exists(Path.Combine(startupFolder, name)))
                    return "✓ 已开启";
            }
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
        if (TimeSpan.TryParse(EditCapturedAtTimeString, out var ts))
        {
            var newCapturedAt = EditCapturedAtDate.Date + ts;
            var oldCapturedAt = SelectedQuote.CapturedAt;

            // Rename screenshot files if timestamp changed
            if (oldCapturedAt != newCapturedAt)
            {
                var oldTimestamp = oldCapturedAt.ToString("yyyy-MM-dd_HHmmss_fff");
                var newTimestamp = newCapturedAt.ToString("yyyy-MM-dd_HHmmss_fff");
                var screenshots = _storageService.GetScreenshots(SelectedQuote.Id);
                foreach (var ss in screenshots)
                {
                    var oldPath = ss.FilePath;
                    if (!File.Exists(oldPath)) continue;
                    var dir = Path.GetDirectoryName(oldPath)!;
                    var oldName = Path.GetFileName(oldPath);
                    var newName = oldName.Replace(oldTimestamp, newTimestamp);
                    if (newName == oldName) continue;
                    var newPath = Path.Combine(dir, newName);
                    try
                    {
                        File.Move(oldPath, newPath);
                        _storageService.UpdateScreenshotPath(ss.Id, newPath);
                    }
                    catch { }
                }
                RefreshCurrentScreenshots();
            }

            SelectedQuote.CapturedAt = newCapturedAt;
        }
        else
        {
            SelectedQuote.CapturedAt = EditCapturedAtDate;
        }

        // Re-populate ScreenshotPath for list thumbnail
        var firstSs = _storageService.GetScreenshots(SelectedQuote.Id).FirstOrDefault();
        SelectedQuote.ScreenshotPath = firstSs?.FilePath ?? "";

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
        var screenshotPaths = _storageService.GetScreenshots(quote.Id)
            .Select(s => s.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct()
            .ToList();
        // Fallback to the legacy single-screenshot field if the table has no rows
        if (screenshotPaths.Count == 0 && !string.IsNullOrWhiteSpace(quote.ScreenshotPath))
            screenshotPaths.Add(quote.ScreenshotPath);

        foreach (var path in screenshotPaths)
        {
            // Only delete the exact recorded paths — a file with the same name in the
            // screenshot dir may belong to a different quote.
            if (File.Exists(path))
                try { FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); } catch { }
        }
    }

    /// <summary>Delete several quotes at once (batch delete). Optionally recycles their screenshot files.</summary>
    public void DeleteQuotes(IEnumerable<Quote> quotes, bool deleteScreenshots)
    {
        foreach (var q in quotes.ToList())
        {
            if (deleteScreenshots) DeleteScreenshots(q);
            DeleteQuoteDirect(q);
        }
    }

    [RelayCommand]
    private void DeleteQuote()
    {
        if (SelectedQuote == null) return;
        var result = MessageBox.Show("确定要删除这条语录吗？", "确认删除",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        // Ask about screenshots (check the Screenshots table first, fall back to legacy field)
        var hasScreenshots = _storageService.GetScreenshots(SelectedQuote.Id).Any();
        if (!hasScreenshots && !string.IsNullOrEmpty(SelectedQuote.ScreenshotPath))
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
    private void RepairScreenshots()
    {
        int fixedCount = 0;
        foreach (var q in _allQuotes)
        {
            if (string.IsNullOrEmpty(q.ScreenshotPath)) continue;
            var existing = _storageService.GetScreenshots(q.Id);
            if (existing.Count == 0)
            {
                _storageService.AddScreenshot(q.Id, q.ScreenshotPath, 0);
                fixedCount++;
            }
        }
        RefreshQuotes();
        if (SelectedQuote != null) RefreshCurrentScreenshots();
        StatusText = fixedCount > 0
            ? $"已修复 {fixedCount} 条语录的截图关联"
            : "所有语录截图关联正常";
    }

    [RelayCommand]
    private void RematchScreenshots()
    {
        if (!Directory.Exists(_screenshotDir))
        {
            StatusText = "截图目录不存在";
            return;
        }

        // Index screenshot files by their 17-char timestamp prefix (yyyy-MM-dd_HHmmss)
        var filesByTime = new Dictionary<string, List<string>>();
        foreach (var pattern in new[] { "*.png", "*.jpg" })
        {
            foreach (var f in Directory.GetFiles(_screenshotDir, pattern))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.Length < 17) continue;
                var key = name[..17];
                if (!filesByTime.TryGetValue(key, out var list))
                    filesByTime[key] = list = new List<string>();
                list.Add(f);
            }
        }

        if (filesByTime.Count == 0)
        {
            StatusText = "截图目录中没有找到截图文件";
            return;
        }

        int matched = 0;
        foreach (var q in _allQuotes)
        {
            // Skip quotes that already have a usable screenshot
            if (_storageService.GetScreenshots(q.Id).Count > 0
                || (!string.IsNullOrEmpty(q.ScreenshotPath) && File.Exists(q.ScreenshotPath)))
                continue;

            // CapturedAt is set after OCR completes, while the filename carries the capture
            // second — allow a tolerance window so the match survives the OCR delay.
            var target = q.CapturedAt;
            string? bestKey = null;
            double bestDelta = double.MaxValue;
            foreach (var kvp in filesByTime)
            {
                if (!DateTime.TryParseExact(kvp.Key, "yyyy-MM-dd_HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var fileTime))
                    continue;
                var delta = Math.Abs((fileTime - target).TotalSeconds);
                if (delta <= 120 && delta < bestDelta) { bestDelta = delta; bestKey = kvp.Key; }
            }

            if (bestKey != null && filesByTime.TryGetValue(bestKey, out var candidates) && candidates.Count > 0)
            {
                var filePath = candidates[0];
                candidates.RemoveAt(0);
                _storageService.AddScreenshot(q.Id, filePath, 0);
                q.ScreenshotPath = filePath;
                _storageService.UpdateQuote(q);
                matched++;
            }
        }

        RefreshQuotes();
        if (SelectedQuote != null) RefreshCurrentScreenshots();
        StatusText = matched > 0
            ? $"已关联 {matched} 条语录与截图"
            : "没有需要关联的语录";
    }

    [RelayCommand]
    private void DeleteUnassociatedScreenshots()
    {
        if (!Directory.Exists(_screenshotDir))
        {
            StatusText = "截图目录不存在";
            return;
        }

        // Every path referenced by the DB (Screenshots table + legacy field)
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _storageService.GetAllScreenshotPaths())
            if (!string.IsNullOrWhiteSpace(p)) referenced.Add(Path.GetFullPath(p));
        foreach (var q in _allQuotes)
            if (!string.IsNullOrWhiteSpace(q.ScreenshotPath))
                referenced.Add(Path.GetFullPath(q.ScreenshotPath));

        var orphans = new List<string>();
        foreach (var f in Directory.GetFiles(_screenshotDir, "*.png")
            .Concat(Directory.GetFiles(_screenshotDir, "*.jpg")))
        {
            if (!referenced.Contains(Path.GetFullPath(f)))
                orphans.Add(f);
        }

        if (orphans.Count == 0)
        {
            MessageBox.Show("没有未关联的截图文件", "删除未关联截图",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var res = MessageBox.Show(
            $"发现 {orphans.Count} 个未被任何语录引用的截图文件，确认删除？\n\n删除后将移入回收站，可恢复。",
            "删除未关联截图", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;

        int deleted = 0;
        foreach (var f in orphans)
        {
            try
            {
                if (File.Exists(f))
                {
                    FileSystem.DeleteFile(f, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    deleted++;
                }
            }
            catch { }
        }

        StatusText = $"已删除 {deleted} 个未关联截图（移入回收站）";
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        await Capture();
    }

    [RelayCommand]
    private async Task CaptureAdditional()
    {
        if (SelectedQuote == null)
        {
            StatusText = "请先选择一条语录再补拍";
            return;
        }

        bool restoreWindow = true;
        try
        {
            StatusText = "补拍截图...";
            var gameHwnd = CaptureService.GetForegroundWindowHandle();
            var windowTitle = CaptureService.GetWindowTitle(gameHwnd);
            if (string.IsNullOrWhiteSpace(windowTitle))
            {
                StatusText = "未检测到活动窗口";
                return;
            }

            _window.WindowState = WindowState.Minimized;
            await Task.Delay(Math.Max(100, _captureDelayMs));

            var gameName = _gameDetectService.DetectGameName(windowTitle);
            var nextOrder = _storageService.GetNextScreenshotOrder(SelectedQuote.Id);
            var screenshotPath = _captureService.CaptureWindow(gameHwnd, _screenshotFormat, nextOrder,
                forceFullscreen: !string.IsNullOrWhiteSpace(gameName));
            _storageService.AddScreenshot(SelectedQuote.Id, screenshotPath, nextOrder);

            var toast = new Views.ToastWindow("已补拍截图", $"第 {nextOrder} 张", 2000);
            toast.Show();

            restoreWindow = false;
            StatusText = $"已为语录补拍第 {nextOrder} 张截图";
        }
        catch (Exception ex)
        {
            StatusText = $"补拍失败: {ex.Message}";
        }
        finally
        {
            if (restoreWindow) _window.WindowState = WindowState.Normal;
        }
    }

    [RelayCommand]
    private void DeleteScreenshot(Screenshot screenshot)
    {
        if (screenshot == null) return;
        var result = MessageBox.Show("确定删除这张截图？（不影响语录）", "删除截图",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        if (File.Exists(screenshot.FilePath))
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(screenshot.FilePath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); } catch { }

        _storageService.DeleteScreenshot(screenshot.Id);
        RefreshCurrentScreenshots();
        StatusText = "已删除截图";
    }

    [RelayCommand]
    private void OpenScreenshotFile(Screenshot screenshot)
    {
        if (screenshot == null || !File.Exists(screenshot.FilePath)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = screenshot.FilePath,
            UseShellExecute = true
        });
    }

    private void RefreshCurrentScreenshots()
    {
        if (SelectedQuote == null) { CurrentScreenshots.Clear(); return; }
        var list = _storageService.GetScreenshots(SelectedQuote.Id);
        CurrentScreenshots = new ObservableCollection<Screenshot>(list);
    }

    [RelayCommand]
    private void AddScreenshotFromFile()
    {
        if (SelectedQuote == null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片文件 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            Title = "选择要添加的截图"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var srcPath = dialog.FileName;
            var ext = Path.GetExtension(srcPath);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff");
            var nextOrder = _storageService.GetNextScreenshotOrder(SelectedQuote.Id);
            var destName = $"{timestamp}_{nextOrder}{ext}";
            var destPath = Path.Combine(_screenshotDir, destName);
            File.Copy(srcPath, destPath, false);

            _storageService.AddScreenshot(SelectedQuote.Id, destPath, nextOrder);
            RefreshCurrentScreenshots();
            StatusText = $"已添加截图 ({nextOrder})";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"添加截图失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenScreenshot()
    {
        if (SelectedQuote == null) return;
        var screenshots = _storageService.GetScreenshots(SelectedQuote.Id);
        var first = screenshots.FirstOrDefault();
        if (first != null && File.Exists(first.FilePath))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = first.FilePath,
                UseShellExecute = true
            });
        }
    }

    private async void OnHotkeyPressedAdd(object? sender, EventArgs e)
    {
        if (SelectedQuote != null)
            await CaptureAdditional();
    }

    private async Task LoadQuotesAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                var quotes = _storageService.GetAllQuotes();
                int updated = 0;
                foreach (var q in quotes)
                {
                    // Populate ScreenshotPath for list thumbnail (first screenshot)
                    var firstSs = _storageService.GetScreenshots(q.Id).FirstOrDefault();
                    q.ScreenshotPath = firstSs?.FilePath ?? q.ScreenshotPath;

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
                    try
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
                        StatusText = $"{AppVersion} | " + (updated > 0
                            ? $"已应用规则，更新了 {updated} 条语录的游戏名 | 自启: {autoStart}"
                            : $"热键: {_hotkeyService.CurrentHotkeyDisplay}   |   共 {_allQuotes.Count} 条语录 | 自启: {autoStart}");
                        CheckForUpdate();
                    }
                    catch (Exception ex)
                    {
                        StatusText = $"加载失败: {ex.Message}";
                    }
                });
            });
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败: {ex.Message}";
        }
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

    [RelayCommand]
    private void ShowAbout()
    {
        var updateUrl = "https://github.com/Liushiweiying/Gal-quote-tool/releases";
        MessageBox.Show(
            $"Gal 语录收藏工具 {AppVersion}\n\n" +
            $"作者: 未时\n" +
            $"Bilibili: @重构时间\n" +
            $"QQ: 3302164450\n\n" +
            $"项目地址:\n{updateUrl}",
            "关于", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void CheckForUpdate()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Gal-quote-tool/{AppVersion}");
            http.Timeout = TimeSpan.FromSeconds(5);
            var response = await http.GetStringAsync(
                "https://api.github.com/repos/Liushiweiying/Gal-quote-tool/releases/latest");
            var json = System.Text.Json.JsonDocument.Parse(response);
            var latest = json.RootElement.GetProperty("tag_name").GetString();
            if (string.IsNullOrWhiteSpace(latest)) return;

            // Compare numerically ("v1.1.8" > "v1.1.7"), never as plain strings, so
            // version ordering is correct and we don't nag when tag == current.
            var latestV = TryParseVersion(latest);
            var currentV = TryParseVersion(AppVersion);
            if (latestV == null || currentV == null || latestV <= currentV) return;

            StatusText = $"发现新版本 {latest} → {StatusText}";
            var updateUrl = "https://github.com/Liushiweiying/Gal-quote-tool/releases";
            MessageBox.Show(
                $"发现新版本: {latest}\n当前版本: {AppVersion}\n\n前往下载:\n{updateUrl}",
                "版本更新", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            // Silently fail - no network or rate limited
        }
    }

    private static Version? TryParseVersion(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        return Version.TryParse(v, out var ver) ? ver : null;
    }

    public void RefreshQuotes()
    {
        IEnumerable<Quote> source = _allQuotes;

        // Load tag/group membership once up front (two queries) so filtering below
        // doesn't run per-quote DB queries on the UI thread (N+1).
        var tagsByQuote = _storageService.GetTagIdsByQuote();
        var groupsByQuote = _storageService.GetGroupIdsByQuote();

        // Filter by group (multi-select + exclude); Id == -1 means "未分组"
        if (SelectedGroupFilters.Count > 0)
        {
            var includeUngrouped = SelectedGroupFilters.Contains(-1);
            var groupIds = new HashSet<int>();
            foreach (var gid in SelectedGroupFilters.Where(id => id > 0))
                foreach (var qid in _storageService.GetQuoteIdsInGroup(gid))
                    groupIds.Add(qid);
            bool MatchGroup(Quote q) =>
                groupIds.Contains(q.Id)
                || (includeUngrouped && (!groupsByQuote.TryGetValue(q.Id, out var gl) || gl.Count == 0));
            source = GroupFilterExclude
                ? source.Where(q => !MatchGroup(q))
                : source.Where(q => MatchGroup(q));
        }

        // Filter by tag (multi-select + exclude); Id == -1 means "无标签"
        if (SelectedTagFilters.Count > 0)
        {
            var includeNoTags = SelectedTagFilters.Contains(-1);
            var filterTagIds = SelectedTagFilters.Where(id => id > 0).ToHashSet();
            bool MatchTag(Quote q)
            {
                var tagIds = tagsByQuote.TryGetValue(q.Id, out var tl) ? tl : [];
                if (includeNoTags && tagIds.Count == 0) return true;
                return tagIds.Any(t => filterTagIds.Contains(t));
            }
            source = TagFilterExclude
                ? source.Where(q => !MatchTag(q))
                : source.Where(q => MatchTag(q));
        }

        // Filter by game (multi-select + exclude); Id == -1 means "未分类"
        if (SelectedGameFilters.Count > 0)
        {
            var includeUncategorized = SelectedGameFilters.Contains(-1);
            var gameNames = AvailableGamesForFilter
                .Where(f => f.Id > 0 && SelectedGameFilters.Contains(f.Id))
                .Select(f => f.Name)
                .Where(n => n != null)
                .ToHashSet();
            bool MatchGame(Quote q) =>
                (includeUncategorized && string.IsNullOrWhiteSpace(q.GameName))
                || (q.GameName != null && gameNames.Contains(q.GameName));
            source = GameFilterExclude
                ? source.Where(q => !MatchGame(q))
                : source.Where(q => MatchGame(q));
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

    public string HotkeyHintText => $"按 {_hotkeyService.CurrentHotkeyDisplay} 采集";
}

