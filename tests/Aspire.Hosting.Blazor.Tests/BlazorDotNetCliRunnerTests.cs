// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;

namespace Aspire.Hosting.Blazor.Tests;

public class BlazorDotNetCliRunnerTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 23)]
    public async Task RunAsyncPreservesCommandEnvironmentAndNormalizedOutput(bool machineReadableOutput, int exitCode)
    {
        var runner = new TestProcessRunner();
        var source = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(42),
            Emit<byte>.Stdout("\nBuilding café 日本語 🦆\r\n\n \t\nfinal output"u8),
            Emit<byte>.Stderr("warning: résumé\n\nlast error"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(exitCode)
        ]);
        runner.Enqueue(source);
        var executable = Path.GetFullPath("custom sdk/dotnet");
        var projectPath = Path.GetFullPath("client apps/Client.csproj");
        Assert.Empty(runner.Commands);

        var result = await BlazorDotNetCliRunner.RunAsync(
            runner, executable, projectPath, "msbuild", ["-getProperty:TargetFramework", "-nologo"],
            machineReadableOutput, TestContext.Current.CancellationToken);

        var command = Assert.Single(runner.Commands);
        Assert.Equal(executable, command.ExecutablePath);
        Assert.Equal(["msbuild", projectPath, "-getProperty:TargetFramework", "-nologo"], command.Arguments);
        Assert.Equal(Path.GetDirectoryName(projectPath), command.WorkingDirectory);
        Assert.True(command.InheritEnvironmentVariables);
        Assert.Null(command.Input);
        if (machineReadableOutput)
        {
            Assert.Equal(2, command.EnvironmentVariables.Count);
            Assert.Equal("1", command.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"]);
            Assert.Equal("1", command.EnvironmentVariables["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"]);
        }
        else
        {
            Assert.Empty(command.EnvironmentVariables);
        }
        Assert.Equal(new BlazorDotNetCliResult(
            executable, true, exitCode,
            $"Building café 日本語 🦆{Environment.NewLine} \t{Environment.NewLine}final output{Environment.NewLine}",
            $"warning: résumé{Environment.NewLine}last error{Environment.NewLine}",
            null), result);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunAsyncOnlyConvertsFailuresBeforeStarted(bool started, bool win32)
    {
        Exception failure = win32 ? new Win32Exception(2) : new InvalidOperationException("Launch failed.");
        List<StreamStep<byte>> steps = [];
        if (started)
        {
            steps.Add(Emit<byte>.Started(42));
        }
        steps.Add(Emit<byte>.Fail(failure));
        var source = new FakeEventStream<byte>(steps);
        var runner = new TestProcessRunner();
        runner.Enqueue(source);

        var completion = BlazorDotNetCliRunner.RunAsync(
            runner, "custom-dotnet", Path.GetFullPath("Client.csproj"), "build", [],
            machineReadableOutput: false, TestContext.Current.CancellationToken);

        if (started)
        {
            Assert.Same(failure, await Assert.ThrowsAsync(failure.GetType(), () => completion));
        }
        else
        {
            var result = await completion;
            Assert.Equal(new BlazorDotNetCliResult("custom-dotnet", false, -1, "", "", failure), result);
        }
    }
}
