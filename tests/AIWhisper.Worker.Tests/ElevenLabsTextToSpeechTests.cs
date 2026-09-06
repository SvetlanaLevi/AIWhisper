using System.Net;
using System.Text.Json;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Tts;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class ElevenLabsTextToSpeechTests
{
    [Fact]
    public async Task SynthesizeAsync_UsesElevenLabsContractAndSavesAudio()
    {
        const string keyVariable = "AIWHISPER_TEST_ELEVENLABS_API_KEY";
        var originalKey = Environment.GetEnvironmentVariable(keyVariable);
        var outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(keyVariable, "test-key");

        try
        {
            var handler = new RecordingHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.elevenlabs.io") };
            using var sut = new ElevenLabsTextToSpeech(
                new TtsOptions
                {
                    Voice = "voice id",
                    ApiKeyEnvironmentVariable = keyVariable,
                    OutputFormat = "mp3_22050_32",
                    PlayOnWindows = false,
                },
                outputDirectory,
                new NullWorkerLog(),
                client);

            var result = await sut.SynthesizeAsync(
                "Hello, Faerûn.",
                new VoiceContext("campaign", "dialogue", "Narrator", null),
                CancellationToken.None);

            Assert.Equal("/v1/text-to-speech/voice%20id?output_format=mp3_22050_32", handler.RequestUri!.PathAndQuery);
            Assert.Equal("test-key", handler.ApiKey);
            Assert.Equal("Hello, Faerûn.", handler.Body.RootElement.GetProperty("text").GetString());
            Assert.Equal("eleven_multilingual_v2", handler.Body.RootElement.GetProperty("model_id").GetString());
            Assert.Equal("mp3", result.Format);
            Assert.EndsWith(".mp3", result.FilePath, StringComparison.Ordinal);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(result.FilePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyVariable, originalKey);
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SynthesizeAsync_PlaysConfiguredCueBeforeGeneratedSpeech()
    {
        const string keyVariable = "AIWHISPER_TEST_ELEVENLABS_CUE_API_KEY";
        var originalKey = Environment.GetEnvironmentVariable(keyVariable);
        var outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var cuePath = Path.GetTempFileName();
        Environment.SetEnvironmentVariable(keyVariable, "test-key");

        try
        {
            var playback = new RecordingPlayback();
            using var client = new HttpClient(new RecordingHandler(() =>
                Assert.Contains(playback.Calls, call => call.Type == "cue"))) { BaseAddress = new Uri("https://api.elevenlabs.io") };
            using var sut = new ElevenLabsTextToSpeech(
                new TtsOptions
                {
                    Voice = "voice",
                    ApiKeyEnvironmentVariable = keyVariable,
                    PlayOnWindows = true,
                },
                outputDirectory,
                new NullWorkerLog(),
                client,
                playback,
                preSpeechCueOptions: new PreSpeechCueOptions
                {
                    Enabled = true,
                    FilePath = cuePath,
                    Volume = 0.4f,
                    DurationMs = 650,
                });

            var result = await sut.SynthesizeAsync(
                "Hello.",
                new VoiceContext("campaign", "dialogue", "Narrator", null),
                CancellationToken.None);

            Assert.Collection(
                playback.Calls,
                call => Assert.Equal(("cue", cuePath, 0.4f, 650), call),
                call => Assert.Equal(("speech", result.FilePath, 1.0f, 0), call));
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyVariable, originalKey);
            File.Delete(cuePath);
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    private sealed class RecordingHandler(Action? onRequest = null) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? ApiKey { get; private set; }
        public JsonDocument Body { get; private set; } = null!;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ApiKey = request.Headers.GetValues("xi-api-key").Single();
            Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            onRequest?.Invoke();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]),
            };
        }
    }

    private sealed class NullWorkerLog : IWorkerLog
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class RecordingPlayback : IAudioPlayback
    {
        public List<(string Type, string Path, float Volume, int DurationMs)> Calls { get; } = [];

        public Task PlayAsync(string filePath, CancellationToken cancellationToken)
        {
            Calls.Add(("speech", filePath, 1.0f, 0));
            return Task.CompletedTask;
        }

        public Task PlayUnprocessedAsync(string filePath, float volume, int durationMs, CancellationToken cancellationToken)
        {
            Calls.Add(("cue", filePath, volume, durationMs));
            return Task.CompletedTask;
        }
    }
}
