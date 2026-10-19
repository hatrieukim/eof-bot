using EofBot.Bench;

namespace EofBot;

// Parent = parent section heading ("7. Requests" for section 7.4), Content = body without the heading line
public sealed record DocSection(string File, string Section, string Title, string? Parent, string Content);

// Lets users see the exact document section the bot cited in "(Nguồn: ...)" to check it themselves
public static class DocSections
{
    private sealed record Cache(string Hash, Dictionary<SourceRef, string> Sections);
    private static Cache? _cache;

    public static DocSection? Find(DocSnapshot docs, string file, string section)
    {
        var sections = Load(docs);
        if (!sections.TryGetValue(new(file, section), out var text)) return null;

        int nl = text.IndexOf('\n');
        string title = Heading(nl < 0 ? text : text[..nl]);
        string content = nl < 0 ? "" : text[(nl + 1)..].Trim();
        int dot = section.LastIndexOf('.');
        string? parent = dot > 0 && sections.TryGetValue(new(file, section[..dot]), out var p) ? Heading(p.Split('\n')[0]) : null;
        return new(file, section, title, parent, content);
    }

    private static string Heading(string line) => line.TrimStart('#', ' ');

    private static Dictionary<SourceRef, string> Load(DocSnapshot docs)
    {
        if (_cache is { } c && c.Hash == docs.Hash) return c.Sections;
        var sections = Citations.LoadSections(docs);
        _cache = new(docs.Hash, sections);
        return sections;
    }
}
