// Copyright (c) Redis, Inc.

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace RedisIrisDevUI;

internal static class AuthenticationRegistration
{
    public static void AddTravelAuthentication(this WebApplicationBuilder builder, SampleOptions settings)
    {
        if (!settings.AuthenticationRequired) return;

        var ticketStore = new InMemoryAuthenticationTicketStore();
        builder.Services.AddSingleton(ticketStore);
        var authentication = builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = settings.Oidc is not null
                ? TravelAuthentication.OpenIdConnectScheme
                : CookieAuthenticationDefaults.AuthenticationScheme;
        });
        authentication.AddCookie(options =>
        {
            options.Cookie.Name = "redis-iris-devui-user";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = settings.Oidc is not null ? SameSiteMode.Lax : SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
            options.SessionStore = ticketStore;
        });
        if (settings.Oidc is { } oidc)
        {
            authentication.AddOpenIdConnect(TravelAuthentication.OpenIdConnectScheme, options =>
            {
                options.Authority = oidc.Authority;
                options.ClientId = oidc.ClientId;
                options.CallbackPath = "/signin-oidc";
                options.SignedOutCallbackPath = "/signout-callback-oidc";
                options.ResponseType = "code";
                options.UsePkce = true;
                LocalOidcLogin.Configure(options, builder.Environment.IsDevelopment());
                options.SaveTokens = true;
                options.UseTokenLifetime = true;
                options.MapInboundClaims = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("email");
                options.Scope.Add(oidc.ContextScope);
                options.TokenValidationParameters.NameClaimType = "email";
                options.Events = new OpenIdConnectEvents
                {
                    OnRemoteFailure = LocalOidcLogin.HandleRemoteFailure,
                    OnRedirectToIdentityProvider = context =>
                    {
                        context.ProtocolMessage.SetParameter("resource", oidc.Audience);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context => ValidateAccessTokenAsync(context, oidc),
                };
            });
        }
        builder.Services.AddAuthorization();
    }

    private static async Task ValidateAccessTokenAsync(TokenValidatedContext context, OidcSettings oidc)
    {
        try
        {
            var accessToken = context.TokenEndpointResponse?.AccessToken;
            var configurationManager = context.Options.ConfigurationManager;
            if (string.IsNullOrWhiteSpace(accessToken) || configurationManager is null || context.Principal is null)
            {
                context.Fail("Cognito did not return a complete authorization-code token response.");
                return;
            }

            var discovery = await configurationManager.GetConfigurationAsync(
                context.HttpContext.RequestAborted).ConfigureAwait(false);
            var validation = await context.Options.TokenHandler.ValidateTokenAsync(accessToken, new TokenValidationParameters
            {
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = discovery.SigningKeys,
                ValidateIssuer = true,
                ValidIssuer = oidc.Authority,
                ValidateAudience = true,
                ValidAudience = oidc.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                NameClaimType = "sub",
            }).ConfigureAwait(false);
            if (!validation.IsValid || validation.ClaimsIdentity is null)
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("RedisIrisDevUI.Oidc")
                    .LogWarning(
                        validation.Exception,
                        "Rejected the Cognito access token during signature, issuer, audience, or lifetime validation: {ValidationError}",
                        validation.Exception?.Message ?? "No validation exception was provided.");
                context.Fail("Cognito returned an invalid Context Retriever access token.");
                return;
            }

            TravelAuthentication.ApplyValidatedAccessTokenClaims(
                context.Principal,
                validation.ClaimsIdentity,
                oidc.ContextScope);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("RedisIrisDevUI.Oidc")
                .LogWarning(exception, "Rejected the Cognito access token.");
            context.Fail("Cognito access-token validation failed.");
        }
    }
}
