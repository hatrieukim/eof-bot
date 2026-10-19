using EofBot.Bench;

namespace EofBot;

public sealed record PromptInput(string? Content, string? Note);

public static class AdminPrompts
{
    public static void MapAdminPrompts(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/prompts").RequireAuthorization(Auth.AdminPolicy);

        // Include the score of the latest finished test run per mode
        group.MapGet("", async (ChatDb db, BenchDb benchDb, DocStore docs) =>
        {
            var prompts = (await db.ListPromptsAsync()).ToList();
            var runs = (await benchDb.ListRunsAsync(null)).Where(r => r.Status == "done").ToList();
            BenchRunner.MarkStale(runs, docs.Current.Hash, prompts);
            return prompts.Select(p => new
            {
                p.Id, p.Content, p.Note, p.IsActive, p.CreatedAt,
                Scores = BenchSet.Tiers.ToDictionary(t => t, t => runs.FirstOrDefault(r => r.PromptId == p.Id && r.Tier == t)),
            });
        });

        group.MapPost("", async (PromptInput body, ChatDb db) =>
        {
            if (Clean(body) is not { } p) return Results.BadRequest("Prompt trống");
            return Results.Ok(new { id = await db.CreatePromptAsync(p.Content, p.Note) });
        });

        // Edits the active version in place: users get the new content from their next question
        group.MapPut("{id:long}", async (long id, PromptInput body, ChatDb db) =>
        {
            if (Clean(body) is not { } p) return Results.BadRequest("Prompt trống");
            return await db.UpdatePromptAsync(id, p.Content, p.Note) ? Results.Ok(new { id }) : Results.NotFound();
        });

        group.MapPost("{id:long}/activate", async (long id, ChatDb db) =>
            await db.ActivatePromptAsync(id) ? Results.Ok(new { id }) : Results.NotFound());
    }

    private static (string Content, string? Note)? Clean(PromptInput body) =>
        string.IsNullOrWhiteSpace(body.Content)
            ? null
            : (body.Content.Replace("\r\n", "\n").Trim(), string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim());
}
