using System.Security.Cryptography;
using System.Text;

namespace EofBot;

public sealed record DocFile(string Path, string Content);

public sealed record DocSnapshot(IReadOnlyList<DocFile> Files, string Combined, string Hash, DateTimeOffset LoadedAt);

public sealed class DocStore(string docsPath, ILogger<DocStore> logger)
{
    public string DocsPath { get; } = docsPath;

    public DocSnapshot Current { get; private set; } = new([], "", "", DateTimeOffset.MinValue);

    public event Action<DocSnapshot>? Loaded;

    public DocSnapshot Load()
    {
        var files = Directory.Exists(DocsPath)
            ? Directory.EnumerateFiles(DocsPath, "*.md", SearchOption.AllDirectories)
                .Select(f => new DocFile(
                    Path.GetRelativePath(DocsPath, f).Replace('\\', '/'),
                    File.ReadAllText(f, Encoding.UTF8).Replace("\r\n", "\n").Trim()))
                // Stable order so the prompt cache hits and the hash does not change for no reason
                .OrderBy(d => d.Path, StringComparer.Ordinal)
                .ToList()
            : [];

        Current = Build(files);
        logger.LogInformation("Đã nạp {Count} file, {Chars:N0} ký tự, hash {Hash} từ {Path}",
            files.Count, Current.Combined.Length, Current.Hash, DocsPath);
        Loaded?.Invoke(Current);
        return Current;
    }

    public static DocSnapshot Build(IReadOnlyList<DocFile> files)
    {
        var sb = new StringBuilder();
        foreach (var doc in files)
        {
            sb.Append("<document path=\"").Append(doc.Path).Append("\">\n")
              .Append(doc.Content)
              .Append("\n</document>\n\n");
        }
        string combined = sb.ToString();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(combined)))[..12].ToLowerInvariant();

        return new DocSnapshot(files, combined, hash, DateTimeOffset.Now);
    }
}
