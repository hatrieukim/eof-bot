using Dapper;
using Microsoft.Data.Sqlite;

namespace EofBot.Bench;

public sealed record TestResultRow(
    long RunId, string Qid, string Kind, string Question, string Expected, string Sources,
    string? Answer, long? InputTokens, long? CachedTokens, long? OutputTokens, long? LatencyMs,
    string? Error, string? CiteCheck, string? Grade, string? GradeReason, string CreatedAt,
    double? Score = null, string? CriteriaJson = null);

// A class with setters instead of a record because aggregate columns (SUM, AVG) have no fixed type; Dapper converts via setters
public sealed class TestRunSummary
{
    public long Id { get; set; }
    public long PromptId { get; set; }
    public string PromptHash { get; set; } = "";
    public string DocHash { get; set; } = "";
    public string Tier { get; set; } = "";
    public string Model { get; set; } = "";
    public string Scale { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Error { get; set; }
    public long Total { get; set; }
    public string StartedAt { get; set; } = "";
    public string? FinishedAt { get; set; }
    public long Done { get; set; }
    public long Dung { get; set; }
    public long MotPhan { get; set; }
    public long Sai { get; set; }
    public long Bia { get; set; }
    public long Loi { get; set; }
    public long Perfect { get; set; }
    public long Failed { get; set; }
    public double? Score { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public double? AvgLatencyMs { get; set; }

    // Not in the DB: null if the score is still valid, otherwise why it is stale
    public string? Stale { get; set; }
}

public sealed class BenchDb
{
    // Runs before the switch to 8 criteria used 4 grades (correct/partial/wrong/fabricated); scores are not comparable
    public const string GradeScale = "grade4", CriteriaScale = "criteria8";

    private readonly string _connectionString;

    public BenchDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        using var conn = Open();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS test_runs (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                prompt_id   INTEGER NOT NULL REFERENCES prompts(id),
                prompt_hash TEXT NOT NULL,
                doc_hash    TEXT NOT NULL,
                tier        TEXT NOT NULL,
                model       TEXT NOT NULL,
                status      TEXT NOT NULL,
                error       TEXT,
                total       INTEGER NOT NULL,
                started_at  TEXT NOT NULL,
                finished_at TEXT
            );
            CREATE TABLE IF NOT EXISTS test_results (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id        INTEGER NOT NULL REFERENCES test_runs(id),
                qid           TEXT NOT NULL,
                kind          TEXT NOT NULL,
                question      TEXT NOT NULL,
                expected      TEXT NOT NULL,
                sources       TEXT NOT NULL,
                answer        TEXT,
                input_tokens  INTEGER,
                cached_tokens INTEGER,
                output_tokens INTEGER,
                latency_ms    INTEGER,
                error         TEXT,
                cite_check    TEXT,
                grade         TEXT,
                grade_reason  TEXT,
                created_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_test_results_run ON test_results(run_id);
            CREATE TABLE IF NOT EXISTS bench_questions (
                id         TEXT PRIMARY KEY,
                ord        INTEGER NOT NULL,
                kind       TEXT NOT NULL,
                question   TEXT NOT NULL,
                expected   TEXT NOT NULL,
                sources    TEXT NOT NULL,
                nhanh      INTEGER NOT NULL,
                trung      INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );
            """);

        // DBs created before 8-criteria grading lack these columns
        if (!conn.Query<string>("SELECT name FROM pragma_table_info('test_runs')").Contains("scale"))
            conn.Execute($"ALTER TABLE test_runs ADD COLUMN scale TEXT NOT NULL DEFAULT '{GradeScale}'");
        if (!conn.Query<string>("SELECT name FROM pragma_table_info('test_results')").Contains("score"))
            conn.Execute("ALTER TABLE test_results ADD COLUMN score REAL; ALTER TABLE test_results ADD COLUMN criteria_json TEXT");

        // If the app stopped mid-run, that run will never finish
        conn.Execute("UPDATE test_runs SET status = 'failed', error = 'App khởi động lại khi đang chạy' WHERE status = 'running'");
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public async Task<long> CreateRunAsync(long promptId, string promptHash, string docHash, string tier, string model, int total)
    {
        await using var conn = Open();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO test_runs (prompt_id, prompt_hash, doc_hash, tier, model, scale, status, total, started_at)
            VALUES (@promptId, @promptHash, @docHash, @tier, @model, @scale, 'running', @total, @now) RETURNING id
            """, new { promptId, promptHash, docHash, tier, model, scale = CriteriaScale, total, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    public async Task AddResultAsync(TestResultRow r)
    {
        await using var conn = Open();
        await conn.ExecuteAsync("""
            INSERT INTO test_results (run_id, qid, kind, question, expected, sources, answer, input_tokens, cached_tokens,
                                      output_tokens, latency_ms, error, cite_check, grade, grade_reason, created_at,
                                      score, criteria_json)
            VALUES (@RunId, @Qid, @Kind, @Question, @Expected, @Sources, @Answer, @InputTokens, @CachedTokens,
                    @OutputTokens, @LatencyMs, @Error, @CiteCheck, @Grade, @GradeReason, @CreatedAt,
                    @Score, @CriteriaJson)
            """, r);
    }

    public async Task FinishRunAsync(long id, string status, string? error)
    {
        await using var conn = Open();
        await conn.ExecuteAsync("UPDATE test_runs SET status = @status, error = @error, finished_at = @now WHERE id = @id",
            new { id, status, error, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    // 8-criteria scale: score = mean per-question score (passed criteria / applicable criteria × 100).
    // Legacy scale: mean (correct 1, partial 0.5, wrong 0, fabricated −1) × 100. Errored questions are excluded
    private const string SummarySql = """
        SELECT r.id AS Id, r.prompt_id AS PromptId, r.prompt_hash AS PromptHash, r.doc_hash AS DocHash, r.tier AS Tier,
               r.model AS Model, r.scale AS Scale, r.status AS Status, r.error AS Error, r.total AS Total,
               r.started_at AS StartedAt, r.finished_at AS FinishedAt,
               COUNT(x.id) AS Done,
               COUNT(CASE WHEN x.grade = 'Đúng' THEN 1 END) AS Dung,
               COUNT(CASE WHEN x.grade = 'Đúng một phần' THEN 1 END) AS MotPhan,
               COUNT(CASE WHEN x.grade = 'Sai' THEN 1 END) AS Sai,
               COUNT(CASE WHEN x.grade = 'Bịa' THEN 1 END) AS Bia,
               COUNT(x.error) AS Loi,
               COUNT(CASE WHEN x.score = 100 THEN 1 END) AS Perfect,
               COUNT(CASE WHEN x.score < 100 THEN 1 END) AS Failed,
               CASE WHEN r.scale = 'criteria8' THEN AVG(x.score)
                    ELSE 100.0 * SUM(CASE x.grade WHEN 'Đúng' THEN 1 WHEN 'Đúng một phần' THEN 0.5 WHEN 'Sai' THEN 0 WHEN 'Bịa' THEN -1 END)
                         / COUNT(x.grade) END AS Score,
               SUM(x.input_tokens) AS InputTokens, SUM(x.output_tokens) AS OutputTokens, AVG(x.latency_ms) AS AvgLatencyMs
        FROM test_runs r LEFT JOIN test_results x ON x.run_id = r.id
        """;

    public async Task<IEnumerable<TestRunSummary>> ListRunsAsync(long? promptId)
    {
        await using var conn = Open();
        return await conn.QueryAsync<TestRunSummary>($"""
            {SummarySql}
            WHERE @promptId IS NULL OR r.prompt_id = @promptId
            GROUP BY r.id ORDER BY r.id DESC
            """, new { promptId });
    }

    public async Task<TestRunSummary?> GetRunAsync(long id)
    {
        await using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<TestRunSummary>($"{SummarySql} WHERE r.id = @id GROUP BY r.id", new { id });
    }

    public async Task<IEnumerable<TestResultRow>> GetResultsAsync(long runId)
    {
        await using var conn = Open();
        return await conn.QueryAsync<TestResultRow>("""
            SELECT run_id AS RunId, qid AS Qid, kind AS Kind, question AS Question, expected AS Expected, sources AS Sources,
                   answer AS Answer, input_tokens AS InputTokens, cached_tokens AS CachedTokens, output_tokens AS OutputTokens,
                   latency_ms AS LatencyMs, error AS Error, cite_check AS CiteCheck, grade AS Grade,
                   grade_reason AS GradeReason, created_at AS CreatedAt, score AS Score, criteria_json AS CriteriaJson
            FROM test_results WHERE run_id = @runId ORDER BY id
            """, new { runId });
    }

    // Dapper does not convert SQLite INTEGER to bool when mapping to a record constructor
    private sealed class QuestionRow
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Question { get; set; } = "";
        public string Expected { get; set; } = "";
        public string Sources { get; set; } = "";
        public long Nhanh { get; set; }
        public long Trung { get; set; }
        public BenchQuestion ToQuestion() => new(Id, Kind, Question, Expected, Sources, Nhanh != 0, Trung != 0);
    }

    private const string QuestionSql = """
        SELECT id AS Id, kind AS Kind, question AS Question, expected AS Expected, sources AS Sources,
               nhanh AS Nhanh, trung AS Trung
        FROM bench_questions
        """;

    public async Task<List<BenchQuestion>> ListQuestionsAsync()
    {
        await using var conn = Open();
        return (await conn.QueryAsync<QuestionRow>($"{QuestionSql} ORDER BY ord")).Select(r => r.ToQuestion()).ToList();
    }

    public async Task<BenchQuestion?> GetQuestionAsync(string id)
    {
        await using var conn = Open();
        return (await conn.QuerySingleOrDefaultAsync<QuestionRow>($"{QuestionSql} WHERE id = @id", new { id }))?.ToQuestion();
    }

    private static object Params(BenchQuestion q) => new
    {
        q.Id, q.Kind, q.Question, q.Expected, q.Sources, q.Nhanh, q.Trung, now = DateTimeOffset.UtcNow.ToString("O"),
    };

    // Only runs while the table is empty: the one-time move from the md file to the DB
    public int ImportIfEmpty(Func<List<BenchQuestion>> load)
    {
        using var conn = Open();
        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM bench_questions") > 0) return 0;
        var questions = load();
        using var tx = conn.BeginTransaction();
        int ord = 0;
        foreach (var q in questions)
        {
            conn.Execute("""
                INSERT INTO bench_questions (id, ord, kind, question, expected, sources, nhanh, trung, updated_at)
                VALUES (@Id, @ord, @Kind, @Question, @Expected, @Sources, @Nhanh, @Trung, @now)
                """, new { q.Id, ord = ord++, q.Kind, q.Question, q.Expected, q.Sources, q.Nhanh, q.Trung,
                           now = DateTimeOffset.UtcNow.ToString("O") }, tx);
        }
        tx.Commit();
        return questions.Count;
    }

    // A new question goes after the last one with the same id prefix (e.g. LV-…) to keep groups together
    public async Task<bool> AddQuestionAsync(BenchQuestion q)
    {
        await using var conn = Open();
        await using var tx = conn.BeginTransaction();
        if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM bench_questions WHERE id = @Id", new { q.Id }, tx) > 0)
            return false;
        string prefix = q.Id[..q.Id.IndexOf('-')];
        long ord = await conn.ExecuteScalarAsync<long?>(
            "SELECT MAX(ord) FROM bench_questions WHERE id LIKE @p", new { p = prefix + "-%" }, tx) is { } last
            ? last + 1
            : await conn.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(ord), -1) + 1 FROM bench_questions", transaction: tx);
        await conn.ExecuteAsync("UPDATE bench_questions SET ord = ord + 1 WHERE ord >= @ord", new { ord }, tx);
        await conn.ExecuteAsync("""
            INSERT INTO bench_questions (id, ord, kind, question, expected, sources, nhanh, trung, updated_at)
            VALUES (@Id, @ord, @Kind, @Question, @Expected, @Sources, @Nhanh, @Trung, @now)
            """, new { q.Id, ord, q.Kind, q.Question, q.Expected, q.Sources, q.Nhanh, q.Trung,
                       now = DateTimeOffset.UtcNow.ToString("O") }, tx);
        await tx.CommitAsync();
        return true;
    }

    public async Task<bool> UpdateQuestionAsync(BenchQuestion q)
    {
        await using var conn = Open();
        return await conn.ExecuteAsync("""
            UPDATE bench_questions SET kind = @Kind, question = @Question, expected = @Expected, sources = @Sources,
                                       nhanh = @Nhanh, trung = @Trung, updated_at = @now
            WHERE id = @Id
            """, Params(q)) > 0;
    }

    public async Task<bool> DeleteQuestionAsync(string id)
    {
        await using var conn = Open();
        return await conn.ExecuteAsync("DELETE FROM bench_questions WHERE id = @id", new { id }) > 0;
    }
}
