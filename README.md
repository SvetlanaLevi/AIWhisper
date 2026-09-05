# AIWhisper

`AIWhisper.Worker` (in `src/`) is a standalone .NET worker that reads
the DialogExtractor mod's JSONL logs, reconstructs BG3 dialogues, and asks an
AI whether to respond via TTS. See [NOTES.md](NOTES.md) for how it's built,
what was verified and how, and every assumption that had to be made.

To test Windows audio playback with an existing file, run:

```powershell
dotnet run --project src/AIWhisper.Worker -- --play "C:\path\to\audio.mp3"
```
