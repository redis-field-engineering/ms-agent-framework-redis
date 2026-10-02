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
