using EofBot.Bench;
using EofBot.Eval;

namespace EofBot;

public sealed record TryTurn(string Question, string Answer);

// With Qid set, grade in benchmark mode: the last turn is question Qid, earlier turns are context
public sealed record TryEval(List<TryTurn> Turns, string? Qid = null);

public static class AdminEvals
{
    public static void MapAdminEvals(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/evals").RequireAuthorization(Auth.AdminPolicy);

        // Grade a hand-written conversation (e.g. with planted mistakes) to test the judge; nothing is saved
        group.MapPost("try", async (TryEval body, SessionJudge judge, DocStore docs, BenchDb benchDb, CancellationToken ct) =>
        {
            if (body.Turns is not { Count: > 0 }) return Results.BadRequest("Cần ít nhất một lượt");
            var snapshot = docs.Current;
            var turns = body.Turns.Select(t => new TurnRow(t.Question, t.Answer, snapshot.Hash)).ToList();
            SessionEval result;
            if (body.Qid is null) result = await judge.JudgeAsync("(thử)", turns, snapshot, ct);
            else if (await benchDb.GetQuestionAsync(body.Qid) is { } q) result = await judge.JudgeBenchAsync(q, turns, snapshot, ct);
            else return Results.NotFound($"Không có câu {body.Qid}");
            return Results.Ok(new
            {
                result.Score,
                scores = result.Scores.Select(s => new { s.Criterion, name = SessionJudge.Criteria[s.Criterion], s.Pass, s.Reason }),
                result.Rationale,
            });
        });

        group.MapPost("sessions/{id}", async (string id, EvalSampler sampler, CancellationToken ct) =>
        {
            var o = await sampler.EvaluateAsync(id, EvalDb.ManualSource, ct);
            return o.Result is null ? Results.Conflict(o.Error) : Results.Ok(Show(o));
        });

        // Average score per chat day, for the chart
        group.MapGet("daily", async (int? days, EvalDb db) =>
        {
            var evals = await db.ListLatestAsync(EvalSampler.AdminTestUser, Since(days));
            return evals
                .GroupBy(e => DateTimeOffset.Parse(e.SessionAt).ToLocalTime().ToString("yyyy-MM-dd"))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { date = g.Key, sessions = g.Count(), avgScore = Math.Round(g.Average(e => e.Score), 1) });
        });

        // Sessions with at least one failed criterion, with the failed criteria and reasons
        group.MapGet("failed", async (int? days, EvalDb db) =>
        {
            var failed = (await db.ListLatestAsync(EvalSampler.AdminTestUser, Since(days))).Where(e => e.Score < 100).ToList();
            var reasons = (await db.ListFailedScoresAsync(failed.Select(e => e.Id))).ToLookup(f => f.EvalId);
            return failed.Select(e => new
            {
                evalId = e.Id,
                e.SessionId,
                e.UserName,
                e.SessionAt,
                e.FirstQuestion,
                e.Turns,
                e.Score,
                failed = reasons[e.Id].Select(f => new { f.Criterion, name = SessionJudge.Criteria[(int)f.Criterion], f.Reason }),
            });
        });

        group.MapGet("{id:long}", async (long id, EvalDb db) =>
        {
            if (await db.GetAsync(id) is not var (eval, scores)) return Results.NotFound();
            return Results.Ok(new
            {
                eval,
                scores = scores.Select(s => new
                {
                    s.Criterion,
                    name = SessionJudge.Criteria[(int)s.Criterion],
                    pass = s.Pass is null ? (bool?)null : s.Pass != 0,
                    s.Reason,
                }),
                turns = (await db.GetTurnsAsync(eval.SessionId)).Select(t => new
                {
                    t.Question,
                    t.Answer,
                    images = t.Images?.Select(i => ChatDb.ImageUrl(i.Id.ToString())) ?? [],
                }),
            });
        });

        // Manually run one sampling round like the daily one, for testing
        group.MapPost("sample", async (int? n, EvalSampler sampler, CancellationToken ct) =>
            (await sampler.SampleAsync(n ?? 1, EvalDb.ManualSource, ct)).Select(Show));
    }

    private static DateTimeOffset Since(int? days) => DateTimeOffset.Now.Date.AddDays(1 - (days ?? 30));

    private static object Show(EvalOutcome o) => new
    {
        o.SessionId,
        o.EvalId,
        o.Error,
        o.Result?.Score,
        scores = o.Result?.Scores.Select(s => new { s.Criterion, name = SessionJudge.Criteria[s.Criterion], s.Pass, s.Reason }),
        o.Result?.InputTokens,
        o.Result?.OutputTokens,
        o.Result?.Rationale,
    };
}
