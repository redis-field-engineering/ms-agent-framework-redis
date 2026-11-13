// Copyright (c) Redis, Inc.

namespace Redis.Iris.AgentMemory.Management;

/// <summary>Explicitly privileged custom-type administration over the Iris control-plane v2 API.</summary>
public interface IAgentMemoryManagementClient
{
    /// <summary>Lists registered custom memory types. Pass NextCursor to retrieve subsequent pages.</summary>
    Task<MemoryTypeListResult> ListMemoryTypesAsync(string storeId, int limit = 50, string? cursor = null, CancellationToken cancellationToken = default);
    /// <summary>Gets a registered type and its current revision.</summary>
    Task<CustomMemoryTypeDefinition> GetMemoryTypeAsync(string storeId, string name, CancellationToken cancellationToken = default);
    /// <summary>Registers a new type. Existing or retired names return 409; concurrent registry changes may return 412.</summary>
    Task<CustomMemoryTypeDefinition> CreateMemoryTypeAsync(string storeId, CustomMemoryTypeDefinition definition, CancellationToken cancellationToken = default);
    /// <summary>Updates an existing strategy, optionally conditional on the last-read revision. Does not modify the type's fields.</summary>
    Task<CustomMemoryTypeDefinition> UpdateExtractionStrategyAsync(string storeId, string name, MemoryExtractionStrategyUpdate update,
        string? expectedRevision = null, CancellationToken cancellationToken = default);
}
