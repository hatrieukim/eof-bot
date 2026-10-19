using EofBot.Bench;

namespace EofBot;

public sealed record StartTest(long PromptId, string Tier);

public static class AdminTests
{
    public static void MapAdminTests(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/tests").RequireAuthorization(Auth.AdminPolicy);

        group.MapGet("tiers", (BenchRunner runner) => runner.TierCountsAsync());

        group.MapPost("", (StartTest body, BenchRunner runner) => runner.StartAsync(body.PromptId, body.Tier));

        group.MapGet("", async (long? promptId, BenchDb db, ChatDb chatDb, DocStore docs) =>
        {
            var runs = (await db.ListRunsAsync(promptId)).ToList();
            BenchRunner.MarkStale(runs, docs.Current.Hash, await chatDb.ListPromptsAsync());
            return runs;
        });

        group.MapGet("{id:long}", async (long id, BenchDb db, ChatDb chatDb, DocStore docs, BenchRunner runner) =>
        {
            if (await db.GetRunAsync(id) is not { } run) return Results.NotFound();
            BenchRunner.MarkStale([run], docs.Current.Hash, await chatDb.ListPromptsAsync());
            // Running run: include all questions in file order and the ones currently being asked
            var progress = runner.Progress(id);
            return Results.Ok(new
            {
                run,
                results = await db.GetResultsAsync(id),
                plan = progress?.Plan.Select(q => new { qid = q.Id, q.Kind, q.Question }),
                active = progress?.Active,
            });
        });

        group.MapPost("{id:long}/cancel", (long id, BenchRunner runner) =>
            runner.Cancel(id) ? Results.Ok(new { id }) : Results.Conflict("Lượt này không còn chạy"));
    }
}
