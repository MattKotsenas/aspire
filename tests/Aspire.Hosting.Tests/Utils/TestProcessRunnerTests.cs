// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests.Utils;

[Trait("Partition", "4")]
public class TestProcessRunnerTests
{
    [Fact]
    public async Task ExecutionSelectsScriptOnFirstMoveAndClaimsOnlyOnce()
    {
        var runner = new TestProcessRunner();
        var firstScript = new FakeEventStream<byte>([Emit<byte>.Exited(11)]);
        var secondScript = new FakeEventStream<byte>([Emit<byte>.Exited(22)]);
        runner.Enqueue(firstScript);
        runner.Enqueue(secondScript);
        var abandoned = runner.CreateExecution(new ProcessCommand("abandoned"));
        var abandonedEnumerator = abandoned.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var createdFirst = runner.CreateExecution(new ProcessCommand("created-first"));
        var consumedFirst = runner.CreateExecution(new ProcessCommand("consumed-first"));
        var enumerator = createdFirst.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.Empty(runner.Commands);
        Assert.Equal(0, firstScript.MoveNextCount);
        Assert.Equal(0, secondScript.MoveNextCount);

        await abandonedEnumerator.DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => abandoned.GetAsyncEnumerator());
        Assert.Empty(runner.Commands);
        Assert.Equal(0, firstScript.DisposeCount);

        Assert.Equal(11, await consumedFirst.ExecuteAsync(TestContext.Current.CancellationToken));
        Assert.Equal("consumed-first", Assert.Single(runner.Commands).ExecutablePath);
        Assert.Equal(1, firstScript.DisposeCount);
        Assert.Equal(0, secondScript.MoveNextCount);
        Assert.Equal(0, secondScript.DisposeCount);

        await using (enumerator)
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Same(Assert.IsType<EventStep<byte>>(secondScript.Steps[0]).Event, enumerator.Current);
            Assert.Throws<InvalidOperationException>(() => createdFirst.GetAsyncEnumerator());
            await Assert.ThrowsAsync<InvalidOperationException>(() => createdFirst.DecodeChars(Encoding.UTF8).ExecuteAsync());
            Assert.False(await enumerator.MoveNextAsync());
        }

        Assert.Equal(["consumed-first", "created-first"], runner.Commands.Select(command => command.ExecutablePath));
        Assert.Equal(1, firstScript.DisposeCount);
        Assert.Equal(1, secondScript.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeFirstMoveDoesNotSelectScript(bool cancelBeforeAcquisition)
    {
        var runner = new TestProcessRunner();
        var script = new FakeEventStream<byte>([Emit<byte>.Exited(7)]);
        runner.Enqueue(script);
        using var cancellation = new CancellationTokenSource();
        if (cancelBeforeAcquisition)
        {
            cancellation.Cancel();
        }

        var execution = runner.CreateExecution(new ProcessCommand("canceled"));
        await using (var enumerator = execution.GetAsyncEnumerator(cancellation.Token))
        {
            cancellation.Cancel();
            Assert.Empty(runner.Commands);
            Assert.Equal(0, script.MoveNextCount);
            Assert.Equal(0, script.DisposeCount);

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Throws<InvalidOperationException>(() => execution.GetAsyncEnumerator());
        }

        Assert.Empty(runner.Commands);
        Assert.Equal(0, script.MoveNextCount);
        Assert.Equal(0, script.DisposeCount);
        Assert.Equal(7, await runner.CreateExecution(new ProcessCommand("next")).ExecuteAsync(TestContext.Current.CancellationToken));
        Assert.Equal("next", Assert.Single(runner.Commands).ExecutablePath);
        Assert.Equal(1, script.DisposeCount);
    }

    [Fact]
    public async Task CallerCancellationReachesPendingScriptAndDisposesItOnce()
    {
        var runner = new TestProcessRunner();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Wait(pending.Task)
        ]);
        runner.Enqueue(script);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var execution = runner.CreateExecution(new ProcessCommand("pending"));
        await using (var enumerator = execution.GetAsyncEnumerator(cancellation.Token))
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.IsType<ProcessEvent<ReadOnlyMemory<byte>>.Started>(enumerator.Current);
            var move = enumerator.MoveNextAsync().AsTask();
            Assert.False(move.IsCompleted);
            Assert.Equal(0, script.DisposeCount);

            await cancellation.CancelAsync();

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }

        Assert.False(pending.Task.IsCompleted);
        Assert.Equal("pending", Assert.Single(runner.Commands).ExecutablePath);
        Assert.Equal(1, script.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionPreservesCleanupFailureAndAnyReadFailure(bool failRead)
    {
        var runner = new TestProcessRunner();
        var primary = new InvalidOperationException("read failed");
        var cleanup = new IOException("cleanup failed");
        // Deliberately script an exit before failing disposal. Even this malformed source
        // must not make the terminal report success before disposal completes.
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            failRead ? Emit<byte>.Fail(primary) : Emit<byte>.Exited(0)
        ], cleanupFailure: cleanup);
        runner.Enqueue(script);
        Assert.Empty(runner.Commands);
        Assert.Equal(0, script.DisposeCount);

        var execution = runner.CreateExecution(new ProcessCommand("failure")).DecodeChars(Encoding.UTF8);
        if (failRead)
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => execution.ExecuteAsync(TestContext.Current.CancellationToken));
            Assert.Collection(error.Flatten().InnerExceptions,
                exception => Assert.Same(primary, exception),
                exception => Assert.Same(cleanup, exception));
        }
        else
        {
            var error = await Assert.ThrowsAsync<IOException>(() => execution.ExecuteAsync(TestContext.Current.CancellationToken));
            Assert.Same(cleanup, error);
        }

        Assert.Equal("failure", Assert.Single(runner.Commands).ExecutablePath);
        Assert.Equal(1, script.DisposeCount);
    }
}
