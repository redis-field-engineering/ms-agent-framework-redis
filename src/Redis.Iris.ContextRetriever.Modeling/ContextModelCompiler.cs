using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Redis.Iris.ContextRetriever.Modeling;

/// <summary>Compiles explicitly registered annotated POCOs into a Context Retriever DataModel.</summary>
public static partial class ContextModelCompiler
{
    /// <summary>Compiles entity types in deterministic name order.</summary>
    public static ContextDataModel Compile(string title, string description, params Type[] entityTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(entityTypes);
        if (entityTypes.Length == 0) throw new ContextModelValidationException("At least one entity type is required.");

        entityTypes = entityTypes.Distinct().ToArray();
        var names = entityTypes.ToDictionary(type => type, GetEntityName);
        if (names.Values.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new ContextModelValidationException("Entity names must be unique.");

        var entities = entityTypes.Select(type => CompileEntity(type, names))
            .OrderBy(entity => entity.Name, StringComparer.Ordinal).ToArray();
        return new(title, description ?? "", entities);
    }

    /// <summary>Compiles annotated types discovered in the supplied assembly.</summary>
    public static ContextDataModel CompileAssembly(string title, string description, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Compile(title, description, assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<ContextEntityAttribute>() is not null).ToArray());
    }

    internal static ContextEntityDefinition CompileEntity(Type type, IReadOnlyDictionary<Type, string>? registeredNames = null)
    {
        var entity = type.GetCustomAttribute<ContextEntityAttribute>()
            ?? throw new ContextModelValidationException($"{type.FullName} is missing ContextEntityAttribute.");
        var name = GetEntityName(type);
        if (string.IsNullOrWhiteSpace(entity.KeyTemplate)) throw new ContextModelValidationException($"{name}: key template is required.");

        var fields = new List<ContextFieldDefinition>();
        var aclFieldMappings = new List<ContextAclFieldMapping>();
        var relationships = new List<ContextRelationshipDefinition>();
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var field = property.GetCustomAttribute<ContextFieldAttribute>();
            var relationship = property.GetCustomAttribute<ContextRelationshipAttribute>();
            if (field is not null && relationship is not null)
                throw new ContextModelValidationException($"{name}.{property.Name}: a property cannot be both a field and relationship.");
            if (field is not null)
            {
                var compiledField = CompileField(name, property, field);
                fields.Add(compiledField);
                if (field.AccessTag is not null)
                {
                    ValidateAccessTag(name, compiledField, field.AccessTag);
                    aclFieldMappings.Add(new($"$.{compiledField.Name}", field.AccessTag));
                }
            }
            if (relationship is not null)
            {
                var targetName = registeredNames is not null && registeredNames.TryGetValue(relationship.TargetType, out var registered)
                    ? registered : GetEntityName(relationship.TargetType);
                relationships.Add(new(ExternalName(property, relationship.Name), targetName, relationship.Description, relationship.SourceField));
            }
        }

        ValidateEntity(name, entity.KeyTemplate, fields, relationships, registeredNames?.Values);
        return new(name, entity.Description, entity.KeyTemplate,
            fields.OrderBy(field => field.Name, StringComparer.Ordinal).ToArray(),
            relationships.OrderBy(relationship => relationship.Name, StringComparer.Ordinal).ToArray(),
            aclFieldMappings.OrderBy(mapping => mapping.DocumentField, StringComparer.Ordinal).ToArray());
    }

    private static ContextFieldDefinition CompileField(string entityName, PropertyInfo property, ContextFieldAttribute field)
    {
        var name = ExternalName(property, field.Name);
        var type = ToDataModelType(property.PropertyType, $"{entityName}.{name}");
        ContextIndexDefinition[] indices = field.Index switch
        {
            ContextIndex.None => [],
            ContextIndex.Text => [new("text", Weight: field.Weight, NoStem: field.NoStem ? true : null)],
            ContextIndex.Tag => [new("tag")],
            ContextIndex.Numeric => [new("numeric", Sortable: field.Sortable ? true : null)],
            ContextIndex.Vector => [new("vector", field.VectorDimensions, field.DistanceMetric, field.Embeds, field.EmbeddingModel)],
            _ => throw new ContextModelValidationException($"{entityName}.{name}: unsupported index type."),
        };
        if (field.Index == ContextIndex.Vector && field.VectorDimensions <= 0)
            throw new ContextModelValidationException($"{entityName}.{name}: vector dimensions must be positive.");
        if ((field.Embeds is not null || field.EmbeddingModel is not null) && field.Index != ContextIndex.Vector)
            throw new ContextModelValidationException($"{entityName}.{name}: embedding options require a vector index.");
        if (field.AllowedValues is not null && field.Index != ContextIndex.Tag)
            throw new ContextModelValidationException($"{entityName}.{name}: allowed values require a tag index.");
        if (field.AllowedValues is { Length: 0 } || field.AllowedValues?.Any(string.IsNullOrWhiteSpace) == true || field.AllowedValues?.Distinct().Count() != field.AllowedValues?.Length)
            throw new ContextModelValidationException($"{entityName}.{name}: allowed values must be non-empty, non-blank, and unique.");
        return new(name, type, field.Description, field.IsKeyComponent, indices, field.AllowedValues);
    }

    internal static void ValidateEntity(string name, string keyTemplate, IReadOnlyList<ContextFieldDefinition> fields,
        IReadOnlyList<ContextRelationshipDefinition> relationships, IEnumerable<string>? registeredEntities = null,
        IReadOnlyList<ContextAclFieldMapping>? aclFieldMappings = null)
    {
        if (fields.Count == 0) throw new ContextModelValidationException($"{name}: at least one ContextField is required.");
        if (fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Count)
            throw new ContextModelValidationException($"{name}: field names must be unique.");
        var placeholders = KeyPlaceholder().Matches(keyTemplate).Select(match => match.Groups[1].Value).ToArray();
        if (placeholders.Length == 0) throw new ContextModelValidationException($"{name}: key template must contain at least one {{field}} component.");
        var keyFields = fields.Where(field => field.IsKeyComponent).Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        if (!placeholders.ToHashSet(StringComparer.Ordinal).SetEquals(keyFields))
            throw new ContextModelValidationException($"{name}: key-template components must exactly match fields marked IsKeyComponent.");
        var fieldNames = fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var relationship in relationships)
        {
            if (!fieldNames.Contains(relationship.SourceField)) throw new ContextModelValidationException($"{name}.{relationship.Name}: source field '{relationship.SourceField}' does not exist.");
            if (registeredEntities is not null && !registeredEntities.Contains(relationship.Target, StringComparer.Ordinal))
                throw new ContextModelValidationException($"{name}.{relationship.Name}: target entity '{relationship.Target}' is not registered.");
        }
        foreach (var mapping in aclFieldMappings ?? [])
        {
            const string prefix = "$.";
            var mappedField = mapping.DocumentField.StartsWith(prefix, StringComparison.Ordinal)
                ? mapping.DocumentField[prefix.Length..] : mapping.DocumentField;
            var field = fields.SingleOrDefault(candidate => candidate.Name == mappedField)
                ?? throw new ContextModelValidationException($"{name}: ACL document field '{mapping.DocumentField}' does not exist.");
            ValidateAccessTag(name, field, mapping.AccessTag);
        }
    }

    internal static void ValidateAccessTag(string entityName, ContextFieldDefinition field, string accessTag)
    {
        if (string.IsNullOrWhiteSpace(accessTag) || !AccessTagName().IsMatch(accessTag))
            throw new ContextModelValidationException($"{entityName}.{field.Name}: access tag must start with a letter and contain only letters, digits, or underscores.");
        if (!field.RedisIndices.Any(index => index.Type == "tag"))
            throw new ContextModelValidationException($"{entityName}.{field.Name}: access-controlled fields require a tag index.");
    }

    /// <summary>Gets the stable external entity name for a CLR type.</summary>
    public static string GetEntityName(Type type) => type.GetCustomAttribute<ContextEntityAttribute>()?.Name ?? type.Name;
    internal static string ExternalName(PropertyInfo property, string? configured) => configured
        ?? property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    internal static string ToDataModelType(Type type, string path)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type == typeof(Guid) || type.IsEnum) return "str";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long) || type == typeof(sbyte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong)) return "int";
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "float";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "datetime";
        if (type.IsArray) return $"list[{ToDataModelType(type.GetElementType()!, path)}]";
        var enumerable = type.GetInterfaces().Append(type).FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is not null) return $"list[{ToDataModelType(enumerable.GetGenericArguments()[0], path)}]";
        throw new ContextModelValidationException($"{path}: CLR type '{type}' cannot be represented in a Context Retriever DataModel.");
    }

    [GeneratedRegex("\\{([A-Za-z_][A-Za-z0-9_]*)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPlaceholder();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AccessTagName();
}
