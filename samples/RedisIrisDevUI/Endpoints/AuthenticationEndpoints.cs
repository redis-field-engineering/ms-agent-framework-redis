// Copyright (c) Redis, Inc.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.WebUtilities;

namespace RedisIrisDevUI;

internal static class AuthenticationEndpoints
{
    public static void MapTravelAuthentication(this WebApplication app, SampleOptions settings)
    {
        if (!settings.AuthenticationRequired) return;

        if (settings.Oidc is { } oidc)
        {
            app.MapGet("/login-failed", LocalOidcLogin.FailurePage).AllowAnonymous();
            app.MapGet("/login", () => Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/" },
                [TravelAuthentication.OpenIdConnectScheme]));
            app.MapPost("/logout", async (HttpContext httpContext) =>
            {
                await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                var logoutUrl = QueryHelpers.AddQueryString(oidc.LogoutEndpoint, new Dictionary<string, string?>
                {
                    ["client_id"] = oidc.ClientId,
                    ["logout_uri"] = oidc.PostLogoutRedirectUri,
                });
                return Results.Redirect(logoutUrl);
            }).RequireAuthorization().DisableAntiforgery();
            if (settings.AccessTokenInspectorEnabled)
            {
                app.MapGet("/debug/jwt/fresh", LocalAccessTokenInspector.FreshDownload);
                app.MapGet("/debug/jwt", async (HttpContext httpContext) =>
                {
                    var accessToken = await httpContext.GetTokenAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        "access_token");
                    return LocalAccessTokenInspector.Inspect(httpContext, accessToken);
                }).RequireAuthorization();
            }
        }
        else
        {
            app.MapGet("/login", () => Results.Content(SamplePages.Login(), "text/html"));
            app.MapPost("/login", async (HttpContext httpContext) =>
            {
                var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
                var user = settings.UserDirectory.Authenticate(form["token"].ToString());
                if (user is null) return Results.Content(SamplePages.Login("Invalid sign-in token."), "text/html", statusCode: StatusCodes.Status401Unauthorized);

                var principal = TravelUserDirectory.CreatePrincipal(user, CookieAuthenticationDefaults.AuthenticationScheme);
                await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
                return Results.Redirect($"/devui/?entity_id={TravelAgentRegistration.AgentName}");
            }).DisableAntiforgery();
            app.MapPost("/logout", async (HttpContext httpContext) =>
            {
                await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect("/login");
            }).RequireAuthorization().DisableAntiforgery();
        }
    }
}
