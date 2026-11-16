// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Testing;

internal static class Emit<T>
{
    public static EventStep<T> Started(int processId)
        => new(new ProcessEvent<ReadOnlyMemory<T>>.Started(processId));

    public static EventStep<T> Stdout(ReadOnlySpan<T> content)
        => new(new ProcessEvent<ReadOnlyMemory<T>>.StandardOutput(content.ToArray()));

    public static EventStep<T> Stderr(ReadOnlySpan<T> content)
        => new(new ProcessEvent<ReadOnlyMemory<T>>.StandardError(content.ToArray()));

    public static EventStep<T> StdoutCompleted()
        => new(new ProcessEvent<ReadOnlyMemory<T>>.StandardOutputCompleted());

    public static EventStep<T> StderrCompleted()
        => new(new ProcessEvent<ReadOnlyMemory<T>>.StandardErrorCompleted());

    public static EventStep<T> Exited(int exitCode)
        => new(new ProcessEvent<ReadOnlyMemory<T>>.Exited(exitCode));

    public static FailureStep<T> Fail(Exception error)
        => new(error);

    public static WaitStep<T> Wait(Task completion)
        => new(completion);
}

internal abstract record StreamStep<T>;

internal sealed record EventStep<T>(ProcessEvent<ReadOnlyMemory<T>> Event) : StreamStep<T>;

internal sealed record FailureStep<T>(Exception Error) : StreamStep<T>;

internal sealed record WaitStep<T>(Task Completion) : StreamStep<T>;

internal sealed class FakeEventStream<T>(IReadOnlyList<StreamStep<T>> steps, Exception? cleanupFailure = null, Task? cleanup = null)
    : IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<T>>>
{
    public IReadOnlyList<StreamStep<T>> Steps => steps;

    public int MoveNextCount { get; private set; }

    public int DisposeCount { get; private set; }

    public IAsyncEnumerator<ProcessEvent<ReadOnlyMemory<T>>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new Enumerator(this, steps, cleanupFailure, cleanup, cancellationToken);

    private sealed class Enumerator(FakeEventStream<T> owner, IReadOnlyList<StreamStep<T>> steps,
        Exception? cleanupFailure, Task? cleanup, CancellationToken cancellationToken) : IAsyncEnumerator<ProcessEvent<ReadOnlyMemory<T>>>
    {
        private int _index = -1;

        public ProcessEvent<ReadOnlyMemory<T>> Current { get; private set; } = null!;

        public async ValueTask<bool> MoveNextAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.MoveNextCount++;
            while (++_index < steps.Count)
            {
                if (steps[_index] is WaitStep<T> wait)
                {
                    await wait.Completion.WaitAsync(cancellationToken);
                    continue;
                }

                Current = steps[_index] switch
                {
                    EventStep<T> emit => emit.Event,
                    FailureStep<T> fail => throw fail.Error,
                    _ => throw new System.Diagnostics.UnreachableException()
                };
                return true;
            }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            owner.DisposeCount++;
            if (cleanup is not null)
            {
                await cleanup;
            }

            if (cleanupFailure is not null)
            {
                throw cleanupFailure;
            }
        }
    }
}
