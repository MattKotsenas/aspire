// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Adapts one process event stream without taking over process or pipe ownership.
/// </summary>
internal static class ProcessEventExtensions
{
    /// <summary>
    /// Observes process startup without changing or removing events.
    /// </summary>
    /// <typeparam name="T">The event payload type.</typeparam>
    /// <param name="events">The deferred process events to observe.</param>
    /// <param name="observer">The callback invoked with the process ID during consumption.</param>
    /// <returns>A deferred stream that forwards the original events after observation.</returns>
    public static IAsyncEnumerable<ProcessEvent<T>> OnStarted<T>(
        this IAsyncEnumerable<ProcessEvent<T>> events, Action<int> observer)
        => Observe(events, observer, static (item, callback) =>
        {
            if (item is ProcessEvent<T>.Started(var processId))
            {
                callback(processId);
            }
        });

    /// <summary>
    /// Observes standard output without changing or removing events.
    /// </summary>
    /// <typeparam name="T">The event payload type.</typeparam>
    /// <param name="events">The deferred process events to observe.</param>
    /// <param name="observer">The callback invoked for each stdout payload during consumption.</param>
    /// <returns>A deferred stream that forwards the original events after observation.</returns>
    public static IAsyncEnumerable<ProcessEvent<T>> OnStandardOutput<T>(
        this IAsyncEnumerable<ProcessEvent<T>> events, Action<T> observer)
        => Observe(events, observer, static (item, callback) =>
        {
            if (item is ProcessEvent<T>.StandardOutput(var content))
            {
                callback(content);
            }
        });

    /// <summary>
    /// Observes standard error without changing or removing events.
    /// </summary>
    /// <typeparam name="T">The event payload type.</typeparam>
    /// <param name="events">The deferred process events to observe.</param>
    /// <param name="observer">The callback invoked for each stderr payload during consumption.</param>
    /// <returns>A deferred stream that forwards the original events after observation.</returns>
    public static IAsyncEnumerable<ProcessEvent<T>> OnStandardError<T>(
        this IAsyncEnumerable<ProcessEvent<T>> events, Action<T> observer)
        => Observe(events, observer, static (item, callback) =>
        {
            if (item is ProcessEvent<T>.StandardError(var content))
            {
                callback(content);
            }
        });

    private static async IAsyncEnumerable<ProcessEvent<T>> Observe<T, TCallback>(
        IAsyncEnumerable<ProcessEvent<T>> events,
        Action<TCallback> observer,
        Action<ProcessEvent<T>, Action<TCallback>> observe,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var enumerator = events.GetAsyncEnumerator(cancellationToken);
        Exception? failure = null;
        try
        {
            while (true)
            {
                ProcessEvent<T> item;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    item = enumerator.Current;
                    observe(item, observer);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }

                yield return item;
            }
        }
        finally
        {
            await DisposeAsync(enumerator, failure).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Decodes each output independently and flushes it at its own EOF.
    /// </summary>
    /// <param name="events">The byte events to decode during consumption.</param>
    /// <param name="encoding">The encoding and fallback policy used by each stream's decoder.</param>
    /// <returns>A deferred stream of owned decoded character buffers and lifecycle events.</returns>
    public static IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<char>>> DecodeChars(
        this IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> events, Encoding encoding)
        => DecodeCharsCore(events, encoding);

    private static async IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<char>>> DecodeCharsCore(
        IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> events,
        Encoding encoding,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var output = encoding.GetDecoder();
        var error = encoding.GetDecoder();
        var enumerator = events.GetAsyncEnumerator(cancellationToken);
        Exception? failure = null;
        try
        {
            while (true)
            {
                (ProcessEvent<ReadOnlyMemory<char>>? Event, ProcessEvent<ReadOnlyMemory<char>>? Completion) decoded;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    // An input event produces at most a payload and its EOF. Decode eagerly
                    // inside the guard so a decoder failure survives upstream disposal.
                    decoded = DecodeEvent(enumerator.Current, output, error, encoding);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }

                if (decoded.Event is { } content)
                {
                    yield return content;
                }

                if (decoded.Completion is { } completion)
                {
                    yield return completion;
                }
            }
        }
        finally
        {
            await DisposeAsync(enumerator, failure).ConfigureAwait(false);
        }
    }

    private static (ProcessEvent<ReadOnlyMemory<char>>? Event, ProcessEvent<ReadOnlyMemory<char>>? Completion) DecodeEvent(
        ProcessEvent<ReadOnlyMemory<byte>> item, Decoder output, Decoder error, Encoding encoding)
    {
        switch (item)
        {
            case ProcessEvent<ReadOnlyMemory<byte>>.Started started:
                return (new ProcessEvent<ReadOnlyMemory<char>>.Started(started.ProcessId), null);
            case ProcessEvent<ReadOnlyMemory<byte>>.StandardOutput data:
                var text = GetChars(output, encoding, data.Content, flush: false);
                return (text.IsEmpty ? null : new ProcessEvent<ReadOnlyMemory<char>>.StandardOutput(text), null);
            case ProcessEvent<ReadOnlyMemory<byte>>.StandardError data:
                var errorText = GetChars(error, encoding, data.Content, flush: false);
                return (errorText.IsEmpty ? null : new ProcessEvent<ReadOnlyMemory<char>>.StandardError(errorText), null);
            case ProcessEvent<ReadOnlyMemory<byte>>.StandardOutputCompleted:
                var finalText = GetChars(output, encoding, default, flush: true);
                return (finalText.IsEmpty ? null : new ProcessEvent<ReadOnlyMemory<char>>.StandardOutput(finalText),
                    new ProcessEvent<ReadOnlyMemory<char>>.StandardOutputCompleted());
            case ProcessEvent<ReadOnlyMemory<byte>>.StandardErrorCompleted:
                var finalError = GetChars(error, encoding, default, flush: true);
                return (finalError.IsEmpty ? null : new ProcessEvent<ReadOnlyMemory<char>>.StandardError(finalError),
                    new ProcessEvent<ReadOnlyMemory<char>>.StandardErrorCompleted());
            case ProcessEvent<ReadOnlyMemory<byte>>.Exited exited:
                return (new ProcessEvent<ReadOnlyMemory<char>>.Exited(exited.ExitCode), null);
            default:
                return (null, null);
        }
    }

    private static ReadOnlyMemory<char> GetChars(Decoder decoder, Encoding encoding, ReadOnlyMemory<byte> bytes, bool flush)
    {
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        var count = decoder.GetChars(bytes.Span, chars, flush);
        return chars.AsMemory(0, count);
    }

    /// <summary>
    /// Frames CR, LF and CRLF lines, including empty lines and a final unterminated line at EOF.
    /// </summary>
    /// <param name="events">The decoded process events to frame into lines during consumption.</param>
    /// <returns>A deferred stream of lines and lifecycle events, with each output flushed only at its own EOF.</returns>
    /// <remarks>
    /// Standard output and standard error are buffered independently. An unterminated line is buffered
    /// without a size limit until a line delimiter or that stream's EOF, which can require substantial memory.
    /// </remarks>
    public static IAsyncEnumerable<ProcessEvent<string>> FrameLines(
        this IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<char>>> events)
        => FrameLinesCore(events);

    private static async IAsyncEnumerable<ProcessEvent<string>> FrameLinesCore(
        IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<char>>> events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var output = new LineBuffer();
        var error = new LineBuffer();
        var enumerator = events.GetAsyncEnumerator(cancellationToken);
        Exception? failure = null;
        try
        {
            while (true)
            {
                IEnumerator<ProcessEvent<string>> lines;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    lines = Read(enumerator.Current, output, error).GetEnumerator();
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }

                using (lines)
                {
                    while (true)
                    {
                        ProcessEvent<string> item;
                        try
                        {
                            // Project lazily without a per-event array. Guard both iterator operations
                            // so a framing failure survives disposal of the upstream event stream.
                            if (!lines.MoveNext())
                            {
                                break;
                            }

                            item = lines.Current;
                        }
                        catch (Exception exception)
                        {
                            failure = exception;
                            throw;
                        }

                        yield return item;
                    }
                }
            }
        }
        finally
        {
            await DisposeAsync(enumerator, failure).ConfigureAwait(false);
        }
    }

    private static IEnumerable<ProcessEvent<string>> Read(
        ProcessEvent<ReadOnlyMemory<char>> item, LineBuffer output, LineBuffer error)
    {
        switch (item)
        {
            case ProcessEvent<ReadOnlyMemory<char>>.Started started:
                yield return new ProcessEvent<string>.Started(started.ProcessId);
                break;
            case ProcessEvent<ReadOnlyMemory<char>>.StandardOutput data:
                foreach (var line in output.Append(data.Content))
                {
                    yield return new ProcessEvent<string>.StandardOutput(line);
                }
                break;
            case ProcessEvent<ReadOnlyMemory<char>>.StandardError data:
                foreach (var line in error.Append(data.Content))
                {
                    yield return new ProcessEvent<string>.StandardError(line);
                }
                break;
            case ProcessEvent<ReadOnlyMemory<char>>.StandardOutputCompleted:
                if (output.Complete() is { } finalLine)
                {
                    yield return new ProcessEvent<string>.StandardOutput(finalLine);
                }
                yield return new ProcessEvent<string>.StandardOutputCompleted();
                break;
            case ProcessEvent<ReadOnlyMemory<char>>.StandardErrorCompleted:
                if (error.Complete() is { } finalError)
                {
                    yield return new ProcessEvent<string>.StandardError(finalError);
                }
                yield return new ProcessEvent<string>.StandardErrorCompleted();
                break;
            case ProcessEvent<ReadOnlyMemory<char>>.Exited exited:
                yield return new ProcessEvent<string>.Exited(exited.ExitCode);
                break;
        }
    }

    /// <summary>
    /// Drains events and awaits disposal before returning the known exit code, including nonzero exits.
    /// </summary>
    /// <typeparam name="T">The event payload type.</typeparam>
    /// <param name="events">The process events to consume through completion and disposal.</param>
    /// <param name="cancellationToken">The token used to cancel consumption and process execution.</param>
    /// <returns>The observed exit code after consumption and cleanup complete.</returns>
    /// <exception cref="InvalidOperationException">The stream completes without an exit code.</exception>
    /// <exception cref="OperationCanceledException">Consumption or execution is canceled.</exception>
    public static async Task<int> ExecuteAsync<T>(
        this IAsyncEnumerable<ProcessEvent<T>> events,
        CancellationToken cancellationToken = default)
    {
        try
        {
            int? exitCode = null;
            var enumerator = events.GetAsyncEnumerator(cancellationToken);
            Exception? failure = null;
            try
            {
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    if (enumerator.Current is ProcessEvent<T>.Exited(var code))
                    {
                        exitCode = code;
                    }
                }
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
            finally
            {
                await DisposeAsync(enumerator, failure).ConfigureAwait(false);
            }

            return exitCode ?? throw new InvalidOperationException("The process event stream completed without an exit code.");
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Process execution was canceled.", exception, cancellationToken);
        }
    }

    /// <summary>
    /// Appends exact decoded standard output to caller-owned storage without changing events.
    /// </summary>
    /// <param name="events">The decoded character events to observe during consumption.</param>
    /// <param name="destination">The caller-owned builder receiving each stdout span, without added delimiters.</param>
    /// <returns>A deferred stream forwarding the original events after appending.</returns>
    /// <remarks>
    /// Retained text is not bounded by this adapter. The caller controls its storage and can inspect
    /// partial diagnostics after failed consumption; only terminal success establishes complete output.
    /// </remarks>
    public static IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<char>>> AppendStandardOutput(
        this IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<char>>> events, StringBuilder destination)
        => events.OnStandardOutput(content => destination.Append(content.Span));

    /// <summary>
    /// Appends exact decoded standard error to caller-owned storage without changing events.
    /// </summary>
    /// <param name="events">The decoded character events to observe during consumption.</param>
    /// <param name="destination">The caller-owned builder receiving each stderr span, without added delimiters.</param>
    /// <returns>A deferred stream forwarding the original events after appending.</returns>
    /// <remarks>
    /// Retained text is not bounded by this adapter. The caller controls its storage and can inspect
    /// partial diagnostics after failed consumption; only terminal success establishes complete output.
    /// </remarks>
    public static IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<char>>> AppendStandardError(
        this IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<char>>> events, StringBuilder destination)
        => events.OnStandardError(content => destination.Append(content.Span));

    // A transform owns both its projection failure and disposal. A plain await foreach
    // lets the latter replace the former before the transform can observe it.
    private static async ValueTask DisposeAsync(IAsyncDisposable enumerator, Exception? failure)
    {
        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (failure is not null)
        {
            throw new AggregateException(failure, cleanupFailure);
        }
    }

    private sealed class LineBuffer
    {
        private readonly StringBuilder _line = new();
        private bool _afterCarriageReturn;

        public IEnumerable<string> Append(ReadOnlyMemory<char> text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                var value = text.Span[index];
                var skip = _afterCarriageReturn && value == '\n';
                _afterCarriageReturn = value == '\r';
                if (skip)
                {
                    continue;
                }
                if (value is '\r' or '\n')
                {
                    yield return _line.ToString();
                    _line.Clear();
                }
                else
                {
                    _line.Append(value);
                }
            }
        }

        public string? Complete() => _line.Length > 0 ? _line.ToString() : null;
    }
}
