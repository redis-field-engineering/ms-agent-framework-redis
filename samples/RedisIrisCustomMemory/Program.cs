using System.Text.Json;
using Redis.Iris.AgentFramework;
using Redis.Iris.AgentMemory;
using Redis.Iris.AgentMemory.Management;

if (args.Length != 1 || args[0] is not ("register" or "enable" or "disable" or "seed" or "recall" or "extract"))
{
    Console.WriteLine("Usage: dotnet run --project samples/RedisIrisCustomMemory -- register|enable|disable|seed|recall|extract");
    return;
}

const string typeName = "trip_preference";
var store = Required("REDIS_IRIS_STORE_ID");
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var ct = deadline.Token;
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

// Only these deployment commands load administrative credentials. Do not add them to an agent host.
if (args[0] is "register" or "enable" or "disable")
{
    var management = new AgentMemoryManagementClient(http,
        new() { Endpoint = new(Required("REDIS_IRIS_MANAGEMENT_ENDPOINT")) },
        new FixedAgentMemoryManagementCredentialProvider(Required("REDIS_IRIS_MANAGEMENT_TOKEN")));
    if (args[0] == "register")
    {
        var type = await management.CreateMemoryTypeAsync(store, new()
        {
            Name = typeName,
            Description = "Explicit trip requirements and destinations",
            Fields = [new("destinations", "Cities the traveler plans to visit", CustomMemoryFieldType.STRING_LIST)],
            ExtractionStrategy = new("Extract a trip_preference only for an explicit travel plan. Put the stated city names in destinations.", Enabled: false),
        }, ct);
        Console.WriteLine($"Registered {type.Name}; extraction disabled. Duplicate registration returns HTTP 409.");
    }
    else
    {
        var current = await management.GetMemoryTypeAsync(store, typeName, ct);
        if (string.IsNullOrEmpty(current.Revision)) throw new InvalidOperationException("The server did not supply a revision for a conditional strategy edit.");
        await management.UpdateExtractionStrategyAsync(store, typeName, new() { Enabled = args[0] == "enable" }, current.Revision, ct);
        Console.WriteLine($"Extraction {(args[0] == "enable" ? "enabled" : "disabled")}.");
    }
    return;
}

var client = new RedisAgentMemoryClient(http, new()
{
    Endpoint = new(Required("REDIS_IRIS_ENDPOINT")),
    StoreId = store,
    ApiMode = AgentMemoryApiMode.Iris,
}, new FixedAgentMemoryCredentialProvider(Required("REDIS_IRIS_API_KEY")));
// In a real agent host, resolve this from authenticated application identity, never from model arguments.
var owner = Required("REDIS_IRIS_EXAMPLE_OWNER");
var search = new RedisIrisLongTermMemorySearch(client);
var searchOptions = new RedisIrisMemorySearchOptions
{
    MemoryType = new() { Eq = typeName },
    Attributes = new Dictionary<string, AttributeFilterClause> { ["destinations"] = AttributeFilterClause.ContainsAll("Tokyo") },
};

if (args[0] == "seed")
{
    var id = "trip-example-" + Guid.NewGuid().ToString("N");
    var request = new BulkCreateLongTermMemoriesRequest
    {
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        Memories = [new()
        {
            Id = id, OwnerId = owner, MemoryType = typeName, Text = "The traveler plans to visit Tokyo and Kyoto.",
            Attributes = new Dictionary<string, JsonElement> { ["destinations"] = JsonSerializer.SerializeToElement(new[] { "Tokyo", "Kyoto" }) },
        }],
    };
    Console.WriteLine($"Creating synthetic memory {id}; retain this ID for cleanup.");
    var created = await client.BulkCreateLongTermMemoriesAsync(request, ct);
    if (created.Errors is { Count: > 0 } || !created.Created.Contains(id))
        throw new InvalidOperationException("Memory creation was not fully successful; inspect the response with trusted application code.");
}
else if (args[0] == "extract")
{
    // Enable extraction explicitly first. A fresh session prevents an older matching memory from satisfying this demonstration.
    var session = "trip-extract-" + Guid.NewGuid().ToString("N");
    await client.AddSessionEventAsync(new()
    {
        ActorId = owner,
        SessionId = session,
        Role = SessionEventRole.USER,
        CreatedAt = DateTimeOffset.UtcNow,
        Content = [new("I am planning a trip to Tokyo and Kyoto.")],
        IdempotencyKey = Guid.NewGuid().ToString("N"),
    }, ct);
    searchOptions = searchOptions with { SessionId = new() { Eq = session } };
    Console.WriteLine($"Waiting up to three minutes for extraction from session {session}. Longer store cadences may time out.");
    while (true)
    {
        var extracted = await search.SearchAsync(owner, (string?)null, searchOptions, ct);
        if (extracted.Items.Count > 0) { Console.WriteLine($"Extracted {extracted.Items.Count} matching memories."); break; }
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
    }
}

var result = await search.SearchAsync(owner, (string?)null, searchOptions, ct);
foreach (var memory in result.Items) Console.WriteLine($"{memory.Id}: {memory.Text}");
if (result.NextPageToken is not null) Console.WriteLine("More results are available; pass NextPageToken with the same filters.");

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Set {name} before running this command.");
