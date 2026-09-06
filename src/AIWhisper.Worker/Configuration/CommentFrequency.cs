namespace AIWhisper.Worker.Configuration;

public enum CommentFrequency
{
    VeryLow,
    Low,
    Medium,
    High,
}

public static class CommentFrequencyInstruction
{
    public static string Create(CommentFrequency frequency) => frequency switch
    {
        CommentFrequency.VeryLow => "Comment frequency: VeryLow. Silence is the overwhelming default; speak only for exceptional, highly relevant moments.",
        CommentFrequency.Low => "Comment frequency: Low. Silence is the default; ordinary dialogue and minor observations should usually receive no comment.",
        CommentFrequency.Medium => "Comment frequency: Medium. Ignore mundane or repetitive dialogue, but react fairly freely to interesting, funny, tense, revealing, suspicious, or personally relevant moments.",
        CommentFrequency.High => "Comment frequency: High. Comment relatively often, including on smaller interesting or interpersonal moments, but still use silent when there is nothing specific to add.",
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, null),
    };
}
