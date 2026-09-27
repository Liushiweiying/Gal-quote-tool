using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace RelinkTool;

/// <summary>
/// 重新关联截图路径：把数据库里"文件已经不存在"的截图路径，
/// 按**同名文件**改指到新的截图目录。
/// 用法：relink &lt;db&gt; &lt;新截图目录&gt; [--apply]
/// 不带 --apply 只试算（dry-run），打印会改多少条。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: relink <quotes.db> <新截图目录> [--apply]");
            return 1;
        }
        var db = args[0];
        var newDir = args[1];
        var apply = args.Contains("--apply");
        if (!Directory.Exists(newDir)) { Console.WriteLine("新截图目录不存在: " + newDir); return 1; }

        using var conn = new SqliteConnection($"Data Source={db}");
        conn.Open();

        // 建立"文件名 → 新路径"索引（大小写不敏感）
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(newDir))
            byName[Path.GetFileName(f)] = f;
        Console.WriteLine($"新目录里 {byName.Count} 个文件；模式 = {(apply ? "APPLY（真的改）" : "dry-run（只看）")}");

        int shotsChanged = 0, shotsOk = 0, shotsMissing = 0;
        var shotUpdates = new List<(long id, string path)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, FilePath FROM Screenshots;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                var p = r.IsDBNull(1) ? "" : r.GetString(1);
                if (p.Length > 0 && File.Exists(p)) { shotsOk++; continue; }
                var name = Path.GetFileName(p ?? "");
                if (name.Length > 0 && byName.TryGetValue(name, out var np)) { shotUpdates.Add((id, np)); shotsChanged++; }
                else shotsMissing++;
            }
        }

        int quoteChanged = 0, quoteOk = 0, quoteMissing = 0;
        var quoteUpdates = new List<(long id, string path)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, ScreenshotPath FROM Quotes WHERE ScreenshotPath IS NOT NULL AND ScreenshotPath <> '';";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                var p = r.GetString(1);
                if (File.Exists(p)) { quoteOk++; continue; }
                var name = Path.GetFileName(p);
                if (name.Length > 0 && byName.TryGetValue(name, out var np)) { quoteUpdates.Add((id, np)); quoteChanged++; }
                else quoteMissing++;
            }
        }

        Console.WriteLine($"Screenshots 表：已有效 {shotsOk}，可修复 {shotsChanged}，找不到文件 {shotsMissing}");
        Console.WriteLine($"Quotes.ScreenshotPath：已有效 {quoteOk}，可修复 {quoteChanged}，找不到文件 {quoteMissing}");
        foreach (var (id, path) in shotUpdates.Take(3)) Console.WriteLine($"   例：Screenshots #{id} → {path}");
        foreach (var (id, path) in quoteUpdates.Take(2)) Console.WriteLine($"   例：Quotes #{id} → {path}");

        if (!apply) { Console.WriteLine("（dry-run，未改动数据库）"); return 0; }

        using var tx = conn.BeginTransaction();
        foreach (var (id, path) in shotUpdates)
        {
            using var c = conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "UPDATE Screenshots SET FilePath = $p WHERE Id = $id;";
            c.Parameters.AddWithValue("$p", path);
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
        foreach (var (id, path) in quoteUpdates)
        {
            using var c = conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "UPDATE Quotes SET ScreenshotPath = $p WHERE Id = $id;";
            c.Parameters.AddWithValue("$p", path);
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
        tx.Commit();
        Console.WriteLine($"已提交：Screenshots 改 {shotUpdates.Count} 条，Quotes 改 {quoteUpdates.Count} 条");
        return 0;
    }
}
