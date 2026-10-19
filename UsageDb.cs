using Dapper;
using Microsoft.Data.Sqlite;

namespace EofBot;

// Source: chat | bench | eval. UserName is only set for chat
public sealed record UsageRow(string Source, string? UserName, long Tokens, string CreatedAt);

public sealed record QuotaRow(string UserName, long DailyQuestions);

// Used = questions asked today
public sealed record UserQuota(long Used, long Limit)
{
    public bool Exceeded => Used >= Limit;
}

// Reads token usage recorded in qa_logs, test_results, evals (created by ChatDb/BenchDb/EvalDb) and stores per-user daily question quotas
public sealed class UsageDb
{
    public const long DefaultDailyQuestions = 30;

    // The default quota row; other rows are per-user overrides
    private const string DefaultKey = "*";

    private readonly string _connectionString;

    public UsageDb(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = Open();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS quotas (
                user_name       TEXT PRIMARY KEY,
                daily_questions INTEGER NOT NULL,
                updated_at      TEXT NOT NULL
            );
            INSERT OR IGNORE INTO quotas (user_name, daily_questions, updated_at) VALUES (@key, @questions, @now);
            """, new { key = DefaultKey, questions = DefaultDailyQuestions, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // test_results only has the bot's tokens; benchmark judge tokens are not stored yet
    public async Task<IEnumerable<UsageRow>> ListSinceAsync(DateTimeOffset since)
    {
        await using var conn = Open();
        return await conn.QueryAsync<UsageRow>("""
            SELECT 'chat' AS Source, user_name AS UserName,
                   COALESCE(input_tokens, 0) + COALESCE(output_tokens, 0) AS Tokens, created_at AS CreatedAt
            FROM qa_logs WHERE created_at >= @since
            UNION ALL
            SELECT 'bench', NULL, COALESCE(input_tokens, 0) + COALESCE(output_tokens, 0), created_at
            FROM test_results WHERE created_at >= @since
            UNION ALL
            SELECT 'eval', NULL, COALESCE(input_tokens, 0) + COALESCE(output_tokens, 0), created_at
            FROM evals WHERE created_at >= @since
            """, new { since = since.ToUniversalTime().ToString("O") });
    }

    public async Task<(long Default, Dictionary<string, long> Users)> GetQuotasAsync()
    {
        await using var conn = Open();
        var rows = (await conn.QueryAsync<QuotaRow>("SELECT user_name AS UserName, daily_questions AS DailyQuestions FROM quotas")).ToList();
        return (rows.Single(r => r.UserName == DefaultKey).DailyQuestions,
            rows.Where(r => r.UserName != DefaultKey).ToDictionary(r => r.UserName, r => r.DailyQuestions));
    }

    public async Task<UserQuota> GetUserQuotaAsync(string userName)
    {
        await using var conn = Open();
        long used = await conn.ExecuteScalarAsync<long>("""
            SELECT COUNT(*) FROM qa_logs WHERE user_name = @userName AND created_at >= @since
            """, new { userName, since = AdminUsage.VnDayStart(DateTimeOffset.UtcNow).ToUniversalTime().ToString("O") });
        long limit = await conn.ExecuteScalarAsync<long>("""
            SELECT daily_questions FROM quotas WHERE user_name IN (@userName, @key)
            ORDER BY user_name = @key LIMIT 1
            """, new { userName, key = DefaultKey });
        return new UserQuota(used, limit);
    }

    public async Task SetDefaultAsync(long questions) => await SetAsync(DefaultKey, questions);

    // questions null = remove the override, back to the default
    public async Task SetUserAsync(string userName, long? questions)
    {
        if (questions is { } q) await SetAsync(userName, q);
        else
        {
            await using var conn = Open();
            await conn.ExecuteAsync("DELETE FROM quotas WHERE user_name = @userName", new { userName });
        }
    }

    private async Task SetAsync(string userName, long questions)
    {
        await using var conn = Open();
        await conn.ExecuteAsync("""
            INSERT INTO quotas (user_name, daily_questions, updated_at) VALUES (@userName, @questions, @now)
            ON CONFLICT(user_name) DO UPDATE SET daily_questions = excluded.daily_questions, updated_at = excluded.updated_at
            """, new { userName, questions, now = DateTimeOffset.UtcNow.ToString("O") });
    }
}
