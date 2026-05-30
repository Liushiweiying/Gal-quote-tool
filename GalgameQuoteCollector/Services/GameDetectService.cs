using System.Text.RegularExpressions;
using GalgameQuoteCollector.Models;

namespace GalgameQuoteCollector.Services;

public class GameDetectService
{
    private List<GameNameRule> _rules = new();

    // Common galgame engine patterns in window titles
    private static readonly Regex EngineSuffixRegex = new(
        @"\s*[-–—]\s*(Kirikiri|吉里吉里|AGTH|ARTKOM|SystemNNN|RealLive|Majiro|Siglus|BGI|Ethornell|CatSystem2|Malie|Yuka)(\s*\d+[\d.]*\s*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Strip trailing date/chapter/version suffixes
    private static readonly Regex TrailingSuffixRegex = new(
        @"\s+(v?\d+[\d.]*|[0-9]+月[0-9]+日|第[0-9]+[章話話]|Chapter\s*\d+|Act\s*\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Set custom rules that take priority: if window title contains Match, return Name.
    /// </summary>
    public void SetRules(List<GameNameRule> rules)
    {
        _rules = rules ?? new();
    }

    /// <summary>
    /// Detect game name from a window title.
    /// First checks custom rules, then falls back to automatic cleaning.
    /// </summary>
    public string DetectGameName(string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
            return string.Empty;

        // 1. Custom rules — longest match wins
        GameNameRule? best = null;
        foreach (var rule in _rules)
        {
            if (!string.IsNullOrWhiteSpace(rule.Match) &&
                windowTitle.Contains(rule.Match, StringComparison.OrdinalIgnoreCase) &&
                (best == null || rule.Match.Length > best.Match.Length))
            {
                best = rule;
            }
        }
        if (best != null)
            return best.Name;

        // 2. Auto-cleaning
        var cleaned = windowTitle.Trim();
        cleaned = EngineSuffixRegex.Replace(cleaned, "").Trim();
        cleaned = TrailingSuffixRegex.Replace(cleaned, "").Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
            return windowTitle.Trim();

        if (cleaned.Length < 2)
            return string.Empty;

        // 3. Skip non-game window patterns
        var nonGamePatterns = new[]
        {
            @"^Program\s+Manager$", @"^Task\s+Manager$",
            @"^Settings?$", @"^Control\s+Panel$",
            @"^File\s+Explorer$", @"^Microsoft\s+",
            @"^Google\s+", @"^Mozilla\s+",
            @"^Visual\s+Studio", @"^Code\s+",
        };

        foreach (var pattern in nonGamePatterns)
        {
            if (Regex.IsMatch(cleaned, pattern, RegexOptions.IgnoreCase))
                return string.Empty;
        }

        return cleaned;
    }
}
