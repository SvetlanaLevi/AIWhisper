using System.Reflection;

namespace AIWhisper.Worker;

public static class ApplicationVersion
{
    public static string Current { get; } =
        typeof(ApplicationVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(ApplicationVersion).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
