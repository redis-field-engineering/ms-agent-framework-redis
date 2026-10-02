using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Redis.Iris.ContextRetriever;

internal sealed class TracingAIFunction(AIFunction inner, SemaphoreSlim executionGate, TimeSpan executionTimeout) : AIFunction
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(executionTimeout);
        var entered = false;
        try
        {
            await executionGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            var result = await inner.InvokeAsync(arguments, timeout.Token).ConfigureAwait(false);
            ContextRetrieverDiagnostics.Tool(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, false);
            return result;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            var timeoutException = new ContextRetrieverTimeoutException("tool execution", ex);
            ContextRetrieverDiagnostics.Tool(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, true);
            ContextRetrieverDiagnostics.Failed(activity, timeoutException);
            throw timeoutException;
        }
        catch (Exception ex)
        {
            ContextRetrieverDiagnostics.Tool(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, true);
            ContextRetrieverDiagnostics.Failed(activity, ex);
            throw;
        }
        finally
        {
            if (entered) executionGate.Release();
        }
    }
}
