using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using GalgameQuoteCollector.Models;

namespace GalgameQuoteCollector.Services;

public class StorageService : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public StorageService(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        _connection = new SqliteConnection(_connectionString);
        _connection.Open();

        var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Quotes (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                Text            TEXT NOT NULL,
                GameName        TEXT NOT NULL DEFAULT '',
                ScreenshotPath  TEXT NOT NULL DEFAULT '',
                CapturedAt      TEXT NOT NULL,
                Notes           TEXT NOT NULL DEFAULT '',
                WindowTitle     TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS Tags (
                Id   INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS QuoteTags (
                QuoteId INTEGER NOT NULL REFERENCES Quotes(Id) ON DELETE CASCADE,
                TagId   INTEGER NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,
                PRIMARY KEY (QuoteId, TagId)
            );
            CREATE TABLE IF NOT EXISTS GroupsTable (
                Id   INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS QuoteGroupMaps (
                QuoteId INTEGER NOT NULL REFERENCES Quotes(Id) ON DELETE CASCADE,
                GroupId INTEGER NOT NULL REFERENCES GroupsTable(Id) ON DELETE CASCADE,
                PRIMARY KEY (QuoteId, GroupId)
            );
            CREATE TABLE IF NOT EXISTS Screenshots (
                Id       INTEGER PRIMARY KEY AUTOINCREMENT,
                QuoteId  INTEGER NOT NULL REFERENCES Quotes(Id) ON DELETE CASCADE,
                FilePath TEXT NOT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();

        // Migrations for older databases
        foreach (var col in new[] {
            "WindowTitle TEXT NOT NULL DEFAULT ''",
            "SlideshowShowGameName INTEGER NOT NULL DEFAULT 1",
            "SlideshowShowText INTEGER NOT NULL DEFAULT 1",
            "SlideshowShowNotes INTEGER NOT NULL DEFAULT 1"
        })
        {
            try { using var m = _connection.CreateCommand(); m.CommandText = $"ALTER TABLE Quotes ADD COLUMN {col}"; m.ExecuteNonQuery(); }
            catch { }
        }

        // Clean up duplicate screenshots (from previous bug) + migrate old ScreenshotPath
        try
        {
            // Deduplicate: keep only the first entry for each distinct (QuoteId, FilePath)
            using var dedup = _connection.CreateCommand();
            dedup.CommandText = "DELETE FROM Screenshots WHERE Id NOT IN (SELECT MIN(Id) FROM Screenshots GROUP BY QuoteId, FilePath)";
            dedup.ExecuteNonQuery();

            // Migrate old ScreenshotPath if Screenshots table is empty
            using var check = _connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM Screenshots";
            if (Convert.ToInt32(check.ExecuteScalar()) == 0)
            {
                using var m = _connection.CreateCommand();
                m.CommandText = "INSERT INTO Screenshots (QuoteId, FilePath, SortOrder) SELECT Id, ScreenshotPath, 1 FROM Quotes WHERE ScreenshotPath != ''";
                m.ExecuteNonQuery();
            }
        }
        catch { }
    }

    public void InsertQuote(Quote quote)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Quotes (Text, GameName, ScreenshotPath, CapturedAt, Notes, WindowTitle,
                SlideshowShowGameName, SlideshowShowText, SlideshowShowNotes)
            VALUES (@Text, @GameName, @ScreenshotPath, @CapturedAt, @Notes, @WindowTitle,
                @SsGame, @SsText, @SsNotes);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@Text", quote.Text);
        cmd.Parameters.AddWithValue("@GameName", quote.GameName);
        cmd.Parameters.AddWithValue("@ScreenshotPath", quote.ScreenshotPath);
        cmd.Parameters.AddWithValue("@CapturedAt", quote.CapturedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@Notes", quote.Notes);
        cmd.Parameters.AddWithValue("@WindowTitle", quote.WindowTitle);
        cmd.Parameters.AddWithValue("@SsGame", quote.SlideshowShowGameName ? 1 : 0);
        cmd.Parameters.AddWithValue("@SsText", quote.SlideshowShowText ? 1 : 0);
        cmd.Parameters.AddWithValue("@SsNotes", quote.SlideshowShowNotes ? 1 : 0);

        var result = cmd.ExecuteScalar();
        if (result is long id)
            quote.Id = (int)id;
    }

    public List<Quote> GetAllQuotes()
    {
        var quotes = new List<Quote>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT Id, Text, GameName, ScreenshotPath, CapturedAt, Notes, WindowTitle, SlideshowShowGameName, SlideshowShowText, SlideshowShowNotes FROM Quotes ORDER BY CapturedAt DESC";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            quotes.Add(new Quote
            {
                Id = reader.GetInt32(0),
                Text = reader.GetString(1),
                GameName = reader.GetString(2),
                ScreenshotPath = reader.GetString(3),
                CapturedAt = DateTime.Parse(reader.GetString(4)),
                Notes = reader.GetString(5),
                WindowTitle = reader.GetString(6),
                SlideshowShowGameName = reader.GetInt32(7) == 1,
                SlideshowShowText = reader.GetInt32(8) == 1,
                SlideshowShowNotes = reader.GetInt32(9) == 1
            });
        }
        return quotes;
    }

    public List<Quote> SearchQuotes(string keyword)
    {
        var quotes = new List<Quote>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Text, GameName, ScreenshotPath, CapturedAt, Notes
            FROM Quotes
            WHERE Text LIKE @Keyword OR GameName LIKE @Keyword
            ORDER BY CapturedAt DESC
            """;
        cmd.Parameters.AddWithValue("@Keyword, $Keyword", $"%{keyword}%");

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            quotes.Add(new Quote
            {
                Id = reader.GetInt32(0),
                Text = reader.GetString(1),
                GameName = reader.GetString(2),
                ScreenshotPath = reader.GetString(3),
                CapturedAt = DateTime.Parse(reader.GetString(4)),
                Notes = reader.GetString(5),
                WindowTitle = reader.GetString(6)
            });
        }
        return quotes;
    }

    public void UpdateQuote(Quote quote)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "UPDATE Quotes SET Text = @Text, GameName = @GameName, CapturedAt = @CapturedAt, Notes = @Notes, WindowTitle = @WindowTitle, SlideshowShowGameName = @SsGame, SlideshowShowText = @SsText, SlideshowShowNotes = @SsNotes WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Text", quote.Text);
        cmd.Parameters.AddWithValue("@GameName", quote.GameName);
        cmd.Parameters.AddWithValue("@CapturedAt", quote.CapturedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@Notes", quote.Notes);
        cmd.Parameters.AddWithValue("@WindowTitle", quote.WindowTitle);
        cmd.Parameters.AddWithValue("@SsGame", quote.SlideshowShowGameName ? 1 : 0);
        cmd.Parameters.AddWithValue("@SsText", quote.SlideshowShowText ? 1 : 0);
        cmd.Parameters.AddWithValue("@SsNotes", quote.SlideshowShowNotes ? 1 : 0);
        cmd.Parameters.AddWithValue("@Id", quote.Id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteQuote(int id)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM Quotes WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    public List<string> GetAllGameNames()
    {
        var names = new List<string>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT GameName FROM Quotes WHERE GameName != '' ORDER BY GameName";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    // ── Tags ──────────────────────────────────────────

    public List<Tag> GetAllTags()
    {
        var tags = new List<Tag>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT Id, Name FROM Tags ORDER BY Name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tags.Add(new Tag { Id = reader.GetInt32(0), Name = reader.GetString(1) });
        return tags;
    }

    public List<Tag> GetTagsForQuote(int quoteId)
    {
        var tags = new List<Tag>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT t.Id, t.Name FROM Tags t
            JOIN QuoteTags qt ON qt.TagId = t.Id
            WHERE qt.QuoteId = @QuoteId
            ORDER BY t.Name
            """;
        cmd.Parameters.AddWithValue("@QuoteId", quoteId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tags.Add(new Tag { Id = reader.GetInt32(0), Name = reader.GetString(1) });
        return tags;
    }

    public Tag AddTag(string name)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO Tags (Name) VALUES (@Name); SELECT Id FROM Tags WHERE Name = @Name";
        cmd.Parameters.AddWithValue("@Name", name.Trim());
        var result = cmd.ExecuteScalar();
        return new Tag { Id = Convert.ToInt32(result), Name = name.Trim() };
    }

    public void DeleteTag(int tagId)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM Tags WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", tagId);
        cmd.ExecuteNonQuery();
    }

    public void AddTagToQuote(int quoteId, int tagId)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO QuoteTags (QuoteId, TagId) VALUES (@QuoteId, @TagId)";
        cmd.Parameters.AddWithValue("@QuoteId", quoteId);
        cmd.Parameters.AddWithValue("@TagId", tagId);
        cmd.ExecuteNonQuery();
    }

    public void RemoveTagFromQuote(int quoteId, int tagId)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM QuoteTags WHERE QuoteId = @QuoteId AND TagId = @TagId";
        cmd.Parameters.AddWithValue("@QuoteId", quoteId);
        cmd.Parameters.AddWithValue("@TagId", tagId);
        cmd.ExecuteNonQuery();
    }

    // ── Groups ────────────────────────────────────────

    public List<QuoteGroup> GetAllGroups()
    {
        var groups = new List<QuoteGroup>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT Id, Name FROM GroupsTable ORDER BY Name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            groups.Add(new QuoteGroup { Id = reader.GetInt32(0), Name = reader.GetString(1) });
        return groups;
    }

    public List<QuoteGroup> GetGroupsForQuote(int quoteId)
    {
        var groups = new List<QuoteGroup>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT g.Id, g.Name FROM GroupsTable g
            JOIN QuoteGroupMaps qgm ON qgm.GroupId = g.Id
            WHERE qgm.QuoteId = @QuoteId
            ORDER BY g.Name
            """;
        cmd.Parameters.AddWithValue("@QuoteId", quoteId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            groups.Add(new QuoteGroup { Id = reader.GetInt32(0), Name = reader.GetString(1) });
        return groups;
    }

    public QuoteGroup AddGroup(string name)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO GroupsTable (Name) VALUES (@Name); SELECT Id FROM GroupsTable WHERE Name = @Name";
        cmd.Parameters.AddWithValue("@Name", name.Trim());
        var result = cmd.ExecuteScalar();
        return new QuoteGroup { Id = Convert.ToInt32(result), Name = name.Trim() };
    }

    public void RenameGroup(int id, string name)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "UPDATE GroupsTable SET Name = @Name WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Name", name.Trim());
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteGroup(int id)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM GroupsTable WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    public void AddQuoteToGroup(int quoteId, int groupId)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO QuoteGroupMaps (QuoteId, GroupId) VALUES (@QuoteId, @GroupId)";
        cmd.Parameters.AddWithValue("@QuoteId", quoteId);
        cmd.Parameters.AddWithValue("@GroupId", groupId);
        cmd.ExecuteNonQuery();
    }

    public void RemoveQuoteFromGroup(int quoteId, int groupId)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM QuoteGroupMaps WHERE QuoteId = @QuoteId AND GroupId = @GroupId";
        cmd.Parameters.AddWithValue("@QuoteId", quoteId);
        cmd.Parameters.AddWithValue("@GroupId", groupId);
        cmd.ExecuteNonQuery();
    }

    public List<int> GetQuoteIdsInGroup(int groupId)
    {
        var ids = new List<int>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT QuoteId FROM QuoteGroupMaps WHERE GroupId = @GroupId";
        cmd.Parameters.AddWithValue("@GroupId", groupId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    // ── Screenshots ────────────────────────────────────

    public List<Screenshot> GetScreenshots(int quoteId)
    {
        var list = new List<Screenshot>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT Id, QuoteId, FilePath, SortOrder FROM Screenshots WHERE QuoteId = @Q ORDER BY SortOrder";
        cmd.Parameters.AddWithValue("@Q", quoteId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Screenshot { Id = r.GetInt32(0), QuoteId = r.GetInt32(1), FilePath = r.GetString(2), SortOrder = r.GetInt32(3) });
        return list;
    }

    public int GetNextScreenshotOrder(int quoteId)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM Screenshots WHERE QuoteId = @Q";
        cmd.Parameters.AddWithValue("@Q", quoteId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void AddScreenshot(int quoteId, string filePath, int sortOrder)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "INSERT INTO Screenshots (QuoteId, FilePath, SortOrder) VALUES (@Q, @F, @S)";
        cmd.Parameters.AddWithValue("@Q", quoteId);
        cmd.Parameters.AddWithValue("@F", filePath);
        cmd.Parameters.AddWithValue("@S", sortOrder);
        cmd.ExecuteNonQuery();
    }

    public void DeleteScreenshot(int id)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM Screenshots WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}
