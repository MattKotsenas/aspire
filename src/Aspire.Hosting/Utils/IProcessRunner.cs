// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Utils;

/// <summary>
/// Owns external process execution and cleanup for each invocation.
/// </summary>
internal partial interface IProcessRunner
{
    /// <summary>
    /// Creates a deferred, single-use process invocation.
    /// </summary>
    /// <param name="command">The launch configuration, input, and termination policy for the invocation.</param>
    /// <returns>A cold event stream that permits one enumerator acquisition, even if never advanced.</returns>
    /// <remarks>
    /// <para>
    /// Creating the stream or acquiring its enumerator does not launch a process. The first advance
    /// starts execution unless cancellation has already been requested. The enumerator's cancellation token
    /// cancels execution; disposing an active enumerator aborts execution and awaits cleanup.
    /// </para>
    /// <para>
    /// On normal completion, one Started event precedes zero or more output payloads.
    /// StandardOutputCompleted and StandardErrorCompleted each report EOF after the last payload on their stream.
    /// Exited follows both EOF events and owned resource disposal, including for nonzero exit codes.
    /// Launch, output-read, cancellation, or cleanup failures throw exceptions. A failed launch need not emit Started.
    /// An invocation can fail even with a zero exit code if input was not delivered.
    /// Input-delivery I/O failures are reported for a zero exit code; nonzero exits retain their exit code and output.
    /// Output payloads have owned backing arrays that are not reused and remain valid after enumeration or disposal.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> CreateExecution(ProcessCommand command);
}
