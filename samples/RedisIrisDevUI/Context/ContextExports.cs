// Copyright (c) Redis, Inc.

using System.Text.Json;
using Redis.Iris.ContextRetriever.Modeling;

namespace RedisIrisDevUI;

internal static class ContextExports
{
    public static JsonSerializerOptions JsonOptions { get; } = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static bool TryWrite(string[] args)
    {
        if (args.Contains("--print-context-model", StringComparer.Ordinal))
        {
            Console.Write(ContextDataModelJson.Serialize(TravelContext.Model));
            return true;
        }

        if (args.Contains("--print-context-seed", StringComparer.Ordinal))
        {
            Console.Write(JsonSerializer.Serialize(TravelContext.Examples, JsonOptions));
            return true;
        }

        if (args.Contains("--print-context-users", StringComparer.Ordinal))
        {
            Console.Write(JsonSerializer.Serialize(TravelContext.Users, JsonOptions));
            return true;
        }

        return false;
    }
}
