// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;

namespace Aspire.Hosting.Utils;

// Legacy members retained only until their owned callers migrate to event execution.
internal partial interface IProcessRunner
{
    /// <summary>
    /// Starts a process with the legacy callback and input configuration.
    /// </summary>
    /// <remarks>
    /// Migrate to <see cref="CreateExecution"/> and consume its events directly, or use
    /// <see cref="ProcessEventExtensions.DecodeChars"/> for character output and
    /// <see cref="ProcessEventExtensions.ExecuteAsync{T}"/> to await the exit code and cleanup.
    /// </remarks>
    /// <param name="processSpec">The process configuration, callbacks, and exit-code policy.</param>
    /// <returns>The process result task and a disposable handle that requests termination of a running process.</returns>
    (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec);

    /// <summary>
    /// Captures UTF-8 output and error text while sending exact input bytes followed by EOF.
    /// </summary>
    /// <remarks>
    /// Migrate to <see cref="CreateExecution"/>, followed by <see cref="ProcessEventExtensions.DecodeChars"/>
    /// with UTF-8, <see cref="ProcessEventExtensions.AppendStandardOutput"/> and
    /// <see cref="ProcessEventExtensions.AppendStandardError"/> for caller-owned accumulation, then
    /// <see cref="ProcessEventExtensions.ExecuteAsync{T}"/> to await completion and cleanup.
    /// </remarks>
    /// <param name="executablePath">The executable path or command name to launch.</param>
    /// <param name="arguments">The arguments passed separately to the process.</param>
    /// <param name="workingDirectory">The child process working directory.</param>
    /// <param name="environmentVariables">Variables overlaid on the inherited environment, or <c>null</c> for no overrides.</param>
    /// <param name="standardInput">The input bytes, which must remain unchanged until the operation completes. Defaults to empty input.</param>
    /// <param name="cancellationToken">The token used to cancel execution.</param>
    /// <returns>A task containing the exit code, including nonzero codes, and the complete captured output and error text.</returns>
    Task<ProcessCaptureResult> RunAndCaptureTextAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        ReadOnlyMemory<byte> standardInput = default,
        CancellationToken cancellationToken = default);
}

// Legacy implementations retired with the old callers.
internal sealed partial class DefaultProcessRunner : IProcessRunner
{
    /// <inheritdoc />
    public (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec)
        => ProcessUtil.Run(processSpec);

    /// <inheritdoc />
    public Task<ProcessCaptureResult> RunAndCaptureTextAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        ReadOnlyMemory<byte> standardInput = default,
        CancellationToken cancellationToken = default)
        => ProcessUtil.RunAndCaptureTextAsync(executablePath, arguments, workingDirectory, environmentVariables, standardInput, cancellationToken);
}
