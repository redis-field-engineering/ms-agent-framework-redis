using System.Linq.Expressions;
using System.Reflection;

namespace Redis.Iris.ContextRetriever.Modeling;

/// <summary>Fluent alternative and override layer for application domain models.</summary>
public sealed class ContextModelBuilder(string title, string description = "")
{
    private readonly List<IEntityBuilder> _entities = [];

    /// <summary>Adds or configures one POCO entity.</summary>
    public ContextModelBuilder Entity<TEntity>(Action<ContextEntityBuilder<TEntity>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ContextEntityBuilder<TEntity>();
        configure(builder);
        _entities.Add(builder);
        return this;
    }

    /// <summary>Builds and validates a deterministic DataModel.</summary>
    public ContextDataModel Build()
    {
        if (_entities.Count == 0) throw new ContextModelValidationException("At least one entity is required.");
        var entityNames = _entities.ToDictionary(entity => entity.EntityType, entity => entity.ConfiguredName);
        var definitions = _entities.Select(entity => entity.Build(entityNames)).OrderBy(entity => entity.Name, StringComparer.Ordinal).ToArray();
        var names = definitions.Select(entity => entity.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new ContextModelValidationException("Entity names must be unique.");
        foreach (var definition in definitions)
            ContextModelCompiler.ValidateEntity(definition.Name, definition.RedisKeyTemplate, definition.Fields, definition.Relationships, names, definition.AclFieldMappings);
        return new(title, description, definitions);
    }

    private interface IEntityBuilder
    {
        Type EntityType { get; }
        string ConfiguredName { get; }
        ContextEntityDefinition Build(IReadOnlyDictionary<Type, string> entityNames);
    }

    /// <summary>Configures one entity while leaving the POCO usable by ordinary application code.</summary>
    public sealed class ContextEntityBuilder<TEntity> : IEntityBuilder
    {
        private string _name = ContextModelCompiler.GetEntityName(typeof(TEntity));
        private string _description = typeof(TEntity).GetCustomAttribute<ContextEntityAttribute>()?.Description ?? "";
        private string _keyTemplate = typeof(TEntity).GetCustomAttribute<ContextEntityAttribute>()?.KeyTemplate ?? "";
        private readonly Dictionary<string, ContextFieldDefinition> _fields = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ContextRelationshipDefinition> _relationships = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ContextAclFieldMapping> _aclFieldMappings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Type> _relationshipTargets = new(StringComparer.Ordinal);

        internal ContextEntityBuilder()
        {
            if (typeof(TEntity).GetCustomAttribute<ContextEntityAttribute>() is { })
            {
                var annotated = ContextModelCompiler.CompileEntity(typeof(TEntity));
                foreach (var field in annotated.Fields) _fields[field.Name] = field;
                foreach (var relationship in annotated.Relationships) _relationships[relationship.Name] = relationship;
                foreach (var mapping in annotated.AclFieldMappings ?? []) _aclFieldMappings[mapping.DocumentField] = mapping;
                foreach (var property in typeof(TEntity).GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (property.GetCustomAttribute<ContextRelationshipAttribute>() is { } attribute)
                        _relationshipTargets[ContextModelCompiler.ExternalName(property, attribute.Name)] = attribute.TargetType;
                }
            }
        }

        /// <summary>Overrides the stable external entity name.</summary>
        public ContextEntityBuilder<TEntity> Name(string value) { _name = value; return this; }
        /// <summary>Overrides the entity description.</summary>
        public ContextEntityBuilder<TEntity> Description(string value) { _description = value; return this; }
        /// <summary>Sets the Redis key template.</summary>
        public ContextEntityBuilder<TEntity> KeyTemplate(string value) { _keyTemplate = value; return this; }

        /// <summary>Adds or overrides a field selected by a strongly typed expression.</summary>
        public ContextEntityBuilder<TEntity> Field<TProperty>(Expression<Func<TEntity, TProperty>> selector, Action<ContextFieldBuilder> configure)
        {
            var property = GetProperty(selector.Body);
            var existingName = ContextModelCompiler.ExternalName(property, property.GetCustomAttribute<ContextFieldAttribute>()?.Name);
            _fields.TryGetValue(existingName, out var existing);
            _aclFieldMappings.TryGetValue($"$.{existingName}", out var existingAcl);
            var builder = new ContextFieldBuilder(property, existing, existingAcl?.AccessTag);
            configure(builder);
            var field = builder.Build();
            if (existing is not null && existing.Name != field.Name) _fields.Remove(existing.Name);
            _fields[field.Name] = field;
            var oldPath = $"$.{existingName}";
            var newPath = $"$.{field.Name}";
            _aclFieldMappings.Remove(oldPath);
            if (builder.AccessTag is not null) _aclFieldMappings[newPath] = new(newPath, builder.AccessTag);
            return this;
        }

        /// <summary>Adds or overrides a relationship.</summary>
        public ContextEntityBuilder<TEntity> Relationship<TProperty, TTarget>(
            Expression<Func<TEntity, TProperty>> selector, string sourceField, string? name = null, string description = "")
        {
            var property = GetProperty(selector.Body);
            var relationshipName = name ?? ContextModelCompiler.ExternalName(property, null);
            _relationships[relationshipName] = new(relationshipName, ContextModelCompiler.GetEntityName(typeof(TTarget)), description, sourceField);
            _relationshipTargets[relationshipName] = typeof(TTarget);
            return this;
        }

        Type IEntityBuilder.EntityType => typeof(TEntity);
        string IEntityBuilder.ConfiguredName => _name;
        ContextEntityDefinition IEntityBuilder.Build(IReadOnlyDictionary<Type, string> entityNames) => Build(entityNames);
        internal ContextEntityDefinition Build(IReadOnlyDictionary<Type, string> entityNames) => new(_name, _description, _keyTemplate,
            _fields.Values.OrderBy(field => field.Name, StringComparer.Ordinal).ToArray(),
            _relationships.Values.Select(relationship => _relationshipTargets.TryGetValue(relationship.Name, out var target) && entityNames.TryGetValue(target, out var targetName)
                ? relationship with { Target = targetName } : relationship).OrderBy(relationship => relationship.Name, StringComparer.Ordinal).ToArray(),
            _aclFieldMappings.Values.OrderBy(mapping => mapping.DocumentField, StringComparer.Ordinal).ToArray());

        private static PropertyInfo GetProperty(Expression expression)
        {
            if (expression is UnaryExpression { NodeType: ExpressionType.Convert } unary) expression = unary.Operand;
            return expression is MemberExpression { Member: PropertyInfo property } ? property
                : throw new ArgumentException("Selector must reference a property.");
        }
    }

    /// <summary>Fluent field configuration.</summary>
    public sealed class ContextFieldBuilder
    {
        private readonly PropertyInfo _property;
        private string _name;
        private string _description;
        private bool _key;
        private ContextIndex _index;
        private IReadOnlyList<string>? _allowedValues;
        private int _vectorDimensions;
        private string _distanceMetric = "cosine";
        private string? _embeds;
        private string? _embeddingModel;
        private double _weight = 1;
        private bool _noStem;
        private bool _sortable;
        private string? _accessTag;

        internal string? AccessTag => _accessTag;

        internal ContextFieldBuilder(PropertyInfo property, ContextFieldDefinition? existing, string? accessTag = null)
        {
            _property = property;
            _name = existing?.Name ?? ContextModelCompiler.ExternalName(property, null);
            _description = existing?.Description ?? "";
            _key = existing?.IsKeyComponent ?? false;
            _allowedValues = existing?.AllowedValues;
            _accessTag = accessTag;
            var index = existing?.RedisIndices.SingleOrDefault();
            _index = index?.Type switch { "text" => ContextIndex.Text, "tag" => ContextIndex.Tag, "numeric" => ContextIndex.Numeric, "vector" => ContextIndex.Vector, _ => ContextIndex.None };
            if (index is not null)
            {
                _vectorDimensions = index.VectorDimensions ?? 0; _distanceMetric = index.DistanceMetric ?? "cosine";
                _embeds = index.SourceField; _embeddingModel = index.EmbeddingModel; _weight = index.Weight ?? 1;
                _noStem = index.NoStem ?? false; _sortable = index.Sortable ?? false;
            }
        }

        /// <summary>Sets the stable external name.</summary>
        public ContextFieldBuilder Name(string value) { _name = value; return this; }
        /// <summary>Sets the description.</summary>
        public ContextFieldBuilder Description(string value) { _description = value; return this; }
        /// <summary>Marks this field as a key-template component.</summary>
        public ContextFieldBuilder KeyComponent() { _key = true; return this; }
        /// <summary>Configures a text index.</summary>
        public ContextFieldBuilder Text(double weight = 1, bool noStem = false) { _index = ContextIndex.Text; _weight = weight; _noStem = noStem; return this; }
        /// <summary>Configures a tag index and optional categorical domain.</summary>
        public ContextFieldBuilder Tag(params string[] allowedValues) { _index = ContextIndex.Tag; _allowedValues = allowedValues.Length == 0 ? null : allowedValues; return this; }
        /// <summary>Maps this tag-indexed field to an agent-key access tag for row-level authorization.</summary>
        public ContextFieldBuilder AccessControlled(string accessTag) { _accessTag = accessTag; return this; }
        /// <summary>Configures a numeric index.</summary>
        public ContextFieldBuilder Numeric(bool sortable = false) { _index = ContextIndex.Numeric; _sortable = sortable; return this; }
        /// <summary>Configures a vector index.</summary>
        public ContextFieldBuilder Vector(int dimensions, string distanceMetric = "cosine", string? embeds = null, string? embeddingModel = null)
        { _index = ContextIndex.Vector; _vectorDimensions = dimensions; _distanceMetric = distanceMetric; _embeds = embeds; _embeddingModel = embeddingModel; return this; }

        internal ContextFieldDefinition Build()
        {
            var index = _index switch
            {
                ContextIndex.None => null,
                ContextIndex.Text => new ContextIndexDefinition("text", Weight: _weight, NoStem: _noStem ? true : null),
                ContextIndex.Tag => new ContextIndexDefinition("tag"),
                ContextIndex.Numeric => new ContextIndexDefinition("numeric", Sortable: _sortable ? true : null),
                ContextIndex.Vector when _vectorDimensions > 0 => new ContextIndexDefinition("vector", _vectorDimensions, _distanceMetric, _embeds, _embeddingModel),
                ContextIndex.Vector => throw new ContextModelValidationException($"{_name}: vector dimensions must be positive."),
                _ => throw new ContextModelValidationException($"{_name}: unsupported index."),
            };
            if (_allowedValues is not null && _index != ContextIndex.Tag) throw new ContextModelValidationException($"{_name}: allowed values require a tag index.");
            if (_accessTag is not null)
                ContextModelCompiler.ValidateAccessTag(_property.DeclaringType?.Name ?? "Entity", new(_name, ContextModelCompiler.ToDataModelType(_property.PropertyType, _name), _description, _key, index is null ? [] : [index], _allowedValues), _accessTag);
            return new(_name, ContextModelCompiler.ToDataModelType(_property.PropertyType, _name), _description, _key, index is null ? [] : [index], _allowedValues);
        }
    }
}
