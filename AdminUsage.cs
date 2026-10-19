namespace EofBot;

public static class AdminUsage
{
    // Vietnam has no daylight saving, so a fixed +7 offset is enough and avoids needing tzdata in the container
    public static readonly TimeSpan VnOffset = TimeSpan.FromHours(7);

    public static DateOnly VnDate(DateTimeOffset t) => DateOnly.FromDateTime(t.ToOffset(VnOffset).DateTime);

    // Midnight (Vietnam time) of the day containing t: when user quotas reset
    public static DateTimeOffset VnDayStart(DateTimeOffset t) => new(VnDate(t).ToDateTime(TimeOnly.MinValue), VnOffset);

    private static readonly int[] Periods = [1, 7, 30];

    public static void MapAdminUsage(this WebApplication app)
    {
        app.MapGet("/api/admin/usage", async (int? days, UsageDb db, IConfiguration config) =>
        {
            int n = Math.Clamp(days ?? 30, 1, 365);
            var today = VnDate(DateTimeOffset.UtcNow);
            var from = today.AddDays(1 - Math.Max(n, Periods[^1]));
            var rows = (await db.ListSinceAsync(new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), VnOffset)))
                .Select(r => (r.Source, r.UserName, r.Tokens, r.CreatedAt, Date: VnDate(DateTimeOffset.Parse(r.CreatedAt))))
                .ToList();
            var inRange = rows.Where(r => r.Date > today.AddDays(-n)).ToList();
            var (defaultQuota, userQuotas) = await db.GetQuotasAsync();
            var chat = inRange.Where(r => r.Source == "chat").ToLookup(r => r.UserName!);

            return new
            {
                today = today.ToString("yyyy-MM-dd"),
                totals = Periods.Select(d => new
                {
                    days = d,
                    bySource = rows.Where(r => r.Date > today.AddDays(-d))
                        .GroupBy(r => r.Source)
                        .ToDictionary(g => g.Key, g => new { count = g.Count(), tokens = g.Sum(r => r.Tokens) }),
                }),
                daily = inRange
                    .GroupBy(r => r.Date)
                    .OrderBy(g => g.Key)
                    .Select(g => new
                    {
                        date = g.Key.ToString("yyyy-MM-dd"),
                        bySource = g.GroupBy(r => r.Source).ToDictionary(s => s.Key, s => s.Sum(r => r.Tokens)),
                    }),
                defaultQuota,
                // Users with a custom quota are shown even if they did not chat in the selected range
                users = chat.Select(g => g.Key).Union(userQuotas.Keys)
                    .Select(u => new
                    {
                        userName = u,
                        todayCount = chat[u].Count(r => r.Date == today),
                        todayTokens = chat[u].Where(r => r.Date == today).Sum(r => r.Tokens),
                        count = chat[u].Count(),
                        tokens = chat[u].Sum(r => r.Tokens),
                        lastAt = chat[u].Select(r => r.CreatedAt).Max(),
                        exempt = Auth.QuotaExempt(u, config),
                        customQuota = userQuotas.TryGetValue(u, out var q) ? q : (long?)null,
                    })
                    .OrderByDescending(u => u.todayTokens).ThenByDescending(u => u.tokens).ThenBy(u => u.userName),
            };
        }).RequireAuthorization(Auth.AdminPolicy);

        app.MapGet("/api/admin/quota", async (bool? refresh, ProxyQuota quota, CancellationToken ct) =>
            ProxyQuota.Configured
                ? Results.Ok(new { configured = true, report = await quota.GetAsync(refresh == true, ct) })
                : Results.Ok(new { configured = false }))
            .RequireAuthorization(Auth.AdminPolicy);

        var quotas = app.MapGroup("/api/admin/quotas").RequireAuthorization(Auth.AdminPolicy);

        quotas.MapPut("default", async (QuotaBody body, UsageDb db) =>
        {
            if (body.Questions is not >= 0) return Results.BadRequest("Cần số câu ≥ 0");
            await db.SetDefaultAsync(body.Questions.Value);
            return Results.NoContent();
        });

        // Questions null = remove the custom quota, use the default
        quotas.MapPut("users/{userName}", async (string userName, QuotaBody body, UsageDb db) =>
        {
            if (body.Questions is < 0) return Results.BadRequest("Cần số câu ≥ 0");
            await db.SetUserAsync(userName.Trim().ToLowerInvariant(), body.Questions);
            return Results.NoContent();
        });
    }
}

public sealed record QuotaBody(long? Questions);
