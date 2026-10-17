using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class BoundedLineReaderTests
{
    [Fact]
    public async Task Reads_multiple_lines_CRLF_split_across_chunks_and_final_unterminated_line()
    {
        using var input = new ChunkReader("one\r\ntwo\n\rthree", 1);
        var reader = new BoundedLineReader(input, 5);

        Assert.Equal("one", await reader.ReadLineAsync(default));
        Assert.Equal("two", await reader.ReadLineAsync(default));
        Assert.Equal(string.Empty, await reader.ReadLineAsync(default));
        Assert.Equal("three", await reader.ReadLineAsync(default));
        Assert.Null(await reader.ReadLineAsync(default));
    }

    [Fact]
    public async Task Accepts_exact_limit_and_rejects_oversized_unterminated_line_before_reading_all_input()
    {
        var exact = new BoundedLineReader(new StringReader("12345\n"), 5);
        Assert.Equal("12345", await exact.ReadLineAsync(default));
        using var input = new ChunkReader(new string('s', 100_000), 4);
        var reader = new BoundedLineReader(input, 16);

        var error = await Assert.ThrowsAsync<CodexRpcException>(() => reader.ReadLineAsync(default));

        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(new string('s', 17), error.Message);
        Assert.InRange(input.CharactersRead, 17, 24);
    }

    [Fact]
    public async Task Cancellation_interrupts_a_line_without_waiting_for_newline()
    {
        var reader = new BoundedLineReader(new BlockingReader());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadLineAsync(cancellation.Token));
    }

    private sealed class ChunkReader(string value, int chunkSize) : TextReader
    {
        public int CharactersRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(Math.Min(chunkSize, buffer.Length), value.Length - CharactersRead);
            value.AsMemory(CharactersRead, count).CopyTo(buffer);
            CharactersRead += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class BlockingReader : TextReader
    {
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
