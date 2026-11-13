using System.Net;
using Xunit;

namespace Redis.Iris.AgentMemory.Management.Tests;

// Opt-in: permanently registers one reusable test type on an explicitly designated test store.
[Trait("Category", "Live")]
public sealed class MemoryTypeLiveTests
{
    [MemoryTypeFact]
    public async Task RegisterReadAndConditionallyUpdateDisabledStrategy()
    {
        const string name = "dotnet_attribute_test";
        var store = Environment.GetEnvironmentVariable("REDIS_IRIS_STORE_ID")!;
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var client = new AgentMemoryManagementClient(http,
            new() { Endpoint = new(Environment.GetEnvironmentVariable("REDIS_IRIS_MANAGEMENT_ENDPOINT")!) },
            new FixedAgentMemoryManagementCredentialProvider(Environment.GetEnvironmentVariable("REDIS_IRIS_MANAGEMENT_TOKEN")!));
        CustomMemoryTypeDefinition type;
        var fields = new CustomMemoryFieldDefinition[]
        {
            new("status", "Synthetic status", CustomMemoryFieldType.STRING),
            new("priority", "Synthetic priority", CustomMemoryFieldType.FLOAT),
            new("approved", "Synthetic approval", CustomMemoryFieldType.BOOLEAN),
            new("tags", "Synthetic tags", CustomMemoryFieldType.STRING_LIST),
        };
        try { type = await client.GetMemoryTypeAsync(store, name, ct); }
        catch (AgentMemoryManagementException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            type = await client.CreateMemoryTypeAsync(store, new()
            {
                Name = name,
                Description = "Reusable .NET SDK attribute test type",
                Fields = fields,
                ExtractionStrategy = new("Extract only explicitly stated synthetic test facts.", false),
            }, ct);
        }
        Assert.Equal(fields.OrderBy(field => field.Name), type.Fields!.OrderBy(field => field.Name));
        Assert.NotNull(type.ExtractionStrategy);
        Assert.False(type.ExtractionStrategy.Enabled);
        Assert.False(string.IsNullOrEmpty(type.Revision));
        var updated = await client.UpdateExtractionStrategyAsync(store, name,
            new() { Prompt = "Extract only explicitly stated synthetic test facts. Test run " + Guid.NewGuid().ToString("N"), Enabled = false }, type.Revision, ct);
        Assert.NotEqual(type.Revision, updated.Revision);
        var stale = await Assert.ThrowsAsync<AgentMemoryManagementException>(() => client.UpdateExtractionStrategyAsync(store, name,
            new() { Enabled = false }, type.Revision, ct));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var duplicate = await Assert.ThrowsAsync<AgentMemoryManagementException>(() => client.CreateMemoryTypeAsync(store, type, ct));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var read = await client.GetMemoryTypeAsync(store, name, ct);
        Assert.Equal(updated.Revision, read.Revision);
    }
}

internal sealed class MemoryTypeFactAttribute : FactAttribute
{
    public MemoryTypeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REDIS_IRIS_RUN_MEMORY_TYPE_TESTS") != "1" ||
            new[] { "REDIS_IRIS_MANAGEMENT_ENDPOINT", "REDIS_IRIS_MANAGEMENT_TOKEN", "REDIS_IRIS_STORE_ID" }.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "Set REDIS_IRIS_RUN_MEMORY_TYPE_TESTS=1 and control-plane values for a dedicated test store; this registers a persistent test type.";
    }
}
