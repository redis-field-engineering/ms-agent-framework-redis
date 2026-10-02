namespace Redis.Iris.ContextRetriever;

/// <summary>Base exception for Context Retriever connector failures.</summary>
public class ContextRetrieverException : Exception
{
    /// <summary>Creates an exception.</summary>
    public ContextRetrieverException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>Raised when MCP connection or initialization fails.</summary>
public sealed class ContextRetrieverConnectionException : ContextRetrieverException
{
    /// <summary>Creates an exception.</summary>
    public ContextRetrieverConnectionException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>Raised when MCP tool discovery fails.</summary>
public sealed class ContextRetrieverDiscoveryException : ContextRetrieverException
{
    /// <summary>Creates an exception.</summary>
    public ContextRetrieverDiscoveryException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>Raised when trusted application context does not authorize Context Retriever access.</summary>
public sealed class ContextRetrieverAccessDeniedException : ContextRetrieverException
{
    /// <summary>Creates an exception.</summary>
    public ContextRetrieverAccessDeniedException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>Raised when a bounded Context Retriever operation exceeds its configured timeout.</summary>
public sealed class ContextRetrieverTimeoutException : ContextRetrieverException
{
    /// <summary>The bounded operation that timed out.</summary>
    public string Operation { get; }
    /// <summary>Creates an exception.</summary>
    public ContextRetrieverTimeoutException(string operation, Exception? innerException = null)
        : base($"Context Retriever {operation} timed out.", innerException) => Operation = operation;
}
