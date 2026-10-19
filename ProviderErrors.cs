using System.ClientModel;
using System.ClientModel.Primitives;

namespace EofBot;

// Turns CLIProxyAPI errors for exhausted or overloaded AI accounts into messages users understand.
// When every account for the model is exhausted the proxy returns 429 (code model_cooldown) with Retry-After
public static class ProviderErrors
{
    public static string? Describe(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is not ClientResultException { Status: 429 or 503 } api) continue;
            if (api.Status == 503) return "Hệ thống AI đang tạm quá tải. Vui lòng thử lại sau ít phút.";

            string wait = api.GetRawResponse()?.Headers.TryGetValue("Retry-After", out var v) == true
                && int.TryParse(v, out int seconds) && seconds > 0
                ? $", dự kiến có lại sau khoảng {Duration(TimeSpan.FromSeconds(seconds))}"
                : "";
            return $"Tài khoản AI của hệ thống đã hết hạn mức sử dụng{wait}. Vui lòng thử lại sau.";
        }
        return null;
    }

    private static string Duration(TimeSpan t) =>
        t.TotalMinutes < 1 ? "1 phút"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} phút"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours} giờ {t.Minutes} phút"
        : $"{(int)t.TotalDays} ngày {t.Hours} giờ";
}

// The OpenAI SDK retries 429 and waits for Retry-After by default; an exhausted proxy returns Retry-After of many hours, hanging the user.
// The proxy already rotates accounts, so a 429 means all are exhausted and retrying is pointless
public sealed class ProxyRetryPolicy : ClientRetryPolicy
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    protected override bool ShouldRetry(PipelineMessage message, Exception? exception) =>
        message.Response?.Status != 429 && base.ShouldRetry(message, exception);

    protected override ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception) =>
        message.Response?.Status == 429 ? ValueTask.FromResult(false) : base.ShouldRetryAsync(message, exception);

    protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount)
    {
        var delay = base.GetNextDelay(message, tryCount);
        return delay < MaxDelay ? delay : MaxDelay;
    }
}
