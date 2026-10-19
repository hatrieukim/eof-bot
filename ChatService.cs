using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace EofBot;

public sealed record ChatRequest(string? SessionId, string UserName, string Message, long? PromptId = null, List<string>? Images = null);

public sealed record ChatResult(string SessionId, string Answer, long? InputTokens, long? CachedTokens, long? OutputTokens, long LatencyMs);

public sealed record ChatTurn(ChatRequest Request, IReadOnlyList<DataContent> Images, string SessionId, SessionRow? Existing, DocSnapshot Snapshot, PromptVersion Prompt, AIAgent Agent, AgentSession Session);

public sealed partial class ChatService(DocStore docs, DocAgentFactory agents, ChatDb db, IConfiguration config)
{
    // null = the given sessionId does not exist or belongs to someone else
    public async Task<ChatTurn?> OpenAsync(ChatRequest req, IReadOnlyList<DataContent> images, PromptVersion prompt, CancellationToken ct)
    {
        var snapshot = docs.Current;
        var agent = agents.For(snapshot, prompt);

        SessionRow? existing = null;
        if (req.SessionId is not null)
        {
            existing = await db.GetSessionAsync(req.SessionId);
            if (existing is null || existing.UserName != req.UserName) return null;
        }

        var session = existing is null
            ? await agent.CreateSessionAsync(ct)
            : await agent.DeserializeSessionAsync(JsonDocument.Parse(existing.SessionJson).RootElement, cancellationToken: ct);

        return new ChatTurn(req, images, existing?.Id ?? Guid.NewGuid().ToString("N"), existing, snapshot, prompt, agent, session);
    }

    public async Task<ChatResult> RunAsync(ChatTurn turn, Func<string, Task>? onDelta, CancellationToken ct)
    {
        var req = turn.Request;
        var sw = Stopwatch.StartNew();
        var updates = new List<AgentResponseUpdate>();
        var message = new ChatMessage(ChatRole.User, [
            .. string.IsNullOrEmpty(req.Message) ? Array.Empty<AIContent>() : [new TextContent(req.Message)],
            .. turn.Images]);
        await foreach (var update in turn.Agent.RunStreamingAsync(message, turn.Session, cancellationToken: ct))
        {
            updates.Add(update);
            if (onDelta is not null && !string.IsNullOrEmpty(update.Text)) await onDelta(update.Text);
        }
        sw.Stop();
        var response = updates.ToAgentResponse();

        var sessionJson = await turn.Agent.SerializeSessionAsync(turn.Session, cancellationToken: ct);
        string now = DateTimeOffset.UtcNow.ToString("O");

        await db.SaveSessionAsync(new SessionRow(
            turn.SessionId, req.UserName,
            turn.Existing?.Title ?? (req.Message.Length > 0 ? Truncate(req.Message, 60) : "[ảnh]"),
            sessionJson.GetRawText(),
            turn.Existing?.CreatedAt ?? now, now));

        var usage = response.Usage;
        await db.AddLogAsync(new QaLog(
            turn.SessionId, req.UserName, req.Message, response.Text, ExtractCitations(response.Text),
            turn.Snapshot.Hash, config["OPENAI_MODEL"] ?? "unknown",
            usage?.InputTokenCount, usage?.CachedInputTokenCount, usage?.OutputTokenCount,
            sw.ElapsedMilliseconds, now, turn.Prompt.Id), turn.Images);

        return new ChatResult(turn.SessionId, response.Text,
            usage?.InputTokenCount, usage?.CachedInputTokenCount, usage?.OutputTokenCount, sw.ElapsedMilliseconds);
    }

    // Extract the "(Nguồn: ...)" (source) part the instructions require at the end of each answer
    private static string? ExtractCitations(string answer)
    {
        var found = CitationRegex().Matches(answer).Select(m => m.Groups[1].Value.Trim()).ToList();
        return found.Count == 0 ? null : string.Join("; ", found);
    }

    [GeneratedRegex(@"\(Nguồn:\s*([^)]*)\)")]
    private static partial Regex CitationRegex();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
