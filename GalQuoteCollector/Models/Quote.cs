using CommunityToolkit.Mvvm.ComponentModel;

namespace GalQuoteCollector.Models;

public partial class Quote : ObservableObject
{
    [ObservableProperty]
    private int _id;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string _gameName = string.Empty;

    [ObservableProperty]
    private string _screenshotPath = string.Empty;

    [ObservableProperty]
    private DateTime _capturedAt = DateTime.Now;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private string _windowTitle = string.Empty;

    [ObservableProperty]
    private bool _slideshowShowGameName = true;

    [ObservableProperty]
    private bool _slideshowShowText = true;

    [ObservableProperty]
    private bool _slideshowShowNotes = true;

    public string CapturedAtDisplay => CapturedAt.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>OCR 没识别出文字时的占位串；界面上不显示这几个字，只显示截图。</summary>
    public const string UnrecognizedText = "[未识别到文字]";

    public bool IsUnrecognized => Text.Contains(UnrecognizedText);

    // 预览里不显示「[未识别到文字]」这几个字（语录本身照常显示，需要的话用上面的开关隐藏整条）
    public string PreviewText => IsUnrecognized ? "" : (Text.Length > 50 ? Text[..50] + "..." : Text);
}
