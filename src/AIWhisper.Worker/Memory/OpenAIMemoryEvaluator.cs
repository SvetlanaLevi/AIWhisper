using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Logging;
using OpenAI;
using OpenAI.Responses;

namespace AIWhisper.Worker.Memory;

public sealed class OpenAIMemoryEvaluator : IMemoryEvaluator
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ResponsesClient _client;
    private readonly OpenAIOptions _options;
    private readonly IWorkerLog _log;
    private readonly IAiRequestLog _aiRequestLog;
    private readonly string _prompt;
    private readonly string _promptId;

    public OpenAIMemoryEvaluator(
        string apiKey,
        OpenAIOptions options,
        IWorkerLog log,
        IAiRequestLog? aiRequestLog,
        string prompt,
        string promptId)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Memory evaluator prompt must not be empty.", nameof(prompt));

        _client = new ResponsesClient(new ApiKeyCredential(apiKey), new ResponsesClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        });
        _options = options;
        _log = log;
        _aiRequestLog = aiRequestLog ?? NullAiRequestLog.Instance;
        _prompt = prompt;
        _promptId = promptId;
    }

    public async Task<MemoryEvaluationResult> EvaluateAsync(
        MemoryEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        var schema = BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "operations": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "kind": { "type": "string", "enum": ["create", "update", "remove"] },
                  "targetId": { "type": ["string", "null"] },
                  "summary": { "type": ["string", "null"] },
                  "category": { "type": ["string", "null"], "enum": [null, "survival", "removalThreat", "illithidPower", "ceremorphosis", "parasiteNature", "hostAttitude", "hostBehavior", "relationship", "trust", "protection", "threat", "betrayal", "conflict", "characterOpinion", "characterRelationship", "generalObservation"] },
                  "characterName": { "type": ["string", "null"] },
                  "tags": { "type": ["array", "null"], "items": { "type": "string" } }
                },
                "required": ["kind", "targetId", "summary", "category", "characterName", "tags"],
                "additionalProperties": false
              }
            },
            "characterKnowledgeUpdates": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "characterName": { "type": "string" },
                  "knownFacts": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["characterName", "knownFacts"],
                "additionalProperties": false
              }
            }
          },
          "required": ["operations", "characterKnowledgeUpdates"],
          "additionalProperties": false
        }
        """);
        var userContext = ParasiteMemoryEvaluatorPrompt.RenderUserContext(request);
        var instructions = new[] { $"memory-evaluator:{_promptId}" };
        var options = new CreateResponseOptions
        {
            Model = _options.Model,
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    "parasite_memory_operations", schema, null, true),
            },
        };
        options.InputItems.Add(ResponseItem.CreateSystemMessageItem(_prompt));
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(userContext));

        var attempt = 0;
        string? responseText = null;
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            attempt++;
            try
            {
                var response = await _client.CreateResponseAsync(options, cancellationToken);
                responseText = response.Value.GetOutputText();
                var result = JsonSerializer.Deserialize<MemoryEvaluationResult>(responseText, SerializerOptions)
                    ?? throw new InvalidOperationException("empty structured memory evaluation response");
                result.Operations ??= [];
                result.CharacterKnowledgeUpdates ??= [];
                _aiRequestLog.Write(request.CampaignId, "memory-evaluation", _options.Model,
                    userContext, responseText, attempt, stopwatch, systemInstructions: instructions);
                return result;
            }
            catch (JsonException ex)
            {
                var malformed = new InvalidOperationException($"malformed structured memory evaluation: {ex.Message}", ex);
                _aiRequestLog.Write(request.CampaignId, "memory-evaluation", _options.Model,
                    userContext, responseText, attempt, stopwatch, malformed, instructions);
                throw malformed;
            }
            catch (Exception ex) when (attempt <= _options.MaxRetries && IsTransient(ex))
            {
                var delay = TimeSpan.FromMilliseconds(_options.RetryBaseDelayMs * Math.Pow(2, attempt - 1));
                _log.Warn($"OpenAI memory evaluation failed (attempt {attempt}/{_options.MaxRetries}), retrying in {delay}: {ex.Message}");
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                _aiRequestLog.Write(request.CampaignId, "memory-evaluation", _options.Model,
                    userContext, responseText, attempt, stopwatch, ex, instructions);
                throw;
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        ClientResultException result => result.Status == 429 || result.Status >= 500,
        HttpRequestException => true,
        TaskCanceledException => true,
        _ => false,
    };
}
