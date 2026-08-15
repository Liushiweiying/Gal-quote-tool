using System.Text.RegularExpressions;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Services;

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
        var best = MatchCustomRule(windowTitle);
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

        if (MatchesNonGamePattern(cleaned))
            return string.Empty;

        return cleaned;
    }

    /// <summary>
    /// Usage tracking: only returns a name when the window clearly looks like a game —
    /// a custom rule matched, or a known game-engine suffix was actually stripped.
    /// Otherwise returns null so the caller records the app's product/process name
    /// instead of e.g. a browser tab title or a file name in a code editor.
    /// </summary>
    public string? DetectUsageName(string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
            return null;

        var best = MatchCustomRule(windowTitle);
        if (best != null)
            return best.Name;

        // Only trust auto-cleaning when a known engine suffix was actually present
        var cleaned = windowTitle.Trim();
        var stripped = EngineSuffixRegex.Replace(cleaned, "").Trim();
        if (stripped == cleaned)
            return null;

        cleaned = TrailingSuffixRegex.Replace(stripped, "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < 2)
            return null;
        if (MatchesNonGamePattern(cleaned))
            return null;
        return cleaned;
    }

    private GameNameRule? MatchCustomRule(string windowTitle)
    {
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
        return best;
    }

    // 3. Skip non-game window patterns
    private static readonly string[] NonGamePatterns =
    {
        @"^Program\s+Manager$", @"^Task\s+Manager$",
        @"^Settings?$", @"^Control\s+Panel$",
        @"^File\s+Explorer$", @"^Microsoft\s+",
        @"^Google\s+", @"^Mozilla\s+",
        @"^Visual\s+Studio", @"^Code\s+",
    };

    private static bool MatchesNonGamePattern(string cleaned)
    {
        foreach (var pattern in NonGamePatterns)
        {
            if (Regex.IsMatch(cleaned, pattern, RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }
}
