using AIWhisper.Worker;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Tts;
using Microsoft.Extensions.Configuration;

if (args is ["--play", var audioFile])
{
    var fullPath = Path.GetFullPath(audioFile);
    if (!File.Exists(fullPath))
    {
        Console.Error.WriteLine($"Audio file not found: {fullPath}");
        return;
    }

    var voiceEffectsOptions = new VoiceEffectsOptions();
    var playbackConfiguration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();
    playbackConfiguration.GetSection("VoiceEffects").Bind(voiceEffectsOptions);
    var ttsOptions = new TtsOptions();
    playbackConfiguration.GetSection("Tts").Bind(ttsOptions);

    Console.WriteLine($"Playing through the Windows default audio device: {fullPath}");
    Console.WriteLine($"Voice effect: {(voiceEffectsOptions.Enabled ? $"enabled (distance {voiceEffectsOptions.Distance:0.00}, shadow: {voiceEffectsOptions.DelayMs} ms, mix {voiceEffectsOptions.DelayMix:0.00}, pitch {voiceEffectsOptions.PitchShiftSemitones:+0.00;-0.00;0.00} semitones)" : "disabled")}");
    Console.WriteLine($"Playback volume: {Math.Clamp(ttsOptions.PlaybackVolume, 0f, 1f):0.00}");
    var playback = new WindowsAudioPlayback(new PsychicDoubleVoiceEffectProcessor(voiceEffectsOptions));
    var cueOptions = LoadPreSpeechCueOptions();
    if (cueOptions.Enabled && !string.IsNullOrWhiteSpace(cueOptions.FilePath))
    {
        var cuePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(cueOptions.FilePath));
        if (File.Exists(cuePath))
        {
            Console.WriteLine($"Playing pre-speech cue: {cuePath}");
            await playback.PlayUnprocessedAsync(cuePath, cueOptions.Volume, cueOptions.DurationMs, CancellationToken.None);
        }
        else
        {
            Console.Error.WriteLine($"Cue audio file not found; continuing without it: {cuePath}");
        }
    }

    await playback.PlayAsync(fullPath, ttsOptions.PlaybackVolume, CancellationToken.None);
    Console.WriteLine("Playback finished.");
    return;
}

if (args is ["--play-cue"])
{
    var cueOptions = LoadPreSpeechCueOptions();
    if (!cueOptions.Enabled)
    {
        Console.Error.WriteLine("Pre-speech cue is disabled in appsettings.json.");
        return;
    }

    if (string.IsNullOrWhiteSpace(cueOptions.FilePath))
    {
        Console.Error.WriteLine("PreSpeechCue:FilePath is empty in appsettings.json.");
        return;
    }

    var cuePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(cueOptions.FilePath));
    if (!File.Exists(cuePath))
    {
        Console.Error.WriteLine($"Cue audio file not found: {cuePath}");
        return;
    }

    Console.WriteLine($"Playing pre-speech cue through the Windows default audio device: {cuePath}");
    await new WindowsAudioPlayback().PlayUnprocessedAsync(cuePath, cueOptions.Volume, cueOptions.DurationMs, CancellationToken.None);
    Console.WriteLine("Cue playback finished.");
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection("OpenAI"));
builder.Services.Configure<AiRequestLoggingOptions>(builder.Configuration.GetSection("AiRequestLogging"));
builder.Services.Configure<TtsOptions>(builder.Configuration.GetSection("Tts"));
builder.Services.Configure<VoiceEffectsOptions>(builder.Configuration.GetSection("VoiceEffects"));
builder.Services.Configure<PreSpeechCueOptions>(builder.Configuration.GetSection("PreSpeechCue"));
builder.Services.Configure<MemoryOptions>(builder.Configuration.GetSection("Memory"));
builder.Services.Configure<ParasiteDevelopmentOptions>(builder.Configuration.GetSection("ParasiteDevelopment"));
builder.Services.AddHostedService<CampaignManagerHostedService>();

var host = builder.Build();
await host.RunAsync();

static PreSpeechCueOptions LoadPreSpeechCueOptions()
{
    var options = new PreSpeechCueOptions();
    new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build()
        .GetSection("PreSpeechCue")
        .Bind(options);
    return options;
}
