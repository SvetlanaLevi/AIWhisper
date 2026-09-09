using System.Text.RegularExpressions;

namespace AIWhisper.Worker.Memory;

public static partial class ParasiteIntentPolicy
{
    [GeneratedRegex(@"\b(?:i|we)\s+(?:want|need|will|won't|refuse|intend|promise|swear|demand|insist|prefer)\b|\b(?:you|we)\s+(?:should|need to|have to)\b|(?:^|[.!?]\s+)(?:\[[^]]+\]\s*)?(?:kill|spare|leave|take|use|trust|follow|avoid|protect|save|destroy|help|stop|end|find|pursue|don't|do not)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DurableIntentRegex();

    public static bool IsDurable(string? parasiteRemark) =>
        !string.IsNullOrWhiteSpace(parasiteRemark) && DurableIntentRegex().IsMatch(parasiteRemark);
}
