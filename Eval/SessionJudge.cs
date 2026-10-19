using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EofBot.Bench;
using OpenAI.Chat;

namespace EofBot.Eval;

public sealed partial class SessionJudge(ChatClient client, IConfiguration config)
{
    public static readonly IReadOnlyDictionary<int, string> Criteria = new Dictionary<int, string>
    {
        [1] = "Hiểu đúng ý người hỏi",
        [2] = "Dùng đúng ngữ cảnh lượt trước",
        [3] = "Bám tài liệu",
        [4] = "Đủ ý",
        [5] = "Từ chối đúng",
        [6] = "Trích nguồn đúng mục",
        [7] = "Gọn, không lặp",
        [8] = "Tuân thủ",
    };

    // Criterion 6 is graded in code, not by the model
    private const int CitationCriterion = 6;

    private const string JudgeInstructions = """
        # Vai trò
        Bạn là người kiểm định chất lượng một chatbot nội bộ. Chatbot chỉ được trả lời dựa trên bộ tài liệu trong thẻ <documents> bên dưới. Bạn chấm MỘT phiên hội thoại (có thể nhiều lượt) giữa người dùng và chatbot.

        # Tiêu chí
        1. Hiểu đúng ý người hỏi: ở mọi lượt, chatbot trả lời đúng điều người dùng thật sự muốn hỏi, không lạc đề.
        2. Dùng đúng ngữ cảnh lượt trước: với câu hỏi nối tiếp, chatbot hiểu đúng nó nối vào đâu và không mâu thuẫn với các lượt trước. KHÔNG ÁP DỤNG nếu phiên chỉ có một lượt hoặc các lượt không liên quan nhau.
        3. Bám tài liệu: mọi con số, tên nút, bước thao tác, quy định trong câu trả lời đều có trong tài liệu. Có bất kỳ chi tiết nào không tìm thấy trong tài liệu hoặc trái với tài liệu là KHÔNG ĐẠT.
        4. Đủ ý: câu trả lời không bỏ sót nội dung quan trọng mà tài liệu có nói về đúng điều được hỏi.
        5. Từ chối đúng: khi chatbot nói tài liệu không đề cập (toàn bộ hoặc một phần câu hỏi) thì tài liệu thật sự không có nội dung đó. KHÔNG ÁP DỤNG nếu chatbot không từ chối lần nào.
        7. Gọn, không lặp: câu trả lời đi thẳng vào việc, không lặp lại ý, không dài dòng so với câu hỏi.
        8. Tuân thủ: trả lời bằng tiếng Việt, lịch sự, không tư vấn hay bàn luận ngoài phạm vi tài liệu.

        # Cách chấm
        - Đối chiếu từng chi tiết của câu trả lời với tài liệu trước khi kết luận tiêu chí 3, 4, 5.
        - Một tiêu chí KHÔNG ĐẠT nếu có ít nhất một lượt vi phạm; nêu rõ lượt nào và chi tiết nào.
        - Xác định lỗi chính: một lỗi chỉ tính vào tiêu chí sát nhất với nó, đừng đánh rớt nhiều tiêu chí vì cùng một lỗi.
        - Không chấm việc trích nguồn (đã có bước kiểm riêng).
        - Người dùng có thể gửi kèm ảnh (thường là ảnh chụp màn hình), đặt ngay sau câu hỏi của lượt đó. Xét cả nội dung ảnh khi chấm tiêu chí 1. Chi tiết chatbot nhắc lại từ ảnh của người dùng (vd mã lỗi, tên nút trên ảnh) không tính là ngoài tài liệu ở tiêu chí 3.

        # Đầu ra
        Viết nhận xét tự do bằng tiếng Việt, lần lượt từng tiêu chí 1, 2, 3, 4, 5, 7, 8: lập luận ngắn, rồi kết luận ĐẠT, KHÔNG ĐẠT hoặc KHÔNG ÁP DỤNG.
        Cuối cùng ghi một dòng "Lượt có nội dung:" liệt kê số thứ tự các lượt mà chatbot đưa ra thông tin nghiệp vụ (không tính lượt chỉ chào hỏi, hỏi lại cho rõ, hoặc chỉ nói tài liệu không đề cập); không có lượt nào thì ghi "không có".
        """;

    private const string FormatInstructions = """
        Bạn nhận một bản nhận xét chấm điểm chatbot theo các tiêu chí 1, 2, 3, 4, 5, 7, 8. Chuyển nó thành JSON, giữ nguyên kết luận của bản nhận xét, không chấm lại.

        Chỉ trả về JSON, không giải thích thêm:
        {"1": {"pass": true, "reason": "<một câu tóm tắt lý do>"}, "2": {...}, "3": {...}, "4": {...}, "5": {...}, "7": {...}, "8": {...}, "content_turns": [1, 3]}

        "content_turns": các số ở dòng "Lượt có nội dung", mảng rỗng nếu không có.
        "pass": true nếu ĐẠT, false nếu KHÔNG ĐẠT, null nếu KHÔNG ÁP DỤNG.
        """;

    private const string BenchInstructions = """
        # Chế độ benchmark
        Phiên này là một câu trong bộ câu hỏi kiểm thử. CHỈ chấm lượt cuối; các lượt trước (nếu có) chỉ là ngữ cảnh, không chấm.
        Kèm theo là đáp án chuẩn của lượt cuối do người soạn tài liệu viết (các ý bắt buộc). Dùng nó khi chấm:
        - Tiêu chí 4: thiếu ý bắt buộc trong đáp án chuẩn là KHÔNG ĐẠT. Ý ghi "(tùy chọn: ...)" không bắt buộc.
        - Loại "Không có": câu trả lời đúng là nói tài liệu không đề cập; đưa ra nội dung thay vì từ chối là KHÔNG ĐẠT tiêu chí 3.
        - Loại "Thiếu một phần": phải trả lời phần tài liệu có và nói rõ phần tài liệu không có; từ chối cả câu là KHÔNG ĐẠT tiêu chí 5.
        - Có thêm chi tiết đúng lấy từ tài liệu mà đáp án chuẩn không nêu vẫn là ĐẠT.
        """;

    public string Model { get; } = config["OPENAI_MODEL"] ?? "unknown";

    public Task<SessionEval> JudgeAsync(string sessionId, IReadOnlyList<TurnRow> turns, DocSnapshot docs, CancellationToken ct) =>
        RunAsync(sessionId, turns, docs, null, ct);

    // Grades the last question of a benchmark group; earlier follow-up questions are context
    public Task<SessionEval> JudgeBenchAsync(BenchQuestion q, IReadOnlyList<TurnRow> turns, DocSnapshot docs, CancellationToken ct) =>
        RunAsync(q.Id, turns, docs, q, ct);

    private async Task<SessionEval> RunAsync(string id, IReadOnlyList<TurnRow> turns, DocSnapshot docs, BenchQuestion? q, CancellationToken ct)
    {
        var options = new ChatCompletionOptions { Temperature = 0 };
        string system = q is null ? JudgeInstructions : $"{JudgeInstructions}\n\n{BenchInstructions}";
        var user = Transcript(turns);
        if (q is not null) user.Add(ChatMessageContentPart.CreateTextPart($"\n# Đáp án chuẩn cho lượt cuối\nLoại câu hỏi: {q.Kind}\n\n{q.Expected}\n"));

        // Step 1 lets the model reason freely; forcing JSON from the start lowers grading quality
        var judged = await client.CompleteChatAsync(
            [new SystemChatMessage($"{system}\n\n<documents>\n{docs.Combined}</documents>"), new UserChatMessage(user)],
            options, ct);
        string rationale = judged.Value.Content[0].Text.Trim();

        var formatted = await client.CompleteChatAsync(
            [new SystemChatMessage(FormatInstructions), new UserChatMessage(rationale)], options, ct);

        var (scores, contentTurns) = Parse(formatted.Value.Content[0].Text);
        scores.Add(q is null ? CheckCitations(turns, docs, contentTurns) : CheckBenchCitation(q, turns[^1].Answer));
        scores.Sort((a, b) => a.Criterion.CompareTo(b.Criterion));

        int applicable = scores.Count(s => s.Pass is not null);
        double score = applicable == 0 ? 0 : 100.0 * scores.Count(s => s.Pass == true) / applicable;

        return new SessionEval(id, turns.Count, docs.Hash, Model, rationale, scores, score,
            judged.Value.Usage?.InputTokenCount + formatted.Value.Usage?.InputTokenCount,
            judged.Value.Usage?.OutputTokenCount + formatted.Value.Usage?.OutputTokenCount);
    }

    // Each turn's images go right after that turn's question so the judge knows which turn they belong to
    private static List<ChatMessageContentPart> Transcript(IReadOnlyList<TurnRow> turns)
    {
        var parts = new List<ChatMessageContentPart>();
        var sb = new StringBuilder("Phiên hội thoại cần chấm:\n");
        for (int i = 0; i < turns.Count; i++)
        {
            var images = turns[i].Images ?? [];
            sb.Append($"\n## Lượt {i + 1}\n[NGƯỜI DÙNG]: {turns[i].Question}\n");
            if (images.Count > 0)
            {
                sb.Append($"[NGƯỜI DÙNG GỬI KÈM {images.Count} ẢNH]\n");
                parts.Add(ChatMessageContentPart.CreateTextPart(sb.ToString()));
                sb.Clear();
                parts.AddRange(images.Select(img => ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(img.Data), img.MediaType)));
            }
            sb.Append($"[CHATBOT]: {turns[i].Answer}\n");
        }
        parts.Add(ChatMessageContentPart.CreateTextPart(sb.ToString()));
        return parts;
    }

    private static (List<CriterionScore> Scores, HashSet<int> ContentTurns) Parse(string text)
    {
        using var json = JsonDocument.Parse(FenceRegex().Replace(text.Trim(), ""));
        var scores = new List<CriterionScore>();
        foreach (int id in Criteria.Keys.Where(k => k != CitationCriterion))
        {
            if (!json.RootElement.TryGetProperty(id.ToString(), out var item))
                throw new InvalidOperationException($"Kết quả chấm thiếu tiêu chí {id}");
            var pass = item.GetProperty("pass");
            scores.Add(new(id,
                pass.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null },
                item.TryGetProperty("reason", out var r) ? r.GetString()?.Trim() ?? "" : ""));
        }
        var contentTurns = json.RootElement.TryGetProperty("content_turns", out var ct) && ct.ValueKind == JsonValueKind.Array
            ? ct.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToHashSet()
            : throw new InvalidOperationException("Kết quả chấm thiếu content_turns");
        return (scores, contentTurns);
    }

    // Benchmarks have expected sources, so cited sections can be compared with them
    private static CriterionScore CheckBenchCitation(BenchQuestion q, string answer)
    {
        if (q.Kind == BenchSet.KhongCo) return new(CitationCriterion, null, "Câu loại Không có, không cần trích nguồn.");
        // Refusals are already graded by criterion 5; no extra penalty for missing sources
        if (answer.Contains(Citations.Fallback) && Citations.Cited(answer).Count == 0)
            return new(CitationCriterion, null, "Chatbot từ chối, không có nội dung để trích.");
        string check = Citations.Check(q.Kind, answer, q.Sources);
        return check == "khớp"
            ? new(CitationCriterion, true, $"Trích đúng mục (nguồn đúng: {q.Sources}).")
            : new(CitationCriterion, false, $"Trích nguồn: {check} (nguồn đúng: {q.Sources}).");
    }

    // Without an expected answer we can only check that substantive turns (per the judge) cite, and that cited sections exist
    private static CriterionScore CheckCitations(IReadOnlyList<TurnRow> turns, DocSnapshot docs, HashSet<int> contentTurns)
    {
        var files = docs.Files.Select(f => Path.GetFileName(f.Path)).ToHashSet();
        var sections = Citations.LoadSections(docs);
        var problems = new List<string>();
        int answered = 0;
        for (int i = 0; i < turns.Count; i++)
        {
            var cited = Citations.Cited(turns[i].Answer);
            if (cited.Count == 0)
            {
                if (!contentTurns.Contains(i + 1)) continue;
                answered++;
                problems.Add($"lượt {i + 1} không trích nguồn");
                continue;
            }
            answered++;
            var missing = cited
                .Where(c => c.Section is null ? !files.Contains(c.File) : !sections.ContainsKey(c))
                .Select(c => c.Section is null ? c.File : $"{c.File} § {c.Section}")
                .ToList();
            if (missing.Count > 0) problems.Add($"lượt {i + 1} trích mục không tồn tại: {string.Join(", ", missing)}");
        }

        if (answered == 0) return new(CitationCriterion, null, "Không có lượt nào đưa thông tin từ tài liệu.");
        return problems.Count == 0
            ? new(CitationCriterion, true, "Mọi lượt có nội dung đều trích nguồn và các mục được trích đều có trong tài liệu.")
            : new(CitationCriterion, false, string.Join("; ", problems) + ".");
    }

    [GeneratedRegex(@"^```(?:json)?\s*|\s*```$")]
    private static partial Regex FenceRegex();
}
