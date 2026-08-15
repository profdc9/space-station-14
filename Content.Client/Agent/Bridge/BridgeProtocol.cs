using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Content.Client.Agent.Bridge;

/// <summary>
///     Wire format shared with the MCP server. See <c>protocol/protocol.md</c>.
/// </summary>
public static class BridgeProtocol
{
    /// <summary>
    ///     Frames are newline-delimited, so the writer must never emit one. <c>WriteIndented</c>
    ///     stays false for that reason, not merely for compactness — newlines inside string values
    ///     are escaped by the encoder, so the framing holds.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Ok(int id, object? result = null)
    {
        return JsonSerializer.Serialize(new { id, ok = true, result }, Options);
    }

    public static string Error(int id, string code, string? message = null)
    {
        return JsonSerializer.Serialize(new { id, ok = false, error = new { code, message } }, Options);
    }

    public static string Event(string name, ulong cursor, object payload)
    {
        return EventNode(name, cursor, payload).ToJsonString(Options);
    }

    /// <summary>
    ///     One event as a JSON object. The name and cursor are merged into the payload rather than
    ///     nested, so the MCP side reads one flat object per event whether it arrives live or as
    ///     part of a <c>since</c> replay.
    /// </summary>
    public static JsonNode EventNode(string name, ulong cursor, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, Options)!.AsObject();

        node["event"] = name;
        node["cursor"] = cursor;

        return node;
    }
}
