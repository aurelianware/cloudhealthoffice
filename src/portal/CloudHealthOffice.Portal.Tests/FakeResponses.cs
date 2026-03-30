using System.Text.Json;

namespace CloudHealthOffice.Portal.Tests;

/// <summary>
/// Static helpers for building common JSON response bodies used with FakeHandler.
/// </summary>
public static class FakeResponses
{
    public static string EmptyArray() => "[]";

    public static string JsonObject(object? obj) => JsonSerializer.Serialize(obj);
}
