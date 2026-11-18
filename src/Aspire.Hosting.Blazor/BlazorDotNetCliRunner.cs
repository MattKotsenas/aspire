// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Text;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting;

/// <summary>
/// Runs .NET CLI commands for Blazor projects.
/// </summary>
internal static class BlazorDotNetCliRunner
{
    public static string GetExecutablePath() =>
        GetExecutablePath(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"));

    internal static string GetExecutablePath(string? dotnetHostPath) =>
        dotnetHostPath is { Length: > 0 } ? dotnetHostPath : "dotnet";

    public static async Task<BlazorDotNetCliResult> RunAsync(
        IProcessRunner processRunner,
        string projectPath,
        string command,
        IReadOnlyList<string> arguments,
        bool machineReadableOutput,
        CancellationToken cancellationToken)
    {
        return await RunAsync(
            processRunner,
            GetExecutablePath(),
            projectPath,
            command,
            arguments,
            machineReadableOutput,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<BlazorDotNetCliResult> RunAsync(
        IProcessRunner processRunner,
        string executablePath,
        string projectPath,
        string command,
        IReadOnlyList<string> arguments,
        bool machineReadableOutput,
        CancellationToken cancellationToken)
    {
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var processCommand = new ProcessCommand(executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            Arguments = [command, projectPath, .. arguments]
        };
        if (machineReadableOutput)
        {
            // MSBuild queries emit JSON on stdout. Disable unrelated CLI messages that could
            // corrupt the machine-readable output before callers have a chance to parse it.
            processCommand.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            processCommand.EnvironmentVariables["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
        }

        var started = false;
        int exitCode;
        try
        {
            exitCode = await processRunner.CreateExecution(processCommand)
                .OnStarted(_ => started = true)
                .DecodeChars(Encoding.UTF8)
                .FrameLines()
                // Preserve the legacy callbacks' normalization: discard empty records, retain
                // whitespace, and append a native newline even to the final record at EOF.
                .OnStandardOutput(line =>
                {
                    if (line.Length > 0)
                    {
                        standardOutput.AppendLine(line);
                    }
                })
                .OnStandardError(line =>
                {
                    if (line.Length > 0)
                    {
                        standardError.AppendLine(line);
                    }
                })
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!started && ex is InvalidOperationException or Win32Exception)
        {
            return new(executablePath, false, -1, "", "", ex);
        }

        return new(
            executablePath,
            true,
            exitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            null);
    }
}

internal readonly record struct BlazorDotNetCliResult(
    string Command,
    bool Started,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    Exception? StartException);
