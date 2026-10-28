using System.Text.Json;
using Xunit;

namespace Redis.Iris.ContextRetriever.Tests;

public sealed class ContextRetrieverCredentialTests
{
    [Fact]
    public void SecretsAreExcludedFromDiagnosticStringsAndJson()
    {
        var credential = new ContextRetrieverCredential("secret-agent-key", "key-version");
        var token = new ContextRetrieverDelegatedToken("secret-user-jwt", "token-version", DateTimeOffset.UtcNow.AddHours(1));
        Assert.DoesNotContain(credential.AgentKey, credential.ToString());
        Assert.DoesNotContain(token.Token, token.ToString());
        using var keyJson = JsonDocument.Parse(JsonSerializer.Serialize(credential));
        using var tokenJson = JsonDocument.Parse(JsonSerializer.Serialize(token));
        Assert.False(keyJson.RootElement.TryGetProperty("AgentKey", out _));
        Assert.False(tokenJson.RootElement.TryGetProperty("Token", out _));
        Assert.Equal("key-version", keyJson.RootElement.GetProperty("Version").GetString());
        Assert.Equal("token-version", tokenJson.RootElement.GetProperty("Version").GetString());
        credential.Validate();
        token.Validate();
        Assert.Equal("secret-agent-key", credential.AgentKey);
        Assert.Equal("secret-user-jwt", token.Token);
    }
}
