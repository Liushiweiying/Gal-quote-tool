namespace GalgameQuoteCollector.Models;

public class Screenshot
{
    public int Id { get; set; }
    public int QuoteId { get; set; }
    public string FilePath { get; set; } = "";
    public int SortOrder { get; set; }
}
