// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Legacy members retained only until their owned callers migrate to event execution.
/// </summary>
internal partial interface IProcessRunner
{
    /// <summary>
    /// Starts a process with the legacy callback and input configuration.
    /// </summary>
    /// <param name="processSpec">The process configuration, callbacks, and exit-code policy.</param>
    /// <returns>The process result task and a disposable handle that requests termination of a running process.</returns>
    (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec);
}

/// <summary>
/// Legacy implementations retired with the old callers.
/// </summary>
internal sealed partial class DefaultProcessRunner : IProcessRunner
{
    /// <inheritdoc />
    public (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec)
        => ProcessUtil.Run(processSpec);
}
