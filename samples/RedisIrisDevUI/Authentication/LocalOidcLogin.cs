using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace RedisIrisDevUI;

internal static class LocalOidcLogin
{
    public static void Configure(OpenIdConnectOptions options, bool development)
    {
        // Lax cookies are sent on the top-level GET callback, not a cross-site form POST.
        options.ResponseMode = "query";
        options.CorrelationCookie = new LoopbackCookieBuilder(options.CorrelationCookie, development);
        options.NonceCookie = new LoopbackCookieBuilder(options.NonceCookie, development);
    }

    public static Task HandleRemoteFailure(RemoteFailureContext context)
    {
        // Never accept the failed callback, replay it, or reflect provider errors/state into HTML.
        context.HandleResponse();
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Redirect(context.Request.PathBase + "/login-failed");
        return Task.CompletedTask;
    }

    public static IResult FailurePage(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Results.Content("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Sign in again</title>
            <h1>Sign-in could not be completed</h1>
            <p>The sign-in attempt expired or its browser cookie was missing. Start a fresh sign-in
            in this browser. Use the same localhost address throughout; do not reuse a copied Cognito URL.</p>
            <p><a href="login">Start a new sign-in</a></p></html>
            """, "text/html", statusCode: StatusCodes.Status400BadRequest);
    }

    private sealed class LoopbackCookieBuilder : CookieBuilder
    {
        private readonly CookieBuilder _original;
        private readonly bool _development;

        public LoopbackCookieBuilder(CookieBuilder original, bool development)
        {
            _original = original;
            _development = development;
            Name = original.Name;
            HttpOnly = original.HttpOnly;
            IsEssential = original.IsEssential;
            SameSite = original.SameSite;
            SecurePolicy = original.SecurePolicy;
            Expiration = original.Expiration;
            MaxAge = original.MaxAge;
            Path = original.Path;
            Domain = original.Domain;
        }

        public override CookieOptions Build(HttpContext context, DateTimeOffset expiresFrom)
        {
            // Preserve the framework's callback/path-base and expiration handling.
            var cookie = _original.Build(context, expiresFrom);
            var host = context.Request.Host.Host;
            var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
            if (_development && !context.Request.IsHttps && loopback)
            {
                // Safari versions before 27 reject Secure cookies on plain HTTP localhost.
                cookie.Secure = false;
                cookie.SameSite = SameSiteMode.Lax;
            }
            return cookie;
        }
    }
}
