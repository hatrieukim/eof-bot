using System.Text;
using System.Text.RegularExpressions;

namespace EofBot;

public sealed record DocContent(string Content);

public static partial class AdminDocs
{
    private const int MaxChars = 1_000_000;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static void MapAdminDocs(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/docs").RequireAuthorization(Auth.AdminPolicy);

        group.MapGet("", (DocStore store) => List(store));

        group.MapGet("{name}", (string name, bool? download, DocStore store) =>
        {
            if (Resolve(store, name) is not { } path || !File.Exists(path)) return Results.NotFound();
            return download == true
                ? Results.File(File.ReadAllBytes(path), "text/markdown; charset=utf-8", name)
                : Results.Ok(new { name, content = File.ReadAllText(path, Encoding.UTF8) });
        });

        group.MapPut("{name}", (string name, DocContent body, DocStore store) =>
        {
            if (Resolve(store, name) is not { } path) return Results.BadRequest("Tên file không hợp lệ (chỉ chữ, số, gạch, chấm, đuôi .md)");
            if (body.Content is null) return Results.BadRequest("Thiếu content");
            if (body.Content.Length > MaxChars) return Results.BadRequest("File quá lớn (tối đa 1 triệu ký tự)");

            Directory.CreateDirectory(store.DocsPath);
            File.WriteAllText(path, body.Content.Replace("\r\n", "\n"), Utf8NoBom);
            store.Load();
            return Results.Ok(List(store));
        });

        group.MapDelete("{name}", (string name, DocStore store) =>
        {
            if (Resolve(store, name) is not { } path || !File.Exists(path)) return Results.NotFound();
            File.Delete(path);
            store.Load();
            return Results.Ok(List(store));
        });
    }

    private static object List(DocStore store)
    {
        var dir = new DirectoryInfo(store.DocsPath);
        var files = dir.Exists
            ? dir.EnumerateFiles("*.md")
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => new
                {
                    name = f.Name,
                    bytes = f.Length,
                    modified = f.LastWriteTime,
                    sections = HeadingRegex().Matches(File.ReadAllText(f.FullName, Encoding.UTF8)).Count,
                })
                .ToList()
            : [];
        return new { hash = store.Current.Hash, loadedAt = store.Current.LoadedAt, totalChars = store.Current.Combined.Length, files };
    }

    // Flat file names only: no subfolders, no "..", so nothing is written outside DocsPath
    private static string? Resolve(DocStore store, string name) =>
        NameRegex().IsMatch(name) && !name.Contains("..") && Path.GetFileName(name) == name
            ? Path.Combine(store.DocsPath, name)
            : null;

    [GeneratedRegex(@"^[\w][\w\-. ]*\.md$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^#{1,6} ", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();
}
