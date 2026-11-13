// Copyright (c) Redis, Inc.

using System.Net;

namespace Redis.Iris.AgentMemory.Management;

/// <summary>A sanitized administrative HTTP or protocol failure, without prompts, response bodies or credentials.</summary>
public sealed class AgentMemoryManagementException : HttpRequestException
{
    /// <summary>Creates a failure. A transport error after dispatch does not prove the write was rejected.</summary>
    public AgentMemoryManagementException(string operation, HttpStatusCode? statusCode = null)
        : base(statusCode is { } code
            ? $"Agent Memory management operation '{operation}' failed with HTTP {(int)code}."
            : $"Agent Memory management operation '{operation}' failed during transport or response parsing.", null, statusCode)
    {
        Operation = operation;
    }

    /// <summary>Gets the operation that failed.</summary>
    public string Operation { get; }
}
