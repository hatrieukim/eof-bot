using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

namespace EofBot;

// Kind: 5h | day | week | month. Remaining = % left, 0–100
public sealed record QuotaWindow(string Kind, double Remaining, DateTimeOffset? ResetAt);

public sealed record QuotaAccount(string Name, string Provider, string? Plan, bool Usable, string? Status,
    IReadOnlyList<QuotaWindow> Windows, string? Error);

// Remaining is the summed % left across accounts in the group, Capacity = number of accounts × 100
public sealed record QuotaTotal(string Kind, double Remaining, int Capacity, DateTimeOffset? NextReset);

public sealed record QuotaGroup(string Provider, string? Plan, int Accounts, IReadOnlyList<QuotaTotal> Windows);

public sealed record QuotaReport(string Model, DateTimeOffset FetchedAt, IReadOnlyList<QuotaGroup> Groups,
    IReadOnlyList<QuotaAccount> Accounts, string? Error);

// Real quotas of the CLIProxyAPI accounts serving the bot's model. The proxy has no quota API of its own:
// /v0/management/api-call calls each provider's internal endpoint with the OAuth token the proxy holds
// (like the proxy's Quota Management page). Internal endpoints can change at any time, so errors are only shown, never blocking
public sealed class ProxyQuota(HttpClient http, IMemoryCache cache, ILogger<ProxyQuota> logger)
{
    public const string KeyVariable = "CLIPROXY_MANAGEMENT_KEY";
    private const string CacheKey = "proxy-quota";

    // ChatGPT blocks browser-like User-Agents (requires JS/cookies), so use a CLI one like the proxy's admin page does
    private const string CodexAgent = "codex_cli_rs/0.76.0 (Debian 13.0.0; x86_64) WindowsTerminal";
    private const string AntigravityAgent = "antigravity/1.11.5 darwin/arm64";
    private const string CloudCode = "https://daily-cloudcode-pa.googleapis.com/v1internal";

    public static bool Configured => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(KeyVariable));

    public async Task<QuotaReport> GetAsync(bool refresh, CancellationToken ct)
    {
        if (refresh) cache.Remove(CacheKey);
        return (await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            var report = await FetchAsync(ct);
            // Cache errors briefly so the next attempt retries soon
            entry.AbsoluteExpirationRelativeToNow = report.Error is null ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(30);
            return report;
        }))!;
    }

    private async Task<QuotaReport> FetchAsync(CancellationToken ct)
    {
        string model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "";
        try
        {
            var accounts = new List<QuotaAccount>();
            foreach (var file in (await GetAsync("v0/management/auth-files", ct))["files"]!.AsArray())
            {
                string name = (string)file!["name"]!;
                var models = (await GetAsync($"v0/management/auth-files/models?name={Uri.EscapeDataString(name)}", ct))["models"]?.AsArray();
                if (models?.Any(m => (string?)m!["id"] == model) == true)
                    accounts.Add(await ReadAccountAsync(file, model, ct));
            }
            return new QuotaReport(model, DateTimeOffset.UtcNow, Group(accounts), accounts,
                accounts.Count == 0 ? $"Không có tài khoản nào trong proxy phục vụ model {model}" : null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Không đọc được hạn mức từ CLIProxyAPI");
            return new QuotaReport(model, DateTimeOffset.UtcNow, [], [], $"Không lấy được hạn mức từ proxy: {ex.Message}");
        }
    }

    private async Task<QuotaAccount> ReadAccountAsync(JsonNode file, string model, CancellationToken ct)
    {
        string provider = (string)file["provider"]!;
        string name = (string?)file["email"] ?? (string)file["name"]!;
        bool active = file["disabled"]?.GetValue<bool>() != true && file["unavailable"]?.GetValue<bool>() != true
            && (string?)file["status"] == "active";
        string? status = active ? null : (string?)file["status_message"] is { Length: > 0 } m ? m : (string?)file["status"] ?? "tắt";
        try
        {
            var (plan, windows) = provider switch
            {
                "codex" => await CodexAsync(file, ct),
                "antigravity" => await AntigravityAsync(file, model, ct),
                _ => throw new NotSupportedException($"Chưa hỗ trợ đọc hạn mức của {provider}"),
            };
            return new QuotaAccount(name, provider, plan, active, status,
                windows.OrderBy(w => Order(w.Kind)).Select(w => w with { Remaining = Math.Round(w.Remaining, 1) }).ToList(), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Không đọc được hạn mức của {Account}", name);
            return new QuotaAccount(name, provider, null, false, status, [], ex.Message);
        }
    }

    private async Task<(string?, List<QuotaWindow>)> CodexAsync(JsonNode file, CancellationToken ct)
    {
        var usage = await UpstreamAsync(file, "GET", "https://chatgpt.com/backend-api/wham/usage", CodexAgent, null, ct);
        var limit = usage["rate_limit"]!;
        var windows = new[] { limit["primary_window"], limit["secondary_window"] }
            .OfType<JsonNode>()
            .Select(w => new QuotaWindow(
                KindOf(w["limit_window_seconds"]!.GetValue<long>()),
                100 - w["used_percent"]!.GetValue<double>(),
                DateTimeOffset.FromUnixTimeSeconds(w["reset_at"]!.GetValue<long>())))
            .ToList();
        return ((string?)file["id_token"]?["plan_type"] ?? (string?)usage["plan_type"], windows);
    }

    private async Task<(string?, List<QuotaWindow>)> AntigravityAsync(JsonNode file, string model, CancellationToken ct)
    {
        string? project = (string?)file["project_id"];
        var tier = await UpstreamAsync(file, "POST", $"{CloudCode}:loadCodeAssist", AntigravityAgent, new JsonObject
        {
            ["cloudaicompanionProject"] = project,
            ["metadata"] = new JsonObject { ["ideType"] = "ANTIGRAVITY", ["platform"] = "PLATFORM_UNSPECIFIED", ["pluginType"] = "GEMINI" },
        }, ct);
        var summary = await UpstreamAsync(file, "POST", $"{CloudCode}:retrieveUserQuotaSummary", AntigravityAgent,
            new JsonObject { ["project"] = project }, ct);

        // Quotas are per model group ("Gemini Models", "Claude and GPT models"), each with its own 5-hour and weekly window
        bool gemini = model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);
        var group = summary["groups"]!.AsArray()
            .First(g => ((string?)g!["displayName"] ?? "").Contains("Gemini", StringComparison.OrdinalIgnoreCase) == gemini)!;
        var windows = group["buckets"]!.AsArray()
            .Select(b => new QuotaWindow(
                (string?)b!["window"] switch { "weekly" => "week", "daily" => "day", "monthly" => "month", var w => w ?? "?" },
                // Protobuf omits zero fields from JSON, so a missing remainingFraction means exhausted
                (b["remainingFraction"]?.GetValue<double>() ?? 0) * 100,
                (string?)b["resetTime"] is { } t ? DateTimeOffset.Parse(t) : null))
            .ToList();
        string? plan = (string?)tier["paidTier"]?["name"] ?? (string?)tier["currentTier"]?["name"];
        return (plan, windows);
    }

    private static string KindOf(long seconds) => seconds switch
    {
        <= 6 * 3600 => "5h",
        <= 86400 => "day",
        <= 8 * 86400 => "week",
        _ => "month",
    };

    private static int Order(string kind) => kind switch { "5h" => 0, "day" => 1, "week" => 2, "month" => 3, _ => 4 };

    // Only sum accounts with the same provider + plan (1% of Free differs from 1% of a paid plan).
    // An account that exhausted a longer window cannot really use its shorter ones (weekly gone but 5h at 100%), so count 0
    private static List<QuotaGroup> Group(List<QuotaAccount> accounts) =>
        accounts.Where(a => a.Error is null)
            .GroupBy(a => (a.Provider, a.Plan))
            .Select(g => new QuotaGroup(g.Key.Provider, g.Key.Plan, g.Count(),
                g.SelectMany(a => a.Windows.Select(w => w.Kind)).Distinct().OrderBy(Order)
                    .Select(kind => new QuotaTotal(
                        kind,
                        Math.Round(g.Sum(a => Effective(a, kind)), 1),
                        g.Count() * 100,
                        g.SelectMany(a => a.Windows).Where(w => w.Kind == kind && w.Remaining < 100).Min(w => w.ResetAt)))
                    .ToList()))
            .ToList();

    private static double Effective(QuotaAccount a, string kind)
    {
        if (!a.Usable || a.Windows.FirstOrDefault(w => w.Kind == kind) is not { } window) return 0;
        return a.Windows.Any(w => Order(w.Kind) > Order(kind) && w.Remaining <= 0) ? 0 : window.Remaining;
    }

    private async Task<JsonNode> GetAsync(string path, CancellationToken ct)
    {
        using var res = await http.GetAsync(path, ct);
        res.EnsureSuccessStatusCode();
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
    }

    // The proxy replaces $TOKEN$ with that account's access token (auth_index)
    private async Task<JsonNode> UpstreamAsync(JsonNode file, string method, string url, string agent, JsonObject? data, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["auth_index"] = (string)file["auth_index"]!,
            ["method"] = method,
            ["url"] = url,
            ["header"] = new JsonObject
            {
                ["Authorization"] = "Bearer $TOKEN$",
                ["Content-Type"] = "application/json",
                ["User-Agent"] = agent,
            },
        };
        if (data is not null) body["data"] = data.ToJsonString();

        using var res = await http.PostAsync("v0/management/api-call",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        res.EnsureSuccessStatusCode();
        var result = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
        int status = result["status_code"]!.GetValue<int>();
        string text = (string?)result["body"] ?? "";
        if (status != 200) throw new HttpRequestException($"{url} trả {status}: {text[..Math.Min(200, text.Length)]}");
        return JsonNode.Parse(text)!;
    }
}
