using System.ClientModel;
using System.Diagnostics;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Development;
using AIWhisper.Worker.Logging;
using OpenAI;
using OpenAI.Responses;

namespace AIWhisper.Worker.AI;

/// <summary>
/// Talks to the OpenAI Responses API via the official OpenAI .NET SDK,
/// asking for a strict-JSON-schema structured response so the pipeline
/// never has to guess at free-form text (see <see cref="AIResponseParser"/>).
///
/// In the installed OpenAI 2.12.0 package this API is exposed through
/// <see cref="ResponsesClient"/> and remains experimental (OPENAI001).
/// </summary>
public sealed class OpenAIDecisionService : IAIDecisionService
{
    private readonly ResponsesClient _client;
    private readonly OpenAIOptions _options;
    private readonly IWorkerLog _log;
    private readonly IAiRequestLog _aiRequestLog;
    private readonly string _creativeSparkPrompt;
    private readonly string _creativeSparkPromptId;

    public OpenAIDecisionService(
        string apiKey,
        OpenAIOptions options,
        IWorkerLog log,
        IAiRequestLog? aiRequestLog = null,
        string creativeSparkPrompt = "",
        string creativeSparkPromptId = "creative-spark:unspecified")
    {
        var credential = new ApiKeyCredential(apiKey);
        var clientOptions = new ResponsesClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };

        _client = new ResponsesClient(credential, clientOptions);
        _options = options;
        _log = log;
        _aiRequestLog = aiRequestLog ?? NullAiRequestLog.Instance;
        _creativeSparkPrompt = creativeSparkPrompt;
        _creativeSparkPromptId = creativeSparkPromptId;
    }

    public async Task<AIDecision> DecideAsync(AIRequestContext context, CancellationToken cancellationToken)
    {
        var appliedSystemInstructions = new List<string> { context.BaseSystemPromptId };
        MinimumReactionLevel? minimumReactionLevel = null;
        var schema = BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["speak", "silent"] },
            "text": { "type": ["string", "null"] }
          },
          "required": ["action", "text"],
          "additionalProperties": false
        }
        """);

        var creationOptions = new CreateResponseOptions
        {
            Model = _options.Model,
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    "dialogue_decision",
                    schema,
                    null,
                    true),
            },
        };

        creationOptions.InputItems.Add(ResponseItem.CreateSystemMessageItem(context.SystemPrompt));
        if (!string.IsNullOrWhiteSpace(context.DevelopmentPhase) && !string.IsNullOrWhiteSpace(context.DevelopmentPrompt))
        {
            creationOptions.InputItems.Add(ResponseItem.CreateSystemMessageItem(
                DevelopmentPromptFormatter.CreateSystemMessage(context.DevelopmentPhase, context.DevelopmentPrompt)));
            appliedSystemInstructions.Add($"parasite-development:{context.DevelopmentPhase}");
        }
        if (MinimumReactionLevelSelector.AppliesToPhase(context.DevelopmentPhase))
        {
            minimumReactionLevel = MinimumReactionLevelSelector.Select(_options.CommentFrequency);
            creationOptions.InputItems.Add(ResponseItem.CreateSystemMessageItem(
                MinimumReactionLevelInstruction.Create(minimumReactionLevel.Value)));
            appliedSystemInstructions.Add($"minimum-reaction-level:{minimumReactionLevel}");
        }
        if (_options.SimplifyEnglishForNonNativeSpeakers)
        {
            creationOptions.InputItems.Add(ResponseItem.CreateSystemMessageItem(SimpleEnglishInstruction.Text));
            appliedSystemInstructions.Add("simple-english");
        }
        if (!string.IsNullOrWhiteSpace(_creativeSparkPrompt) && CreativeSparkInstruction.ShouldApply(
                _options.CreativeSparkChance,
                context.DevelopmentPhase,
                Random.Shared.NextDouble()))
        {
            creationOptions.InputItems.Add(ResponseItem.CreateSystemMessageItem(_creativeSparkPrompt));
            appliedSystemInstructions.Add(_creativeSparkPromptId);
        }
        creationOptions.InputItems.Add(ResponseItem.CreateUserMessageItem(context.UserPrompt));
        context.SystemInstructionsApplied?.Invoke(appliedSystemInstructions.ToArray());

        var attempt = 0;
        string? responseText = null;
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            attempt++;
            try
            {
                var response = await _client.CreateResponseAsync(creationOptions, cancellationToken);
                var text = response.Value.GetOutputText();
                responseText = text;

                if (!AIResponseParser.TryParse(text, out var decision, out var parseError) || decision is null)
                {
                    throw new InvalidOperationException($"malformed structured AI response: {parseError}. Raw: {text}");
                }

                _aiRequestLog.Write(
                    context.CampaignId,
                    "decision",
                    _options.Model,
                    context.UserPrompt,
                    text,
                    attempt,
                    stopwatch,
                    systemInstructions: appliedSystemInstructions,
                    commentFrequency: _options.CommentFrequency.ToString(),
                    minimumReactionLevel: minimumReactionLevel?.ToString());
                return decision;
            }
            catch (Exception ex) when (attempt <= _options.MaxRetries && IsTransient(ex))
            {
                var delay = TimeSpan.FromMilliseconds(_options.RetryBaseDelayMs * Math.Pow(2, attempt - 1));
                _log.Warn($"OpenAI request failed (attempt {attempt}/{_options.MaxRetries}), retrying in {delay}: {ex.Message}");
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                _aiRequestLog.Write(
                    context.CampaignId,
                    "decision",
                    _options.Model,
                    context.UserPrompt,
                    responseText,
                    attempt,
                    stopwatch,
                    ex,
                    appliedSystemInstructions,
                    _options.CommentFrequency.ToString(),
                    minimumReactionLevel?.ToString());
                throw;
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        ClientResultException cre => cre.Status == 429 || cre.Status >= 500,
        HttpRequestException => true,
        TaskCanceledException => true,
        _ => false,
    };
}
