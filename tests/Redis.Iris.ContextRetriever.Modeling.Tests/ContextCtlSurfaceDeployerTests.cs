using Xunit;

namespace Redis.Iris.ContextRetriever.Modeling.Tests;

public sealed class ContextCtlSurfaceDeployerTests
{
    [Fact]
    public async Task DeploysGeneratedJsonWithDatamodelAndStoredAdminAuthentication()
    {
        var process = new RecordingProcess();
        var deployer = new ContextCtlSurfaceDeployer(process);
        var model = new ContextDataModel("Travel", "", [new("Traveler", "", "traveler:{id}", [new("id", "str", "", true, [])], [])]);

        var result = await deployer.CreateAsync(model, new()
        {
            Name = "travel-surface",
            RedisAddress = "redis.example:6380",
            RedisTls = true,
            RedisUsername = "app",
            RedisPassword = "redis-secret",
        });

        Assert.Equal("surface-1", result.SurfaceId);
        var create = process.Calls[1];
        Assert.Contains("--datamodel", create);
        Assert.DoesNotContain("--models", create);
        Assert.DoesNotContain("--admin-key", create);
        var modelPath = create[create.IndexOf("--datamodel") + 1];
        Assert.False(File.Exists(modelPath));
    }

    [Fact]
    public async Task RejectsAnIncompatibleCliBeforeSendingDeploymentSecrets()
    {
        var process = new RecordingProcess { Compatible = false };
        var deployer = new ContextCtlSurfaceDeployer(process);
        var model = new ContextDataModel("x", "", [new("X", "", "x:{id}", [new("id", "str", "", true, [])], [])]);

        var error = await Assert.ThrowsAsync<ContextCtlException>(() => deployer.CreateAsync(model,
            new() { Name = "x", RedisAddress = "redis:6379", RedisPassword = "must-not-be-sent" }));

        Assert.Single(process.Calls);
        Assert.DoesNotContain("must-not-be-sent", error.ToString());
    }

    private sealed class RecordingProcess : IContextCtlProcess
    {
        public bool Compatible { get; init; } = true;
        public List<List<string>> Calls { get; } = [];
        public Task<ContextCtlProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToList());
            return Task.FromResult(Calls.Count == 1
                ? new ContextCtlProcessResult(Compatible ? 0 : 1, Compatible ? "--datamodel" : "old cli")
                : new ContextCtlProcessResult(0, "{\"id\":\"surface-1\",\"name\":\"travel-surface\",\"status\":\"active\"}"));
        }
    }
}
