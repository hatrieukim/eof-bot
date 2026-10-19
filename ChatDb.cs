using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace EofBot;

public sealed record SessionRow(string Id, string UserName, string Title, string SessionJson, string CreatedAt, string UpdatedAt);

public sealed record SessionSummary(string Id, string Title, string UpdatedAt);

public sealed record MessageRow(string Question, string Answer, string CreatedAt, [property: JsonIgnore] string? ImageIds)
{
    public string[] Images => ImageIds is null ? [] : ImageIds.Split(',').Select(ChatDb.ImageUrl).ToArray();
}

public sealed record StoredImage(string UserName, string MediaType, byte[] Data);

public sealed record QaLog(
    string SessionId, string UserName, string Question, string Answer, string? Citations,
    string DocHash, string Model, long? InputTokens, long? CachedTokens, long? OutputTokens,
    long LatencyMs, string CreatedAt, long PromptId);

public sealed record PromptVersion(long Id, string Content, string? Note, long IsActive, string CreatedAt);

public sealed class ChatDb
{
    private readonly string _connectionString;

    public ChatDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        using var conn = Open();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS sessions (
                id           TEXT PRIMARY KEY,
                user_name    TEXT NOT NULL,
                title        TEXT NOT NULL,
                session_json TEXT NOT NULL,
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS qa_logs (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id     TEXT NOT NULL REFERENCES sessions(id),
                user_name      TEXT NOT NULL,
                question       TEXT NOT NULL,
                answer         TEXT NOT NULL,
                citations      TEXT,
                doc_hash       TEXT NOT NULL,
                model          TEXT NOT NULL,
                input_tokens   INTEGER,
                cached_tokens  INTEGER,
                output_tokens  INTEGER,
                latency_ms     INTEGER NOT NULL,
                created_at     TEXT NOT NULL,
                prompt_id      INTEGER REFERENCES prompts(id)
            );
            CREATE INDEX IF NOT EXISTS ix_qa_logs_session ON qa_logs(session_id);
            CREATE TABLE IF NOT EXISTS qa_images (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                qa_log_id  INTEGER NOT NULL REFERENCES qa_logs(id),
                media_type TEXT NOT NULL,
                data       BLOB NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_qa_images_log ON qa_images(qa_log_id);
            CREATE TABLE IF NOT EXISTS prompts (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                content    TEXT NOT NULL,
                note       TEXT,
                is_active  INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            """);

        // DBs created before the prompts table existed lack this column in qa_logs
        if (!conn.Query<string>("SELECT name FROM pragma_table_info('qa_logs')").Contains("prompt_id"))
        {
            conn.Execute("ALTER TABLE qa_logs ADD COLUMN prompt_id INTEGER REFERENCES prompts(id)");
        }

        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM prompts") == 0)
        {
            conn.Execute("INSERT INTO prompts (content, note, is_active, created_at) VALUES (@content, @note, 1, @now)",
                new { content = DocAgentFactory.DefaultRules, note = "9 luật ban đầu", now = DateTimeOffset.UtcNow.ToString("O") });
        }
    }

    private const string PromptColumns =
        "id AS Id, content AS Content, note AS Note, is_active AS IsActive, created_at AS CreatedAt";

    public async Task<PromptVersion> GetActivePromptAsync()
    {
        await using var conn = Open();
        return await conn.QuerySingleAsync<PromptVersion>($"SELECT {PromptColumns} FROM prompts WHERE is_active = 1");
    }

    public async Task<PromptVersion?> GetPromptAsync(long id)
    {
        await using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<PromptVersion>($"SELECT {PromptColumns} FROM prompts WHERE id = @id", new { id });
    }

    public async Task<IEnumerable<PromptVersion>> ListPromptsAsync()
    {
        await using var conn = Open();
        return await conn.QueryAsync<PromptVersion>($"SELECT {PromptColumns} FROM prompts ORDER BY id DESC");
    }

    public async Task<long> CreatePromptAsync(string content, string? note)
    {
        await using var conn = Open();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO prompts (content, note, created_at) VALUES (@content, @note, @now) RETURNING id
            """, new { content, note, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    public async Task<bool> UpdatePromptAsync(long id, string content, string? note)
    {
        await using var conn = Open();
        return await conn.ExecuteAsync("UPDATE prompts SET content = @content, note = @note WHERE id = @id",
            new { id, content, note }) > 0;
    }

    // A single UPDATE sets the whole table, so exactly one version is always active
    public async Task<bool> ActivatePromptAsync(long id)
    {
        await using var conn = Open();
        if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM prompts WHERE id = @id", new { id }) == 0) return false;
        await conn.ExecuteAsync("UPDATE prompts SET is_active = (id = @id)", new { id });
        return true;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public async Task<SessionRow?> GetSessionAsync(string id)
    {
        await using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<SessionRow>("""
            SELECT id AS Id, user_name AS UserName, title AS Title, session_json AS SessionJson,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM sessions WHERE id = @id
            """, new { id });
    }

    public async Task SaveSessionAsync(SessionRow s)
    {
        await using var conn = Open();
        await conn.ExecuteAsync("""
            INSERT INTO sessions (id, user_name, title, session_json, created_at, updated_at)
            VALUES (@Id, @UserName, @Title, @SessionJson, @CreatedAt, @UpdatedAt)
            ON CONFLICT(id) DO UPDATE SET session_json = excluded.session_json, updated_at = excluded.updated_at
            """, s);
    }

    public async Task<IEnumerable<SessionSummary>> ListSessionsAsync(string userName)
    {
        await using var conn = Open();
        return await conn.QueryAsync<SessionSummary>("""
            SELECT id AS Id, title AS Title, updated_at AS UpdatedAt
            FROM sessions WHERE user_name = @userName ORDER BY updated_at DESC
            """, new { userName });
    }

    public async Task<IEnumerable<MessageRow>> GetMessagesAsync(string sessionId)
    {
        await using var conn = Open();
        return await conn.QueryAsync<MessageRow>("""
            SELECT question AS Question, answer AS Answer, created_at AS CreatedAt,
                   (SELECT group_concat(id) FROM (SELECT id FROM qa_images WHERE qa_log_id = qa_logs.id ORDER BY id)) AS ImageIds
            FROM qa_logs WHERE session_id = @sessionId ORDER BY id
            """, new { sessionId });
    }

    public static string ImageUrl(string id) => $"/api/images/{id}";

    public async Task AddLogAsync(QaLog log, IReadOnlyList<DataContent> images)
    {
        await using var conn = Open();
        await using var tx = conn.BeginTransaction();
        long logId = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO qa_logs (session_id, user_name, question, answer, citations, doc_hash, model,
                                 input_tokens, cached_tokens, output_tokens, latency_ms, created_at, prompt_id)
            VALUES (@SessionId, @UserName, @Question, @Answer, @Citations, @DocHash, @Model,
                    @InputTokens, @CachedTokens, @OutputTokens, @LatencyMs, @CreatedAt, @PromptId)
            RETURNING id
            """, log, tx);
        foreach (var image in images)
        {
            await conn.ExecuteAsync("INSERT INTO qa_images (qa_log_id, media_type, data) VALUES (@logId, @mediaType, @data)",
                new { logId, mediaType = image.MediaType, data = image.Data.ToArray() }, tx);
        }
        await tx.CommitAsync();
    }

    public async Task<StoredImage?> GetImageAsync(long id)
    {
        await using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<StoredImage>("""
            SELECT l.user_name AS UserName, i.media_type AS MediaType, i.data AS Data
            FROM qa_images i JOIN qa_logs l ON l.id = i.qa_log_id WHERE i.id = @id
            """, new { id });
    }
}
