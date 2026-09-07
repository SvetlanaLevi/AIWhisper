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

Server events keep `campaignId` in the envelope and use optional `data.snapshotId`
(`Guid?`, no custom converter). Event names remain `save.start`, `memory.load`,
and `save.end`.

- `save.start` requires a snapshot GUID and saves memory, parasite development
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

The former `data.memoryId` contract and flat memory-only snapshot files are
not used by this format. Lua must send `snapshotId`; this worker does not
modify the mod's Lua files.
