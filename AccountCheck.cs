using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Identity.Web;

namespace EofBot;

// Asks Microsoft Graph whether the account is still enabled. The login cookie is long-lived, so this is the only place
// that notices an account disabled/deleted in Entra after sign-in
public sealed class AccountCheck(ITokenAcquisition tokens, HttpClient http, IMemoryCache cache, ILogger<AccountCheck> logger)
{
    static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    // id = object id (oid) or UPN
    public async Task<bool> IsActiveAsync(string id)
    {
        if (cache.TryGetValue(id, out bool active)) return active;
        try
        {
            string token = await tokens.GetAccessTokenForAppAsync(
                "https://graph.microsoft.com/.default", authenticationScheme: OpenIdConnectDefaults.AuthenticationScheme, tenant: null);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(id)}?$select=accountEnabled");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await http.SendAsync(req);
            if (res.StatusCode == HttpStatusCode.NotFound)
                active = false;
            else
            {
                res.EnsureSuccessStatusCode();
                active = (await res.Content.ReadFromJsonAsync<GraphUser>())?.AccountEnabled == true;
            }
        }
        catch (Exception ex)
        {
            // On Graph/network errors let the user through (not cached) instead of signing everyone out
            logger.LogWarning(ex, "Không kiểm tra được tài khoản {Id} với Microsoft Graph", id);
            return true;
        }
        cache.Set(id, active, CacheFor);
        return active;
    }

    sealed record GraphUser(bool? AccountEnabled);
}
