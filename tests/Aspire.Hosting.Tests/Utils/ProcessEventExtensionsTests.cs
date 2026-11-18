// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests.Utils;

[Trait("Partition", "4")]
public class ProcessEventExtensionsTests
{
    [Fact]
    public async Task EventStreamIsDeferredAndDerivedPipelinesShareItsClaim()
    {
        var source = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("first\n"u8),
            Emit<byte>.Stderr("warning\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]);
        var execution = new ExecutionEventStream(source);
        var decoded = execution.DecodeChars(Encoding.UTF8);
        Assert.Equal(0, source.MoveNextCount);
        Assert.Equal(0, source.DisposeCount);

        await using (var enumerator = decoded.GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.Equal(0, source.MoveNextCount);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new ProcessEvent<ReadOnlyMemory<char>>.Started(1), enumerator.Current);
            using var rejectedCancellation = new CancellationTokenSource();
            rejectedCancellation.Cancel();
            Assert.Throws<InvalidOperationException>(() => execution.GetAsyncEnumerator(rejectedCancellation.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => execution.OnStandardError(_ => { }).ExecuteAsync());
            Assert.Equal(1, source.MoveNextCount);
            Assert.Equal(0, source.DisposeCount);

            var remaining = new List<ProcessEvent<ReadOnlyMemory<char>>>();
            while (await enumerator.MoveNextAsync())
            {
                remaining.Add(enumerator.Current);
            }

            Assert.Collection(remaining,
                item => Assert.Equal("first\n", Assert.IsType<ProcessEvent<ReadOnlyMemory<char>>.StandardOutput>(item).Content.ToString()),
                item => Assert.Equal("warning\n", Assert.IsType<ProcessEvent<ReadOnlyMemory<char>>.StandardError>(item).Content.ToString()),
                item => Assert.IsType<ProcessEvent<ReadOnlyMemory<char>>.StandardOutputCompleted>(item),
                item => Assert.IsType<ProcessEvent<ReadOnlyMemory<char>>.StandardErrorCompleted>(item),
                item => Assert.Equal(new ProcessEvent<ReadOnlyMemory<char>>.Exited(0), item));
        }

        Assert.Equal(source.Steps.Count + 1, source.MoveNextCount);
        Assert.Equal(1, source.DisposeCount);
        Assert.Throws<InvalidOperationException>(() => execution.GetAsyncEnumerator());

        var output = new StringBuilder();
        var error = new StringBuilder();
        var secondSource = FragmentedEvents("second\n");
        var second = await new ExecutionEventStream(secondSource).DecodeChars(Encoding.UTF8)
            .AppendStandardOutput(output)
            .AppendStandardError(error)
            .ExecuteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, second);
        Assert.Equal("second\n", output.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal(secondSource.Steps.Count + 1, secondSource.MoveNextCount);
        Assert.Equal(1, secondSource.DisposeCount);
    }

    [Fact]
    public async Task ObserversForwardEveryEventUnchangedAndPreserveObserverOrder()
    {
        var source = new FakeEventStream<char>(
        [
            Emit<char>.Started(1),
            Emit<char>.Stdout("first\n"),
            Emit<char>.Stderr("warning\n"),
            Emit<char>.Stdout("last\n"),
            Emit<char>.StdoutCompleted(),
            Emit<char>.StderrCompleted(),
            Emit<char>.Exited(0)
        ]);
        var observed = new List<string>();
        var pipeline = source
            .OnStarted(processId => observed.Add("started-1:" + processId))
            .OnStarted(processId => observed.Add("started-2:" + processId))
            .OnStandardOutput(text => observed.Add("output-1:" + text.ToString()))
            .OnStandardOutput(text => observed.Add("output-2:" + text.ToString()))
            .OnStandardError(text => observed.Add("error:" + text.ToString()));
        Assert.Empty(observed);

        var events = await CollectAsync(pipeline);

        Assert.Equal(source.Steps.Count, events.Count);
        for (var index = 0; index < events.Count; index++)
        {
            var original = Assert.IsType<EventStep<char>>(source.Steps[index]);
            Assert.Same(original.Event, events[index]);
        }

        Assert.Equal(
            ["started-1:1", "started-2:1", "output-1:first\n", "output-2:first\n", "error:warning\n", "output-1:last\n", "output-2:last\n"],
            observed);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(17, false)]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(17, true)]
    public async Task TerminalRequiresKnownExitAfterCleanup(int? exitCode, bool capture)
    {
        List<StreamStep<byte>> steps =
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("drain this"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted()
        ];
        if (exitCode is { } knownExitCode)
        {
            steps.Add(Emit<byte>.Exited(knownExitCode));
        }

        var source = new FakeEventStream<byte>(steps);
        var observed = new StringBuilder();
        var error = new StringBuilder();
        Assert.Equal(0, source.MoveNextCount);
        Assert.Equal(0, source.DisposeCount);
        Assert.Equal(0, observed.Length);

        var completion = capture
            ? source.DecodeChars(Encoding.UTF8).AppendStandardOutput(observed).AppendStandardError(error)
                .ExecuteAsync(TestContext.Current.CancellationToken)
            : source.OnStandardOutput(content => observed.Append(Encoding.UTF8.GetString(content.Span)))
                .ExecuteAsync(TestContext.Current.CancellationToken);
        if (exitCode is { } known)
        {
            Assert.Equal(known, await completion);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => completion);
        }

        Assert.Equal("drain this", observed.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal(source.Steps.Count + 1, source.MoveNextCount);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionAwaitsDisposalEvenAfterAnExitEvent(bool capture)
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("x"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ], cleanup: cleanup.Task);
        Assert.Equal(0, source.DisposeCount);
        var output = new StringBuilder();
        var error = new StringBuilder();
        Assert.Equal(0, output.Length);
        Assert.Equal(0, error.Length);
        var completion = capture
            ? source.DecodeChars(Encoding.UTF8).AppendStandardOutput(output).AppendStandardError(error)
                .ExecuteAsync(TestContext.Current.CancellationToken)
            : source.ExecuteAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(1, source.DisposeCount);
            Assert.False(completion.IsCompleted);
        }
        finally
        {
            cleanup.SetResult();
        }

        Assert.Equal(0, await completion);
        Assert.Equal(capture ? "x" : "", output.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal(source.Steps.Count + 1, source.MoveNextCount);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("chars")]
    [InlineData("lines")]
    [InlineData("started")]
    public async Task ObserverAndCleanupFailuresSurviveComposition(string stage)
    {
        var observerFailure = new InvalidOperationException("Observation failed.");
        var cleanupFailure = new IOException("Disposal failed.");
        var source = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("line\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ], cleanupFailure: cleanupFailure);
        Assert.Equal(0, source.DisposeCount);
        Func<Task> read = stage switch
        {
            "started" => () => source.OnStarted(_ => throw observerFailure).DecodeChars(Encoding.UTF8)
                .FrameLines().ExecuteAsync(TestContext.Current.CancellationToken),
            "bytes" => () => source.OnStandardOutput(_ => throw observerFailure).DecodeChars(Encoding.UTF8)
                .ExecuteAsync(TestContext.Current.CancellationToken),
            "chars" => () => source.DecodeChars(Encoding.UTF8).OnStandardOutput(_ => throw observerFailure)
                .ExecuteAsync(TestContext.Current.CancellationToken),
            "lines" => () => source.DecodeChars(Encoding.UTF8).FrameLines().OnStandardOutput(_ => throw observerFailure)
                .ExecuteAsync(TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };

        var failure = await Assert.ThrowsAsync<AggregateException>(read);

        Assert.Equal([observerFailure, cleanupFailure], failure.Flatten().InnerExceptions);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPreservesObserverAndCleanupFailuresAndPartialDiagnostics(bool capture)
    {
        using var cancellation = new CancellationTokenSource();
        var observerFailure = new IOException("Observation failed while canceled.");
        var cleanupFailure = new InvalidOperationException("Disposal failed.");
        var source = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("x"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ], cleanupFailure: cleanupFailure);
        var retained = new StringBuilder();
        var error = new StringBuilder();
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(0, retained.Length);
        Assert.Equal(0, source.MoveNextCount);
        Task completion = capture
            ? source.DecodeChars(Encoding.UTF8).AppendStandardOutput(retained).AppendStandardError(error)
                .OnStandardOutput(_ =>
                {
                    cancellation.Cancel();
                    throw observerFailure;
                }).ExecuteAsync(cancellation.Token)
            : source.OnStandardOutput(bytes =>
                {
                    retained.Append(Encoding.UTF8.GetString(bytes.Span));
                    cancellation.Cancel();
                    throw observerFailure;
                }).ExecuteAsync(cancellation.Token);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal([observerFailure, cleanupFailure], Assert.IsType<AggregateException>(failure.InnerException).Flatten().InnerExceptions);
        Assert.Equal("x", retained.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal(2, source.MoveNextCount);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n\nA\rB\r\n日本語\0last")]
    public async Task DecoderPreservesSplitCharactersAndExactChars(string text)
    {
        var events = await CollectAsync(FragmentedEvents(text).DecodeChars(Encoding.UTF8));
        var retained = events.OfType<ProcessEvent<ReadOnlyMemory<char>>.StandardOutput>()
            .Select(item => item.Content).ToArray();

        // Inspect retained memory after all later reads and disposal, without line normalization.
        Assert.Equal(text, string.Concat(retained.Select(content => content.ToString())));
        Assert.Equal(new ProcessEvent<ReadOnlyMemory<char>>.Started(1), events[0]);
        Assert.Single(events.OfType<ProcessEvent<ReadOnlyMemory<char>>.StandardOutputCompleted>());
        Assert.Single(events.OfType<ProcessEvent<ReadOnlyMemory<char>>.StandardErrorCompleted>());
        Assert.Equal(new ProcessEvent<ReadOnlyMemory<char>>.Exited(0), events[^1]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DecoderAndCleanupFailuresAreBothPreserved(bool invalidEncoding, bool failedCleanup)
    {
        var cleanupFailure = failedCleanup ? new IOException("Disposal failed.") : null;
        var source = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout(new byte[] { 0xFF }),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ], cleanupFailure: cleanupFailure);
        Assert.Equal(0, source.DisposeCount);
        var decoded = source.DecodeChars(new UTF8Encoding(false, invalidEncoding));
        if (invalidEncoding && failedCleanup)
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => CollectAsync(decoded));
            Assert.IsType<DecoderFallbackException>(failure.InnerExceptions[0]);
            Assert.Same(cleanupFailure, failure.InnerExceptions[1]);
        }
        else if (invalidEncoding)
        {
            await Assert.ThrowsAsync<DecoderFallbackException>(() => CollectAsync(decoded));
        }
        else if (failedCleanup)
        {
            Assert.Same(cleanupFailure, await Assert.ThrowsAsync<IOException>(() => CollectAsync(decoded)));
        }
        else
        {
            var events = await CollectAsync(decoded);
            Assert.Equal("\uFFFD", Assert.Single(events.OfType<ProcessEvent<ReadOnlyMemory<char>>.StandardOutput>()).Content.ToString());
            Assert.Equal(new ProcessEvent<ReadOnlyMemory<char>>.Exited(0), events[^1]);
        }

        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData("chars")]
    [InlineData("lines")]
    [InlineData("capture")]
    [InlineData("observer")]
    [InlineData("execute")]
    public async Task UpstreamAndCleanupFailuresSurviveAdapters(string adapter)
    {
        var readFailure = new IOException("Reading failed.");
        var cleanupFailure = new InvalidOperationException("Disposal failed.");
        var source = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("x"u8),
            Emit<byte>.Fail(readFailure)
        ], cleanupFailure: cleanupFailure);
        Assert.Equal(0, source.DisposeCount);
        var output = new StringBuilder();
        var error = new StringBuilder();
        Assert.Equal(0, output.Length);
        Assert.Equal(0, error.Length);
        Func<Task> read = adapter switch
        {
            "chars" => () => CollectAsync(source.DecodeChars(Encoding.UTF8)),
            "lines" => () => CollectAsync(source.DecodeChars(Encoding.UTF8).FrameLines()),
            "capture" => () => source.DecodeChars(Encoding.UTF8).AppendStandardOutput(output).AppendStandardError(error)
                .ExecuteAsync(TestContext.Current.CancellationToken),
            "observer" => () => source.DecodeChars(Encoding.UTF8).OnStandardOutput(_ => { }).ExecuteAsync(TestContext.Current.CancellationToken),
            "execute" => () => source.ExecuteAsync(TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(adapter))
        };

        var failure = await Assert.ThrowsAsync<AggregateException>(read);

        Assert.Equal([readFailure, cleanupFailure], failure.Flatten().InnerExceptions);
        Assert.Equal(adapter == "capture" ? "x" : "", output.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal(3, source.MoveNextCount);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n\nA\rB\r\n日本語\0last")]
    public async Task LinesDecodeSplitCharactersAndCrLfWithoutDroppingEmptyLines(string text)
    {
        var source = FragmentedEvents(text);
        var pipeline = source.DecodeChars(Encoding.UTF8).FrameLines();
        Assert.Equal(0, source.MoveNextCount);
        Assert.Equal(0, source.DisposeCount);

        var events = await CollectAsync(pipeline);
        using var expected = new StringReader(text);
        var expectedLines = new List<string>();
        while (expected.ReadLine() is { } line)
        {
            expectedLines.Add(line);
        }

        Assert.Equal(expectedLines, events.OfType<ProcessEvent<string>.StandardOutput>().Select(item => item.Content));
        Assert.Equal(new ProcessEvent<string>.Started(1), events[0]);
        Assert.Single(events.OfType<ProcessEvent<string>.StandardOutputCompleted>());
        Assert.Single(events.OfType<ProcessEvent<string>.StandardErrorCompleted>());
        Assert.Equal(new ProcessEvent<string>.Exited(0), events[^1]);
        Assert.Equal(source.Steps.Count + 1, source.MoveNextCount);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData("", new string[] { })]
    [InlineData("\r", new string[] { "" })]
    [InlineData("\n", new string[] { "" })]
    [InlineData("\r\n", new string[] { "" })]
    [InlineData("\n\n", new string[] { "", "" })]
    [InlineData("last", new string[] { "last" })]
    [InlineData("A\rB\nC\r\nlast", new string[] { "A", "B", "C", "last" })]
    public async Task FramingHandlesEachDelimiterAndFinalEofOnBothStreams(string chars, string[] expectedLines)
    {
        List<StreamStep<char>> steps = [Emit<char>.Started(1), Emit<char>.Stdout(""), Emit<char>.Stderr("")];
        foreach (var value in chars)
        {
            steps.Add(Emit<char>.Stdout([value]));
            steps.Add(Emit<char>.Stderr([value]));
        }

        steps.Add(Emit<char>.StdoutCompleted());
        steps.Add(Emit<char>.StderrCompleted());
        steps.Add(Emit<char>.Exited(0));
        var source = new FakeEventStream<char>(steps);

        var events = await CollectAsync(source.FrameLines());

        Assert.Equal(expectedLines, events.OfType<ProcessEvent<string>.StandardOutput>().Select(item => item.Content));
        Assert.Equal(expectedLines, events.OfType<ProcessEvent<string>.StandardError>().Select(item => item.Content));
        Assert.Equal(new ProcessEvent<string>.Started(1), events[0]);
        Assert.Single(events.OfType<ProcessEvent<string>.StandardOutputCompleted>());
        Assert.Single(events.OfType<ProcessEvent<string>.StandardErrorCompleted>());
        Assert.Equal(new ProcessEvent<string>.Exited(0), events[^1]);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task FramingKeepsStreamsIndependentAndFlushesOnlyAtTheirOwnEof()
    {
        var source = new FakeEventStream<char>(
        [
            Emit<char>.Started(9),
            Emit<char>.Stdout("out\r"),
            Emit<char>.Stderr("\nerr"),
            Emit<char>.Stdout("\nlast"),
            Emit<char>.StdoutCompleted(),
            Emit<char>.Stderr("\r\n"),
            Emit<char>.Stderr("tail"),
            Emit<char>.StderrCompleted(),
            Emit<char>.Exited(17)
        ]);

        var events = await CollectAsync(source.FrameLines());

        Assert.Collection(events,
            item => Assert.Equal(new ProcessEvent<string>.Started(9), item),
            item => Assert.Equal(new ProcessEvent<string>.StandardOutput("out"), item),
            item => Assert.Equal(new ProcessEvent<string>.StandardError(""), item),
            item => Assert.Equal(new ProcessEvent<string>.StandardOutput("last"), item),
            item => Assert.IsType<ProcessEvent<string>.StandardOutputCompleted>(item),
            item => Assert.Equal(new ProcessEvent<string>.StandardError("err"), item),
            item => Assert.Equal(new ProcessEvent<string>.StandardError("tail"), item),
            item => Assert.IsType<ProcessEvent<string>.StandardErrorCompleted>(item),
            item => Assert.Equal(new ProcessEvent<string>.Exited(17), item));
        Assert.Equal(source.Steps.Count + 1, source.MoveNextCount);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task FramingIsDeferredAndDoesNotReadAheadWhileYieldingLines()
    {
        var unreadFailure = new IOException("The next upstream read must remain deferred.");
        var source = new FakeEventStream<char>(
        [
            Emit<char>.Started(1),
            Emit<char>.Stdout("one\ntwo\n"),
            Emit<char>.Fail(unreadFailure)
        ]);
        var pipeline = source.FrameLines();
        Assert.Equal(0, source.MoveNextCount);
        Assert.Equal(0, source.DisposeCount);

        await using (var enumerator = pipeline.GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.Equal(0, source.MoveNextCount);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new ProcessEvent<string>.Started(1), enumerator.Current);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new ProcessEvent<string>.StandardOutput("one"), enumerator.Current);
            Assert.Equal(2, source.MoveNextCount);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new ProcessEvent<string>.StandardOutput("two"), enumerator.Current);
            Assert.Equal(2, source.MoveNextCount);
            Assert.Equal(0, source.DisposeCount);
        }

        Assert.Equal(2, source.MoveNextCount);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task FramingPropagatesCancellationAndCleanupCauseWhileWaiting()
    {
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new IOException("Disposal failed.");
        var source = new FakeEventStream<char>(
        [
            Emit<char>.Started(1),
            Emit<char>.Stdout("partial"),
            Emit<char>.Wait(gate.Task),
            Emit<char>.StdoutCompleted(),
            Emit<char>.StderrCompleted(),
            Emit<char>.Exited(0)
        ], cleanupFailure: cleanupFailure);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(0, source.MoveNextCount);
        var completion = source.FrameLines().ExecuteAsync(cancellation.Token);
        try
        {
            Assert.False(completion.IsCompleted);
            Assert.Equal(3, source.MoveNextCount);
            Assert.Equal(0, source.DisposeCount);
        }
        finally
        {
            cancellation.Cancel();
        }
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        var causes = Assert.IsType<AggregateException>(failure.InnerException).Flatten().InnerExceptions;
        Assert.Collection(causes,
            cause => Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(cause).CancellationToken),
            cause => Assert.Same(cleanupFailure, cause));
        Assert.Equal(3, source.MoveNextCount);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n\nA\rB\r\n日本語😀\0last")]
    public async Task AppendObserversCaptureExactCharsOnBothStreams(string chars)
    {
        List<StreamStep<char>> steps = [Emit<char>.Started(1), Emit<char>.Stdout(""), Emit<char>.Stderr("")];
        foreach (var value in chars)
        {
            steps.Add(Emit<char>.Stdout([value]));
            steps.Add(Emit<char>.Stderr([value]));
        }

        steps.Add(Emit<char>.StdoutCompleted());
        steps.Add(Emit<char>.StderrCompleted());
        steps.Add(Emit<char>.Exited(17));
        var source = new FakeEventStream<char>(steps);
        var output = new StringBuilder("output:");
        var error = new StringBuilder("error:");
        var pipeline = source.AppendStandardOutput(output).AppendStandardError(error);
        Assert.Equal("output:", output.ToString());
        Assert.Equal("error:", error.ToString());
        Assert.Equal(0, source.MoveNextCount);

        Assert.Equal(17, await pipeline.ExecuteAsync(TestContext.Current.CancellationToken));

        Assert.Equal("output:" + chars, output.ToString());
        Assert.Equal("error:" + chars, error.ToString());
        Assert.Equal(source.Steps.Count + 1, source.MoveNextCount);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task AppendObserversAreLazyAndPreserveEventIdentityAndObserverOrder()
    {
        var source = new FakeEventStream<char>(
        [
            Emit<char>.Started(1),
            Emit<char>.Stdout("first"),
            Emit<char>.Stderr("warning"),
            Emit<char>.Stdout(""),
            Emit<char>.Stdout("last"),
            Emit<char>.StdoutCompleted(),
            Emit<char>.StderrCompleted(),
            Emit<char>.Exited(0)
        ]);
        var output = new StringBuilder();
        var error = new StringBuilder();
        var observed = new List<string>();
        var pipeline = source
            .OnStandardOutput(_ => observed.Add("before:" + output))
            .AppendStandardOutput(output)
            .OnStandardOutput(_ => observed.Add("after:" + output))
            .AppendStandardError(error)
            .OnStandardError(_ => observed.Add("error:" + error));
        Assert.Empty(observed);
        Assert.Equal(0, output.Length);
        Assert.Equal(0, error.Length);
        Assert.Equal(0, source.MoveNextCount);

        var events = await CollectAsync(pipeline);

        Assert.Equal(source.Steps.Count, events.Count);
        for (var index = 0; index < events.Count; index++)
        {
            Assert.Same(Assert.IsType<EventStep<char>>(source.Steps[index]).Event, events[index]);
        }

        Assert.Equal(
            ["before:", "after:first", "error:warning", "before:first", "after:first", "before:first", "after:firstlast"],
            observed);
        Assert.Equal("firstlast", output.ToString());
        Assert.Equal("warning", error.ToString());
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task AppendObserversUseTheCallerOwnedDestinationDuringConsumption()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeEventStream<char>(
        [
            Emit<char>.Started(1),
            Emit<char>.Stdout("before"),
            Emit<char>.Wait(gate.Task),
            Emit<char>.Stdout("after"),
            Emit<char>.StdoutCompleted(),
            Emit<char>.StderrCompleted(),
            Emit<char>.Exited(0)
        ]);
        var output = new StringBuilder();
        Assert.Equal(0, output.Length);
        Assert.Equal(0, source.MoveNextCount);
        var completion = source.AppendStandardOutput(output).ExecuteAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(completion.IsCompleted);
            Assert.Equal("before", output.ToString());
            Assert.Equal(3, source.MoveNextCount);
            Assert.Equal(0, source.DisposeCount);
            output.Clear().Append("changed:");
        }
        finally
        {
            gate.SetResult();
        }

        Assert.Equal(0, await completion);
        Assert.Equal("changed:after", output.ToString());
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendFailurePreservesTheCleanupCauseAndPriorCapture(bool standardError)
    {
        var cleanupFailure = new IOException("Disposal failed.");
        var source = new FakeEventStream<char>(
        [
            Emit<char>.Started(1),
            standardError ? Emit<char>.Stderr("x") : Emit<char>.Stdout("x"),
            standardError ? Emit<char>.Stderr("overflow") : Emit<char>.Stdout("overflow"),
            Emit<char>.StdoutCompleted(),
            Emit<char>.StderrCompleted(),
            Emit<char>.Exited(0)
        ], cleanupFailure: cleanupFailure);
        var destination = new StringBuilder(1, 1);
        Assert.True("overflow".Length > destination.MaxCapacity);
        Assert.Equal(0, destination.Length);
        Assert.Equal(0, source.MoveNextCount);
        var pipeline = standardError
            ? source.AppendStandardError(destination)
            : source.AppendStandardOutput(destination);

        var failure = await Assert.ThrowsAsync<AggregateException>(() => pipeline.ExecuteAsync(TestContext.Current.CancellationToken));

        Assert.Collection(failure.Flatten().InnerExceptions,
            cause => Assert.IsType<ArgumentOutOfRangeException>(cause),
            cause => Assert.Same(cleanupFailure, cause));
        Assert.Equal("x", destination.ToString());
        Assert.Equal(3, source.MoveNextCount);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task OneChainCapturesExactCharsBeforeFramingAndDrainsThroughExit()
    {
        var source = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("a\r"u8),
            Emit<byte>.Stderr("warn\n"u8),
            Emit<byte>.Stdout("\nb\n尾"u8),
            Emit<byte>.Stderr("last"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(17)
        ]);
        var execution = new ExecutionEventStream(source);
        var output = new StringBuilder();
        var error = new StringBuilder();
        var lines = new List<string>();
        var pipeline = execution.DecodeChars(Encoding.UTF8)
            .AppendStandardOutput(output)
            .AppendStandardError(error)
            .FrameLines()
            .OnStandardOutput(line => lines.Add("output:" + line))
            .OnStandardError(line => lines.Add("error:" + line));
        Assert.Equal(0, source.MoveNextCount);
        Assert.Equal(0, output.Length);
        Assert.Equal(0, error.Length);
        Assert.Empty(lines);

        Assert.Equal(17, await pipeline.ExecuteAsync(TestContext.Current.CancellationToken));

        Assert.Equal("a\r\nb\n尾", output.ToString());
        Assert.Equal("warn\nlast", error.ToString());
        Assert.Equal(["output:a", "error:warn", "output:b", "output:尾", "error:last"], lines);
        Assert.Equal(source.Steps.Count + 1, source.MoveNextCount);
        Assert.Equal(1, source.DisposeCount);
        Assert.Throws<InvalidOperationException>(() => execution.GetAsyncEnumerator());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendCapturePreservesDecoderFlushFailureAndCleanupCause(bool failedCleanup)
    {
        var cleanupFailure = failedCleanup ? new IOException("Disposal failed.") : null;
        var source = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("prefix"u8),
            Emit<byte>.Stdout(new byte[] { 0xC3 }),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ], cleanupFailure: cleanupFailure);
        var output = new StringBuilder();
        var error = new StringBuilder();
        Assert.Equal(0, output.Length);
        Assert.Equal(0, source.MoveNextCount);
        var completion = source.DecodeChars(new UTF8Encoding(false, true))
            .AppendStandardOutput(output).AppendStandardError(error)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        if (failedCleanup)
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => completion);
            Assert.Collection(failure.Flatten().InnerExceptions,
                cause => Assert.IsType<DecoderFallbackException>(cause),
                cause => Assert.Same(cleanupFailure, cause));
        }
        else
        {
            await Assert.ThrowsAsync<DecoderFallbackException>(() => completion);
        }

        Assert.Equal("prefix", output.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal(4, source.MoveNextCount);
        Assert.Equal(1, source.DisposeCount);
    }

    private static FakeEventStream<byte> FragmentedEvents(string text)
    {
        List<StreamStep<byte>> steps = [Emit<byte>.Started(1)];
        foreach (var value in Encoding.UTF8.GetBytes(text))
        {
            steps.Add(Emit<byte>.Stdout([value]));
        }

        steps.Add(Emit<byte>.StdoutCompleted());
        steps.Add(Emit<byte>.StderrCompleted());
        steps.Add(Emit<byte>.Exited(0));
        return new(steps);
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source.WithCancellation(TestContext.Current.CancellationToken))
        {
            result.Add(item);
        }

        return result;
    }
}
