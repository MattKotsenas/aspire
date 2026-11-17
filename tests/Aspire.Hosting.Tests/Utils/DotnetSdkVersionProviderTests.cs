// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class DotnetSdkVersionProviderTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("10.0.999", false)]
    [InlineData("11.0.100-preview.7.25380.108", false)]
    [InlineData("11.0.100-beta.1", false)]
    [InlineData("11.0.100-rc.0.1", false)]
    [InlineData("11.0.100-rc.1", true)]
    [InlineData("11.0.100-rc.2.26504.105", true)]
    [InlineData("11.0.100-rc.2.1", true)]
    [InlineData("11.0.100", true)]
    [InlineData("11.0.101-servicing.1", true)]
    [InlineData("11.0.200-preview.1", true)]
    [InlineData("12.0.100-preview.1", true)]
    [InlineData("12.0.100-beta.1", true)]
    [InlineData("12.0.100-rc.1", true)]
    [InlineData("12.0.100", true)]
    [InlineData("13.0.100-alpha.1", true)]
    [InlineData("11.0.100-rc.1+build.42", true)]
    public void SupportsMultiThreadedBuildUsesSemVersionPrecedence(string? version, bool expected)
    {
        var parsedVersion = version is null
            ? null
            : SemVersion.Parse(version, SemVersionStyles.Strict);

        Assert.Equal(expected, DotnetSdkUtils.SupportsMultiThreadedBuild(parsedVersion));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("10.0.999", false)]
    [InlineData("11.0.100-preview.7.25380.108", false)]
    [InlineData("11.0.100-rc.2.26504.105", false)]
    [InlineData("11.0.100-rc.2.1", false)]
    [InlineData("11.0.100-rc.3.99999.1", false)]
    [InlineData("11.0.100-rtm.26473.103", false)]
    [InlineData("11.0.100-rtm.26473.104", true)]
    [InlineData("11.0.100-rtm.26473.105", true)]
    [InlineData("11.0.100-rtm.26473.104+build.42", true)]
    [InlineData("11.0.100", true)]
    [InlineData("11.0.101-servicing.1", true)]
    [InlineData("11.0.200-preview.1", true)]
    [InlineData("12.0.100-preview.1", true)]
    public void SupportsFileBasedMultiThreadedBuildUsesVerifiedSdkFloor(string? version, bool expected)
    {
        var parsedVersion = version is null
            ? null
            : SemVersion.Parse(version, SemVersionStyles.Strict);

        Assert.Equal(expected, DotnetSdkUtils.SupportsFileBasedMultiThreadedBuild(parsedVersion));
    }

    [Fact]
    public async Task TryGetVersionAsyncRunsQuietVersionProbe()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("11.0.100-rc.2.26504.105\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);

        var version = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal("11.0.100-rc.2.26504.105", version?.ToString());
        var command = Assert.Single(processRunner.Commands);
        Assert.Equal("dotnet", command.ExecutablePath);
        Assert.Equal(workspace.Path, command.WorkingDirectory);
        Assert.Equal(["--version"], command.Arguments);
        Assert.True(command.ResolveExecutablePath);
        Assert.True(command.InheritEnvironmentVariables);
        Assert.Equal("false", command.EnvironmentVariables["DOTNET_GENERATE_ASPNET_CERTIFICATE"]);
        Assert.Equal("true", command.EnvironmentVariables["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"]);
        Assert.Equal("true", command.EnvironmentVariables["SuppressNETCoreSdkPreviewMessage"]);
        Assert.Equal("true", command.EnvironmentVariables["DOTNET_NOLOGO"]);
        Assert.Equal("true", command.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"]);
    }

    [Fact]
    public async Task TryGetVersionAsyncUsesSpecifiedExecutable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("11.0.100-rc.1\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);

        var version = await provider.TryGetVersionAsync(
            workspace.Path,
            "custom-dotnet-host",
            TestContext.Current.CancellationToken);

        Assert.Equal("11.0.100-rc.1", version?.ToString());
        Assert.Equal("custom-dotnet-host", Assert.Single(processRunner.Commands).ExecutablePath);
    }

    [Fact]
    public async Task SupportsMultiThreadedBuildAsyncUsesBuildEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("11.0.100-rc.1\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);
        var buildEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = "custom-dotnet-path",
            ["DOTNET_NOLOGO"] = "false",
        };

        var supported = await provider.SupportsMultiThreadedBuildAsync(
            workspace.Path,
            buildEnvironment,
            TestContext.Current.CancellationToken);

        Assert.True(supported);
        var command = Assert.Single(processRunner.Commands);
        Assert.Equal("custom-dotnet-path", command.EnvironmentVariables["PATH"]);
        Assert.Equal("false", command.EnvironmentVariables["DOTNET_NOLOGO"]);
        Assert.Equal("true", command.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"]);
    }

    [Fact]
    public async Task SupportsMultiThreadedBuildAsyncUsesPathDotnetWhenDotnetHostPathConflicts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("10.0.401\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);
        var buildEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = "sdk-10-dotnet-path",
            ["DOTNET_HOST_PATH"] = "sdk-11-dotnet-host",
        };

        var supported = await provider.SupportsMultiThreadedBuildAsync(
            workspace.Path,
            buildEnvironment,
            TestContext.Current.CancellationToken);

        Assert.False(supported);
        var command = Assert.Single(processRunner.Commands);
        Assert.Equal("dotnet", command.ExecutablePath);
        Assert.Equal("sdk-10-dotnet-path", command.EnvironmentVariables["PATH"]);
        Assert.Equal("sdk-11-dotnet-host", command.EnvironmentVariables["DOTNET_HOST_PATH"]);
    }

    [Fact]
    public async Task SupportsFileBasedMultiThreadedBuildAsyncUsesBuildEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("11.0.100-rtm.26473.104\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);
        var buildEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = "custom-dotnet-path",
            ["DOTNET_NOLOGO"] = "false",
        };

        var supported = await provider.SupportsFileBasedMultiThreadedBuildAsync(
            workspace.Path,
            buildEnvironment,
            TestContext.Current.CancellationToken);

        Assert.True(supported);
        var command = Assert.Single(processRunner.Commands);
        Assert.Equal("custom-dotnet-path", command.EnvironmentVariables["PATH"]);
        Assert.Equal("false", command.EnvironmentVariables["DOTNET_NOLOGO"]);
        Assert.Equal("true", command.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"]);
    }

    [Fact]
    public async Task SupportsFileBasedMultiThreadedBuildAsyncUsesPathDotnetWhenDotnetHostPathConflicts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("10.0.401\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);
        var buildEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = "sdk-10-dotnet-path",
            ["DOTNET_HOST_PATH"] = "sdk-11-dotnet-host",
        };

        var supported = await provider.SupportsFileBasedMultiThreadedBuildAsync(
            workspace.Path,
            buildEnvironment,
            TestContext.Current.CancellationToken);

        Assert.False(supported);
        var command = Assert.Single(processRunner.Commands);
        Assert.Equal("dotnet", command.ExecutablePath);
        Assert.Equal("sdk-10-dotnet-path", command.EnvironmentVariables["PATH"]);
        Assert.Equal("sdk-11-dotnet-host", command.EnvironmentVariables["DOTNET_HOST_PATH"]);
    }

    [Fact]
    public async Task SupportsMultiThreadedBuildAsyncCachesByEnvironmentContents()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("11.0.100-rc.1\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("10.0.999\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);
        var firstEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = "first-dotnet-path",
            ["BUILD_FLAVOR"] = "custom",
        };
        var equivalentEnvironment = new Dictionary<string, string>
        {
            ["BUILD_FLAVOR"] = "custom",
            ["PATH"] = "first-dotnet-path",
        };
        var changedEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = "second-dotnet-path",
            ["BUILD_FLAVOR"] = "custom",
        };

        var first = await provider.SupportsMultiThreadedBuildAsync(
            workspace.Path,
            firstEnvironment,
            TestContext.Current.CancellationToken);
        var equivalent = await provider.SupportsMultiThreadedBuildAsync(
            workspace.Path,
            equivalentEnvironment,
            TestContext.Current.CancellationToken);
        var changed = await provider.SupportsMultiThreadedBuildAsync(
            workspace.Path,
            changedEnvironment,
            TestContext.Current.CancellationToken);

        Assert.True(first);
        Assert.True(equivalent);
        Assert.False(changed);
        Assert.Collection(
            processRunner.Commands,
            command => Assert.Equal(
                "first-dotnet-path",
                command.EnvironmentVariables["PATH"]),
            command => Assert.Equal(
                "second-dotnet-path",
                command.EnvironmentVariables["PATH"]));
    }

    [Fact]
    public async Task TryGetVersionAsyncCoalescesConcurrentRequests()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        var processCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Wait(processCompletion.Task),
            Emit<byte>.Stdout("11.0.100-rc.1\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]);
        processRunner.Enqueue(script);
        var provider = CreateProvider(processRunner);

        var first = provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);
        Assert.False(first.IsCompleted);
        Assert.Equal(0, script.DisposeCount);
        var second = provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Single(processRunner.Commands);

        processCompletion.SetResult();
        var versions = await Task.WhenAll(first, second);

        Assert.All(versions, version => Assert.Equal("11.0.100-rc.1", version?.ToString()));
        Assert.Single(processRunner.Commands);
        Assert.Equal(1, script.DisposeCount);
    }

    [Fact]
    public async Task TryGetVersionAsyncSharesNearestGlobalJsonContext()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "global.json"), "{}");
        var firstDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "src", "First")).FullName;
        var secondDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "src", "Second")).FullName;
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("11.0.100\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);

        var first = await provider.TryGetVersionAsync(
            firstDirectory,
            TestContext.Current.CancellationToken);
        var second = await provider.TryGetVersionAsync(
            secondDirectory,
            TestContext.Current.CancellationToken);

        Assert.Equal("11.0.100", first?.ToString());
        Assert.Equal(first, second);
        Assert.Single(processRunner.Commands);
    }

    [Fact]
    public async Task TryGetVersionAsyncSeparatesDifferentGlobalJsonContexts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var firstDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "First")).FullName;
        var secondDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "Second")).FullName;
        File.WriteAllText(Path.Combine(firstDirectory, "global.json"), "{}");
        File.WriteAllText(Path.Combine(secondDirectory, "global.json"), "{}");
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("11.0.100\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("12.0.100-preview.1\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);

        var first = await provider.TryGetVersionAsync(
            firstDirectory,
            TestContext.Current.CancellationToken);
        var second = await provider.TryGetVersionAsync(
            secondDirectory,
            TestContext.Current.CancellationToken);

        Assert.Equal("11.0.100", first?.ToString());
        Assert.Equal("12.0.100-preview.1", second?.ToString());
        Assert.Equal(2, processRunner.Commands.Count);
    }

    [Fact]
    public async Task TryGetVersionAsyncRefreshesWhenGlobalJsonChanges()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var globalJsonPath = Path.Combine(workspace.Path, "global.json");
        File.WriteAllText(globalJsonPath, "{}");
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("11.0.100\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("10.0.100\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);

        var first = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);
        File.WriteAllText(globalJsonPath, """{"sdk":{"version":"10.0.100"}}""");
        var second = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal("11.0.100", first?.ToString());
        Assert.Equal("10.0.100", second?.ToString());
        Assert.Equal(2, processRunner.Commands.Count);
    }

    [Fact]
    public async Task TryGetVersionAsyncRetriesAfterFailedProbe()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(1)
        ]));
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("11.0.100-rc.1\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);

        var failed = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);
        var retried = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Null(failed);
        Assert.Equal("11.0.100-rc.1", retried?.ToString());
        Assert.Equal(2, processRunner.Commands.Count);
    }

    [Fact]
    public async Task TryGetVersionAsyncReturnsNullForMalformedOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("not-a-version\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);

        var version = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Null(version);
    }

    [Fact]
    public async Task CanceledConsumerDoesNotRetainFailedProbe()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        var processCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Wait(processCompletion.Task),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(1)
        ]);
        processRunner.Enqueue(script);
        var provider = CreateProvider(processRunner);
        using var callerCancellationSource = new CancellationTokenSource();

        var canceledCall = provider.TryGetVersionAsync(
            workspace.Path,
            callerCancellationSource.Token);
        Assert.False(canceledCall.IsCompleted);
        Assert.Equal(0, script.DisposeCount);
        var sharedCall = provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);
        callerCancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCall);
        Assert.False(sharedCall.IsCompleted);
        Assert.False(processCompletion.Task.IsCompleted);
        Assert.Equal(0, script.DisposeCount);

        processCompletion.SetResult();
        Assert.Null(await sharedCall);
        Assert.Equal(1, script.DisposeCount);

        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("11.0.100-rc.1\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var retried = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal("11.0.100-rc.1", retried?.ToString());
        Assert.Equal(2, processRunner.Commands.Count);
    }

    [Fact]
    public async Task CanceledConsumerDoesNotCancelSharedProbe()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        var processCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Wait(processCompletion.Task),
            Emit<byte>.Stdout("11.0.100-rc.1\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]);
        processRunner.Enqueue(script);
        var provider = CreateProvider(processRunner);
        using var callerCancellationSource = new CancellationTokenSource();
        Assert.Empty(processRunner.Commands);

        var canceledCall = provider.TryGetVersionAsync(workspace.Path, callerCancellationSource.Token);
        Assert.False(canceledCall.IsCompleted);
        var sharedCall = provider.TryGetVersionAsync(workspace.Path, TestContext.Current.CancellationToken);
        Assert.False(sharedCall.IsCompleted);
        callerCancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCall);
        processCompletion.SetResult();
        var version = await sharedCall;
        var cachedVersion = await provider.TryGetVersionAsync(workspace.Path, TestContext.Current.CancellationToken);

        Assert.Equal("11.0.100-rc.1", version?.ToString());
        Assert.Equal(version, cachedVersion);
        Assert.Single(processRunner.Commands);
        Assert.Equal(1, script.DisposeCount);
    }

    [Fact]
    public async Task ApplicationStoppingCancelsProbeAndDisposesProcess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        var processCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Wait(processCompletion.Task)
        ]);
        processRunner.Enqueue(script);
        using var applicationStoppingSource = new CancellationTokenSource();
        var provider = CreateProvider(processRunner, applicationStoppingSource.Token);

        var versionTask = provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);
        Assert.False(versionTask.IsCompleted);
        Assert.Equal(0, script.DisposeCount);
        applicationStoppingSource.Cancel();

        Assert.Null(await versionTask);
        Assert.False(processCompletion.Task.IsCompleted);
        Assert.Equal(1, script.DisposeCount);
    }

    [Theory]
    [InlineData(" \t11.0.100-rc.1 \t", "11.0.100-rc.1")]
    [InlineData("11.0", null)]
    [InlineData("v11.0.100", null)]
    [InlineData("11.0.100-rc.01", null)]
    [InlineData(" \t", null)]
    public async Task TryGetVersionAsyncParsesStrictSemVersionFromStandardError(string output, string? expectedVersion)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("SDK diagnostics\r\n"u8),
            Emit<byte>.Stderr(Encoding.UTF8.GetBytes(output)),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]);
        processRunner.Enqueue(script);
        var provider = CreateProvider(processRunner);

        var version = await provider.TryGetVersionAsync(workspace.Path, TestContext.Current.CancellationToken);

        Assert.Equal(expectedVersion, version?.ToString());
        Assert.Equal(1, script.DisposeCount);
    }

    [Theory]
    [InlineData(false, 15, "11.0.100-rc.1")]
    [InlineData(true, 15, "11.0.100-rc.1")]
    [InlineData(false, 16, null)]
    [InlineData(true, 16, null)]
    public async Task TryGetVersionAsyncRetainsLastSixteenNonemptyRecordsFromBothStreams(
        bool versionOnStandardError, int trailingRecordCount, string? expectedVersion)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        List<StreamStep<byte>> steps =
        [
            Emit<byte>.Started(1),
            versionOnStandardError
                ? Emit<byte>.Stderr("11.0.100-rc.1\r\n"u8)
                : Emit<byte>.Stdout("11.0.100-rc.1\r\n"u8)
        ];
        // Empty records must leave the tail unchanged; whitespace-only records must
        // consume it, including when they arrive on the other output stream.
        for (var index = 0; index < trailingRecordCount; index++)
        {
            steps.Add(Emit<byte>.Stdout("\n"u8));
            steps.Add(versionOnStandardError
                ? Emit<byte>.Stdout(" \t\r\n"u8)
                : Emit<byte>.Stderr(" \t\r\n"u8));
        }
        steps.Add(Emit<byte>.StdoutCompleted());
        steps.Add(Emit<byte>.StderrCompleted());
        steps.Add(Emit<byte>.Exited(0));
        var script = new FakeEventStream<byte>(steps);
        processRunner.Enqueue(script);
        var provider = CreateProvider(processRunner);

        var version = await provider.TryGetVersionAsync(workspace.Path, TestContext.Current.CancellationToken);

        Assert.Equal(expectedVersion, version?.ToString());
        Assert.Single(processRunner.Commands);
        Assert.Equal(1, script.DisposeCount);
    }

    [Theory]
    [InlineData(false, "11.0.100")]
    [InlineData(true, "12.0.100")]
    public async Task TryGetVersionAsyncParsesRetainedRecordsInOrder(bool standardErrorFirst, string expectedVersion)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        var stdout = Emit<byte>.Stdout("11.0.100\n"u8);
        var stderr = Emit<byte>.Stderr("12.0.100\n"u8);
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            standardErrorFirst ? stderr : stdout,
            standardErrorFirst ? stdout : stderr,
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);

        var version = await provider.TryGetVersionAsync(workspace.Path, TestContext.Current.CancellationToken);

        Assert.Equal(expectedVersion, version?.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryGetVersionAsyncWaitsForDisposalBeforeApplyingProbePolicy(bool failCleanup)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("11.0.100\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ], cleanupFailure: failCleanup ? new IOException("cleanup failed") : null, cleanup: cleanup.Task);
        processRunner.Enqueue(script);
        var provider = CreateProvider(processRunner);

        var first = provider.TryGetVersionAsync(workspace.Path, TestContext.Current.CancellationToken);
        Assert.Equal(1, script.DisposeCount);
        Assert.False(first.IsCompleted);
        var second = provider.TryGetVersionAsync(workspace.Path, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        Assert.Single(processRunner.Commands);

        cleanup.SetResult();
        var versions = await Task.WhenAll(first, second);

        Assert.All(versions, version => Assert.Equal(failCleanup ? null : "11.0.100", version?.ToString()));
        Assert.Equal(1, script.DisposeCount);

        var nextScript = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(2),
            Emit<byte>.Stdout("12.0.100\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]);
        processRunner.Enqueue(nextScript);
        var next = await provider.TryGetVersionAsync(workspace.Path, TestContext.Current.CancellationToken);

        Assert.Equal(failCleanup ? "12.0.100" : "11.0.100", next?.ToString());
        Assert.Equal(failCleanup ? 2 : 1, processRunner.Commands.Count);
        Assert.Equal(failCleanup ? 1 : 0, nextScript.DisposeCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task TryGetVersionAsyncRejectsVersionWithoutSuccessfulExit(int? exitCode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        List<StreamStep<byte>> steps =
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout("11.0.100\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted()
        ];
        if (exitCode is { } knownExitCode)
        {
            steps.Add(Emit<byte>.Exited(knownExitCode));
        }
        var failedProbe = new FakeEventStream<byte>(steps);
        processRunner.Enqueue(failedProbe);
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(2),
            Emit<byte>.Stdout("12.0.100\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var provider = CreateProvider(processRunner);

        var failed = await provider.TryGetVersionAsync(workspace.Path, TestContext.Current.CancellationToken);
        Assert.Null(failed);
        Assert.Equal(1, failedProbe.DisposeCount);

        var retried = await provider.TryGetVersionAsync(workspace.Path, TestContext.Current.CancellationToken);

        Assert.Equal("12.0.100", retried?.ToString());
        Assert.Equal(2, processRunner.Commands.Count);
    }

    private static DotnetSdkVersionProvider CreateProvider(
        IProcessRunner processRunner,
        CancellationToken applicationStopping = default) =>
        new(
            processRunner,
            NullLogger<DotnetSdkVersionProvider>.Instance,
            applicationStopping);
}
