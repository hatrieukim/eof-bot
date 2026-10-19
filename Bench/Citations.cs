using System.Text.RegularExpressions;

namespace EofBot.Bench;

// Section = null means only the file name was cited
public sealed record SourceRef(string File, string? Section);

public static partial class Citations
{
    public const string Fallback = "Tài liệu hiện có không đề cập đến nội dung này.";

    // "a.md § 5.5, § 10; b.md § 7.4–7.6" → (a.md,5.5) (a.md,10) (b.md,7.4) (b.md,7.5) ...
    public static HashSet<SourceRef> ParseSources(string text)
    {
        var result = new HashSet<SourceRef>();
        string? current = null;
        foreach (var raw in SeparatorRegex().Split(text))
        {
            var m = FileRegex().Match(raw);
            if (m.Success)
            {
                current = m.Groups[1].Value;
                result.Add(new(current, null));
            }
            if (current is null) continue;

            foreach (Match r in RangeRegex().Matches(raw))
            {
                foreach (var s in ExpandRange(r.Groups[1].Value.TrimEnd('.'), r.Groups[2].Value.TrimEnd('.')))
                    result.Add(new(current, s));
            }
            foreach (Match s in SectionRegex().Matches(RangeRegex().Replace(raw, "")))
                result.Add(new(current, s.Groups[1].Value.TrimEnd('.')));
        }
        return result;
    }

    // "4.1".."4.3" → 4.1, 4.2, 4.3; different levels or parents: only the two ends
    private static IEnumerable<string> ExpandRange(string a, string b)
    {
        int ia = a.LastIndexOf('.'), ib = b.LastIndexOf('.');
        if (ia > 0 && ib > 0 && a[..ia] == b[..ib]
            && int.TryParse(a[(ia + 1)..], out int x) && int.TryParse(b[(ib + 1)..], out int y))
            return Enumerable.Range(x, Math.Max(0, y - x + 1)).Select(i => $"{a[..ia]}.{i}");
        if (int.TryParse(a, out x) && int.TryParse(b, out y))
            return Enumerable.Range(x, Math.Max(0, y - x + 1)).Select(i => i.ToString());
        return [a, b];
    }

    private static bool SectionMatch(string a, string b) =>
        a == b || a.StartsWith(b + ".") || b.StartsWith(a + ".");

    public static HashSet<SourceRef> Cited(string answer)
    {
        var cited = new HashSet<SourceRef>();
        foreach (Match m in CiteRegex().Matches(answer)) cited.UnionWith(ParseSources(m.Groups[1].Value));
        return cited;
    }

    public static string Check(string kind, string answer, string expectedSources)
    {
        if (kind == "Không có") return answer.Contains(Fallback) ? "fallback" : "không fallback";

        var cited = Cited(answer);
        if (cited.Count == 0) return "không trích";

        var expected = ParseSources(expectedSources);
        var expectedSecs = expected.Where(e => e.Section is not null).ToList();
        if (cited.Any(c => c.Section is { } s && expectedSecs.Any(e => e.File == c.File && SectionMatch(s, e.Section!))))
            return "khớp";
        return cited.Select(c => c.File).Intersect(expected.Select(e => e.File)).Any() ? "lệch mục" : "khác file";
    }

    // {(a.md, 4.8): section text}; section "## 4." includes its subsections
    public static Dictionary<SourceRef, string> LoadSections(DocSnapshot docs)
    {
        var sections = new Dictionary<SourceRef, string>();
        foreach (var doc in docs.Files)
        {
            string name = Path.GetFileName(doc.Path);
            var lines = doc.Content.Split('\n');
            var open = new Stack<(int Level, string Key, int Start)>();

            void Close(int uptoLevel, int end)
            {
                while (open.Count > 0 && open.Peek().Level >= uptoLevel)
                {
                    var (_, key, start) = open.Pop();
                    sections[new(name, key)] = string.Join('\n', lines[start..end]).Trim();
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var m = HeadingRegex().Match(lines[i]);
                if (!m.Success) continue;
                Close(m.Groups[1].Length, i);
                open.Push((m.Groups[1].Length, m.Groups[2].Value, i));
            }
            Close(2, lines.Length);
        }
        return sections;
    }

    [GeneratedRegex(@"\(Nguồn:\s*([^)]*)\)")]
    private static partial Regex CiteRegex();

    [GeneratedRegex(@"[;,]")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"([\w-]+\.md)")]
    private static partial Regex FileRegex();

    [GeneratedRegex(@"§\s*([\d.]+)\s*[–-]\s*([\d.]+)")]
    private static partial Regex RangeRegex();

    [GeneratedRegex(@"§\s*([\d.]+)")]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"^(#{2,3}) (\d+(?:\.\d+)?)\.? ")]
    private static partial Regex HeadingRegex();
}
