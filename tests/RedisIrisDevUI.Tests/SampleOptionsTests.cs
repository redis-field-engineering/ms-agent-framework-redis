// Copyright (c) Redis, Inc.

using System.Text.Json;
using Redis.Iris.AgentMemory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RedisIrisDevUI.Tests;

public sealed class SampleOptionsTests
{
    [Fact]
    public void SingleUserModeKeepsLegacyIdentityAndDefaults()
    {
        var settings = CreateOptions();

        Assert.Equal(AgentMemoryApiMode.Legacy, settings.ApiMode);
        Assert.False(settings.AuthenticationRequired);
        Assert.False(settings.AccessTokenInspectorEnabled);
        Assert.Equal(4, settings.ShortTermWindow);
        Assert.Equal("gpt-4.1-mini", settings.Model);
        Assert.Equal("redis-iris-devui-shopper", settings.CreateLegacyUser().TravelerId);
        Assert.NotNull(settings.ContextRetrieverCredential);
    }

    [Fact]
    public void LocalUsersDoNotRequireAnEnvironmentAgentKey()
    {
        var users = new[]
        {
            new TravelUser("avery", "Avery", "tenant", "traveler-001", "token-1", "key-1", "v1", null, null, null),
            new TravelUser("riley", "Riley", "tenant", "traveler-002", "token-2", "key-2", "v1", null, null, null),
        };
        var settings = CreateOptions(new()
        {
            ["DEVUI_USERS_JSON"] = JsonSerializer.Serialize(users),
            ["REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY"] = null,
        });

        Assert.True(settings.AuthenticationRequired);
        Assert.Null(settings.ContextRetrieverCredential);
        Assert.Equal("traveler-002", settings.UserDirectory.Authenticate("token-2")?.TravelerId);
    }

    [Fact]
    public void OidcModeIgnoresTheLocalUserDirectoryAndDerivesTheContextScope()
    {
        var settings = CreateOptions(OidcConfiguration());

        Assert.True(settings.AuthenticationRequired);
        Assert.False(settings.UserDirectory.IsMultiUser);
        Assert.NotNull(settings.ContextRetrieverCredential);
        Assert.Equal("https://issuer.example", settings.Oidc?.Authority);
        Assert.Equal("https://context.example/context.read", settings.Oidc?.ContextScope);
    }

    [Theory]
    [InlineData("REDIS_IRIS_API_MODE", "invalid")]
    [InlineData("REDIS_IRIS_API_MODE", "99")]
    [InlineData("REDIS_IRIS_SHORT_TERM_WINDOW", "0")]
    [InlineData("REDIS_IRIS_SHORT_TERM_WINDOW", "invalid")]
    [InlineData("DEVUI_ALLOW_REMOTE_ACCESS", "invalid")]
    [InlineData("REDIS_IRIS_DEMO_TELEMETRY", "invalid")]
    [InlineData("REDIS_IRIS_RAM_DELEGATED_AUTH", "true")]
    public void InvalidSettingsFailAtStartup(string name, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CreateOptions(new() { [name] = value }));
        Assert.Contains(name, error.Message);
    }

    [Theory]
    [InlineData("DEVUI_OIDC_AUTHORITY", "http://issuer.example")]
    [InlineData("DEVUI_OIDC_LOGOUT_ENDPOINT", "http://issuer.example/logout")]
    [InlineData("DEVUI_OIDC_POST_LOGOUT_REDIRECT_URI", "relative-path")]
    public void InvalidOidcEndpointsFailAtStartup(string name, string value)
    {
        var configuration = OidcConfiguration();
        configuration[name] = value;

        var error = Assert.Throws<InvalidOperationException>(() => CreateOptions(configuration));
        Assert.Contains(name, error.Message);
    }

    [Fact]
    public void IrisApiModeCanBeSelectedForSelfManagedDeployments()
    {
        var settings = CreateOptions(new() { ["REDIS_IRIS_API_MODE"] = "iris" });
        Assert.Equal(AgentMemoryApiMode.Iris, settings.ApiMode);
    }

    private static Dictionary<string, string?> OidcConfiguration() => new()
    {
        ["DEVUI_AUTH_MODE"] = "oidc",
        ["DEVUI_USERS_JSON"] = "unused invalid local user JSON",
        ["DEVUI_OIDC_AUTHORITY"] = "https://issuer.example/",
        ["DEVUI_OIDC_CLIENT_ID"] = "test-client",
        ["DEVUI_OIDC_AUDIENCE"] = "https://context.example/",
        ["DEVUI_OIDC_LOGOUT_ENDPOINT"] = "https://issuer.example/logout",
        ["DEVUI_OIDC_POST_LOGOUT_REDIRECT_URI"] = "http://localhost:5080/",
    };

    private static SampleOptions CreateOptions(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["REDIS_IRIS_ENDPOINT"] = "https://ram.example",
            ["REDIS_IRIS_STORE_ID"] = "test-store",
            ["REDIS_IRIS_API_KEY"] = "test-ram-key",
            ["OPENAI_API_KEY"] = "test-model-key",
            ["REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT"] = "https://context.example/mcp",
            ["REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY"] = "test-agent-key",
        };
        foreach (var pair in overrides ?? []) values[pair.Key] = pair.Value;
        return new SampleOptions(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }
}
