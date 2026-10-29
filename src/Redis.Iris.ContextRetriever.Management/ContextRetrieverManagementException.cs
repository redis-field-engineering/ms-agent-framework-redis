using System.Net;

namespace Redis.Iris.ContextRetriever.Management;

/// <summary>Administrative HTTP or protocol failure without response bodies, endpoints, or credentials.</summary>
public sealed class ContextRetrieverManagementException : HttpRequestException
{
    /// <summary>Creates a sanitized HTTP failure. A failure after dispatch does not prove a write was rejected.</summary>
    public ContextRetrieverManagementException(string operation, HttpStatusCode? statusCode = null)
        : base(statusCode is { } code
            ? $"Context Retriever management operation '{operation}' failed with HTTP {(int)code}."
            : $"Context Retriever management operation '{operation}' failed during transport or response parsing.", null, statusCode)
    {
        Operation = operation;
    }

    /// <summary>Operation that failed: surface.create, surface.get, or surface.update.</summary>
    public string Operation { get; }
}
