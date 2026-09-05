# DialogExtractor.Worker - implementation notes

This documents what was built for `DialogExtractor.Worker`, and - per the
brief's own rule ("if a requirement isn't defined, don't silently invent it -
state the assumption") - every place an assumption had to be made because the
brief or the mod's code didn't pin it down.

## Layout

```
AIWhisper.sln
src/DialogExtractor.Worker/       - the worker itself (see Program.cs)
tests/DialogExtractor.Worker.Tests/ - xUnit tests for the core pipeline
```

Namespaces mirror the folders: `FileMonitoring`, `EventProcessing`,
`Persistence`, `Conversation`, `AI`, `Tts`, `Pipeline`, `Configuration`,
`Logging` - matching section 46 of the brief. The dialogue/event pipeline
(`FileMonitoring`, `EventProcessing`, `Persistence`, `Conversation`, `AI`'s
parsing/interfaces, `Tts`'s interface, `Pipeline`) has **zero** dependency on
Microsoft.Extensions.* or the OpenAI SDK - only `Program.cs`,
`CampaignManagerHostedService.cs`, and `AI/OpenAIDecisionService.cs` know
about hosting/DI/the concrete AI provider. That's what section 54's "three
independent layers" asks for.

## How this was verified (and the one thing that wasn't)

The environment this was built in had no network access to NuGet at all (not
even to restore `Microsoft.Extensions.Hosting`), so the usual `dotnet build`
loop wasn't available for the whole project. To still get real verification
rather than just "it looks right":

- Every file that doesn't need Microsoft.Extensions.* or the OpenAI SDK was
  copied into a throwaway offline classlib (no NuGet packages, just the
  .NET 8 shared framework) and compiled there with nullable warnings and
  analyzers set to error. It compiled clean.
- That same code was then exercised with real inputs taken straight from the
  brief's own examples: JSONL lines were parsed, published out of arrival
  order into the `EventMerger`, and correctly re-ordered by timestamp;
  `DialogueAggregator` correctly built a `5392` dialogue's transcript,
  stripped `<i>Leave.</i>` markup, and handled malformed JSON, a
  campaignId mismatch, and an unknown event type without crashing.
- Two concurrent dialogues in the same campaign were run through the
  aggregator at once and both completed independently; a late event
  arriving after a dialogue's completion was logged and ignored rather than
  reopening it.
- `LogFileReader` was run through: file-doesn't-exist-yet, a partial
  (non-newline-terminated) trailing line withheld across polls, multiple
  appended lines, a simulated restart resuming from a saved checkpoint
  (only the genuinely new content came through), and a truncated/replaced
  file being detected and re-read from scratch.
  **This caught two real bugs**, both fixed and re-verified:
  1. The reader was advancing its checkpoint position past an
     unterminated trailing line, which would have silently dropped that
     line's bytes on a restart. Fixed: position now only ever advances past
     confirmed, newline-terminated lines.
  2. File-replacement detection originally also compared
     `FileInfo.CreationTimeUtc`. On this Linux test environment .NET falls
     back to the inode change time for that property, which updates on
     every ordinary append - so every write looked like "the file was
     replaced" and caused a spurious full re-read. Fixed: replacement is
     now detected only by the file having shrunk, which is unambiguous
     everywhere. See the comment on `LogFileReader` for the tradeoff this
     implies (a file deleted and recreated at >= the old size, between two
     polls, won't be caught) - acceptable for a first version per the
     brief's own "keep it simple, don't over-guarantee" tone in section 35.
- `CheckpointStore` round-trips a checkpoint through disk and recovers to an
  empty checkpoint instead of throwing when the file is corrupt.
- `AIResponseParser` was checked against `speak`, `silent`, missing `text`,
  an unknown action, and non-JSON text - each behaves as specified (section
  22-23: `silent` is not an error; malformed output is a parse failure, not
  a guess).
- The xUnit test files under `tests/` are the same scenarios, written as
  proper `[Fact]`s. They were compile-checked and *run* against a small
  hand-written stand-in for `xunit` (same `Assert`/`Fact` surface, no
  behavior differences that matter here) since the real `xunit` package
  couldn't be restored either - all 30 passed. They should just work with
  the real `Microsoft.NET.Test.Sdk`/`xunit` packages once you restore
  normally; there's nothing to change.

**The one file that could not be verified at all** is
`AI/OpenAIDecisionService.cs` - it calls the official `OpenAI` NuGet
package's Responses API, which needs the real package to even compile
against. The type/method names used there (`ResponseCreationOptions`,
`ResponseTextFormat.CreateJsonSchemaFormat`, `ResponseItem.Create*MessageItem`,
`GetOutputText`, ...) reflect that SDK's documented surface, but it's a young
SDK and names move between versions. Run `dotnet build` after your first
`dotnet restore` and fix up names there if the compiler disagrees - nothing
else in the project depends on getting this exactly right, since it's
reached only through `IAIDecisionService`.

**Update after review:** the project originally pinned `OpenAI` to `2.1.0`
as a guess, which turned out to predate the Responses API entirely - that's
why `OpenAI.Responses` didn't show up in IntelliSense. Confirmed via the
SDK's own changelog: `OpenAIResponseClient`/`OpenAI.Responses` was
introduced in **2.2.0-beta.3**, and as of the latest stable release at the
time of checking (**2.12.0**) it is *still* marked
`[Experimental("OPENAI001")]` - not yet graduated to stable. The `.csproj`
now pins `OpenAI` to `2.12.0` and adds `<NoWarn>OPENAI001</NoWarn>` (scoped
to this one project, called out in a comment) so the experimental-API
warning doesn't block the build. If you'd rather avoid an experimental API
entirely, the alternative is switching this one file to the stable Chat
Completions API (`OpenAI.Chat` / `ChatClient`), which also supports
strict-JSON-schema structured output via `ChatResponseFormat` - everything
else in the project is unaffected either way, since `IAIDecisionService` is
the only seam that matters here.

## Assumptions made (flagged per the brief's own rule in section 53)

- **Event Lab integration surface.** Not specified anywhere in the brief.
  `Tts/EventLabTextToSpeech.cs` assumes a local HTTP endpoint that accepts
  `POST { text, voice, format, speaker }` and returns raw audio bytes,
  configured via the `Tts` section in `appsettings.json` (`BaseUrl`,
  `Endpoint`, `Voice`, `Format`, `ApiKeyEnvironmentVariable`). If Event Lab's
  actual integration is a CLI, a named pipe, or a different HTTP shape,
  only this one file needs to change - `ITextToSpeech` is the seam the rest
  of the pipeline depends on, exactly as the brief's notes asked for.
- **OpenAI model name.** Defaulted to `gpt-4.1-mini` in `appsettings.json`;
  change `OpenAI:Model` to whatever you want to use.
- **AI system prompt.** A starting prompt lives at
  `src/DialogExtractor.Worker/config/ai-system-prompt.txt` and is loaded at
  startup (falls back to a short built-in default if the file is missing,
  with a warning in the log). Replace its content freely - nothing in the
  code depends on its wording.
- **Timestamps are treated as an ordering-only, single-clock value.** The
  mod's timestamp strings (`"2026-09-05 17:13:06.8766646"`) carry no
  timezone. Since both `server.log` and `client.log` come from the same
  game process on the same machine, they're parsed as a plain `DateTime`
  and compared to each other only - no timezone conversion is attempted or
  needed.
- **Conversation history is in-memory only**, per campaign, rebuilt as
  dialogues are (re-)processed. It is not written to `worker-state.json`.
  The brief's checkpoint requirements (section 32-35) are specifically
  about file *read position*, which is what's persisted; nothing in the
  brief asks for AI conversation history to survive a restart, and
  at-least-once processing means a dialogue could be reprocessed anyway.
  If you want history to survive a restart, that's an additional, separate
  persistence layer on top of `CampaignContext`.
- **Sequential AI/TTS processing per campaign**, as section 38 explicitly
  allows: multiple dialogues can be *active* and *complete* concurrently,
  but `ConversationManager` drains the completed-dialogue queue one at a
  time. Simple, and avoids an unbounded number of simultaneous OpenAI/TTS
  calls in v1.
- **Reset detection uses only "the file got shorter"**, not creation time -
  see the bug note above.
- **Retry policy for OpenAI** is exponential backoff (`RetryBaseDelayMs *
  2^attempt`), capped at `OpenAI:MaxRetries`, only for what looks like a
  transient failure (HTTP 429/5xx, network error, timeout). Anything else
  (including a malformed structured response) is not retried - it's logged
  and the dialogue is left unanswered, per section 25/41.

## Running it

1. `dotnet restore` from the repo root (needs real NuGet access, unlike the
   sandbox this was built in).
2. Set the `OPENAI_API_KEY` environment variable (required - the worker
   refuses to start without it, and never logs it).
3. Optionally set `EVENTLAB_API_KEY` if your Event Lab endpoint needs one.
4. Point `Worker:RootDirectory` in `appsettings.json` at wherever
   `DialogExtractor` mod writes its campaign folders (defaults to a
   relative `DialogExtractor` folder next to the worker).
5. `dotnet run --project src/DialogExtractor.Worker`.
6. `dotnet test` runs the test suite (once packages are restored).

Per-campaign `worker.log`, `worker-state.json`, and an `audio/` folder are
created next to that campaign's `server.log`/`client.log` - the worker never
writes into those two files itself.
