using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace RedisIrisDevUI.Tests;

public sealed class LocalOidcLoginTests
{
    [Theory]
    [InlineData("localhost:5080", "http", true, false)]
    [InlineData("127.0.0.1:5080", "http", true, false)]
    [InlineData("[::1]:5080", "http", true, false)]
    [InlineData("localhost:5080", "https", true, true)]
    [InlineData("example.com", "http", true, true)]
    [InlineData("localhost.example.com", "http", true, true)]
    [InlineData("localhost:5080", "http", false, true)]
    public void CookieCompatibilityIsLimitedToDevelopmentHttpLoopback(string host, string scheme, bool development, bool secure)
    {
        var options = new OpenIdConnectOptions();
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Scheme = scheme;
        context.Request.PathBase = "/sample";
        var now = DateTimeOffset.UtcNow;
        var original = new[] { options.CorrelationCookie.Build(context, now), options.NonceCookie.Build(context, now) };
        LocalOidcLogin.Configure(options, development);
        Assert.Equal("query", options.ResponseMode);
        Assert.True(options.UsePkce);
        Assert.True(options.ProtocolValidator.RequireNonce);
        var cookies = new[] { options.CorrelationCookie.Build(context, now), options.NonceCookie.Build(context, now) };
        for (var i = 0; i < cookies.Length; i++)
        {
            Assert.Equal(secure, cookies[i].Secure);
            Assert.Equal(secure ? SameSiteMode.None : SameSiteMode.Lax, cookies[i].SameSite);
            Assert.True(cookies[i].HttpOnly);
            Assert.Equal(original[i].Path, cookies[i].Path);
            Assert.Equal(original[i].Expires, cookies[i].Expires);
        }
    }

    [Fact]
    public async Task FailedCallbackDoesNotAuthenticateOrReflectUntrustedDetails()
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/sample";
        context.Request.QueryString = new("?returnUrl=https://evil.example&code=private-code");
        var failure = new RemoteFailureContext(context, new AuthenticationScheme("Cognito", null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(), new Exception("private provider details"));

        await LocalOidcLogin.HandleRemoteFailure(failure);

        Assert.True(failure.Result!.Handled);
        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
        Assert.Equal("/sample/login-failed", context.Response.Headers.Location.ToString());
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
    }
}
