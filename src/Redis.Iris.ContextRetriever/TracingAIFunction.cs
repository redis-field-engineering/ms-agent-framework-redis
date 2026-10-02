using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Redis.Iris.ContextRetriever;

internal sealed class TracingAIFunction(AIFunction inner) : AIFunction
{
    public override string Name => inner.Name;
    public override string Description => inner.Description;
    public override JsonElement JsonSchema => inner.JsonSchema;
    public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;
    public override JsonSerializerOptions JsonSerializerOptions => inner.JsonSerializerOptions;
    public override MethodInfo? UnderlyingMethod => inner.UnderlyingMethod;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        using var activity = ContextRetrieverDiagnostics.Start("execute");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var result = await inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            ContextRetrieverDiagnostics.Tool(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, false);
            return result;
        }
        catch (Exception ex)
        {
            ContextRetrieverDiagnostics.Tool(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, true);
            ContextRetrieverDiagnostics.Failed(activity, ex);
            throw;
        }
    }
}
