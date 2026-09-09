using System.Text.RegularExpressions;

namespace AIWhisper.Worker.Conversation;

public static partial class DialogueResourceName
{
    [GeneratedRegex(@"[\s_-]*\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TrailingGuidRegex();

    public static string? Normalize(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource)) return null;
        var name = TrailingGuidRegex().Replace(resource.Trim(), string.Empty).Trim();
        return name.Length == 0 ? null : name;
    }
}
