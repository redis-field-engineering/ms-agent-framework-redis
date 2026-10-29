using System.Diagnostics;
using Xunit;

namespace Redis.Iris.ContextRetriever.Modeling.Tests;

public sealed class ContextCtlCancellationTests
{
    [UnixProcessFact]
    public async Task CancellationInterruptsOutputDrainAfterParentExit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iris-ctxctl-pipes-" + Guid.NewGuid().ToString("N"));
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
                directory={{shellDirectory}}
                sleep 30 &
                echo $! > "$directory/child"
                echo $$ > "$directory/parent"
                echo '{"id":"surface","name":"synthetic"}'
                exit 0
                """);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var model = new ContextDataModel("Synthetic", "", [new("X", "", "x:{id}", [new("id", "str", "", true, [])], [])]);
            pending = new ContextCtlSurfaceDeployer(script).CreateAsync(model, new() { Name = "synthetic", RedisAddress = "unused:1" }, cancellation.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(directory, "parent"))) await Task.Delay(10, deadline.Token);
            var parentId = int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "parent")));
            try
            {
                using var parent = Process.GetProcessById(parentId);
                await parent.WaitForExitAsync(deadline.Token);
            }
            catch (ArgumentException) { /* Already reaped by the runner. */ }
            Assert.False(pending.IsCompleted); // The inherited pipe is still open.
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            cancellation.Cancel();
            // This intentionally orphaned fixture is outside the exited parent's process tree.
            if (File.Exists(Path.Combine(directory, "child")))
            {
                try
                {
                    using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "child"))));
                    child.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }

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
