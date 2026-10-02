using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Redis.Iris.ContextRetriever.Modeling.Tests;

public sealed class ContextModelRegistrationTests
{
    [Fact]
    public void RegistersOneValidatedCachedModelForApplicationCode()
    {
        var services = new ServiceCollection();
        services.AddRedisContextModel("App", "", model => model.Entity<Customer>(entity => entity
            .KeyTemplate("customer:{id}")
            .Field(x => x.Id, field => field.Name("id").KeyComponent())));
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<ContextDataModel>(), provider.GetRequiredService<ContextDataModel>());
    }

    private sealed class Customer { public string Id { get; init; } = ""; }
}
