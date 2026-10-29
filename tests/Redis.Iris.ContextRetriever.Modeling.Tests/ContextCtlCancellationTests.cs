using System.Diagnostics;
using Xunit;

namespace Redis.Iris.ContextRetriever.Modeling.Tests;

public sealed class ContextCtlCancellationTests
{
    [UnixProcessFact]
    public async Task CancellationStopsTheProcessTreeBeforeRemovingItsManifest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iris-ctxctl-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        Task<ContextSurfaceDeploymentResult>? pending = null;
        try
        {
            var script = Path.Combine(directory, "ctxctl");
            var shellDirectory = "'" + directory.Replace("'", "'\"'\"'") + "'";
            await File.WriteAllTextAsync(script, $$"""
                #!/bin/sh
                if [ "$3" = "--help" ]; then echo --datamodel; exit 0; fi
                if [ "$6" = "complete" ]; then echo '{"id":"surface","name":"complete"}'; exit 0; fi
                directory={{shellDirectory}}
                echo "$8" > "$directory/manifest"
                (sleep 3; echo unexpected > "$directory/late-effect") &
                echo $$ > "$directory/pid"
                echo ready > "$directory/ready"
                wait
                echo '{"id":"surface","name":"slow"}'
                """);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var deployer = new ContextCtlSurfaceDeployer(script);
            var model = new ContextDataModel("Synthetic", "", [new("X", "", "x:{id}", [new("id", "str", "", true, [])], [])]);
            var control = await deployer.CreateAsync(model, new() { Name = "complete", RedisAddress = "unused:1" });
            Assert.Equal("surface", control.SurfaceId);
            pending = deployer.CreateAsync(model, new() { Name = "slow", RedisAddress = "unused:1" }, cancellation.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(directory, "ready"))) await Task.Delay(10, deadline.Token);
            using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "pid"))));
            var manifest = (await File.ReadAllTextAsync(Path.Combine(directory, "manifest"))).Trim();
            Assert.True(File.Exists(manifest));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(child.HasExited);
            Assert.False(File.Exists(manifest));
            await Task.Delay(TimeSpan.FromSeconds(3.2));
            Assert.False(File.Exists(Path.Combine(directory, "late-effect")));
        }
        finally
        {
            cancellation.Cancel();
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }
}

// The process-tree integration probe uses /bin/sh; Windows needs its own executable fixture.
public sealed class UnixProcessFactAttribute : FactAttribute
{
    public UnixProcessFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "This subprocess integration probe requires /bin/sh.";
    }
}
