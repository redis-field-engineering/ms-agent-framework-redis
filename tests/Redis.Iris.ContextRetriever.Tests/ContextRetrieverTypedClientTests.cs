using System.Text.Json;
using Microsoft.Extensions.AI;
using Redis.Iris.ContextRetriever.Modeling;
using Xunit;

namespace Redis.Iris.ContextRetriever.Tests;

public sealed class ContextRetrieverTypedClientTests
{
    [Fact]
    public async Task ReadsAnAnnotatedPocoThroughItsAuthorizedGeneratedTool()
    {
        var tool = AIFunctionFactory.Create(
            (string id) => JsonDocument.Parse($$"""{"content":[{"type":"text","text":"{\"id\":\"{{id}}\",\"name\":\"Ada\"}"}],"isError":false}""").RootElement.Clone(),
            "get_customer_by_id");
        var session = new Client([tool]);
        var model = ContextModelCompiler.Compile("Commerce", "", typeof(Customer));
        var typed = new ContextRetrieverTypedClient(session, model);

        var customer = await typed.GetByIdAsync<Customer>("42");

        Assert.NotNull(customer);
        Assert.Equal("42", customer.Id);
        Assert.Equal("Ada", customer.Name);
    }

    [Fact]
    public async Task CannotInvokeAToolOutsideTheDiscoveredAuthorizationScope()
    {
        var typed = new ContextRetrieverTypedClient(new Client([]), new("x", "", []));
        await Assert.ThrowsAsync<ContextRetrieverDiscoveryException>(() => typed.InvokeAsync<Customer>("get_customer_by_id", new Dictionary<string, object?>()));
    }

    [Fact]
    public async Task ReadsSdkResultObjectsUsingWebPropertyNames()
    {
        var tool = AIFunctionFactory.Create(
            () => new ToolResult([new ToolContent("text", "{\"id\":\"42\",\"name\":\"Ada\"}")], false),
            "get_customer_by_id");
        var typed = new ContextRetrieverTypedClient(
            new Client([tool]),
            ContextModelCompiler.Compile("Commerce", "", typeof(Customer)));

        var customer = await typed.GetByIdAsync<Customer>("ignored");

        Assert.NotNull(customer);
        Assert.Equal("42", customer.Id);
        Assert.Equal("Ada", customer.Name);
    }

    [Fact]
    public async Task ReadsTheTextContentShapeReturnedByMcpTools()
    {
        var tool = AIFunctionFactory.Create(
            () => new TextContent("{\"id\":\"42\",\"name\":\"Ada\"}"),
            "get_customer_by_id");
        var typed = new ContextRetrieverTypedClient(
            new Client([tool]),
            ContextModelCompiler.Compile("Commerce", "", typeof(Customer)));

        var customer = await typed.GetByIdAsync<Customer>("ignored");

        Assert.NotNull(customer);
        Assert.Equal("42", customer.Id);
    }

    [ContextEntity(KeyTemplate = "customer:{id}")]
    private sealed class Customer
    {
        [ContextField(Name = "id", IsKeyComponent = true)] public string Id { get; init; } = "";
        [ContextField(Index = ContextIndex.Text)] public string Name { get; init; } = "";
    }

    private sealed class Client(IReadOnlyList<AIFunction> tools) : IContextRetrieverClient
    {
        public IReadOnlyList<AIFunction> Tools => tools;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record ToolResult(IReadOnlyList<ToolContent> Content, bool IsError);
    private sealed record ToolContent(string Type, string Text);
}
