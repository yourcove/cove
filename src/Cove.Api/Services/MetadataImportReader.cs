using System.Text;
using System.Text.Json;

namespace Cove.Api.Services;

/// <summary>
/// Reads an export without retaining its unused sections. Only the current supported
/// entity (or a single JSON token in an unused section) can grow the read buffer.
/// Callbacks must consume the document before returning; the reader owns its lifetime.
/// </summary>
internal sealed class MetadataImportReader
{
    // Upper bound on bytes offered to the parser, including repeated incomplete input.
    internal long ParseInputBytes { get; private set; }
    internal long BufferCopyBytes { get; private set; }

    private JsonReaderState state;
    private string? section;
    private bool sectionValuePending;
    private bool rootSeen;

    internal async Task ReadAsync(
        Stream input,
        Func<string, CancellationToken, Task> startSection,
        Func<string, JsonElement, CancellationToken, Task> readEntity,
        CancellationToken ct,
        Func<string, CancellationToken, Task>? invalidSection = null)
    {
        using var text = new StreamReader(new EncodingDetectionStream(input), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var characters = new char[16 * 1024];
        var encoder = Encoding.UTF8.GetEncoder();
        var buffer = new byte[64 * 1024];
        var count = 0;
        var offset = 0;
        var final = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = Parse(buffer.AsSpan(offset, count), final);
            ParseInputBytes += result.Document != null || result.StartSection != null || result.InvalidSection != null
                ? result.Consumed : count;
            offset += result.Consumed;
            count -= result.Consumed;
            if (result.InvalidSection is { } invalid)
            {
                if (invalidSection == null)
                    throw new JsonException("Supported metadata sections must be arrays or null.");
                await invalidSection(invalid, ct);
                continue;
            }
            if (result.StartSection is { } started)
            {
                await startSection(started, ct);
                continue;
            }
            if (result.Document is { } document)
            {
                using (document)
                    await readEntity(section!, document.RootElement, ct);
                continue;
            }
            if (final)
                break;
            // Leave read-ahead in place while emitting entities. Compact only for a refill,
            // otherwise a large value followed by many small ones repeatedly copies the tail.
            if (offset != 0)
            {
                buffer.AsSpan(offset, count).CopyTo(buffer);
                BufferCopyBytes += count;
                offset = 0;
            }
            // Incomplete values restart parsing at their beginning. Double the retained
            // input before retrying so a large entity/token is scanned in linear total work.
            // Keep individual reads small for cancellation and short-read streams.
            var refillTarget = Math.Max(characters.Length, checked(count * 2));
            do
            {
                ct.ThrowIfCancellationRequested();
                if (buffer.Length - count < Encoding.UTF8.GetMaxByteCount(characters.Length))
                    Array.Resize(ref buffer, checked(buffer.Length * 2));
                var read = await text.ReadAsync(characters.AsMemory(), ct);
                final = read == 0;
                count += encoder.GetBytes(characters.AsSpan(0, read), buffer.AsSpan(count), flush: final);
            } while (!final && count < refillTarget);
        }
        if (!rootSeen)
            throw new JsonException("Metadata must be a JSON object.");
    }

    private ParseResult Parse(ReadOnlySpan<byte> data, bool final)
    {
        var reader = new Utf8JsonReader(data, final, state);
        while (true)
        {
            var before = reader.CurrentState;
            var consumedBefore = (int)reader.BytesConsumed;
            if (!reader.Read())
            {
                state = reader.CurrentState;
                return new((int)reader.BytesConsumed);
            }
            if (!rootSeen)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                    throw new JsonException("Metadata must be a JSON object.");
                rootSeen = true;
            }
            else if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                section = reader.ValueTextEquals("tags") ? "tags"
                    : reader.ValueTextEquals("studios") ? "studios"
                    : reader.ValueTextEquals("performers") ? "performers"
                    : reader.ValueTextEquals("groups") ? "groups" : null;
                sectionValuePending = section != null;
                if (section != null)
                {
                    state = reader.CurrentState;
                    return new((int)reader.BytesConsumed, StartSection: section);
                }
            }
            else if (sectionValuePending)
            {
                sectionValuePending = false;
                if (reader.TokenType == JsonTokenType.Null)
                    section = null;
                else if (reader.TokenType != JsonTokenType.StartArray)
                {
                    var invalid = section;
                    section = null;
                    state = reader.CurrentState;
                    return new((int)reader.BytesConsumed, InvalidSection: invalid);
                }
            }
            else if (section != null && reader.CurrentDepth == 2)
            {
                if (!JsonDocument.TryParseValue(ref reader, out var document))
                {
                    state = before;
                    return new(consumedBefore);
                }
                state = reader.CurrentState;
                return new((int)reader.BytesConsumed, Document: document);
            }
            else if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 1)
                section = null;
        }
    }

    // StreamReader's BOM detection needs the first four bytes together. File reads
    // normally provide them, but streams may legally return one byte at a time.
    private sealed class EncodingDetectionStream(Stream inner) : Stream
    {
        private bool firstRead = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!firstRead) return inner.Read(buffer, offset, count);
            firstRead = false;
            return inner.ReadAtLeast(buffer.AsSpan(offset, count), Math.Min(4, count), throwOnEndOfStream: false);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!firstRead) return await inner.ReadAsync(buffer, cancellationToken);
            firstRead = false;
            return await inner.ReadAtLeastAsync(buffer, Math.Min(4, buffer.Length), throwOnEndOfStream: false, cancellationToken);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private readonly record struct ParseResult(
        int Consumed, JsonDocument? Document = null, string? StartSection = null, string? InvalidSection = null);
}
