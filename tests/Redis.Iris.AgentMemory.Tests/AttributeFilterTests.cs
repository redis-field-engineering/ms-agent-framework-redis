using System.Net;
using System.Text.Json;

namespace Redis.Iris.AgentMemory.Tests;

public sealed class AttributeFilterTests
{
    [Fact]
    public async Task IrisSearchPreservesTypedClausesScopeAndPagination()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var values = new[] { "Tokyo", "Kyoto" };
        var list = AttributeFilterClause.ContainsAll(values);
        values[0] = "changed";
        var result = await Client(http).SearchLongTermMemoryAsync(new()
        {
            Text = "travel",
            PageToken = "previous",
            Limit = 3,
            Filter = new()
            {
                OwnerId = new() { Eq = "owner" },
                MemoryType = new() { Eq = "trip" },
                Attributes = new Dictionary<string, AttributeFilterClause>
                {
                    ["Seat-Code"] = AttributeFilterClause.StringEquals("aisle"),
                    ["count"] = AttributeFilterClause.NumberEquals(0),
                    ["price"] = AttributeFilterClause.NumberEquals(1.25),
                    ["flag"] = AttributeFilterClause.BooleanEquals(false),
                    ["cities"] = list,
                },
            },
        });
        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        Assert.Equal("travel", root.GetProperty("query").GetString());
        Assert.Equal("previous", root.GetProperty("cursor").GetString());
        var filter = root.GetProperty("filter");
        Assert.Equal("ALL", filter.GetProperty("op").GetString());
        Assert.Equal("owner", filter.GetProperty("ownerId").GetProperty("eq").GetString());
        Assert.Equal("trip", filter.GetProperty("memoryType").GetProperty("eq").GetString());
        var attributes = filter.GetProperty("attributes");
        Assert.Equal("aisle", attributes.GetProperty("Seat-Code").GetProperty("string").GetString());
        Assert.Equal(0, attributes.GetProperty("count").GetProperty("number").GetDouble());
        Assert.Equal(1.25, attributes.GetProperty("price").GetProperty("number").GetDouble());
        Assert.False(attributes.GetProperty("flag").GetProperty("boolean").GetBoolean());
        Assert.Equal("Tokyo", attributes.GetProperty("cities").GetProperty("list")[0].GetString());
        Assert.Equal("next", result.NextPageToken);
    }

    [Fact]
    public async Task LegacyAndInvalidAttributeFiltersFailBeforeDispatch()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var request = new LongTermMemorySearchRequest { Filter = new() { Attributes = new Dictionary<string, AttributeFilterClause> { ["status"] = AttributeFilterClause.StringEquals("open") } } };
        await Assert.ThrowsAsync<NotSupportedException>(() => Client(http, AgentMemoryApiMode.Legacy).SearchLongTermMemoryAsync(request));
        foreach (var name in new[] { "", "x.y", "1field", new string('a', 65) })
            await Assert.ThrowsAsync<ArgumentException>(() => Client(http).SearchLongTermMemoryAsync(request with
            { Filter = new() { Attributes = new Dictionary<string, AttributeFilterClause> { [name] = AttributeFilterClause.BooleanEquals(true) } } }));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(http).SearchLongTermMemoryAsync(request with
        { Filter = new() { Attributes = new Dictionary<string, AttributeFilterClause> { ["field"] = null! } } }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AttributeFreeRequestsRemainCompatibleAndFilterOnlyOmitsQuery()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        foreach (var mode in new[] { AgentMemoryApiMode.Legacy, AgentMemoryApiMode.Iris })
        {
            await Client(http, mode).SearchLongTermMemoryAsync(new() { Filter = new() { OwnerId = new() { Eq = "owner" } } });
            using var json = JsonDocument.Parse(handler.Body!);
            Assert.False(json.RootElement.GetProperty("filter").TryGetProperty("attributes", out _));
            Assert.False(json.RootElement.TryGetProperty("query", out _));
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"string\":\"x\",\"number\":1}")]
    [InlineData("{\"string\":\"x\",\"string\":\"y\"}")]
    [InlineData("{\"string\":{\"eq\":\"x\"}}")]
    [InlineData("{\"list\":[1]}")]
    [InlineData("{\"boolean\":null}")]
    [InlineData("{\"number\":1e999}")]
    public void MalformedUnionIsRejected(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AttributeFilterClause>(json));

    [Theory]
    [InlineData("{\"string\":\"\"}")]
    [InlineData("{\"number\":9007199254740991}")]
    [InlineData("{\"number\":-1.25}")]
    [InlineData("{\"boolean\":false}")]
    [InlineData("{\"list\":[]}")]
    public void UnionRoundTripsWithoutLosingValues(string json) =>
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<AttributeFilterClause>(json)));

    [Fact]
    public void FactoriesRejectInvalidValues()
    {
        foreach (var number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => AttributeFilterClause.NumberEquals(number));
        Assert.Throws<ArgumentNullException>(() => AttributeFilterClause.StringEquals(null!));
        Assert.Throws<ArgumentException>(() => AttributeFilterClause.ContainsAll("ok", null!));
    }

    private static RedisAgentMemoryClient Client(HttpClient http, AgentMemoryApiMode mode = AgentMemoryApiMode.Iris) =>
        new(http, new() { Endpoint = new("https://example.test"), StoreId = "store", ApiMode = mode }, new FixedAgentMemoryCredentialProvider("synthetic"));

    private sealed class Handler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"items\":[],\"nextCursor\":\"next\"}") };
        }
    }
}
