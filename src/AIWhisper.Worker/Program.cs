using AIWhisper.Worker;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Tts;

if (args is ["--play", var audioFile])
{
    var fullPath = Path.GetFullPath(audioFile);
    if (!File.Exists(fullPath))
    {
        Console.Error.WriteLine($"Audio file not found: {fullPath}");
        return;
    }

    Console.WriteLine($"Playing through the Windows default audio device: {fullPath}");
    await new WindowsAudioPlayback().PlayAsync(fullPath, CancellationToken.None);
    Console.WriteLine("Playback finished.");
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection("OpenAI"));
builder.Services.Configure<TtsOptions>(builder.Configuration.GetSection("Tts"));

builder.Services.AddHostedService<CampaignManagerHostedService>();

var host = builder.Build();
await host.RunAsync();
