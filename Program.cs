using System.ClientModel;
using System.Security.Claims;
using EofBot;
using EofBot.Bench;
using EofBot.Eval;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.FileProviders;
using OpenAI;

DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

builder.AddEntraAuth();

string docsPath = Path.GetFullPath(builder.Configuration["DocsPath"] ?? "data/docs", builder.Environment.ContentRootPath);
builder.Services.AddSingleton(sp =>
{
    var store = new DocStore(docsPath, sp.GetRequiredService<ILogger<DocStore>>());
    store.Loaded += sp.GetRequiredService<EvalDb>().SaveSnapshot;
    store.Load();
    return store;
});

builder.Services.AddSingleton(_ => new OpenAIClient(
    new ApiKeyCredential(Required("OPENAI_API_KEY")),
    new OpenAIClientOptions { Endpoint = new Uri(Required("OPENAI_BASE_URL")), RetryPolicy = new ProxyRetryPolicy() }));
builder.Services.AddSingleton(sp => sp.GetRequiredService<OpenAIClient>().GetChatClient(Required("OPENAI_MODEL")));
builder.Services.AddSingleton<DocAgentFactory>();

string dbPath = Path.GetFullPath(builder.Configuration["DbPath"] ?? "data/app.db", builder.Environment.ContentRootPath);
builder.Services.AddSingleton(new ChatDb(dbPath));
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton(new BenchDb(dbPath));
builder.Services.AddSingleton<BenchRunner>();
builder.Services.AddSingleton(new EvalDb(dbPath));
builder.Services.AddSingleton(new UsageDb(dbPath));
builder.Services.AddMemoryCache();
// The management API lives on the same proxy as OPENAI_BASE_URL (without /v1)
builder.Services.AddHttpClient<ProxyQuota>(c =>
{
    c.BaseAddress = new Uri(new Uri(Required("OPENAI_BASE_URL")), "/");
    c.Timeout = TimeSpan.FromSeconds(30);
    if (ProxyQuota.Configured) c.DefaultRequestHeaders.Authorization = new("Bearer", Environment.GetEnvironmentVariable(ProxyQuota.KeyVariable));
});
builder.Services.AddSingleton<SessionJudge>();
builder.Services.AddSingleton<EvalSampler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EvalSampler>());

// The agent is created once at startup with the documents present then. Hot-reloading documents would need to change this
builder.AddAIAgent(DocAgentFactory.AgentName, (sp, _) =>
    sp.GetRequiredService<DocAgentFactory>().For(
        sp.GetRequiredService<DocStore>().Current,
        sp.GetRequiredService<ChatDb>().GetActivePromptAsync().GetAwaiter().GetResult()));

builder.Services.AddOpenAIResponses();
builder.Services.AddOpenAIConversations();

if (builder.Environment.IsDevelopment())
{
    builder.AddDevUI();
}

var app = builder.Build();

var docStore = app.Services.GetRequiredService<DocStore>();

// Benchmark questions live in the DB; the md file is only imported once while the table is empty
string benchMd = Path.GetFullPath(builder.Configuration["BenchPath"] ?? "data/bench/cau-hoi-benchmark.md", builder.Environment.ContentRootPath);
if (File.Exists(benchMd) && app.Services.GetRequiredService<BenchDb>().ImportIfEmpty(() => BenchSet.Parse(benchMd)) is > 0 and var imported)
    app.Logger.LogInformation("Imported {Count} benchmark questions from {Path}", imported, benchMd);

// Fonts are not secret, and the /login page (not signed in yet) needs them too
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(app.Environment.WebRootPath, "fonts")),
    RequestPath = "/fonts",
});

// Auth goes before static files so index.html, admin/... also require sign-in (FallbackPolicy)
app.UseAuthentication();
app.UseAuthorization();
app.UseAdminPages();

app.UseDefaultFiles();
// Without this header Chrome keeps stale JS/CSS after code changes; no-cache = always revalidate, unchanged files get 304
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapAuth();

app.MapGet("/api/status", () =>
{
    var s = docStore.Current;
    return new
    {
        docsPath = docStore.DocsPath,
        hash = s.Hash,
        loadedAt = s.LoadedAt,
        totalChars = s.Combined.Length,
        files = s.Files.Select(f => new { f.Path, chars = f.Content.Length }),
    };
}).RequireAuthorization(Auth.AdminPolicy);

// The exact text block that goes into the prompt, for debugging wrong answers
app.MapGet("/docs", () => Results.Text(docStore.Current.Combined, "text/plain; charset=utf-8")).RequireAuthorization(Auth.AdminPolicy);

app.MapGet("/ask", async (string q, DocAgentFactory agents, ChatDb db) =>
{
    var response = await agents.For(docStore.Current, await db.GetActivePromptAsync()).RunAsync(q);
    return Results.Text(response.Text, "text/plain; charset=utf-8");
}).RequireAuthorization(Auth.AdminPolicy);

app.MapPost("/api/chat", async (ChatRequest req, ClaimsPrincipal user, ChatService chat, ChatDb db, UsageDb usage, CancellationToken ct) =>
{
    bool isAdmin = user.IsAdmin(app.Configuration);
    if (Normalize(req, user, isAdmin) is not { } r) return Results.BadRequest("Cần câu hỏi hoặc ảnh");
    var (images, imageError) = ChatImages.Parse(r.Images);
    if (imageError is not null) return Results.BadRequest(imageError);
    if (!isAdmin && await QuotaBlock(r.UserName, usage) is { } blocked) return blocked;

    if (await PromptFor(r, db) is not { } prompt) return Results.NotFound($"Không có bản prompt {r.PromptId}");
    var turn = await chat.OpenAsync(r, images, prompt, ct);
    if (turn is null) return Results.NotFound($"Không có session {r.SessionId}");
    try
    {
        return Results.Ok(await chat.RunAsync(turn, null, ct));
    }
    catch (Exception ex) when (ProviderErrors.Describe(ex) is { } message)
    {
        return Results.Text(message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/chat/stream", async (ChatRequest req, ClaimsPrincipal user, ChatService chat, ChatDb db, UsageDb usage, HttpContext http, ILogger<ChatService> logger, CancellationToken ct) =>
{
    bool isAdmin = user.IsAdmin(app.Configuration);
    if (Normalize(req, user, isAdmin) is not { } r) return Results.BadRequest("Cần câu hỏi hoặc ảnh");
    var (images, imageError) = ChatImages.Parse(r.Images);
    if (imageError is not null) return Results.BadRequest(imageError);
    if (!isAdmin && await QuotaBlock(r.UserName, usage) is { } blocked) return blocked;

    if (await PromptFor(r, db) is not { } prompt) return Results.NotFound($"Không có bản prompt {r.PromptId}");
    var turn = await chat.OpenAsync(r, images, prompt, ct);
    if (turn is null) return Results.NotFound($"Không có session {r.SessionId}");

    // Once the 200 header is sent the status code cannot change, so errors are reported as an event
    Sse.Start(http.Response);
    try
    {
        var result = await chat.RunAsync(turn, text => Sse.WriteAsync(http.Response, "delta", new { text }, ct), ct);
        await Sse.WriteAsync(http.Response, "done", result, ct);
    }
    catch (Exception ex) when (!ct.IsCancellationRequested)
    {
        logger.LogError(ex, "Lỗi khi stream câu trả lời");
        string? friendly = ProviderErrors.Describe(ex);
        await Sse.WriteAsync(http.Response, "error", new { message = friendly ?? ex.Message, quota = friendly is not null }, ct);
    }
    return Results.Empty;
});

app.MapGet("/api/sessions", (ClaimsPrincipal user, ChatDb db) => db.ListSessionsAsync(user.UserKey()));
app.MapGet("/api/sessions/{id}/messages", async (string id, ClaimsPrincipal user, ChatDb db) =>
    await db.GetSessionAsync(id) is { } s && s.UserName == user.UserKey()
        ? Results.Ok(await db.GetMessagesAsync(id))
        : Results.NotFound());

// Images never change once stored, so let the browser cache them; admins can see everyone's images (eval page)
app.MapGet("/api/images/{id:long}", async (long id, ClaimsPrincipal user, ChatDb db, HttpContext http) =>
{
    if (await db.GetImageAsync(id) is not { } img || (img.UserName != user.UserKey() && !user.IsAdmin(app.Configuration)))
        return Results.NotFound();
    http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
    return Results.File(img.Data, img.MediaType);
});

// The welcome screen shows "X of Y questions left" so users know before they are blocked
app.MapGet("/api/me/quota", async (ClaimsPrincipal user, UsageDb usage) =>
{
    string key = user.UserKey();
    if (Auth.QuotaExempt(key, app.Configuration)) return Results.Ok(new { exempt = true });
    var q = await usage.GetUserQuotaAsync(key);
    return Results.Ok(new { exempt = false, used = q.Used, limit = q.Limit });
});

app.MapGet("/api/docs/section", (string file, string section) =>
    DocSections.Find(docStore.Current, file, section) is { } s ? Results.Ok(s) : Results.NotFound());

app.MapAdminDocs();
app.MapAdminPrompts();
app.MapAdminTests();
app.MapAdminBench();
app.MapAdminEvals();
app.MapAdminUsage();

app.MapOpenAIResponses();
app.MapOpenAIConversations();

// DevUI exposes the full instructions (= all documents), so it is only enabled in Development
if (app.Environment.IsDevelopment())
{
    app.MapDevUI();
}

app.Run();

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing {name} in .env");

// Counts questions logged in qa_logs: failed or interrupted questions are not counted
static async Task<IResult?> QuotaBlock(string userName, UsageDb usage)
{
    var q = await usage.GetUserQuotaAsync(userName);
    if (!q.Exceeded) return null;
    string message = q.Limit == 0
        ? "Tài khoản của bạn chưa được cấp lượt hỏi (0 câu/ngày). Vui lòng liên hệ admin."
        : $"Bạn đã hỏi đủ {q.Limit} câu hôm nay. Hạn mức được tính lại lúc 0 giờ.";
    return Results.Text(message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status429TooManyRequests);
}

// With promptId (admin testing a draft) use that version, otherwise the active one
static async Task<PromptVersion?> PromptFor(ChatRequest r, ChatDb db) =>
    r.PromptId is { } id ? await db.GetPromptAsync(id) : await db.GetActivePromptAsync();

// The asker's name comes from the sign-in cookie, never from the client. Exception: admin test chats keep "admin-thử"
// (so eval sampling skips them) and may pick a draft prompt version
static ChatRequest? Normalize(ChatRequest req, ClaimsPrincipal user, bool isAdmin) =>
    string.IsNullOrWhiteSpace(req.Message) && req.Images is null or { Count: 0 }
        ? null
        : req with
        {
            UserName = isAdmin && req.UserName == EvalSampler.AdminTestUser ? EvalSampler.AdminTestUser : user.UserKey(),
            Message = req.Message?.Trim() ?? "",
            PromptId = isAdmin ? req.PromptId : null,
        };
