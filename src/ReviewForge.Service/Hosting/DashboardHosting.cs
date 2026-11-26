using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Radzen;
using ReviewForge.Service.Components;

namespace ReviewForge.Service;

public static class DashboardHosting
{
    public static IServiceCollection AddDashboard(this IServiceCollection services)
    {
        // Container rootfs/home is read-only. Process-local keys deliberately invalidate sessions on restart.
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<DashboardSessions>();
        services.AddAuthentication(DashboardSessions.Scheme).AddCookie(DashboardSessions.Scheme, options =>
        {
            options.Cookie.Name = "__Host-ReviewForge";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = DashboardSessions.Lifetime;
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context =>
            {
                // Redirect page navigation only; API and circuit clients must receive 401, not login HTML.
                var request = context.Request;
                if (HttpMethods.IsGet(request.Method) &&
                    string.Equals(request.Path.Value?.TrimEnd('/'), "/dashboard", StringComparison.OrdinalIgnoreCase))
                    context.Response.Redirect($"{request.PathBase}/dashboard/login");
                else
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            options.Events.OnValidatePrincipal = context =>
            {
                if (!context.HttpContext.RequestServices.GetRequiredService<DashboardSessions>().IsValid(context.Principal!))
                    context.RejectPrincipal();
                return Task.CompletedTask;
            };
        });
        services.AddAuthorization();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__Host-ReviewForge-CSRF";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddRateLimiter(options => options.AddPolicy("dashboard-login", context =>
            RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, DashboardAuthenticationStateProvider>();
        services.AddScoped<DashboardBrowserClient>();
        services.AddRadzenComponents();
        return services;
    }

    public static WebApplication MapDashboard(this WebApplication app)
    {
        app.MapGet("/", (HttpContext context) => Results.Redirect($"{context.Request.PathBase}/dashboard"));
        app.MapGet("/dashboard/login", (HttpContext context, IAntiforgery csrf) =>
        {
            if (!context.Request.IsHttps) return Results.StatusCode(400);
            var token = HtmlEncoder.Default.Encode(csrf.GetAndStoreTokens(context).RequestToken!);
            return Results.Content($"""
                <!doctype html><html lang="en"><head><meta charset="utf-8"><title>ReviewForge login</title></head>
                <body><h1>ReviewForge login</h1><form method="post" action="/dashboard/login">
                <input type="hidden" name="__RequestVerificationToken" value="{token}">
                <label>API key <input type="password" name="apiKey" autocomplete="off" required></label>
                <button type="submit">Log in</button></form></body></html>
                """, "text/html");
        });
        app.MapPost("/dashboard/login", LoginAsync).RequireRateLimiting("dashboard-login");
        app.MapPost("/dashboard/logout", async (HttpContext context, IAntiforgery csrf, DashboardSessions sessions) =>
        {
            if (!await ValidCsrf(context, csrf)) return Results.BadRequest();
            sessions.Revoke(context.User);
            await context.SignOutAsync(DashboardSessions.Scheme);
            return Results.Redirect("/dashboard/login");
        }).RequireAuthorization();
        app.MapGet("/dashboard/session", (HttpContext context, IAntiforgery csrf) =>
            Results.Ok(new { token = csrf.GetAndStoreTokens(context).RequestToken }))
            .RequireAuthorization().RequireRateLimiting(ApiKeyOptions.StatusPolicy);
        // Only manifest-listed assets; the development catch-all changes unknown API POSTs into 405s.
        app.Configuration["DisableStaticAssetNotFoundRuntimeFallback"] = "true";
        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode(options =>
                options.ConfigureConnection = connection => connection.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization();
        return app;
    }

    private static async Task<IResult> LoginAsync(HttpContext context, IAntiforgery csrf,
        IOptions<ApiKeyOptions> keys, DashboardSessions sessions)
    {
        if (!context.Request.IsHttps || !await ValidCsrf(context, csrf)) return Results.BadRequest();
        if (!context.Request.HasFormContentType) return Results.BadRequest();
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (form["apiKey"].Count != 1 || !ApiKeyValidator.IsValid(form["apiKey"][0], keys.Value.Keys))
            return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);
        if (sessions.Create() is not { } session) return Results.StatusCode(503);
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "dashboard-user"), new Claim(DashboardSessions.SessionClaim, session.Id),
        ], DashboardSessions.Scheme));
        await context.SignInAsync(DashboardSessions.Scheme, user, new AuthenticationProperties
        {
            IsPersistent = false, AllowRefresh = false, ExpiresUtc = session.Expires,
        });
        return Results.Redirect("/dashboard");
    }

    internal static async Task<bool> ValidCsrf(HttpContext context, IAntiforgery csrf)
    {
        try { await csrf.ValidateRequestAsync(context); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }
}
