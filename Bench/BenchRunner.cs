using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EofBot.Eval;
using Microsoft.Agents.AI;

namespace EofBot.Bench;

public sealed class BenchRunner(
    DocStore docs, DocAgentFactory agents, ChatDb chatDb, BenchDb db, SessionJudge judge,
    IConfiguration config, IHostApplicationLifetime lifetime, ILogger<BenchRunner> logger)
{
    private readonly object _lock = new();
    private long? _runningId;
    private CancellationTokenSource? _cts;
    // Only so the admin page can show pending/running questions; finished ones are in the DB
    private List<BenchQuestion> _plan = [];
    private readonly ConcurrentDictionary<string, byte> _active = new();

    // Question groups are independent, so run them in parallel; questions within a follow-up group stay sequential
    private int Parallel => int.TryParse(config["BenchParallel"], out int n) && n > 0 ? n : 3;

    public long? RunningId => _runningId;

    public (IReadOnlyList<BenchQuestion> Plan, ICollection<string> Active)? Progress(long runId) =>
        _runningId == runId ? (_plan, _active.Keys) : null;

    public async Task<Dictionary<string, int>> TierCountsAsync()
    {
        var questions = await db.ListQuestionsAsync();
        return BenchSet.Tiers.ToDictionary(t => t, t => questions.Count(q => q.In(t)));
    }

    // Scores are only comparable on the same documents and the same prompt content
    public static void MarkStale(IEnumerable<TestRunSummary> runs, string docHash, IEnumerable<PromptVersion> prompts)
    {
        var promptHashes = prompts.ToDictionary(p => p.Id, p => Hash(p.Content));
        foreach (var r in runs)
        {
            r.Stale = r.Scale != BenchDb.CriteriaScale ? "đổi sang chấm 8 tiêu chí"
                : r.DocHash != docHash ? "docs đã đổi"
                : promptHashes.GetValueOrDefault(r.PromptId) != r.PromptHash ? "prompt đã sửa"
                : null;
        }
    }

    public async Task<IResult> StartAsync(long promptId, string tier)
    {
        if (!BenchSet.Tiers.Contains(tier)) return Results.BadRequest($"Chế độ phải là {string.Join(", ", BenchSet.Tiers)}");
        if (await chatDb.GetPromptAsync(promptId) is not { } prompt) return Results.NotFound($"Không có bản prompt {promptId}");
        var questions = (await db.ListQuestionsAsync()).Where(q => q.In(tier)).ToList();
        if (questions.Count == 0) return Results.Problem($"Chế độ {tier} không có câu nào");

        var snapshot = docs.Current;
        // Running against other docs (e.g. the sample docs) makes the score meaningless
        var loaded = snapshot.Files.Select(f => Path.GetFileName(f.Path)).ToHashSet();
        var missing = questions.SelectMany(q => Citations.ParseSources(q.Sources)).Select(s => s.File)
            .Where(f => !loaded.Contains(f)).Distinct().Order().ToList();
        if (missing.Count > 0)
            return Results.BadRequest($"Bộ docs đang nạp ({docs.DocsPath}) thiếu file mà câu hỏi cần: {string.Join(", ", missing)}");

        lock (_lock)
        {
            if (_runningId is not null) return Results.Conflict($"Đang chạy lượt test {_runningId}");
            _runningId = 0;
        }

        long runId;
        try
        {
            runId = await db.CreateRunAsync(promptId, Hash(prompt.Content), snapshot.Hash, tier,
                config["OPENAI_MODEL"] ?? "unknown", questions.Count);
        }
        catch
        {
            _runningId = null;
            throw;
        }

        _plan = questions;
        _active.Clear();
        _runningId = runId;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _ = Task.Run(() => RunAsync(runId, prompt, snapshot, questions, _cts.Token));
        return Results.Ok(new { runId, total = questions.Count });
    }

    public bool Cancel(long runId)
    {
        if (_runningId != runId) return false;
        _cts?.Cancel();
        return true;
    }

    private async Task RunAsync(long runId, PromptVersion prompt, DocSnapshot snapshot, List<BenchQuestion> questions, CancellationToken ct)
    {
        string status = "done";
        string? error = null;
        try
        {
            var agent = agents.For(snapshot, prompt);
            var options = new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = ct };

            await System.Threading.Tasks.Parallel.ForEachAsync(questions.GroupBy(q => q.Group), options, async (group, token) =>
            {
                AgentSession? session = null;
                // The judge needs earlier follow-up questions as context
                var history = new List<TurnRow>();
                foreach (var q in group)
                {
                    _active[q.Id] = 0;
                    session ??= await agent.CreateSessionAsync(token);
                    var row = await AskAndGradeAsync(runId, agent, session, q, history, snapshot, token);
                    await db.AddResultAsync(row);
                    if (row.Answer is not null) history.Add(new(q.Question, row.Answer, snapshot.Hash));
                    _active.TryRemove(q.Id, out _);
                }
            });
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lượt test {RunId} lỗi", runId);
            status = "failed";
            error = ex.Message;
        }
        finally
        {
            await db.FinishRunAsync(runId, status, error);
            _active.Clear();
            _runningId = null;
            logger.LogInformation("Lượt test {RunId} kết thúc: {Status}", runId, status);
        }
    }

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private async Task<TestResultRow> AskAndGradeAsync(long runId, AIAgent agent, AgentSession session, BenchQuestion q,
        IReadOnlyList<TurnRow> history, DocSnapshot snapshot, CancellationToken ct)
    {
        string? answer = null, err = null, citeCheck = null, reason = null, criteria = null;
        long? inTok = null, cachedTok = null, outTok = null, latency = null;
        double? score = null;
        try
        {
            var sw = Stopwatch.StartNew();
            var response = await RetryAsync(() => agent.RunAsync(q.Question, session, cancellationToken: ct), ct);
            latency = sw.ElapsedMilliseconds;
            answer = response.Text;
            inTok = response.Usage?.InputTokenCount;
            cachedTok = response.Usage?.CachedInputTokenCount;
            outTok = response.Usage?.OutputTokenCount;
            citeCheck = Citations.Check(q.Kind, answer, q.Sources);
            var eval = await RetryAsync(() => judge.JudgeBenchAsync(q, [.. history, new(q.Question, answer, snapshot.Hash)], snapshot, ct), ct);
            score = eval.Score;
            criteria = JsonSerializer.Serialize(eval.Scores, JsonWeb);
            reason = string.Join(" ", eval.Scores.Where(s => s.Pass == false).Select(s => $"[{s.Criterion}] {s.Reason}"));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Record this question's error and continue with the next
            err = $"{ex.GetType().Name}: {ex.Message}";
        }

        return new TestResultRow(runId, q.Id, q.Kind, q.Question, q.Expected, q.Sources,
            answer, inTok, cachedTok, outTok, latency, err, citeCheck, null, reason,
            DateTimeOffset.UtcNow.ToString("O"), score, criteria);
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
    }

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
}
