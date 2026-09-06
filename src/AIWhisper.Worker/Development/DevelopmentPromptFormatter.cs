namespace AIWhisper.Worker.Development;

public static class DevelopmentPromptFormatter
{
    public static string CreateSystemMessage(string phase, string prompt) => $"""
        PARASITE DEVELOPMENT
        Current phase: {phase}

        {prompt.Trim()}
        """;
}
