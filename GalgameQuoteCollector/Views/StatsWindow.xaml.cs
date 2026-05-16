using System.Windows;
using GalgameQuoteCollector.Models;
using GalgameQuoteCollector.Services;

namespace GalgameQuoteCollector.Views;

public partial class StatsWindow : Window
{
    private readonly StorageService _storage;
    private readonly List<Quote> _allQuotes;

    public StatsWindow(Window owner, StorageService storage, List<Quote> allQuotes, List<Tag> allTags, List<QuoteGroup> allGroups)
    {
        InitializeComponent();
        Owner = owner;
        _storage = storage;
        _allQuotes = allQuotes;

        // Overview
        TotalQuotesText.Text = allQuotes.Count.ToString();
        TotalGamesText.Text = allQuotes.Select(q => q.GameName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Count().ToString();
        TotalTagsText.Text = allTags.Count.ToString();
        TotalGroupsText.Text = allGroups.Count.ToString();

        // By game
        var gameStats = allQuotes
            .GroupBy(q => string.IsNullOrWhiteSpace(q.GameName) ? "未分类" : q.GameName)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count);
        GameStatsList.ItemsSource = gameStats.ToList();

        // By group
        var groupStats = allGroups
            .Select(g => new
            {
                Name = g.Name,
                Count = allQuotes.Count(q => storage.GetGroupsForQuote(q.Id).Any(gg => gg.Id == g.Id))
            })
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count);
        GroupStatsList.ItemsSource = groupStats.ToList();

        // By tag
        var tagStats = allTags
            .Select(t => new
            {
                Name = $"#{t.Name}",
                Count = allQuotes.Count(q => storage.GetTagsForQuote(q.Id).Any(t2 => t2.Id == t.Id))
            })
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count);
        TagStatsList.ItemsSource = tagStats.ToList();

        // By time (default monthly)
        RefreshTimeStats(true);
    }

    private void RefreshTimeStats(bool monthly)
    {
        var grouped = _allQuotes
            .GroupBy(q => monthly ? q.CapturedAt.ToString("yyyy-MM") : q.CapturedAt.ToString("yyyy"))
            .Select(g => new { Period = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Period);
        TimeStatsList.ItemsSource = grouped.ToList();
    }

    private void OnTimeModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshTimeStats(MonthlyRadio.IsChecked == true);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
