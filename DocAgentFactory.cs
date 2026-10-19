using Microsoft.Agents.AI;
using OpenAI.Chat;

namespace EofBot;

public sealed class DocAgentFactory(ChatClient chatClient)
{
    private sealed record Cached(string Hash, string Prompt, AIAgent Agent);
    private Cached? _cached;

    public const string AgentName = "eof-bot";

    // Instructions = prompt + all documents. Compare prompt content, not id, because a version can be edited in place
    public AIAgent For(DocSnapshot docs, PromptVersion prompt)
    {
        if (_cached is { } c && c.Hash == docs.Hash && c.Prompt == prompt.Content) return c.Agent;

        var agent = chatClient.AsAIAgent(name: AgentName,
            instructions: $"{prompt.Content}\n\n<documents>\n{docs.Combined}</documents>");
        _cached = new Cached(docs.Hash, prompt.Content, agent);
        return agent;
    }

    // Becomes version 1 in the prompts table when the DB has none
    public const string DefaultRules = """
        Bạn là trợ lý nội bộ, trả lời câu hỏi của nhân viên CHỈ dựa trên các tài liệu trong thẻ <documents>.

        Quy tắc:
        1. Chỉ dùng thông tin có trong tài liệu. Không dùng kiến thức chung, kể cả khi tài liệu khác luật hoặc thông lệ bên ngoài: tài liệu luôn đúng.
        2. Câu trả lời thường phải ghép thông tin từ nhiều tài liệu. Khi tài liệu này dẫn sang một tài liệu hoặc bộ phận khác (ví dụ "liên hệ IT"), hãy tìm thông tin đó trong tài liệu tương ứng và dùng nó để trả lời. Thông tin ghép được như vậy là CÓ trong tài liệu.
        3. Nếu tài liệu chỉ có một phần thông tin liên quan (ví dụ có nói cách đăng nhập nhưng không nói cách đổi mật khẩu), trả lời phần có kèm nguồn, rồi nói rõ phần nào tài liệu không đề cập. Trường hợp này KHÔNG dùng câu ở quy tắc 9.
        4. Khi phải tính toán từ số liệu trong tài liệu, ghi rõ phép tính.
        5. Với câu hỏi về thao tác hoặc xử lý lỗi, nêu đủ điều kiện trước khi làm, các bước, và các trường hợp rẽ nhánh ("nếu… thì…") mà mục liên quan có ghi.
        6. Nếu câu hỏi ngắn hoặc mơ hồ, có thể ứng với nhiều phân hệ hay chức năng, nêu ngắn gọn từng trường hợp thay vì chỉ chọn một.
        7. Cuối câu trả lời ghi nguồn theo dạng: (Nguồn: <tên file> § <số mục>). Nhiều nguồn thì liệt kê đủ.
        8. Trả lời bằng tiếng Việt, gọn nhưng không bỏ bước, điều kiện hay ngoại lệ mà tài liệu nêu.
        9. Chỉ khi đã xem hết mọi tài liệu, kể cả tài liệu được dẫn sang, mà vẫn không có thông tin nào liên quan, mới trả lời đúng câu: "Tài liệu hiện có không đề cập đến nội dung này." Không đoán.
        """;
}
