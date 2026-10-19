using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace EofBot.Eval;

public sealed record TurnRow(string Question, string Answer, string DocHash, IReadOnlyList<TurnImage>? Images = null);

public sealed record TurnImage(long Id, string MediaType, byte[] Data);

// Pass = null means the criterion does not apply to this session
public sealed record CriterionScore(int Criterion, bool? Pass, string Reason);

public sealed record SessionEval(
    string SessionId, int Turns, string DocHash, string Model, string Rationale,
    IReadOnlyList<CriterionScore> Scores, double Score, long? InputTokens, long? OutputTokens);

public sealed record EvalSummary(long Id, string SessionId, string UserName, string SessionAt, string FirstQuestion, double Score, long Turns);

public sealed record FailedScore(long EvalId, long Criterion, string Reason);

public sealed record EvalDetail(long Id, string SessionId, string UserName, string SessionAt, string DocHash, string Model,
    string Source, double Score, string Rationale, string CreatedAt);

public sealed record ScoreRow(long Criterion, long? Pass, string Reason);

public sealed class EvalDb
{
    public const string AutoSource = "auto";
    public const string ManualSource = "manual";

    private readonly string _connectionString;

    public EvalDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        using var conn = Open();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS evals (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id    TEXT NOT NULL REFERENCES sessions(id),
                turns         INTEGER NOT NULL,
                doc_hash      TEXT NOT NULL,
                model         TEXT NOT NULL,
                rationale     TEXT NOT NULL,
                score         REAL NOT NULL,
                input_tokens  INTEGER,
                output_tokens INTEGER,
                created_at    TEXT NOT NULL,
                source        TEXT NOT NULL DEFAULT 'manual'
            );
            CREATE INDEX IF NOT EXISTS ix_evals_session ON evals(session_id);
            CREATE TABLE IF NOT EXISTS eval_scores (
                eval_id   INTEGER NOT NULL REFERENCES evals(id),
                criterion INTEGER NOT NULL,
                pass      INTEGER,
                reason    TEXT NOT NULL,
                PRIMARY KEY (eval_id, criterion)
            );
            CREATE TABLE IF NOT EXISTS doc_snapshots (
                hash       TEXT PRIMARY KEY,
                files_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """);

        // evals tables created before automatic grading lack this column
        if (!conn.Query<string>("SELECT name FROM pragma_table_info('evals')").Contains("source"))
        {
            conn.Execute("ALTER TABLE evals ADD COLUMN source TEXT NOT NULL DEFAULT 'manual'");
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // qa_logs only stores the hash; keep each document version's content to grade sessions against the version the bot used
    public void SaveSnapshot(DocSnapshot s)
    {
        using var conn = Open();
        conn.Execute("INSERT OR IGNORE INTO doc_snapshots (hash, files_json, created_at) VALUES (@Hash, @json, @now)",
            new { s.Hash, json = JsonSerializer.Serialize(s.Files), now = DateTimeOffset.UtcNow.ToString("O") });
    }

    public async Task<DocSnapshot?> GetSnapshotAsync(string hash)
    {
        await using var conn = Open();
        string? json = await conn.ExecuteScalarAsync<string?>("SELECT files_json FROM doc_snapshots WHERE hash = @hash", new { hash });
        return json is null ? null : DocStore.Build(JsonSerializer.Deserialize<List<DocFile>>(json)!);
    }

    private sealed record LogRow(long Id, string Question, string Answer, string DocHash);

    private sealed record LogImage(long Id, long QaLogId, string MediaType, byte[] Data);

    public async Task<List<TurnRow>> GetTurnsAsync(string sessionId)
    {
        await using var conn = Open();
        var logs = (await conn.QueryAsync<LogRow>("""
            SELECT id AS Id, question AS Question, answer AS Answer, doc_hash AS DocHash
            FROM qa_logs WHERE session_id = @sessionId ORDER BY id
            """, new { sessionId })).ToList();
        var images = (await conn.QueryAsync<LogImage>("""
            SELECT i.id AS Id, i.qa_log_id AS QaLogId, i.media_type AS MediaType, i.data AS Data
            FROM qa_images i JOIN qa_logs l ON l.id = i.qa_log_id WHERE l.session_id = @sessionId ORDER BY i.id
            """, new { sessionId })).ToLookup(i => i.QaLogId);
        return logs.Select(l => new TurnRow(l.Question, l.Answer, l.DocHash,
            images[l.Id].Select(i => new TurnImage(i.Id, i.MediaType, i.Data)).ToList())).ToList();
    }

    // Only gradable sessions: ungraded, idle, a single document version that is still stored
    public async Task<List<string>> PickSessionsAsync(int n, string excludeUser, DateTimeOffset idleBefore)
    {
        await using var conn = Open();
        return (await conn.QueryAsync<string>("""
            SELECT s.id FROM sessions s JOIN qa_logs l ON l.session_id = s.id
            WHERE s.user_name <> @excludeUser AND s.updated_at < @idleBefore
              AND NOT EXISTS (SELECT 1 FROM evals e WHERE e.session_id = s.id)
            GROUP BY s.id
            HAVING COUNT(DISTINCT l.doc_hash) = 1 AND MIN(l.doc_hash) IN (SELECT hash FROM doc_snapshots)
            ORDER BY random() LIMIT @n
            """, new { n, excludeUser, idleBefore = idleBefore.ToUniversalTime().ToString("O") })).ToList();
    }

    public async Task<bool> HasEvalSinceAsync(string source, DateTimeOffset since)
    {
        await using var conn = Open();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM evals WHERE source = @source AND created_at >= @since",
            new { source, since = since.ToUniversalTime().ToString("O") }) > 0;
    }

    // For sessions graded several times take the latest; the timestamp is the chat time, not the grading time
    public async Task<List<EvalSummary>> ListLatestAsync(string excludeUser, DateTimeOffset since)
    {
        await using var conn = Open();
        return (await conn.QueryAsync<EvalSummary>("""
            SELECT e.id AS Id, e.session_id AS SessionId, s.user_name AS UserName, s.updated_at AS SessionAt,
                   (SELECT question FROM qa_logs WHERE session_id = e.session_id ORDER BY id LIMIT 1) AS FirstQuestion,
                   e.score AS Score, e.turns AS Turns
            FROM evals e JOIN sessions s ON s.id = e.session_id
            WHERE e.id IN (SELECT MAX(id) FROM evals GROUP BY session_id)
              AND s.user_name <> @excludeUser AND s.updated_at >= @since
            ORDER BY s.updated_at DESC
            """, new { excludeUser, since = since.ToUniversalTime().ToString("O") })).ToList();
    }

    public async Task<List<FailedScore>> ListFailedScoresAsync(IEnumerable<long> evalIds)
    {
        await using var conn = Open();
        return (await conn.QueryAsync<FailedScore>("""
            SELECT eval_id AS EvalId, criterion AS Criterion, reason AS Reason
            FROM eval_scores WHERE pass = 0 AND eval_id IN @evalIds ORDER BY eval_id, criterion
            """, new { evalIds })).ToList();
    }

    public async Task<(EvalDetail Eval, List<ScoreRow> Scores)?> GetAsync(long id)
    {
        await using var conn = Open();
        var eval = await conn.QuerySingleOrDefaultAsync<EvalDetail>("""
            SELECT e.id AS Id, e.session_id AS SessionId, s.user_name AS UserName, s.updated_at AS SessionAt,
                   e.doc_hash AS DocHash, e.model AS Model, e.source AS Source, e.score AS Score,
                   e.rationale AS Rationale, e.created_at AS CreatedAt
            FROM evals e JOIN sessions s ON s.id = e.session_id WHERE e.id = @id
            """, new { id });
        if (eval is null) return null;
        var scores = (await conn.QueryAsync<ScoreRow>(
            "SELECT criterion AS Criterion, pass AS Pass, reason AS Reason FROM eval_scores WHERE eval_id = @id ORDER BY criterion",
            new { id })).ToList();
        return (eval, scores);
    }

    public async Task<long> AddAsync(SessionEval e, string source)
    {
        await using var conn = Open();
        await using var tx = conn.BeginTransaction();
        long id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO evals (session_id, turns, doc_hash, model, rationale, score, input_tokens, output_tokens, created_at, source)
            VALUES (@SessionId, @Turns, @DocHash, @Model, @Rationale, @Score, @InputTokens, @OutputTokens, @now, @source) RETURNING id
            """, new { e.SessionId, e.Turns, e.DocHash, e.Model, e.Rationale, e.Score, e.InputTokens, e.OutputTokens,
                       now = DateTimeOffset.UtcNow.ToString("O"), source }, tx);
        foreach (var s in e.Scores)
        {
            await conn.ExecuteAsync(
                "INSERT INTO eval_scores (eval_id, criterion, pass, reason) VALUES (@id, @Criterion, @Pass, @Reason)",
                new { id, s.Criterion, s.Pass, s.Reason }, tx);
        }
        await tx.CommitAsync();
        return id;
    }
}
