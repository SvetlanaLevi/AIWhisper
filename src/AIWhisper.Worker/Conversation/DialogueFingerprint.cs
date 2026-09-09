using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIWhisper.Worker.EventProcessing;

namespace AIWhisper.Worker.Conversation;

/// <summary>
/// Builds a stable identity for the branch of a dialogue that was actually
/// played. Runtime timestamps and invocation IDs are deliberately excluded.
/// </summary>
public static class DialogueFingerprint
{
    public static string Create(DialogueState dialogue)
    {
        var canonical = new StringBuilder();
        canonical.Append("resource:")
            .Append(string.IsNullOrWhiteSpace(dialogue.DialogueResource)
                ? $"id:{dialogue.DialogueId}"
                : dialogue.DialogueResource.Trim())
            .Append('\n');

        foreach (var evt in dialogue.Events
                     .Where(evt => evt.Type is "dialogue.line" or "dialogue.choice")
                     .OrderBy(evt => evt.Timestamp))
        {
            canonical.Append(evt.Type).Append('|');
            AppendValue(canonical, evt.Data, "nodeId");
            AppendValue(canonical, evt.Data, "contentUid");
            AppendValue(canonical, evt.Data, "speaker");
            canonical.Append(NormalizeText(GetString(evt.Data, "text"))).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendValue(StringBuilder target, JsonElement data, string property)
        => target.Append(GetString(data, property)?.Trim()).Append('|');

    private static string? GetString(JsonElement data, string property)
        => data.ValueKind == JsonValueKind.Object &&
           data.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NormalizeText(string? text)
        => string.Join(' ', AIContextBuilder.CleanText(text)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
