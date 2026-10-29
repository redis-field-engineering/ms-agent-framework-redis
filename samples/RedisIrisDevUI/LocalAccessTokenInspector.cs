using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace RedisIrisDevUI;

internal static class LocalAccessTokenInspector
{
    public static IResult FreshDownload(HttpContext context)
    {
        PreventCaching(context);
        // Always obtain a new authorization-code token response, even if the local
        // authentication cookie still contains a previously issued access token.
        return Results.Challenge(new AuthenticationProperties
        {
            RedirectUri = context.Request.PathBase + "/debug/jwt?download=true",
        }, [TravelAuthentication.OpenIdConnectScheme]);
    }

    public static IResult Inspect(HttpContext context, string? accessToken)
    {
        PreventCaching(context);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Results.Problem("The authenticated ticket does not contain an access token.");
        }

        var inspected = TravelAuthentication.InspectAccessToken(accessToken);
        var response = new
        {
            warning = "Development-only output. This bearer token grants access until it expires; keep it private and delete saved copies after testing.",
            accessToken = inspected.Token,
            header = inspected.Header,
            claims = inspected.Claims,
        };
        return context.Request.Query["download"] == "true"
            ? Results.File(JsonSerializer.SerializeToUtf8Bytes(response), "application/json", "delegated-jwt.json")
            : Results.Json(response);
    }

    private static void PreventCaching(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
