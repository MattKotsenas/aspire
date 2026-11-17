// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Go.Tests;

public class GoRunDebuggingTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task GoFlagsProbeDoesNotResolveUnrelatedApplicationValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var runner = new TestProcessRunner();
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("""{"GOFLAGS":""}"""u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]);
        runner.Enqueue(script);
        builder.Services.AddSingleton<IProcessRunner>(runner);
        var go = builder.AddGoApp("api", builder.AppHostDirectory);
        var unrelated = builder.AddParameter("unrelated", () => throw new InvalidOperationException("Unrelated value was resolved."));
        var goEnvironment = builder.AddParameter("go-environment", () => "selected.env");

        await using var app = builder.Build();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, go.Resource);
        context.EnvironmentVariables["UNRELATED"] = unrelated.Resource;
        context.EnvironmentVariables["GOENV"] = goEnvironment.Resource;
        context.EnvironmentVariables["GOTOOLCHAIN"] = "auto";

        await GoRunDebugging.ConfigureEnvironmentAsync(context);

        Assert.Equal("\"-ldflags=-s=false -w=false\" ", context.EnvironmentVariables["GOFLAGS"]);
        var probeEnvironment = Assert.Single(runner.Commands).EnvironmentVariables;
        Assert.Equal(["GOENV", "GOTOOLCHAIN"], probeEnvironment.Keys);
        Assert.Equal("selected.env", probeEnvironment["GOENV"]);
        Assert.Equal("local", probeEnvironment["GOTOOLCHAIN"]);
        Assert.Equal(1, script.DisposeCount);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(null, true)]
    public async Task GoFlagsProbeUsesConfiguredCommandAndPreservesItsFlags(string? resourceFlags, bool mixedQuotes)
    {
        var flags = mixedQuotes ? "-tags=wrapper -ldflags=-X=main.version=a'\"b" : "-tags=wrapper -ldflags=-X=main.version=café";
        var expectedFlags = mixedQuotes ? flags :
            "\"-ldflags=-s=false -w=false\" -tags=wrapper -ldflags=-X=main.version=café \"-ldflags=-s=false -w=false -X=main.version=café\"";
        // Keep literal UTF-8 in the ordinary go env fixture to exercise the probe's selected encoding.
        var probeOutput = mixedQuotes ? JsonSerializer.Serialize(new { GOFLAGS = flags }) : $$"""{"GOFLAGS":"{{flags}}"}""";
        using var builder = TestDistributedApplicationBuilder.Create();
        var command = Path.Combine(builder.AppHostDirectory, "custom go");
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stderr("wrapper diagnostic: reading Go settings\n"u8),
            Emit<byte>.Stdout(Encoding.UTF8.GetBytes(probeOutput)),
            Emit<byte>.Stderr("""{"GOFLAGS":"-ldflags=-s"}"""u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        builder.Services.AddSingleton<IProcessRunner>(runner);
        var go = builder.AddGoApp("api", builder.AppHostDirectory)
            .WithCommand(command);
        if (resourceFlags is not null)
        {
            go.WithEnvironment("GOFLAGS", resourceFlags);
        }

        var environment = await GetEnvironmentAsync(builder, go.Resource);

        var probe = Assert.Single(runner.Commands);
        Assert.Equal(command, probe.ExecutablePath);
        Assert.Equal(["env", "-json", "GOFLAGS"], probe.Arguments);
        Assert.Equal(go.Resource.WorkingDirectory, probe.WorkingDirectory);
        Assert.True(probe.ResolveExecutablePath);
        Assert.True(probe.InheritEnvironmentVariables);
        Assert.Equal("local", probe.EnvironmentVariables["GOTOOLCHAIN"]);
        Assert.Equal(resourceFlags is not null, probe.EnvironmentVariables.TryGetValue("GOFLAGS", out var probedFlags));
        Assert.Equal(resourceFlags, probedFlags);
        var probeEnvironment = Assert.IsType<Dictionary<string, string>>(probe.EnvironmentVariables);
        Assert.Same(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal, probeEnvironment.Comparer);
        Assert.Empty(runner.ProcessSpecs);
        Assert.Equal(expectedFlags, environment["GOFLAGS"]);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public void GoFlagsProbeFailureDoesNotPreventResourceEnvironmentEvaluation(
        bool processStarted, bool hasAmbientFlags, bool emptyResourceFlags)
    {
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment.Remove("GOFLAGS");
        if (hasAmbientFlags)
        {
            options.StartInfo.Environment["GOFLAGS"] = "-tags=ambient";
        }

        RemoteExecutor.Invoke(static async (processStartedValue, hasAmbientFlagsValue, emptyResourceFlagsValue) =>
        {
            using var builder = TestDistributedApplicationBuilder.Create();
            var runner = new TestProcessRunner();
            var started = bool.Parse(processStartedValue);
            var startFailure = new Win32Exception(2, "The configured Go command could not be started.");
            var script = new FakeEventStream<byte>(started ?
            [
                Emit<byte>.Started(1),
                Emit<byte>.Stdout("""{"GOFLAGS":"-ldflags=-s"}"""u8),
                Emit<byte>.StdoutCompleted(),
                Emit<byte>.StderrCompleted(),
                Emit<byte>.Exited(1)
            ] :
            [
                Emit<byte>.Fail(startFailure)
            ]);
            runner.Enqueue(script);

            builder.Services.AddSingleton<IProcessRunner>(runner);
            var sink = new TestSink(writeEnabled: context => context.LogLevel is LogLevel.Warning or LogLevel.Debug);
            var logger = new TestLogger(nameof(GoRunDebuggingTests), sink, enabled: true);
            var go = builder.AddGoApp("api", builder.AppHostDirectory)
                .WithEnvironment("PUBLIC_VALUE", "unchanged");
            var emptyFlags = bool.Parse(emptyResourceFlagsValue);
            if (emptyFlags)
            {
                go.WithEnvironment("GOFLAGS", "");
            }

            await using var app = builder.Build();
            await builder.Eventing.PublishAsync(new BeforeStartEvent(
                app.Services, app.Services.GetRequiredService<DistributedApplicationModel>()));
            var configuration = await ExecutionConfigurationBuilder.Create(go.Resource)
                .WithEnvironmentVariablesConfig()
                .BuildAsync(builder.ExecutionContext, logger);

            var expectedFlags = bool.Parse(hasAmbientFlagsValue) && !emptyFlags
                ? "\"-ldflags=-s=false -w=false\" -tags=ambient"
                : "\"-ldflags=-s=false -w=false\"";
            Assert.Null(configuration.Exception);
            Assert.Equal(expectedFlags, Assert.Single(
                configuration.EnvironmentVariables, variable => variable.Key == "GOFLAGS").Value?.Trim());
            Assert.Equal("unchanged", Assert.Single(
                configuration.EnvironmentVariables, variable => variable.Key == "PUBLIC_VALUE").Value);
            var diagnostic = Assert.Single(sink.Writes);
            if (started)
            {
                Assert.IsType<InvalidOperationException>(diagnostic.Exception);
            }
            else
            {
                Assert.Same(startFailure, diagnostic.Exception);
            }

            Assert.Equal(1, script.DisposeCount);
            Assert.Contains("GOFLAGS", diagnostic.Message, StringComparison.Ordinal);
        }, processStarted.ToString(), hasAmbientFlags.ToString(), emptyResourceFlags.ToString(), options).Dispose();
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("{}")]
    [InlineData("""{"GOFLAGS":null}""")]
    [InlineData("""{"GOFLAGS":42}""")]
    public void InvalidGoFlagsProbeOutputDoesNotPreventEnvironmentEvaluation(string output)
    {
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment.Remove("GOFLAGS");

        RemoteExecutor.Invoke(static async probeOutput =>
        {
            using var builder = TestDistributedApplicationBuilder.Create();
            var runner = new TestProcessRunner();
            var script = new FakeEventStream<byte>(
            [
                Emit<byte>.Started(1),
                Emit<byte>.Stdout(Encoding.UTF8.GetBytes(probeOutput)),
                Emit<byte>.StdoutCompleted(),
                Emit<byte>.StderrCompleted(),
                Emit<byte>.Exited(0)
            ]);
            runner.Enqueue(script);
            builder.Services.AddSingleton<IProcessRunner>(runner);
            var go = builder.AddGoApp("api", builder.AppHostDirectory);

            var environment = await GetEnvironmentAsync(builder, go.Resource);

            Assert.Equal("\"-ldflags=-s=false -w=false\"", environment["GOFLAGS"].Trim());
            Assert.Equal(1, script.DisposeCount);
        }, output, options).Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GoFlagsProbeCancellationIsPropagated(bool processExitsNonzero)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var cancellation = new CancellationTokenSource();
        var producerCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<StreamStep<byte>> steps =
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("""{"GOFLAGS":"-tags=probe"}"""u8)
        ];
        if (processExitsNonzero)
        {
            // A nonzero observation must not apply fallback while producer EOF is still pending.
            steps.Add(Emit<byte>.Exited(1));
        }
        steps.Add(Emit<byte>.Wait(producerCompletion.Task));
        steps.Add(Emit<byte>.StdoutCompleted());
        steps.Add(Emit<byte>.StderrCompleted());
        var script = new FakeEventStream<byte>(steps);
        var runner = new TestProcessRunner();
        runner.Enqueue(script);
        builder.Services.AddSingleton<IProcessRunner>(runner);
        var go = builder.AddGoApp("api", builder.AppHostDirectory);
        var sink = new TestSink();
        await using var app = builder.Build();
        var context = new EnvironmentCallbackContext(
            builder.ExecutionContext, go.Resource, cancellationToken: cancellation.Token)
        {
            Logger = new TestLogger(nameof(GoRunDebuggingTests), sink, enabled: true)
        };

        var pending = GoRunDebugging.ConfigureEnvironmentAsync(context);
        Assert.False(pending.IsCompleted);
        Assert.Equal(processExitsNonzero ? 4 : 3, script.MoveNextCount);
        Assert.Equal(0, script.DisposeCount);
        Assert.Empty(context.EnvironmentVariables);
        Assert.Empty(sink.Writes);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(producerCompletion.Task.IsCompleted);
        Assert.Empty(context.EnvironmentVariables);
        Assert.Empty(sink.Writes);
        Assert.Equal(1, script.DisposeCount);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(1, true)]
    public async Task GoFlagsProbeParsesOnlyAfterKnownZeroExit(int? exitCode, bool invalidOutput)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        List<StreamStep<byte>> steps =
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout(invalidOutput ? "invalid-json"u8 : """{"GOFLAGS":"-tags=probe"}"""u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted()
        ];
        if (exitCode is { } knownExitCode)
        {
            steps.Add(Emit<byte>.Exited(knownExitCode));
        }
        var script = new FakeEventStream<byte>(steps);
        var runner = new TestProcessRunner();
        runner.Enqueue(script);
        builder.Services.AddSingleton<IProcessRunner>(runner);
        var go = builder.AddGoApp("api", builder.AppHostDirectory);
        var sink = new TestSink();
        await using var app = builder.Build();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, go.Resource)
        {
            Logger = new TestLogger(nameof(GoRunDebuggingTests), sink, enabled: true)
        };
        context.EnvironmentVariables["GOFLAGS"] = "";

        await GoRunDebugging.ConfigureEnvironmentAsync(context);

        var flags = Assert.IsType<string>(context.EnvironmentVariables["GOFLAGS"]);
        Assert.Equal("\"-ldflags=-s=false -w=false\"", flags.Trim());
        var diagnostic = Assert.Single(sink.Writes);
        Assert.Equal(LogLevel.Warning, diagnostic.LogLevel);
        Assert.IsType<InvalidOperationException>(diagnostic.Exception);
        Assert.Equal(1, script.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GoFlagsProbeWaitsForDisposalBeforeParsingOrFallback(bool failCleanup)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new IOException("Go settings probe cleanup failed.");
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("""{"GOFLAGS":"-tags=probe"}"""u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ], cleanupFailure: failCleanup ? cleanupFailure : null, cleanup: cleanup.Task);
        var runner = new TestProcessRunner();
        runner.Enqueue(script);
        builder.Services.AddSingleton<IProcessRunner>(runner);
        var go = builder.AddGoApp("api", builder.AppHostDirectory);
        var sink = new TestSink();
        await using var app = builder.Build();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, go.Resource)
        {
            Logger = new TestLogger(nameof(GoRunDebuggingTests), sink, enabled: true)
        };
        context.EnvironmentVariables["GOFLAGS"] = "";

        var pending = GoRunDebugging.ConfigureEnvironmentAsync(context);
        try
        {
            Assert.Equal(1, script.DisposeCount);
            Assert.False(pending.IsCompleted);
            Assert.Equal("", context.EnvironmentVariables["GOFLAGS"]);
            Assert.Empty(sink.Writes);
        }
        finally
        {
            cleanup.SetResult();
        }
        await pending.WaitAsync(TestContext.Current.CancellationToken);

        var flags = Assert.IsType<string>(context.EnvironmentVariables["GOFLAGS"]);
        if (failCleanup)
        {
            Assert.Equal("\"-ldflags=-s=false -w=false\"", flags.Trim());
            var diagnostic = Assert.Single(sink.Writes);
            Assert.Same(cleanupFailure, diagnostic.Exception);
        }
        else
        {
            Assert.Equal("\"-ldflags=-s=false -w=false\" -tags=probe", flags);
            Assert.Empty(sink.Writes);
        }
        Assert.Equal(1, script.DisposeCount);
    }

    [Fact]
    public async Task GoFlagsProbeDoesNotFallbackForCleanupFailureAfterCancellation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var cancellation = new CancellationTokenSource();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("""{"GOFLAGS":"-tags=probe"}"""u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ], cleanupFailure: new IOException("Go settings probe cleanup failed."), cleanup: cleanup.Task);
        var runner = new TestProcessRunner();
        runner.Enqueue(script);
        builder.Services.AddSingleton<IProcessRunner>(runner);
        var go = builder.AddGoApp("api", builder.AppHostDirectory);
        var sink = new TestSink();
        await using var app = builder.Build();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, go.Resource, cancellationToken: cancellation.Token)
        {
            Logger = new TestLogger(nameof(GoRunDebuggingTests), sink, enabled: true)
        };

        var pending = GoRunDebugging.ConfigureEnvironmentAsync(context);
        try
        {
            Assert.Equal(1, script.DisposeCount);
            Assert.False(pending.IsCompleted);
            Assert.Empty(context.EnvironmentVariables);
            Assert.Empty(sink.Writes);
            cancellation.Cancel();
        }
        finally
        {
            cleanup.SetResult();
        }
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(context.EnvironmentVariables);
        Assert.Empty(sink.Writes);
        Assert.Equal(1, script.DisposeCount);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ResourceGoFlagsPreserveConfiguredValuesAndSensitivity(
        bool useEnvironmentCallback, bool secret, bool mixedQuotes)
    {
        var flags = mixedQuotes ? "-ldflags=all=-X=main.version=before -tags=netgo -ldflags=-X=main.version=a'\"b" : "-ldflags=-X=main.version=1.2.3";
        var expectedFlags = mixedQuotes ? flags :
            "\"-ldflags=-s=false -w=false\" -ldflags=-X=main.version=1.2.3 \"-ldflags=-s=false -w=false -X=main.version=1.2.3\"";
        using var builder = TestDistributedApplicationBuilder.Create();
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
        var sink = new TestSink(writeEnabled: context => context.LogLevel == LogLevel.Warning);
        var logger = new TestLogger(nameof(GoRunDebuggingTests), sink, enabled: true);
        var parameter = builder.AddParameter("linker-flags", () => flags, secret: secret);
        var go = builder.AddGoApp("api", builder.AppHostDirectory)
            .WithEnvironment("PUBLIC_FLAGS", "-tags=netgo");
        if (useEnvironmentCallback)
        {
            go.WithEnvironment(context => context.EnvironmentVariables["GOFLAGS"] = parameter);
        }
        else
        {
            go.WithEnvironment("GOFLAGS", parameter);
        }

        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(
            app.Services, app.Services.GetRequiredService<DistributedApplicationModel>()));
        var configuration = await ExecutionConfigurationBuilder.Create(go.Resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(builder.ExecutionContext, logger);

        Assert.Null(configuration.Exception);
        Assert.Equal(expectedFlags, Assert.Single(configuration.EnvironmentVariables, variable => variable.Key == "GOFLAGS").Value);
        if (mixedQuotes)
        {
            Assert.Single(sink.Writes);
        }
        else
        {
            Assert.Empty(sink.Writes);
        }

        await app.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(go.Resource, snapshot => snapshot with
        {
            EnvironmentVariables = [.. configuration.EnvironmentVariables
                .Select(variable => new EnvironmentVariableSnapshot(variable.Key, variable.Value, true))]
        });
        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);
        var snapshot = Assert.Single(await target.GetResourceSnapshotsAsync(), resource => resource.DisplayName == "api");

        Assert.Equal(secret ? null : expectedFlags, Assert.Single(snapshot.EnvironmentVariables, variable => variable.Name == "GOFLAGS").Value);
        Assert.Equal("-tags=netgo", Assert.Single(snapshot.EnvironmentVariables, variable => variable.Name == "PUBLIC_FLAGS").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitLinkerOptionsAndDelveDoNotReceiveRunDefaults(bool delve)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
        var go = builder.AddGoApp("api", builder.AppHostDirectory, ldFlags: delve ? null : "-s -w");
        if (delve)
        {
            go.WithDelveServer();
        }

        var environment = await GetEnvironmentAsync(builder, go.Resource);

        Assert.False(environment.ContainsKey("GOFLAGS"));
        Assert.Empty(runner.ProcessSpecs);
        Assert.Empty(runner.Commands);
    }

    [Theory]
    [InlineData("\"-ldflags=-w")]
    [InlineData("-ldflags=-X=main.version=a'\"b \"-tags=broken")]
    public async Task MalformedGoFlagsAreRejected(string flags)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var go = builder.AddGoApp("api", builder.AppHostDirectory)
            .WithEnvironment("GOFLAGS", flags);

        await Assert.ThrowsAsync<FormatException>(() => GetEnvironmentAsync(builder, go.Resource));
    }

    [Theory]
    [InlineData(false, "", "unset", false, true, true)]
    [InlineData(false, "-ldflags=-X=main.version=1.2.3", "1.2.3", false, true, true)]
    [InlineData(false, "'-ldflags=-X \"main.version=hello world\"'", "hello world", false, true, true)]
    [InlineData(false, "-ldflags=all=-X=main.version=1.2.3", "1.2.3", false, true, true)]
    [InlineData(true, "-ldflags=-X=main.version=1.2.3", "1.2.3", false, true, true)]
    [InlineData(false, "-tags=stamp -ldflags=-X=main.version=1.2.3", "1.2.3", true, true, true)]
    [InlineData(false, "-ldflags=all=-X=main.version=all -ldflags=example.com/debugflags=-X=main.version=specific", "specific", false, true, true)]
    [InlineData(false, "-ldflags=example.com/debugflags=-X=main.version=specific -ldflags=all=-X=main.version=all", "all", false, true, true)]
    [InlineData(false, "-ldflags=-s", "unset", false, false, false)]
    [InlineData(false, "\"-ldflags=-s -w\"", "unset", false, false, false)]
    [InlineData(false, "-ldflags=-w", "unset", false, true, false)]
    [InlineData(false, "-ldflags=-w=false", "unset", false, true, true)]
    [InlineData(false, "-ldflags=-s=false", "unset", false, true, true)]
    [InlineData(false, "-ldflags=--s", "unset", false, false, false)]
    [InlineData(false, "-ldflags=--w", "unset", false, true, false)]
    [InlineData(false, "-ldflags=--s=false", "unset", false, true, true)]
    [InlineData(false, "-ldflags=--w=false", "unset", false, true, true)]
    [InlineData(false, "-ldflags=-s=1", "unset", false, false, false)]
    [InlineData(false, "-ldflags=--s=0", "unset", false, true, true)]
    [InlineData(false, "\"-ldflags=-s -s=false\"", "unset", false, true, true)]
    [InlineData(false, "\"-ldflags=-w -w=false\"", "unset", false, true, true)]
    // null uses native go run because Windows Go releases differ on whether -s -w=false preserves DWARF.
    // https://github.com/golang/go/blob/go1.24.13/src/cmd/link/internal/ld/pe.go
    [InlineData(false, "\"-ldflags=-s -w=false\"", "unset", false, false, null)]
    [InlineData(false, "\"-ldflags=-L -s\"", "unset", false, true, true)]
    [InlineData(false, "'-ldflags=-L \"-w\"'", "unset", false, true, true)]
    [InlineData(false, "-ldflags=-L=-s", "unset", false, true, true)]
    [InlineData(false, "\"-ldflags=-L -s -s\"", "unset", false, false, false)]
    [InlineData(false, "'-ldflags=-buildid \"-s\"'", "unset", false, true, true)]
    [InlineData(true, "-ldflags=all=-s", "unset", false, false, false)]
    [InlineData(false, "\"-ldflags=-s -s=false -w=false\"", "unset", false, true, true)]
    [InlineData(false, "\"-ldflags= all = -X=main.version=space\"", "space", false, true, true)]
    [InlineData(false, "-ldflags=", "unset", false, true, true)]
    [InlineData(false, "-ldflags=-X=main.version=a'\"b", "a'\"b", false, false, false)]
    [InlineData(true, "-ldflags=-X=main.version=a'\"b", "a'\"b", false, false, false)]
    [RequiresTools(["go"])]
    public async Task GoRunPreservesVersionStampAndDebugSymbolsWhenSupported(
        bool persisted, string flags, string expectedVersion, bool expectedTagged, bool expectedSymbols, bool? expectedDwarf)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "go.mod"), "module example.com/debugflags\n\ngo 1.18\n");
        File.WriteAllText(Path.Combine(workspace.Path, "main.go"), """
            package main

            import (
                "debug/dwarf"
                "debug/elf"
                "debug/macho"
                "debug/pe"
                "encoding/json"
                "os"
                "runtime"
            )

            var version = "unset"
            var tagged = false

            type executable interface {
                DWARF() (*dwarf.Data, error)
                Close() error
            }

            func main() {
                path, err := os.Executable()
                if err != nil { panic(err) }
                var image executable
                var hasSymbols bool
                switch runtime.GOOS {
                case "windows":
                    file, err := pe.Open(path)
                    if err != nil { panic(err) }
                    image = file
                    hasSymbols = len(file.Symbols) > 0
                case "darwin":
                    file, err := macho.Open(path)
                    if err != nil { panic(err) }
                    image = file
                    hasSymbols = file.Symtab != nil && len(file.Symtab.Syms) > 0
                default:
                    file, err := elf.Open(path)
                    if err != nil { panic(err) }
                    image = file
                    symbols, symbolErr := file.Symbols()
                    hasSymbols = symbolErr == nil && len(symbols) > 0
                }
                defer image.Close()
                var hasDwarf bool
                if data, err := image.DWARF(); err == nil {
                    reader := data.Reader()
                    for {
                        entry, err := reader.Next()
                        if err != nil { panic(err) }
                        if entry == nil { break }
                        if entry.Tag == dwarf.TagSubprogram && entry.Val(dwarf.AttrName) == "main.main" {
                            hasDwarf = true
                            break
                        }
                    }
                }
                if err := json.NewEncoder(os.Stdout).Encode(map[string]any{
                    "version": version,
                    "dwarf": hasDwarf,
                    "symbols": hasSymbols,
                    "tagged": tagged,
                }); err != nil { panic(err) }
            }
            """);
        File.WriteAllText(Path.Combine(workspace.Path, "tag.go"), """
            //go:build stamp

            package main

            func init() { tagged = true }
            """);

        using var builder = TestDistributedApplicationBuilder.Create();
        Assert.Null(builder.Configuration["DEBUG_SESSION_PORT"]);
        var go = builder.AddGoApp("api", workspace.Path)
            .WithEnvironment("GOFLAGS", persisted ? "" : flags)
            .WithEnvironment("GOENV", "off");
        if (persisted)
        {
            var goEnvironment = Path.Combine(workspace.Path, "go.env");
            File.WriteAllText(goEnvironment, $"GOFLAGS={flags}\n");
            go.WithEnvironment("GOENV", goEnvironment);
        }

        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(
            app.Services, app.Services.GetRequiredService<DistributedApplicationModel>()));
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(go.Resource, serviceProvider: app.Services);
        var arguments = await ArgumentEvaluator.GetArgumentListAsync(go.Resource, app.Services);
        var processRunner = app.Services.GetRequiredService<IProcessRunner>();
        using var document = await RunGoAsync(processRunner, go.Resource, arguments, environment, TestContext.Current.CancellationToken);
        if (expectedDwarf is null)
        {
            var nativeEnvironment = new Dictionary<string, string>(environment, environment.Comparer)
            {
                ["GOFLAGS"] = flags
            };
            using var nativeDocument = await RunGoAsync(processRunner, go.Resource, ["run", "."], nativeEnvironment, TestContext.Current.CancellationToken);
            expectedDwarf = nativeDocument.RootElement.GetProperty("dwarf").GetBoolean();
        }

        Assert.Equal(expectedVersion, document.RootElement.GetProperty("version").GetString());
        Assert.Equal(expectedDwarf.Value, document.RootElement.GetProperty("dwarf").GetBoolean());
        Assert.Equal(expectedSymbols, document.RootElement.GetProperty("symbols").GetBoolean());
        Assert.Equal(expectedTagged, document.RootElement.GetProperty("tagged").GetBoolean());
    }

    private static async Task<JsonDocument> RunGoAsync(
        IProcessRunner processRunner, GoAppResource resource, IReadOnlyList<string> arguments,
        Dictionary<string, string> environment, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var output = new StringBuilder();
        var diagnostics = new ProcessOutputCapture(ProcessSpec.DefaultRetainedOutputLineCount);
        var exitCode = await processRunner.CreateExecution(new ProcessCommand(resource.Command)
        {
            WorkingDirectory = resource.WorkingDirectory,
            Arguments = arguments,
            EnvironmentVariables = environment,
            ResolveExecutablePath = true
        })
            .DecodeChars(Encoding.UTF8)
            .AppendStandardOutput(output)
            .FrameLines()
            .OnStandardOutput(line =>
            {
                if (line.Length > 0)
                {
                    diagnostics.Add(line);
                }
            })
            .OnStandardError(line =>
            {
                if (line.Length > 0)
                {
                    diagnostics.Add(line);
                }
            })
            .ExecuteAsync(cancellation.Token);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Go command '{resource.Command}' exited with code {exitCode}.{Environment.NewLine}{diagnostics.GetFormattedOutput()}");
        }

        return JsonDocument.Parse(output.ToString());
    }

    private static async Task<Dictionary<string, string>> GetEnvironmentAsync(
        IDistributedApplicationBuilder builder, GoAppResource resource)
    {
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(
            app.Services, app.Services.GetRequiredService<DistributedApplicationModel>()));

        return await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource, serviceProvider: app.Services);
    }
}
