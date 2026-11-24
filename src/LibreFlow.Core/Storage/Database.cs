using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LibreFlow.Core.Storage;

public sealed class HistoryEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string RawText { get; set; } = "";
    public string Text { get; set; } = "";
    public int Words { get; set; }
    public double Duration { get; set; }
    public string? App { get; set; }
    public string? WindowTitle { get; set; }
    public string? Language { get; set; }
    public string Mode { get; set; } = "ptt";
    /// <summary>ok | raw (cleanup skipped or failed) | empty | failed</summary>
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }
    public int AsrMs { get; set; }
    public int LlmMs { get; set; }
    public int TotalMs { get; set; }
    public string? AudioPath { get; set; }
}

public sealed record DictionaryEntry(long Id, string Phrase, string? ReplaceFrom, bool Starred, string Source);
public sealed record SnippetEntry(long Id, string Trigger, string Expansion);
public sealed record CorrectionRow(long Id, DateTime Timestamp, string Original, string Corrected, string Kind, string? App, bool Undone);
public sealed record CorrectionPair(string Original, string Corrected, int Count);
public sealed record CorrectionStats(long Total, long Today, long ThisWeek, long Undone, IReadOnlyList<CorrectionPair> Top);

/// <summary>All local data. One connection guarded by a lock: volume is tiny and callers come from several threads.</summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public Database(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS history(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                raw_text TEXT NOT NULL,
                text TEXT NOT NULL,
                words INTEGER NOT NULL,
                duration REAL NOT NULL,
                app TEXT, window_title TEXT, language TEXT,
                mode TEXT NOT NULL,
                status TEXT NOT NULL,
                error TEXT,
                asr_ms INTEGER NOT NULL DEFAULT 0,
                llm_ms INTEGER NOT NULL DEFAULT 0,
                total_ms INTEGER NOT NULL DEFAULT 0,
                audio_path TEXT);
            CREATE INDEX IF NOT EXISTS history_ts ON history(ts);
            CREATE TABLE IF NOT EXISTS dictionary(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                phrase TEXT NOT NULL UNIQUE COLLATE NOCASE,
                replace_from TEXT,
                starred INTEGER NOT NULL DEFAULT 0,
                source TEXT NOT NULL DEFAULT 'manual',
                created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS snippets(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                trigger TEXT NOT NULL UNIQUE COLLATE NOCASE,
                expansion TEXT NOT NULL,
                created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS corrections(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                original TEXT NOT NULL,
                corrected TEXT NOT NULL,
                kind TEXT NOT NULL,
                app TEXT,
                undone INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS corrections_ts ON corrections(ts);
            CREATE TABLE IF NOT EXISTS learned(word TEXT PRIMARY KEY COLLATE NOCASE, created TEXT NOT NULL);
            """);
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }

    // ---------- history ----------

    public long AddHistory(HistoryEntry e)
    {
        lock (_gate)
        {
            using var cmd = Cmd("""
                INSERT INTO history(ts, raw_text, text, words, duration, app, window_title, language, mode, status, error, asr_ms, llm_ms, total_ms, audio_path)
                VALUES ($ts, $raw, $text, $words, $dur, $app, $title, $lang, $mode, $status, $error, $asr, $llm, $total, $audio);
                SELECT last_insert_rowid();
                """);
            Bind(cmd, e);
            e.Id = (long)cmd.ExecuteScalar()!;
            return e.Id;
        }
    }

    public void UpdateHistory(HistoryEntry e)
    {
        lock (_gate)
        {
            using var cmd = Cmd("""
                UPDATE history SET ts=$ts, raw_text=$raw, text=$text, words=$words, duration=$dur, app=$app, window_title=$title,
                    language=$lang, mode=$mode, status=$status, error=$error, asr_ms=$asr, llm_ms=$llm, total_ms=$total, audio_path=$audio
                WHERE id=$id
                """);
            Bind(cmd, e);
            cmd.Parameters.AddWithValue("$id", e.Id);
            cmd.ExecuteNonQuery();
        }
    }

    private static void Bind(SqliteCommand cmd, HistoryEntry e)
    {
        cmd.Parameters.AddWithValue("$ts", Iso(e.Timestamp));
        cmd.Parameters.AddWithValue("$raw", e.RawText);
        cmd.Parameters.AddWithValue("$text", e.Text);
        cmd.Parameters.AddWithValue("$words", e.Words);
        cmd.Parameters.AddWithValue("$dur", e.Duration);
        cmd.Parameters.AddWithValue("$app", (object?)e.App ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)e.WindowTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lang", (object?)e.Language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mode", e.Mode);
        cmd.Parameters.AddWithValue("$status", e.Status);
        cmd.Parameters.AddWithValue("$error", (object?)e.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$asr", e.AsrMs);
        cmd.Parameters.AddWithValue("$llm", e.LlmMs);
        cmd.Parameters.AddWithValue("$total", e.TotalMs);
        cmd.Parameters.AddWithValue("$audio", (object?)e.AudioPath ?? DBNull.Value);
    }

    public List<HistoryEntry> GetHistory(int limit, int offset, string? query = null)
    {
        lock (_gate)
        {
            using var cmd = Cmd($"""
                SELECT id, ts, raw_text, text, words, duration, app, window_title, language, mode, status, error, asr_ms, llm_ms, total_ms, audio_path
                FROM history {Where(query)} ORDER BY ts DESC, id DESC LIMIT $limit OFFSET $offset
                """);
            if (!string.IsNullOrWhiteSpace(query)) cmd.Parameters.AddWithValue("$q", "%" + query.Trim() + "%");
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);
            using var r = cmd.ExecuteReader();
            var list = new List<HistoryEntry>();
            while (r.Read()) list.Add(ReadHistory(r));
            return list;
        }
    }

    public int CountHistory(string? query = null)
    {
        lock (_gate)
        {
            using var cmd = Cmd($"SELECT COUNT(*) FROM history {Where(query)}");
            if (!string.IsNullOrWhiteSpace(query)) cmd.Parameters.AddWithValue("$q", "%" + query.Trim() + "%");
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    private static string Where(string? query) =>
        string.IsNullOrWhiteSpace(query) ? "" : "WHERE text LIKE $q OR raw_text LIKE $q OR app LIKE $q";

    public HistoryEntry? GetHistoryById(long id) => One("WHERE id=$v", id);

    public HistoryEntry? LastSuccessful() => One("WHERE status IN ('ok','raw') AND $v=$v ORDER BY ts DESC, id DESC LIMIT 1", 1);

    private HistoryEntry? One(string where, object value)
    {
        lock (_gate)
        {
            using var cmd = Cmd($"""
                SELECT id, ts, raw_text, text, words, duration, app, window_title, language, mode, status, error, asr_ms, llm_ms, total_ms, audio_path
                FROM history {where}
                """);
            cmd.Parameters.AddWithValue("$v", value);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadHistory(r) : null;
        }
    }

    private static HistoryEntry ReadHistory(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Timestamp = ParseIso(r.GetString(1)),
        RawText = r.GetString(2),
        Text = r.GetString(3),
        Words = r.GetInt32(4),
        Duration = r.GetDouble(5),
        App = r.IsDBNull(6) ? null : r.GetString(6),
        WindowTitle = r.IsDBNull(7) ? null : r.GetString(7),
        Language = r.IsDBNull(8) ? null : r.GetString(8),
        Mode = r.GetString(9),
        Status = r.GetString(10),
        Error = r.IsDBNull(11) ? null : r.GetString(11),
        AsrMs = r.GetInt32(12),
        LlmMs = r.GetInt32(13),
        TotalMs = r.GetInt32(14),
        AudioPath = r.IsDBNull(15) ? null : r.GetString(15),
    };

    public void DeleteHistory(long id) => Exec("DELETE FROM history WHERE id=$a", id);

    public void ClearHistory() => Exec("DELETE FROM history");

    /// <summary>Audio files kept for retry, newest first.</summary>
    public List<(long Id, string Path)> AudioFiles()
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT id, audio_path FROM history WHERE audio_path IS NOT NULL ORDER BY ts DESC, id DESC");
            using var r = cmd.ExecuteReader();
            var list = new List<(long, string)>();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
            return list;
        }
    }

    public void ClearAudioPath(long id) => Exec("UPDATE history SET audio_path=NULL WHERE id=$a", id);

    /// <summary>Successful dictations only, in local time, for the Insights numbers.</summary>
    public List<DictationRow> GetDictationRows()
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT ts, words, duration, app FROM history WHERE status IN ('ok','raw')");
            using var r = cmd.ExecuteReader();
            var list = new List<DictationRow>();
            while (r.Read())
                list.Add(new DictationRow(ParseIso(r.GetString(0)).ToLocalTime(), r.GetInt32(1), r.GetDouble(2), r.IsDBNull(3) ? null : r.GetString(3)));
            return list;
        }
    }

    // ---------- dictionary ----------

    public List<DictionaryEntry> GetDictionary()
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT id, phrase, replace_from, starred, source FROM dictionary ORDER BY starred DESC, phrase COLLATE NOCASE");
            using var r = cmd.ExecuteReader();
            var list = new List<DictionaryEntry>();
            while (r.Read())
                list.Add(new DictionaryEntry(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3) != 0, r.GetString(4)));
            return list;
        }
    }

    /// <summary>Adds a word, or updates the existing one with the same spelling (case-insensitive).</summary>
    public void SaveDictionaryEntry(long id, string phrase, string? replaceFrom, bool starred, string source = "manual")
    {
        phrase = phrase.Trim();
        if (phrase.Length == 0) return;
        replaceFrom = string.IsNullOrWhiteSpace(replaceFrom) ? null : replaceFrom.Trim();
        if (id > 0)
        {
            Exec("UPDATE OR REPLACE dictionary SET phrase=$a, replace_from=$b, starred=$c WHERE id=$d", phrase, replaceFrom, starred ? 1 : 0, id);
            return;
        }
        Exec("""
            INSERT INTO dictionary(phrase, replace_from, starred, source, created) VALUES ($a, $b, $c, $d, $e)
            ON CONFLICT(phrase) DO UPDATE SET replace_from=excluded.replace_from, starred=excluded.starred
            """, phrase, replaceFrom, starred ? 1 : 0, source, Iso(DateTime.UtcNow));
    }

    public void DeleteDictionaryEntry(long id) => Exec("DELETE FROM dictionary WHERE id=$a", id);

    // ---------- snippets ----------

    public List<SnippetEntry> GetSnippets()
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT id, trigger, expansion FROM snippets ORDER BY trigger COLLATE NOCASE");
            using var r = cmd.ExecuteReader();
            var list = new List<SnippetEntry>();
            while (r.Read()) list.Add(new SnippetEntry(r.GetInt64(0), r.GetString(1), r.GetString(2)));
            return list;
        }
    }

    public void SaveSnippet(long id, string trigger, string expansion)
    {
        trigger = trigger.Trim();
        if (trigger.Length == 0 || expansion.Length == 0) return;
        if (id > 0)
        {
            Exec("UPDATE OR REPLACE snippets SET trigger=$a, expansion=$b WHERE id=$c", trigger, expansion, id);
            return;
        }
        Exec("""
            INSERT INTO snippets(trigger, expansion, created) VALUES ($a, $b, $c)
            ON CONFLICT(trigger) DO UPDATE SET expansion=excluded.expansion
            """, trigger, expansion, Iso(DateTime.UtcNow));
    }

    public void DeleteSnippet(long id) => Exec("DELETE FROM snippets WHERE id=$a", id);

    // ---------- autocorrect ----------

    public void AddCorrection(string original, string corrected, string kind, string? app) =>
        Exec("INSERT INTO corrections(ts, original, corrected, kind, app) VALUES ($a, $b, $c, $d, $e)",
            Iso(DateTime.UtcNow), original, corrected, kind, app);

    public void MarkCorrectionUndone(string original, string corrected) =>
        Exec("""
            UPDATE corrections SET undone=1 WHERE id=(
                SELECT id FROM corrections WHERE original=$a AND corrected=$b AND undone=0 ORDER BY id DESC LIMIT 1)
            """, original, corrected);

    public List<CorrectionRow> RecentCorrections(int limit)
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT id, ts, original, corrected, kind, app, undone FROM corrections ORDER BY id DESC LIMIT $limit");
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<CorrectionRow>();
            while (r.Read())
                list.Add(new CorrectionRow(r.GetInt64(0), ParseIso(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6) != 0));
            return list;
        }
    }

    public CorrectionStats GetCorrectionStats(DateTime nowLocal)
    {
        var today = Iso(nowLocal.Date.ToUniversalTime());
        var week = Iso(InsightsCalculator.WeekStart(nowLocal.Date).ToUniversalTime());
        lock (_gate)
        {
            long Count(string where, string? arg = null)
            {
                using var c = Cmd($"SELECT COUNT(*) FROM corrections WHERE {where}");
                if (arg != null) c.Parameters.AddWithValue("$a", arg);
                return (long)c.ExecuteScalar()!;
            }

            var top = new List<CorrectionPair>();
            using (var cmd = Cmd("""
                SELECT lower(original), lower(corrected), COUNT(*) n FROM corrections
                WHERE undone=0 AND kind<>'Capitalization' GROUP BY 1, 2 ORDER BY n DESC, 1 LIMIT 8
                """))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) top.Add(new CorrectionPair(r.GetString(0), r.GetString(1), r.GetInt32(2)));

            return new CorrectionStats(Count("undone=0"), Count("undone=0 AND ts>=$a", today), Count("undone=0 AND ts>=$a", week), Count("undone=1"), top);
        }
    }

    public void ClearCorrections() => Exec("DELETE FROM corrections");

    public List<string> GetLearned()
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT word FROM learned ORDER BY word");
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public void AddLearned(string word) =>
        Exec("INSERT OR IGNORE INTO learned(word, created) VALUES ($a, $b)", word.Trim().ToLowerInvariant(), Iso(DateTime.UtcNow));

    public void RemoveLearned(string word) => Exec("DELETE FROM learned WHERE word=$a", word);

    // ---------- helpers ----------

    private SqliteCommand Cmd(string sql)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    /// <summary>Runs a statement with positional arguments bound to $a, $b, $c…</summary>
    private void Exec(string sql, params object?[] args)
    {
        lock (_gate)
        {
            using var cmd = Cmd(sql);
            for (var i = 0; i < args.Length; i++)
                cmd.Parameters.AddWithValue("$" + (char)('a' + i), args[i] ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTime ParseIso(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
