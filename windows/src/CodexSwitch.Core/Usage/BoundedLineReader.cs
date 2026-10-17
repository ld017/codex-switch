using System.Text;

namespace CodexSwitch.Core.Usage;

// Reads protocol lines incrementally so a missing newline cannot allocate an unbounded string.
// The reader belongs to the RPC request gate and is intentionally not safe for concurrent reads.
public sealed class BoundedLineReader
{
    public const int DefaultMaximumLineLength = 1_048_576;
    private readonly TextReader _reader;
    private readonly int _maximumLineLength;
    private readonly char[] _buffer = new char[4096];
    private int _position;
    private int _length;
    private bool _skipLineFeed;

    public BoundedLineReader(TextReader reader, int maximumLineLength = DefaultMaximumLineLength)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maximumLineLength < 1) throw new ArgumentOutOfRangeException(nameof(maximumLineLength));
        _reader = reader;
        _maximumLineLength = maximumLineLength;
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder(Math.Min(_maximumLineLength, 4096));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position == _length)
            {
                _length = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _position = 0;
                if (_length == 0) return line.Length == 0 ? null : line.ToString();
            }
            var character = _buffer[_position++];
            if (_skipLineFeed)
            {
                _skipLineFeed = false;
                if (character == '\n') continue;
            }
            if (character == '\r')
            {
                _skipLineFeed = true;
                return line.ToString();
            }
            if (character == '\n') return line.ToString();
            if (line.Length == _maximumLineLength)
                throw new CodexRpcException($"Codex app-server stdout line exceeded the {_maximumLineLength} character limit.");
            line.Append(character);
        }
    }
}
