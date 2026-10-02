using Xunit;

namespace Redis.Iris.ContextRetriever.Modeling.Tests;

public sealed class ContextModelBuilderTests
{
    [Fact]
    public void FluentConfigurationSupportsCleanDomainPocosAndAttributeOverrides()
    {
        var model = new ContextModelBuilder("Travel")
            .Entity<Traveler>(entity => entity
                .Name("Passenger")
                .Description("A traveler")
                .KeyTemplate("traveler:{id}")
                .Field(x => x.Id, field => field.Name("id").KeyComponent())
                .Field(x => x.Name, field => field.Text(weight: 2)))
            .Entity<Trip>(entity => entity
                .KeyTemplate("trip:{id}")
                .Field(x => x.Id, field => field.Name("id").KeyComponent())
                .Field(x => x.TravelerId, field => field.Name("traveler_id").Tag())
                .Relationship<Traveler?, Traveler>(x => x.Traveler, "traveler_id", "traveler"))
            .Build();

        var passenger = model.Entities.Single(entity => entity.Name == "Passenger");
        Assert.Equal("text", Assert.Single(passenger.Fields.Single(field => field.Name == "name").RedisIndices).Type);
        Assert.Equal("Passenger", model.Entities.Single(entity => entity.Name == "Trip").Relationships.Single().Target);
    }

    private sealed class Traveler { public string Id { get; init; } = ""; public string Name { get; init; } = ""; }
    private sealed class Trip { public string Id { get; init; } = ""; public string TravelerId { get; init; } = ""; public Traveler? Traveler { get; init; } }
}
