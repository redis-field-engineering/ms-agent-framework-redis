using Microsoft.Agents.AI.Redis.Iris;
using Redis.Iris.ContextRetriever;

var endpoint = Environment.GetEnvironmentVariable("REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT")
    ?? throw new InvalidOperationException("Set REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT.");
var agentKey = Environment.GetEnvironmentVariable("REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY")
    ?? throw new InvalidOperationException("Set REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY.");

var profile = new ContextRetrieverAccessProfile(
    new Uri(endpoint),
    "fixed-service-identity",
    new FixedContextRetrieverCredentialProvider(new(agentKey, "environment-v1")));

// Keep this lease alive for as long as the tools are attached to an agent.
// Every caller of that agent receives this fixed identity's server-authorized scope.
await using var lease = await RedisContextRetrieverTools.ConnectAsync(profile, new ContextRetrieverClientFactory());
Console.WriteLine($"Discovered {lease.Tools.Count} fixed-identity Context Retriever tool(s):");
foreach (var tool in lease.Tools) Console.WriteLine($"- {tool.Name}: {tool.Description}");
