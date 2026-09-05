namespace AIWhisper.Worker.Configuration;

public sealed class WorkerOptions
{
    public string RootDirectory { get; set; } = "DialogExtractor";
    public int EventOrderingDelayMs { get; set; } = 250;
    public int DialogueEndDelayMs { get; set; } = 500;
    public int DirectoryPollIntervalMs { get; set; } = 2000;
    public int FilePollIntervalMs { get; set; } = 500;
    public string ServerLogFileName { get; set; } = "server.log";
    public string ClientLogFileName { get; set; } = "client.log";
    public string WorkerLogFileName { get; set; } = "worker.log";
    public string CheckpointFileName { get; set; } = "worker-state.json";
    public string AudioDirectoryName { get; set; } = "audio";
    public string SystemPromptPath { get; set; } = "config/ai-system-prompt.txt";
    public int MaxConversationHistoryEntries { get; set; } = 20;
    public int LateEventCompletedRetentionMinutes { get; set; } = 30;
}
