using Redis.Iris.ContextRetriever.Modeling;

var model = ContextModelCompiler.Compile(
    "Travel assistant",
    "Synthetic customer and trip entities",
    typeof(Customer),
    typeof(Trip));

var outputPath = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal))
    ?? Path.Combine(Environment.CurrentDirectory, "travel-context-model.json");
await ContextDataModelJson.WriteAsync(model, outputPath);
Console.WriteLine($"Wrote {model.EntityCount} entities to {Path.GetFullPath(outputPath)}");

if (args.Contains("--deploy", StringComparer.Ordinal))
{
    var redisAddress = Environment.GetEnvironmentVariable("CONTEXT_RETRIEVER_REDIS_ADDRESS")
        ?? throw new InvalidOperationException("Set CONTEXT_RETRIEVER_REDIS_ADDRESS.");
    var deployer = new ContextCtlSurfaceDeployer();
    var result = await deployer.CreateAsync(model, new()
    {
        Name = "travel-assistant",
        Description = model.Description,
        RedisAddress = redisAddress,
        RedisUsername = Environment.GetEnvironmentVariable("CONTEXT_RETRIEVER_REDIS_USERNAME"),
        RedisPassword = Environment.GetEnvironmentVariable("CONTEXT_RETRIEVER_REDIS_PASSWORD"),
        RedisTls = true,
    });
    Console.WriteLine($"Created surface {result.SurfaceId} ({result.Status}).");
}

[ContextEntity(KeyTemplate = "customer:{id}", Description = "Travel customer")]
internal sealed class Customer
{
    [ContextField(Name = "id", IsKeyComponent = true, Description = "Customer ID")]
    public required string Id { get; init; }
    [ContextField(Index = ContextIndex.Text, Description = "Customer name")]
    public required string Name { get; init; }
    [ContextField(Index = ContextIndex.Tag, Description = "Customer tier", AllowedValues = ["standard", "premium"])]
    public required string Tier { get; init; }
}

[ContextEntity(KeyTemplate = "trip:{id}", Description = "Booked trip")]
internal sealed class Trip
{
    [ContextField(Name = "id", IsKeyComponent = true, Description = "Trip ID")]
    public required string Id { get; init; }
    [ContextField(Name = "customer_id", Index = ContextIndex.Tag, Description = "Owning customer ID")]
    public required string CustomerId { get; init; }
    [ContextField(Index = ContextIndex.Text, Description = "Destination")]
    public required string Destination { get; init; }
    [ContextRelationship(typeof(Customer), Name = "customer", SourceField = "customer_id", Description = "Customer who booked the trip")]
    public Customer? Customer { get; init; }
}
