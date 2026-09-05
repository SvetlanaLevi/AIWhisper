using System.ClientModel;
using System.Text.Json;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Conversation;
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

    public OpenAIDecisionService(string apiKey, OpenAIOptions options, IWorkerLog log)
    {
        var credential = new ApiKeyCredential(apiKey);
        var clientOptions = new ResponsesClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };

        _client = new ResponsesClient(credential, clientOptions);
        _options = options;
        _log = log;
    }

    public async Task<AIDecision> DecideAsync(AIRequestContext context, CancellationToken cancellationToken)
    {
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
        creationOptions.InputItems.Add(ResponseItem.CreateUserMessageItem(context.UserPrompt));

        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                var response = await _client.CreateResponseAsync(creationOptions, cancellationToken);
                var text = response.Value.GetOutputText();

                if (!AIResponseParser.TryParse(text, out var decision, out var parseError) || decision is null)
                {
                    throw new InvalidOperationException($"malformed structured AI response: {parseError}. Raw: {text}");
                }

                return decision;
            }
            catch (Exception ex) when (attempt <= _options.MaxRetries && IsTransient(ex))
            {
                var delay = TimeSpan.FromMilliseconds(_options.RetryBaseDelayMs * Math.Pow(2, attempt - 1));
                _log.Warn($"OpenAI request failed (attempt {attempt}/{_options.MaxRetries}), retrying in {delay}: {ex.Message}");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public async Task<CampaignMemoryUpdate> UpdateCampaignMemoryAsync(
        CampaignMemory currentMemory,
        string transcript,
        CancellationToken cancellationToken)
    {
        var schema = BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "UpdatedSummary": { "type": ["string", "null"] },
            "ImportantEventsToAdd": { "type": "array", "items": { "type": "string" } },
            "RelationshipUpdates": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "Name": { "type": "string" },
                  "Description": { "type": "string" }
                },
                "required": ["Name", "Description"],
                "additionalProperties": false
              }
            },
            "PlayerTraitsToAdd": { "type": "array", "items": { "type": "string" } },
            "RunningJokesToAdd": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["UpdatedSummary", "ImportantEventsToAdd", "RelationshipUpdates", "PlayerTraitsToAdd", "RunningJokesToAdd"],
          "additionalProperties": false
        }
        """);

        var prompt = $"""
        You maintain long-term memory for a character observing a Baldur's Gate 3 campaign.

        Store only information likely to matter later. Good candidates are important story developments, meaningful relationship changes, repeated player behavior, promises, betrayals, conflicts, romantic developments, facts useful for later callbacks, and recurring patterns that can support a running joke.

        Do not store ordinary dialogue, generic greetings, short-lived facts, duplicates already present in memory, or trivial wording details. PlayerTraits must describe recurring behavior, not a conclusion from one isolated choice unless that event is exceptionally significant. RunningJokes must be genuinely reusable recurring patterns, not a single funny event. Keep UpdatedSummary concise.

        Return a delta only. Never repeat existing items merely to preserve them. Use empty lists, an empty object, and null UpdatedSummary when there is nothing worth adding or changing.

        CURRENT CAMPAIGN MEMORY
        {JsonSerializer.Serialize(currentMemory)}

        NEWLY PROCESSED DIALOGUE
        {transcript}
        """;

        var creationOptions = new CreateResponseOptions
        {
            Model = _options.Model,
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    "campaign_memory_update",
                    schema,
                    null,
                    true),
            },
        };
        creationOptions.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));

        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                var response = await _client.CreateResponseAsync(creationOptions, cancellationToken);
                var text = response.Value.GetOutputText();
                var update = JsonSerializer.Deserialize<CampaignMemoryUpdate>(text);
                return update ?? new CampaignMemoryUpdate();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"malformed structured campaign memory update: {ex.Message}", ex);
            }
            catch (Exception ex) when (attempt <= _options.MaxRetries && IsTransient(ex))
            {
                var delay = TimeSpan.FromMilliseconds(_options.RetryBaseDelayMs * Math.Pow(2, attempt - 1));
                _log.Warn($"OpenAI memory request failed (attempt {attempt}/{_options.MaxRetries}), retrying in {delay}: {ex.Message}");
                await Task.Delay(delay, cancellationToken);
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
