# AIWhisper

`AIWhisper.Worker` (in `src/`) is a standalone .NET worker that reads
the DialogExtractor mod's JSONL logs, reconstructs BG3 dialogues, and asks an
AI whether to respond via TTS. See [NOTES.md](NOTES.md) for how it's built,
what was verified and how, and every assumption that had to be made.

To test Windows audio playback with an existing file, run:

```powershell
dotnet run --project src/AIWhisper.Worker -- --play "C:\path\to\audio.mp3"
```

## Save snapshots

Server events keep `campaignId` in the envelope and identify save-specific
memory with `data.memoryId` (`Guid?`). The existing `data.snapshotId` name is
also accepted for compatibility. Event names remain `save.start`,
`memory.load`, and `save.end`.

- `save.start` requires a memory GUID and saves subjective long-term memory,
  parasite development
  (including delivered introductions), and session context together in
  `<campaignId>/snaphots/snapshot_<guid>/state.json`. The directory spelling
  `snaphots` is intentional to match the agreed path. Existing snapshots are
  immutable; repeated save events do not overwrite them.
- `memory.load` restores that state. Omitted/null `snapshotId` initializes empty
  memory and the initial development phase; the following `session.start`
  applies the loaded region. A missing or malformed snapshot logs a warning
  and uses the same empty-state fallback.
- Loading cancels current dialogue work/voice playback, clears recent history
  and pending dialogues, and invalidates old dialogue completion timers.
- Subsequent updates persist to working memory/checkpoint files, never to the
  loaded snapshot. Only the next `save.start` creates a new snapshot.
- `save.end` is ignored. Log reader offsets are not part of the snapshot and
  are not rewound on load. This assumes events from the previous game session
  do not arrive after `memory.load`.

Subjective memory is stored as semantic items with application-generated IDs.
For each completed dialogue batch, memory evaluation runs independently of the
reaction decision. The reaction receives only a compact active subset selected
first by development phase and then by the current characters and context.

For a small configured whitelist of important characters, the same evaluation
also maintains discovered character facts. Static JSON profiles contain only
identity and first impressions; facts explicitly revealed during play are stored
per campaign and shown only in later dialogues involving that character. These
facts are included in save snapshots together with subjective memory.
