using AIWhisper.Worker;
using AIWhisper.Worker.Configuration;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection("OpenAI"));
builder.Services.Configure<TtsOptions>(builder.Configuration.GetSection("Tts"));

builder.Services.AddHostedService<CampaignManagerHostedService>();

var host = builder.Build();
await host.RunAsync();
