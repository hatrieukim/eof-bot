namespace EofBot.Eval;

public sealed record EvalOutcome(string SessionId, long? EvalId, SessionEval? Result, string? Error);

// Each day, grade a random sample of ungraded real chat sessions instead of all of them, to save tokens
public sealed class EvalSampler(
    EvalDb db, SessionJudge judge, DocStore docs, IConfiguration config, ILogger<EvalSampler> logger) : BackgroundService
{
    // Admin test chats on draft prompts do not reflect the live bot
    public const string AdminTestUser = "admin-thử";

    // A session that just got a question may continue; grading too early misses turns
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

    private int PerDay => int.TryParse(config["EvalPerDay"], out int n) && n >= 0 ? n : 5;

    private DateTime? _lastRun;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (PerDay == 0) return;
        await Task.Yield();

        // Check hourly instead of at a fixed time, so a restart at any time of day does not skip that day's round
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RunDailyAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Lỗi khi bốc mẫu chấm session");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task RunDailyAsync(CancellationToken ct)
    {
        var today = DateTime.Today;
        if (_lastRun == today || await db.HasEvalSinceAsync(EvalDb.AutoSource, new DateTimeOffset(today))) return;
        // Set before running: failed sessions get resampled tomorrow, not retried every hour
        _lastRun = today;

        var outcomes = await SampleAsync(PerDay, EvalDb.AutoSource, ct);
        logger.LogInformation("Chấm mẫu hằng ngày: {Done}/{Total} session xong", outcomes.Count(o => o.Error is null), outcomes.Count);
    }

    public async Task<List<EvalOutcome>> SampleAsync(int n, string source, CancellationToken ct)
    {
        var ids = await db.PickSessionsAsync(n, AdminTestUser, DateTimeOffset.UtcNow - Idle);
        var outcomes = new List<EvalOutcome>();
        foreach (string id in ids)
        {
            try
            {
                outcomes.Add(await EvaluateAsync(id, source, ct));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Lỗi khi chấm session {SessionId}", id);
                outcomes.Add(new(id, null, null, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }
        return outcomes;
    }

    public async Task<EvalOutcome> EvaluateAsync(string sessionId, string source, CancellationToken ct)
    {
        var turns = await db.GetTurnsAsync(sessionId);
        if (turns.Count == 0) return new(sessionId, null, null, $"Không có session {sessionId}");

        // Grade against the document version the bot answered from; another version makes criteria 3–6 meaningless
        var hashes = turns.Select(t => t.DocHash).Distinct().ToList();
        if (hashes.Count > 1) return new(sessionId, null, null, "Session này trải qua nhiều bản tài liệu, không chấm được");
        var snapshot = docs.Current.Hash == hashes[0] ? docs.Current : await db.GetSnapshotAsync(hashes[0]);
        if (snapshot is null) return new(sessionId, null, null, $"Không còn lưu bản tài liệu {hashes[0]} mà session này dùng");

        var result = await judge.JudgeAsync(sessionId, turns, snapshot, ct);
        long evalId = await db.AddAsync(result, source);
        return new(sessionId, evalId, result, null);
    }
}
