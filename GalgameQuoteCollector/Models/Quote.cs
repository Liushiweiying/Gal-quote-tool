using CommunityToolkit.Mvvm.ComponentModel;

namespace GalgameQuoteCollector.Models;

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

    public string CapturedAtDisplay => CapturedAt.ToString("yyyy-MM-dd HH:mm:ss");

    public string PreviewText => Text.Length > 50 ? Text[..50] + "..." : Text;
}
