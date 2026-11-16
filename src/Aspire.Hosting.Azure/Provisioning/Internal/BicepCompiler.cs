// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

/// <summary>
/// Default implementation of <see cref="IBicepCompiler"/>.
/// </summary>
internal sealed class BicepCliCompiler(ILogger<BicepCliCompiler> logger, IProcessRunner processRunner) : IBicepCompiler
{
    public async Task<string> CompileBicepToArmAsync(string bicepFilePath, CancellationToken cancellationToken = default)
    {
        try
        {
            return await CompileAsync("bicep", ["build", bicepFilePath, "--stdout"], bicepFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception) when (exception.FileName == "bicep")
        {
            try
            {
                return await CompileAsync("az", ["bicep", "build", "--file", bicepFilePath, "--stdout"], bicepFilePath, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException azException) when (azException.FileName == "az")
            {
                throw new AzureCliNotOnPathException();
            }
        }
    }

    private async Task<string> CompileAsync(string command, string[] arguments, string bicepFilePath, CancellationToken cancellationToken)
    {
        logger.LogDebug("Running {CommandPath} with arguments: {Arguments}", command, arguments);
        var output = new StringBuilder();
        var error = new StringBuilder();
        var diagnostics = new ProcessOutputCapture(maxRetainedLineCount: 256);
        try
        {
            // Capture decoded characters before framing removes line terminators. Observing
            // framed lines in the same pass keeps diagnostics live even if execution fails.
            var exitCode = await processRunner.CreateExecution(new ProcessCommand(command)
            {
                Arguments = arguments,
                ResolveExecutablePath = true
            })
                .DecodeChars(Encoding.UTF8)
                .AppendStandardOutput(output)
                .AppendStandardError(error)
                .FrameLines()
                .OnStandardOutput(line =>
                {
                    if (line.Length > 0)
                    {
                        logger.LogDebug("{CommandPath} (stdout): {Output}", command, line);
                        diagnostics.Add(line);
                    }
                })
                .OnStandardError(line =>
                {
                    if (line.Length > 0)
                    {
                        logger.LogDebug("{CommandPath} (stderr): {Error}", command, line);
                        diagnostics.Add(line);
                    }
                })
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);

            // ExecuteAsync has drained and disposed the stream. A known nonzero exit is
            // compilation policy, not a reason to interrupt process cleanup or try another tool.
            if (exitCode != 0)
            {
                logger.LogError("Bicep compilation for {BicepFilePath} failed with exit code {ExitCode}.", bicepFilePath, exitCode);
                var message = $"Command '{command}' with arguments [{string.Join(", ", arguments)}] failed with exit code {exitCode}.";
                var tail = diagnostics.GetFormattedOutput();
                throw new InvalidOperationException(string.IsNullOrEmpty(tail)
                    ? message
                    : $"{message}{Environment.NewLine}{tail}");
            }
        }
        catch (InvalidOperationException exception)
        {
            // Only compilation/operation failures receive caller context. Preserve native
            // failures and cancellation unchanged so PATH fallback and cancellation causes survive.
            var errorMessage = error.ToString().Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(errorMessage)
                ? $"Failed to compile bicep file: {bicepFilePath}"
                : $"Failed to compile bicep file: {bicepFilePath}{Environment.NewLine}{errorMessage}", exception);
        }

        logger.LogDebug("Bicep compilation for {BicepFilePath} succeeded.", bicepFilePath);
        return output.ToString();
    }
}
