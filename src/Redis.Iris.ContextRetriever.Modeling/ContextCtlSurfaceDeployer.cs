using System.Diagnostics;
using System.Text.Json;

namespace Redis.Iris.ContextRetriever.Modeling;

/// <summary>Creates Context Retriever surfaces through the official ctxctl CLI.</summary>
/// <remarks>Authentication uses the admin credential already stored by <c>ctxctl auth login</c> or <c>ctxctl admin create</c>.</remarks>
public sealed class ContextCtlSurfaceDeployer
{
    private readonly IContextCtlProcess _process;

    /// <summary>Creates a deployer using the named ctxctl executable.</summary>
    public ContextCtlSurfaceDeployer(string executablePath = "ctxctl") : this(new SystemContextCtlProcess(executablePath)) { }

    /// <summary>Creates a deployer over an injectable process boundary.</summary>
    public ContextCtlSurfaceDeployer(IContextCtlProcess process) => _process = process ?? throw new ArgumentNullException(nameof(process));

    /// <summary>Checks that ctxctl exposes JSON DataModel input.</summary>
    public async Task CheckCompatibilityAsync(CancellationToken cancellationToken = default)
    {
        var result = await _process.RunAsync(["surface", "create", "--help"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !result.StandardOutput.Contains("--datamodel", StringComparison.Ordinal))
            throw new ContextCtlException("Installed ctxctl does not support 'surface create --datamodel'.", result.ExitCode);
    }

    /// <summary>Writes the manifest to a temporary JSON artifact and creates a surface.</summary>
    public async Task<ContextSurfaceDeploymentResult> CreateAsync(
        ContextDataModel model,
        ContextSurfaceDeployment deployment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(deployment);
        deployment.Validate();
        await CheckCompatibilityAsync(cancellationToken).ConfigureAwait(false);

        var directory = Path.Combine(Path.GetTempPath(), "redis-iris-context-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "datamodel.json");
        try
        {
            await ContextDataModelJson.WriteAsync(model, path, cancellationToken).ConfigureAwait(false);
            var result = await _process.RunAsync(BuildArguments(deployment, path), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new ContextCtlException("ctxctl surface creation failed. Run ctxctl directly for sanitized diagnostics.", result.ExitCode);
            return ParseResult(result.StandardOutput);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    internal static IReadOnlyList<string> BuildArguments(ContextSurfaceDeployment deployment, string dataModelPath)
    {
        var arguments = new List<string> { "--output", "json", "surface", "create", "--name", deployment.Name, "--datamodel", dataModelPath, "--redis-addr", deployment.RedisAddress };
        if (!string.IsNullOrWhiteSpace(deployment.Description)) arguments.AddRange(["--description", deployment.Description]);
        if (!string.IsNullOrWhiteSpace(deployment.RedisUsername)) arguments.AddRange(["--redis-username", deployment.RedisUsername]);
        if (!string.IsNullOrWhiteSpace(deployment.RedisPassword)) arguments.AddRange(["--redis-password", deployment.RedisPassword]);
        if (deployment.RedisDatabase != 0) arguments.AddRange(["--redis-db", deployment.RedisDatabase.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (deployment.RedisTls) arguments.Add("--redis-tls");
        return arguments;
    }

    private static ContextSurfaceDeploymentResult ParseResult(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            return new(root.GetProperty("id").GetString()!, root.GetProperty("name").GetString()!,
                root.TryGetProperty("status", out var status) ? status.GetString() : null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ContextCtlException("ctxctl returned an unexpected JSON response.", 0, ex);
        }
    }
}

/// <summary>Non-secret surface deployment configuration.</summary>
public sealed class ContextSurfaceDeployment
{
    /// <summary>Surface name.</summary>
    public required string Name { get; init; }
    /// <summary>Surface description.</summary>
    public string Description { get; init; } = "";
    /// <summary>Redis address in host:port form.</summary>
    public required string RedisAddress { get; init; }
    /// <summary>Optional Redis username.</summary>
    public string? RedisUsername { get; init; }
    /// <summary>Optional Redis password. ctxctl currently accepts this as a process argument.</summary>
    public string? RedisPassword { get; init; }
    /// <summary>Redis logical database.</summary>
    public int RedisDatabase { get; init; }
    /// <summary>Whether ctxctl should use TLS for Redis.</summary>
    public bool RedisTls { get; init; } = true;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(RedisAddress);
        if (RedisDatabase < 0) throw new ArgumentOutOfRangeException(nameof(RedisDatabase));
    }
}

/// <summary>Result returned by ctxctl after surface creation.</summary>
public sealed record ContextSurfaceDeploymentResult(string SurfaceId, string Name, string? Status);

/// <summary>Executes ctxctl without shell interpolation.</summary>
public interface IContextCtlProcess
{
    /// <summary>Runs ctxctl with an argument vector.</summary>
    Task<ContextCtlProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

/// <summary>Captured ctxctl process result.</summary>
public sealed record ContextCtlProcessResult(int ExitCode, string StandardOutput);

/// <summary>Raised for ctxctl compatibility or execution failures.</summary>
public sealed class ContextCtlException : Exception
{
    /// <summary>ctxctl exit code.</summary>
    public int ExitCode { get; }
    /// <summary>Creates an exception without embedding CLI output or credentials.</summary>
    public ContextCtlException(string message, int exitCode, Exception? innerException = null) : base(message, innerException) => ExitCode = exitCode;
}

internal sealed class SystemContextCtlProcess(string executablePath) : IContextCtlProcess
{
    public async Task<ContextCtlProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(executablePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new ContextCtlException("Unable to start ctxctl.", -1);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stderr.ConfigureAwait(false); // Observe but never disclose potentially sensitive CLI output.
        return new(process.ExitCode, await stdout.ConfigureAwait(false));
    }
}
