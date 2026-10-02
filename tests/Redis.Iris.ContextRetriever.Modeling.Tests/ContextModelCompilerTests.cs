using System.Text.Json.Serialization;
using Xunit;

namespace Redis.Iris.ContextRetriever.Modeling.Tests;

public sealed class ContextModelCompilerTests
{
    [Fact]
    public void CompilesAnnotatedApplicationPocosWithoutInstantiatingThem()
    {
        var model = ContextModelCompiler.Compile("Commerce", "Customer data", typeof(Order), typeof(Customer));

        Assert.Equal(2, model.EntityCount);
        var customer = Assert.Single(model.Entities, entity => entity.Name == "Customer");
        Assert.Equal("customer:{id}", customer.RedisKeyTemplate);
        Assert.Equal(["email", "id", "name"], customer.Fields.Select(field => field.Name));
        Assert.True(customer.Fields.Single(field => field.Name == "id").IsKeyComponent);
        Assert.Equal("tag", Assert.Single(customer.Fields.Single(field => field.Name == "email").RedisIndices).Type);
        var relationship = Assert.Single(model.Entities.Single(entity => entity.Name == "Order").Relationships);
        Assert.Equal("Customer", relationship.Target);
        Assert.Equal("customer_id", relationship.SourceField);
    }

    [Fact]
    public void RejectsKeySchemaRelationshipAndVectorErrorsBeforeDeployment()
    {
        Assert.Throws<ContextModelValidationException>(() => ContextModelCompiler.Compile("bad", "", typeof(BadKey)));
        Assert.Throws<ContextModelValidationException>(() => ContextModelCompiler.Compile("bad", "", typeof(BadVector)));
        Assert.Throws<ContextModelValidationException>(() => ContextModelCompiler.Compile("bad", "", typeof(BadRelationship), typeof(Customer)));
    }

    [Fact]
    public void IgnoresDuplicateClrTypesDuringAssemblyStyleDiscovery()
    {
        var model = ContextModelCompiler.Compile("Commerce", "", typeof(Customer), typeof(Customer));

        Assert.Equal(1, model.EntityCount);
        Assert.Equal("Customer", Assert.Single(model.Entities).Name);
    }

    [ContextEntity(KeyTemplate = "customer:{id}", Description = "Customer account")]
    private sealed class Customer
    {
        [ContextField(Name = "id", IsKeyComponent = true, Description = "Unique ID")]
        public required string Id { get; init; }
        [ContextField(Index = ContextIndex.Text)]
        public required string Name { get; init; }
        [JsonPropertyName("email")]
        [ContextField(Index = ContextIndex.Tag)]
        public required string EmailAddress { get; init; }
        public string ApplicationOnlySecret { get; init; } = "not exported";
    }

    [ContextEntity(KeyTemplate = "order:{id}")]
    private sealed class Order
    {
        [ContextField(Name = "id", IsKeyComponent = true)] public required string Id { get; init; }
        [ContextField(Name = "customer_id", Index = ContextIndex.Tag)] public required string CustomerId { get; init; }
        [ContextRelationship(typeof(Customer), Name = "customer", SourceField = "customer_id")] public Customer? Customer { get; init; }
    }

    [ContextEntity(KeyTemplate = "bad:{missing}")]
    private sealed class BadKey { [ContextField(Name = "id", IsKeyComponent = true)] public string Id { get; init; } = ""; }
    [ContextEntity(KeyTemplate = "vector:{id}")]
    private sealed class BadVector
    {
        [ContextField(Name = "id", IsKeyComponent = true)] public string Id { get; init; } = "";
        [ContextField(Index = ContextIndex.Vector)] public float[] Embedding { get; init; } = [];
    }
    [ContextEntity(KeyTemplate = "badrel:{id}")]
    private sealed class BadRelationship
    {
        [ContextField(Name = "id", IsKeyComponent = true)] public string Id { get; init; } = "";
        [ContextRelationship(typeof(Customer), SourceField = "missing")] public Customer? Customer { get; init; }
    }
}
