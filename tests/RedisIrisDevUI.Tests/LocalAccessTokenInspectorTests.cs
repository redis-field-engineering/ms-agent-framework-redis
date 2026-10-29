using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace RedisIrisDevUI.Tests;

public sealed class LocalAccessTokenInspectorTests
{
    [Fact]
    public void FreshDownloadAlwaysChallengesWithFixedLocalReturnPath()
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/sample";
        context.Request.QueryString = new("?returnUrl=https://untrusted.example&download=elsewhere");

        var result = Assert.IsType<ChallengeHttpResult>(LocalAccessTokenInspector.FreshDownload(context));

        Assert.Equal([TravelAuthentication.OpenIdConnectScheme], result.AuthenticationSchemes);
        Assert.Equal("/sample/debug/jwt?download=true", result.Properties!.RedirectUri);
        Assert.Equal("no-store, max-age=0", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
    }

    [Fact]
    public void DownloadContainsCurrentTicketTokenWithFixedFilenameAndNoCaching()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new("?download=true&filename=untrusted.json");
        var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"test\"}")) + "." +
            WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("{\"sub\":\"test-user\"}")) + ".test";

        var result = Assert.IsType<FileContentHttpResult>(LocalAccessTokenInspector.Inspect(context, token));

        Assert.Equal("application/json", result.ContentType);
        Assert.Equal("delegated-jwt.json", result.FileDownloadName);
        using var document = JsonDocument.Parse(result.FileContents);
        Assert.Equal(token, document.RootElement.GetProperty("accessToken").GetString());
        Assert.Equal("test-user", document.RootElement.GetProperty("claims").GetProperty("sub").GetString());
        Assert.Equal("no-store, max-age=0", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-cache", context.Response.Headers.Pragma.ToString());
        Assert.Equal("0", context.Response.Headers.Expires.ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
    }

    [Fact]
    public void MissingTokenCannotProduceDownload()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new("?download=true");

        Assert.IsType<ProblemHttpResult>(LocalAccessTokenInspector.Inspect(context, null));
        Assert.Equal("no-store, max-age=0", context.Response.Headers.CacheControl.ToString());
    }
}
