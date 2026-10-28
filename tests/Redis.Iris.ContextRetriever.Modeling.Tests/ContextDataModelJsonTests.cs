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

    [Fact]
    public void RelationshipChangesCannotDisappearFromCompatibilityReport()
    {
        var relationship = new ContextRelationshipDefinition("orders", "Order", "", "order_id");
        var baseline = Contract(relationships: [relationship]);
        AssertChange(baseline, Contract(), ContextModelChangeKind.Destructive, "Relationship removed");
        AssertChange(Contract(), baseline, ContextModelChangeKind.Additive, "Relationship added");
        AssertChange(baseline, Contract(relationships: [relationship with { Target = "Invoice" }]), ContextModelChangeKind.Destructive, "Relationship contract");
        AssertChange(baseline, Contract(relationships: [relationship with { SourceField = "invoice_id" }]), ContextModelChangeKind.Destructive, "Relationship contract");
        Assert.Empty(ContextDataModelComparer.Compare(baseline, Contract(relationships: [relationship with { Description = "New description" }])));
    }

    [Fact]
    public void AllowedValuesDetectNarrowingExpansionAndUnrestrictedTransitions()
    {
        AssertChange(Contract(), Contract(["open"]), ContextModelChangeKind.Destructive, "Allowed values");
        AssertChange(Contract(["open", "closed"]), Contract(["open"]), ContextModelChangeKind.Destructive, "Allowed values");
        AssertChange(Contract(["open"]), Contract(["closed"]), ContextModelChangeKind.Destructive, "Allowed values");
        AssertChange(Contract(["open"]), Contract(["open", "closed"]), ContextModelChangeKind.Additive, "Allowed values");
        AssertChange(Contract(["open"]), Contract(), ContextModelChangeKind.Additive, "Allowed values");
        AssertChange(Contract(["open"]), Contract([]), ContextModelChangeKind.Destructive, "Allowed values");
        Assert.Empty(ContextDataModelComparer.Compare(Contract(["open", "closed"]), Contract(["closed", "open"])));
        Assert.Empty(ContextDataModelComparer.Compare(Contract(), Contract()));
    }

    private static void AssertChange(ContextDataModel baseline, ContextDataModel proposed, ContextModelChangeKind kind, string description)
    {
        var change = Assert.Single(ContextDataModelComparer.Compare(baseline, proposed));
        Assert.Equal(kind, change.Kind);
        Assert.StartsWith(description, change.Description);
    }

    private static ContextDataModel Contract(IReadOnlyList<string>? values = null, IReadOnlyList<ContextRelationshipDefinition>? relationships = null) =>
        new("Commerce", "", [new("Customer", "", "customer:{id}", [new("status", "str", "", false, [], values)], relationships ?? [])]);

    private static ContextDataModel Model(string keyTemplate, bool includeName)
    {
        var fields = new List<ContextFieldDefinition> { new("id", "str", "", true, []) };
        if (includeName) fields.Add(new("name", "str", "", false, [new("text", Weight: 1)]));
        fields.Add(new("tenant_id", "str", "", false, [new("tag")]));
        return new("Commerce", "", [new("Customer", "", keyTemplate, fields, [], [new("$.tenant_id", "tenant")])]);
    }
}
