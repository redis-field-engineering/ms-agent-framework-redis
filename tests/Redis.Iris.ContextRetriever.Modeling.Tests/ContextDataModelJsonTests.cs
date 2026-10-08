using System.Text.Json;
using Xunit;

namespace Redis.Iris.ContextRetriever.Modeling.Tests;

public sealed class ContextDataModelJsonTests
{
    [Fact]
    public void JsonMatchesCtxctlDataModelShapeAndIsDeterministic()
    {
        var model = Model("customer:{id}", includeName: true);
        var first = ContextDataModelJson.Serialize(model);
        var second = ContextDataModelJson.Serialize(model);

        Assert.Equal(first, second);
        using var json = JsonDocument.Parse(first);
        Assert.Equal("Commerce", json.RootElement.GetProperty("title").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("entity_count").GetInt32());
        var entity = json.RootElement.GetProperty("entities")[0];
        Assert.Equal("customer:{id}", entity.GetProperty("redis_key_template").GetString());
        Assert.Equal("text", entity.GetProperty("fields")[1].GetProperty("redis_indices")[0].GetProperty("type").GetString());
        Assert.Equal("$.tenant_id", entity.GetProperty("acl_field_mappings")[0].GetProperty("document_field").GetString());
        Assert.Equal("tenant", entity.GetProperty("acl_field_mappings")[0].GetProperty("access_tag").GetString());
        Assert.Equal(first, ContextDataModelJson.Serialize(ContextDataModelJson.Deserialize(first)));
    }

    [Fact]
    public void ChangeReviewFlagsDestructiveContracts()
    {
        var baseline = Model("customer:{id}", includeName: true);
        var proposed = Model("customer:v2:{id}", includeName: false);
        var changes = ContextDataModelComparer.Compare(baseline, proposed);

        Assert.Contains(changes, change => change.Kind == ContextModelChangeKind.Destructive && change.Description.Contains("Key template"));
        Assert.Contains(changes, change => change.Kind == ContextModelChangeKind.Destructive && change.Description.Contains("Field removed"));
    }

    private static ContextDataModel Model(string keyTemplate, bool includeName)
    {
        var fields = new List<ContextFieldDefinition> { new("id", "str", "", true, []) };
        if (includeName) fields.Add(new("name", "str", "", false, [new("text", Weight: 1)]));
        fields.Add(new("tenant_id", "str", "", false, [new("tag")]));
        return new("Commerce", "", [new("Customer", "", keyTemplate, fields, [], [new("$.tenant_id", "tenant")])]);
    }
}
