using Redis.Iris.ContextRetriever.Modeling;

namespace Redis.Iris.ContextRetriever.Management;

/// <summary>Optional privileged surface administration. Do not expose this client as an agent tool.</summary>
public interface IContextRetrieverManagementClient
{
    /// <summary>Creates a surface from a complete DataModel and Redis connection settings. Does not import records.</summary>
    Task<ContextSurface> CreateSurfaceAsync(ContextDataModel model, ContextSurfaceDeployment deployment, CancellationToken cancellationToken = default);

    /// <summary>Reads an existing surface through the control plane.</summary>
    Task<ContextSurface> GetSurfaceAsync(string surfaceId, CancellationToken cancellationToken = default);

    /// <summary>Updates supplied properties of a surface. Does not migrate existing Redis data.</summary>
    Task<ContextSurface> UpdateSurfaceAsync(string surfaceId, ContextSurfaceUpdate update, CancellationToken cancellationToken = default);
}
