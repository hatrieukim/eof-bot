using EofBot.Bench;

namespace EofBot;

public static class AdminBench
{
    public static void MapAdminBench(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/bench").RequireAuthorization(Auth.AdminPolicy);

        group.MapGet("", (BenchDb db) => db.ListQuestionsAsync());

        group.MapPost("", async (BenchQuestion body, BenchDb db, DocStore docs, BenchRunner runner) =>
        {
            var q = Normalize(body);
            if (Check(q, docs, runner) is { } error) return error;
            return await db.AddQuestionAsync(q) ? Results.Ok(q) : Results.Conflict($"Đã có câu {q.Id}");
        });

        group.MapPut("{id}", async (string id, BenchQuestion body, BenchDb db, DocStore docs, BenchRunner runner) =>
        {
            var q = Normalize(body with { Id = id });
            if (Check(q, docs, runner) is { } error) return error;
            return await db.UpdateQuestionAsync(q) ? Results.Ok(q) : Results.NotFound();
        });

        group.MapDelete("{id}", async (string id, BenchDb db, BenchRunner runner) =>
        {
            if (runner.RunningId is { } running) return Results.Conflict($"Đang chạy lượt test {running}, chờ xong rồi sửa");
            return await db.DeleteQuestionAsync(id) ? Results.Ok(new { id }) : Results.NotFound();
        });
    }

    // A question in the quick set is also in the medium set, as in the old md file
    private static BenchQuestion Normalize(BenchQuestion q) => q with
    {
        Id = q.Id.Trim(),
        Kind = q.Kind?.Trim() ?? "",
        Question = q.Question?.Trim() ?? "",
        Expected = q.Expected?.Trim() ?? "",
        Sources = q.Sources?.Trim() ?? "",
        Trung = q.Trung || q.Nhanh,
    };

    // Editing questions during a run would mix old and new questions in the results
    private static IResult? Check(BenchQuestion q, DocStore docs, BenchRunner runner)
    {
        if (runner.RunningId is { } running) return Results.Conflict($"Đang chạy lượt test {running}, chờ xong rồi sửa");
        var snapshot = docs.Current;
        return BenchSet.Validate(q, snapshot, Citations.LoadSections(snapshot)) is { } error ? Results.BadRequest(error) : null;
    }
}
