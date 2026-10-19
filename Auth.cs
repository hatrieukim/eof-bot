using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using EofBot.Eval;
using Microsoft.Identity.Web;

namespace EofBot;

public static class Auth
{
    public const string AdminPolicy = "Admin";

    // Auth:Enabled=false disables Microsoft sign-in (e.g. a host without HTTPS, where Entra rejects the redirect URI).
    // Each browser gets an anonymous id and everyone is an admin
    public static bool Enabled(IConfiguration config) => config.GetValue("Auth:Enabled", true);

    public static void AddEntraAuth(this WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        builder.Services.AddAuthorization(o =>
        {
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            o.AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().RequireAssertion(ctx => ctx.User.IsAdmin(config)));
        });

        if (!Enabled(config))
        {
            builder.Services.AddAuthentication(AnonymousAuth.Scheme)
                .AddScheme<AuthenticationSchemeOptions, AnonymousAuth>(AnonymousAuth.Scheme, null);
            return;
        }

        // Cookie is the default scheme even for challenges: unauthenticated users go to /login instead of straight to Microsoft
        builder.Services
            .AddAuthentication(o =>
            {
                o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddMicrosoftIdentityWebApp(
                o => builder.Configuration.Bind("AzureAd", o),
                cookie =>
                {
                    // Long lifetime so users rarely sign in again; disabled accounts are kicked out by OnValidatePrincipal
                    cookie.ExpireTimeSpan = TimeSpan.FromDays(365);
                    cookie.SlidingExpiration = true;
                    cookie.Events.OnValidatePrincipal = async ctx =>
                    {
                        var check = ctx.HttpContext.RequestServices.GetRequiredService<AccountCheck>();
                        if (ctx.Principal?.GetObjectId() is { } oid && await check.IsActiveAsync(oid)) return;
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    };
                    cookie.Events.OnRedirectToLogin = ctx =>
                    {
                        // fetch cannot follow a redirect to the login page, so return 401 and let JS handle it
                        if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/v1"))
                            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        else
                            ctx.Response.Redirect("/login");
                        return Task.CompletedTask;
                    };
                    // By default the cookie handler redirects to an AccessDenied page the app does not have
                    cookie.Events.OnRedirectToAccessDenied = ctx =>
                    {
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    };
                })
            // App-only token (client credentials) for calling Graph in AccountCheck
            .EnableTokenAcquisitionToCallDownstreamApi()
            .AddInMemoryTokenCaches();
        builder.Services.AddHttpClient<AccountCheck>();
    }

    // /admin/* pages are static files, not endpoints, so they cannot carry a policy and are guarded by middleware.
    // Placed after UseAuthorization: unauthenticated users were already sent to /login by FallbackPolicy
    public static void UseAdminPages(this WebApplication app) =>
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/admin"), a => a.Use(async (ctx, next) =>
        {
            if (ctx.User.IsAdmin(app.Configuration)) { await next(); return; }
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("Chỉ admin mới vào được trang này");
        }));

    public static void MapAuth(this WebApplication app)
    {
        bool enabled = Enabled(app.Configuration);
        app.MapGet("/api/me", (ClaimsPrincipal user) =>
            new { email = user.UserKey(), name = user.FindFirstValue("name"), isAdmin = user.IsAdmin(app.Configuration), anonymous = !enabled });
        if (!enabled)
        {
            app.MapGet("/login", () => Results.Redirect("/")).AllowAnonymous();
            return;
        }

        string loginPage = Path.Combine(app.Environment.WebRootPath, "login.html");
        app.MapGet("/login", () => Results.File(loginPage, "text/html; charset=utf-8")).AllowAnonymous();

        // select_account: after sign-out the user can pick another account instead of silently reusing the old one.
        // IsPersistent: without it the cookie dies with the browser and ExpireTimeSpan is meaningless
        app.MapGet("/auth/login", () => Results.Challenge(
            new OpenIdConnectChallengeProperties { RedirectUri = "/", Prompt = "select_account", IsPersistent = true },
            [OpenIdConnectDefaults.AuthenticationScheme])).AllowAnonymous();

        // Only clears the app cookie; does not sign out of Microsoft (Outlook, Teams... in the browser stay signed in)
        app.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });
    }

    // Sign-in email (UPN), used as user_name in the DB
    public static string UserKey(this ClaimsPrincipal user) =>
        (user.FindFirstValue("preferred_username") ?? throw new InvalidOperationException("Token thiếu preferred_username"))
        .ToLowerInvariant();

    // Re-reads config every time: editing "Admins" takes effect immediately, no restart needed
    public static bool IsAdmin(this ClaimsPrincipal user, IConfiguration config) =>
        user.Identity?.IsAuthenticated == true && (!Enabled(config) || IsAdminKey(user.UserKey(), config));

    public static bool IsAdminKey(string userKey, IConfiguration config) =>
        (config.GetSection("Admins").Get<string[]>() ?? []).Contains(userKey, StringComparer.OrdinalIgnoreCase);

    // Admins (including test chats as admin-thử) have no daily quota
    public static bool QuotaExempt(string userKey, IConfiguration config) =>
        userKey == EvalSampler.AdminTestUser || IsAdminKey(userKey, config);
}
