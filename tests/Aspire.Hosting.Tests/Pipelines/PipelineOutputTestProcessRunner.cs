// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests.Pipelines;

internal sealed class PipelineOutputTestProcessRunner(Func<IReadOnlyList<string>, ProcessCaptureResult> capture) : IProcessRunner
{
    internal List<IReadOnlyList<string>> Calls { get; } = [];
    internal List<byte[]> Inputs { get; } = [];

    public (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec) => throw new NotSupportedException();

    public IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> CreateExecution(ProcessCommand command)
        => throw new NotSupportedException();
}
