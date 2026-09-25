// Copyright (c) Redis, Inc.

using System.Net;

namespace Redis.Iris.AgentMemory;

/// <summary>Represents a non-success response from Redis Agent Memory.</summary>
public sealed class AgentMemoryException : HttpRequestException
{
    /// <summary>Initializes a new instance of the <see cref="AgentMemoryException"/> class.</summary>
    public AgentMemoryException(HttpStatusCode statusCode, string operation, string? errorCode = null)
        : base($"Redis Agent Memory operation '{operation}' failed with HTTP {(int)statusCode}.", null, statusCode)
    {
        this.Operation = operation;
        this.ErrorCode = errorCode;
    }

    /// <summary>Gets the connector operation name.</summary>
    public string Operation { get; }

    /// <summary>Gets a service error code when one was returned.</summary>
    public string? ErrorCode { get; }
}
