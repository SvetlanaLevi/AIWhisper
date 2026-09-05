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
/// This still could not be built against the real `OpenAI` NuGet package in
/// the environment this was authored in (no NuGet registry access there
/// either). This revision corrects the previous draft's invented type names
/// (`ResponsesClient` / `ResponsesClientOptions`, which do not exist in the
/// SDK) to the real surface: the client type is `OpenAIResponseClient`, and
/// the *client-level* options type shared across every OpenAI.* sub-client
/// (chat, responses, embeddings, ...) is `OpenAIClientOptions` - there is no
/// per-service "ResponsesClientOptions". Per-call options for a single
/// CreateResponseAsync invocation are a separate type, `ResponseCreationOptions`.
/// Run `dotnet build` after restoring packages and adjust names here if the
/// compiler disagrees - in particular, double check `OpenAIClientOptions`
/// still exposes `NetworkTimeout` (inherited from `ClientPipelineOptions`)
/// under 2.12.0. The retry/parsing logic around the call does not depend on
/// getting every name exactly right.
/// </summary>
public sealed class OpenAIDecisionService : IAIDecisionService
{
    private readonly OpenAIResponseClient _client;
    private readonly OpenAIOptions _options;
    private readonly IWorkerLog _log;

    public OpenAIDecisionService(string apiKey, OpenAIOptions options, IWorkerLog log)
    {
        var credential = new ApiKeyCredential(apiKey);
        var clientOptions = new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };

        _client = new OpenAIResponseClient(options.Model, credential, clientOptions);
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
