using System.ClientModel;
using DialogExtractor.Worker.Configuration;
using DialogExtractor.Worker.Logging;
using OpenAI;
using OpenAI.Responses;

namespace DialogExtractor.Worker.AI;

/// <summary>
/// Talks to the OpenAI Responses API via the official OpenAI .NET SDK,
/// asking for a strict-JSON-schema structured response so the pipeline
/// never has to guess at free-form text (see <see cref="AIResponseParser"/>).
///
/// The Responses API (this whole `OpenAI.Responses` namespace) only exists
/// from package version 2.2.0-beta.3 onward, and remains marked
/// [Experimental("OPENAI001")] even as of the latest stable release (2.12.0)
/// at the time this was last checked - hence the project-level
/// `&lt;NoWarn&gt;OPENAI001&lt;/NoWarn&gt;` in the .csproj. If a later SDK
/// release has since graduated it to stable, that NoWarn line can be
/// removed.
///
/// *** NOT COMPILE-VERIFIED ***
/// This file could not be built against the real `OpenAI` NuGet package in
/// the environment this was authored in (that sandbox had no NuGet
/// registry access at all - every other file in this project WAS
/// independently compiled and exercised with real inputs there, just not
/// this one). The type/method names below (ResponseCreationOptions,
/// ResponseTextOptions, ResponseTextFormat.CreateJsonSchemaFormat,
/// ResponseItem.Create*MessageItem, GetOutputText, ClientResultException)
/// reflect the Responses API surface as documented at the time of writing,
/// but the SDK is young and its exact surface has moved between versions.
/// Run `dotnet build` after restoring packages and adjust names here if
/// the compiler disagrees - the retry/parsing logic around the call does
/// not depend on getting every name exactly right.
/// </summary>
public sealed class OpenAIDecisionService : IAIDecisionService
{
    private readonly OpenAIClient OpenAIResponseClient _client;
    private readonly OpenAIOptions _options;
    private readonly IWorkerLog _log;

    public OpenAIDecisionService(string apiKey, OpenAIOptions options, IWorkerLog log)
    {
        _client = new OpenAIResponseClient(options.Model, apiKey);
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

        var creationOptions = new ResponseCreationOptions
        {
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    name: "dialogue_decision",
                    jsonSchema: schema,
                    jsonSchemaIsStrict: true),
            },
        };

        var input = new List<ResponseItem>
        {
            ResponseItem.CreateSystemMessageItem(context.SystemPrompt),
            ResponseItem.CreateUserMessageItem(context.UserPrompt),
        };

        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                var response = await _client.CreateResponseAsync(input, creationOptions, cancellationToken);
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

    private static bool IsTransient(Exception ex) => ex switch
    {
        ClientResultException cre => cre.Status == 429 || cre.Status >= 500,
        HttpRequestException => true,
        TaskCanceledException => true,
        _ => false,
    };
}
