// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestProcessRunner : IProcessRunner
{
    private readonly object _lock = new();
    private readonly Queue<TestProcessRun> _runs = [];
    private readonly Queue<IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>>> _scripts = [];
    private readonly List<ProcessCommand> _commands = [];
    private readonly List<TestProcessDisposable> _disposables = [];

    private readonly List<ProcessSpec> _processSpecs = [];

    public IReadOnlyList<ProcessSpec> ProcessSpecs
    {
        get
        {
            lock (_lock)
            {
                return [.. _processSpecs];
            }
        }
    }

    public Action<ProcessSpec>? RunCallback { get; set; }

    public IReadOnlyList<ProcessCommand> Commands
    {
        get
        {
            lock (_lock)
            {
                return [.. _commands];
            }
        }
    }

    public IReadOnlyList<TestProcessDisposable> Disposables
    {
        get
        {
            lock (_lock)
            {
                return [.. _disposables];
            }
        }
    }

    public TaskCompletionSource RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void EnqueueResult(
        int? exitCode = 0,
        IReadOnlyList<string>? output = null,
        IReadOnlyList<string>? error = null,
        int? totalOutputLineCount = null,
        IReadOnlyList<TestProcessOutput>? outputEvents = null)
    {
        lock (_lock)
        {
            _runs.Enqueue(TestProcessRun.Result(exitCode, output, error, outputEvents) with { TotalOutputLineCount = totalOutputLineCount });
        }
    }

    public void EnqueueException(Exception exception)
    {
        lock (_lock)
        {
            _runs.Enqueue(TestProcessRun.Failed(exception));
        }
    }

    public void EnqueuePending(Task<ProcessResult> processResult)
    {
        lock (_lock)
        {
            _runs.Enqueue(TestProcessRun.Result() with { PendingResult = processResult });
        }
    }

    public (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec)
    {
        TestProcessRun run;
        TestProcessDisposable disposable;
        lock (_lock)
        {
            _processSpecs.Add(processSpec);

            disposable = new TestProcessDisposable();
            _disposables.Add(disposable);

            run = _runs.Count > 0 ? _runs.Dequeue() : TestProcessRun.Result();
        }

        RunCallback?.Invoke(processSpec);
        RunStarted.TrySetResult();

        if (run.FailureException is { } exception)
        {
            throw exception;
        }

        if (run.PendingResult is { } pendingResult)
        {
            return (pendingResult, disposable);
        }

        foreach (var output in run.OutputEvents)
        {
            if (output.IsError)
            {
                processSpec.OnErrorData?.Invoke(output.Value);
            }
            else
            {
                processSpec.OnOutputData?.Invoke(output.Value);
            }
        }

        var processOutput = run.OutputEvents.Select(static output => output.Value).ToArray();
        var totalOutputLineCount = run.TotalOutputLineCount ?? processOutput.Length;
        if (processSpec.RetainedOutputLineCount is { } retainedOutputLineCount)
        {
            processOutput = processOutput.TakeLast(retainedOutputLineCount).ToArray();
        }

        var processResult = new ProcessResult(run.ExitCode ?? throw new InvalidOperationException("Legacy test execution requires a known exit code."), processOutput, totalOutputLineCount);

        return (Task.FromResult(processResult), disposable);
    }

    public void Enqueue(IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> script)
    {
        lock (_lock)
        {
            _scripts.Enqueue(script);
        }
    }

    public IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> CreateExecution(ProcessCommand command)
        => new ExecutionEventStream(new DeferredExecution(this, command));

    // Select only on the first move. Forward reads and disposal separately so the consuming
    // pipeline can retain both failures; an await-foreach iterator's finally would mask a read failure.
    private sealed class DeferredExecution(TestProcessRunner owner, ProcessCommand command)
        : IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>>, IAsyncEnumerator<ProcessEvent<ReadOnlyMemory<byte>>>
    {
        private CancellationToken _cancellationToken;
        private IAsyncEnumerator<ProcessEvent<ReadOnlyMemory<byte>>>? _enumerator;

        public ProcessEvent<ReadOnlyMemory<byte>> Current => _enumerator!.Current;

        // ExecutionEventStream owns the single-use claim, so this is acquired only once.
        public IAsyncEnumerator<ProcessEvent<ReadOnlyMemory<byte>>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            _cancellationToken = cancellationToken;
            return this;
        }

        public ValueTask<bool> MoveNextAsync()
        {
            if (_enumerator is null)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                lock (owner._lock)
                {
                    var script = owner._scripts.Dequeue();
                    owner._commands.Add(command);
                    _enumerator = script.GetAsyncEnumerator(_cancellationToken);
                }
            }

            return _enumerator.MoveNextAsync();
        }

        public ValueTask DisposeAsync() => _enumerator?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed record TestProcessRun(
        int? ExitCode,
        IReadOnlyList<TestProcessOutput> OutputEvents,
        Exception? FailureException)
    {
        public Task<ProcessResult>? PendingResult { get; init; }

        public int? TotalOutputLineCount { get; init; }

        public static TestProcessRun Result(
            int? exitCode = 0,
            IReadOnlyList<string>? output = null,
            IReadOnlyList<string>? error = null,
            IReadOnlyList<TestProcessOutput>? outputEvents = null)
        {
            outputEvents ??=
            [
                .. (output ?? Array.Empty<string>()).Select(static value => new TestProcessOutput(IsError: false, Value: value)),
                .. (error ?? Array.Empty<string>()).Select(static value => new TestProcessOutput(IsError: true, Value: value))
            ];

            return new TestProcessRun(exitCode, outputEvents, null);
        }

        public static TestProcessRun Failed(Exception exception)
            => new(0, [], exception);
    }
}

internal sealed record TestProcessOutput(bool IsError, string Value);

internal sealed class TestProcessDisposable : IAsyncDisposable
{
    public int DisposeCallCount { get; private set; }

    public Func<ValueTask>? OnDisposeAsync { get; set; }

    public ValueTask DisposeAsync()
    {
        DisposeCallCount++;

        return OnDisposeAsync?.Invoke() ?? ValueTask.CompletedTask;
    }
}
