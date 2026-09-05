using System.Text;
using System.Threading.Channels;
using AIWhisper.Worker.Persistence;

namespace AIWhisper.Worker.FileMonitoring;

/// <summary>
/// Tails a single JSONL file: tracks a byte position that only ever advances
/// past *complete* (newline-terminated) lines, and detects truncation/
/// replacement so a restart or a file swap never silently drops or
/// duplicates content beyond what at-least-once semantics already tolerate.
///
/// The trailing, not-yet-terminated portion of the file is never persisted
/// anywhere: it is simply re-read (a few dozen bytes, typically) from
/// <see cref="CurrentCheckpoint"/>'s position on the next poll, once it has
/// grown a trailing newline. This makes the checkpoint trivial (a single
/// byte offset) and safe to restore into a brand-new instance after a
/// restart.
///
/// Reset detection deliberately relies only on the file having become
/// shorter than our recorded position - not on FileInfo.CreationTimeUtc.
/// That property is the real NTFS creation time on Windows (the mod's
/// primary platform), but on several Unix filesystems .NET falls back to
/// the inode change time, which updates on every ordinary append - using it
/// there produced false "file replaced" resets on every write during
/// testing. A shrink is an unambiguous, cross-platform replacement signal;
/// "file recreated with equal-or-greater length than the old position,
/// between two polls" is a known, accepted gap for the first version (see
/// project notes, section 35).
///
/// This class performs no notification/polling of its own - callers decide
/// when to call <see cref="PollOnceAsync"/> (a FileSystemWatcher callback, a
/// timer, or a unit test driving it directly).
/// </summary>
public sealed class LogFileReader
{
    readonly string _filePath;
    readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

    long _position;

    public event Action<Exception>? PollFailed;

    public LogFileReader(string filePath)
    {
        _filePath = filePath;
    }

    public ChannelReader<string> Lines => _lines.Reader;

    public void RestoreCheckpoint(FileCheckpoint checkpoint)
    {
        _position = checkpoint.Position;
    }

    public FileCheckpoint CurrentCheckpoint()
    {
        var length = _position;
        try
        {
            if (File.Exists(_filePath)) length = new FileInfo(_filePath).Length;
        }
        catch (IOException)
        {
            // best-effort only
        }

        return new FileCheckpoint
        {
            Position = _position,
            Length = length,
        };
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PollOnceCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Transient conditions (file locked, momentarily missing, etc.)
            // must never take down the reader loop.
            PollFailed?.Invoke(ex);
        }
    }

    private async Task PollOnceCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        var length = new FileInfo(_filePath).Length;

        if (length < _position)
        {
            // Unambiguous truncation/replacement: start over.
            _position = 0;
        }

        if (length <= _position)
        {
            return;
        }

        string text;
        await using (var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Seek(_position, SeekOrigin.Begin);
            using var streamReader = new StreamReader(stream, Encoding.UTF8);
            text = await streamReader.ReadToEndAsync(cancellationToken);
        }

        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            // Only an in-progress, not-yet-terminated line since _position.
            // Nothing to emit yet; do not advance the position.
            return;
        }

        var completeSegment = text[..(lastNewline + 1)];
        foreach (var rawLine in completeSegment.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length > 0)
            {
                _lines.Writer.TryWrite(line);
            }
        }

        // Advance by the byte length of exactly the text we've turned into
        // complete lines (assumes no BOM appears mid-file, which JSONL
        // written by the mod never does). Anything after the last newline
        // is left for a future poll once it, too, is newline-terminated.
        _position += Encoding.UTF8.GetByteCount(completeSegment);
    }

    public void Complete() => _lines.Writer.TryComplete();
}
