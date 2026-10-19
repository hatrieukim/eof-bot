using Microsoft.Extensions.AI;

namespace EofBot;

public static class ChatImages
{
    public const int MaxCount = 4;
    public const int MaxBytes = 5 * 1024 * 1024;

    private static readonly HashSet<string> AllowedTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    // The client sends data URLs ("data:image/png;base64,..."); errors are returned as user-facing messages
    public static (List<DataContent> Images, string? Error) Parse(IReadOnlyList<string>? dataUrls)
    {
        var images = new List<DataContent>();
        if (dataUrls is null or { Count: 0 }) return (images, null);
        if (dataUrls.Count > MaxCount) return (images, $"Mỗi câu gửi tối đa {MaxCount} ảnh.");

        foreach (string url in dataUrls)
        {
            DataContent image;
            try { image = new DataContent(url); }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException) { return (images, "Ảnh không đọc được."); }

            if (!AllowedTypes.Contains(image.MediaType)) return (images, $"Không hỗ trợ định dạng {image.MediaType}, chỉ nhận PNG, JPEG, WebP, GIF.");
            if (image.Data.Length > MaxBytes) return (images, $"Ảnh lớn hơn {MaxBytes / 1024 / 1024}MB.");
            images.Add(image);
        }
        return (images, null);
    }
}
