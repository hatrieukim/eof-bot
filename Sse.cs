using System.Text.Json;

namespace EofBot;

// .NET 8 has no TypedResults.ServerSentEvents, so write the format "event: ...\ndata: ...\n\n"
public static class Sse
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Start(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
    }

    public static async Task WriteAsync(HttpResponse response, string evt, object data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {evt}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
