using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EofBot;

// Used when sign-in is disabled: each browser gets a "khach-xxxxxxxx" (guest) id in a cookie,
// so it still has its own chat history and question quota, as when signed in
public sealed class AnonymousAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Anonymous";
    const string CookieName = "eof_anon";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Cookies[CookieName] is not { } key || !key.StartsWith("khach-"))
        {
            key = "khach-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            Response.Cookies.Append(CookieName, key, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddYears(1),
            });
        }
        var identity = new ClaimsIdentity([new Claim("preferred_username", key)], Scheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}
