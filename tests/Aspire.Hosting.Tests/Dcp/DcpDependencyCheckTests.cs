// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public sealed class DcpDependencyCheckTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("runtime with \"quotes\"")]
    public async Task RegisteredRunnerSuppliesVersionAndInfo(string? containerRuntime)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(42),
            Emit<byte>.Stdout("{\r\n\"version\":\"99.1.0\"\n}\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        runner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(43),
            Emit<byte>.Stdout(Encoding.UTF8.GetBytes("{\n\"version\":\"99.2.0\",\r\n\"containers\":{\"runtime\":\"podman\",\"installed\":true,\"running\":true,\"hostName\":\"hôte-東京\"}\n}")),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.PostConfigure<DcpOptions>(options => options.ContainerRuntime = containerRuntime);
        Assert.Empty(runner.Commands);

        using var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value;
        Assert.True(File.Exists(options.CliPath));
        var dependencyCheck = app.Services.GetRequiredService<IDcpDependencyCheckService>();

        var version = await dependencyCheck.GetDcpVersionAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(version);
        Assert.Equal(new Version(99, 1, 0), version.Version);
        Assert.Null(version.Containers);
        Assert.Same(version, await dependencyCheck.GetDcpVersionAsync(TestContext.Current.CancellationToken));
        Assert.Single(runner.Commands);

        var info = await dependencyCheck.GetDcpInfoAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.Equal(new Version(99, 2, 0), info.Version);
        Assert.NotNull(info.Containers);
        Assert.Equal("hôte-東京", info.Containers.HostName);
        Assert.True(info.Containers.Installed);
        Assert.True(info.Containers.Running);
        Assert.Same(info, await dependencyCheck.GetDcpVersionAsync(TestContext.Current.CancellationToken));
        Assert.Same(info, await dependencyCheck.GetDcpInfoAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Collection(runner.Commands,
            command => Assert.Equal(["version"], command.Arguments),
            command => Assert.Equal(
                string.IsNullOrEmpty(containerRuntime) ? ["info"] : new[] { "info", "--container-runtime", containerRuntime },
                command.Arguments));
        foreach (var command in runner.Commands)
        {
            Assert.Equal(options.CliPath, command.ExecutablePath);
            Assert.Null(command.WorkingDirectory);
            Assert.True(command.InheritEnvironmentVariables);
            Assert.Empty(command.EnvironmentVariables);
            Assert.False(command.ResolveExecutablePath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingExecutableIsNotWrapped(bool fullInfo)
    {
        using var fileSystem = new TestFileSystemService();
        using var directory = fileSystem.TempDirectory.CreateTempSubdirectory();
        var path = Path.Combine(directory.Path, "missing-dcp");
        var runner = new TestProcessRunner();
        var dependencyCheck = new DcpDependencyCheck(Options.Create(new DcpOptions { CliPath = path }), runner);
        Assert.False(File.Exists(path));

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => fullInfo
            ? dependencyCheck.GetDcpInfoAsync(cancellationToken: TestContext.Current.CancellationToken)
            : dependencyCheck.GetDcpVersionAsync(TestContext.Current.CancellationToken));

        Assert.Equal($"The Aspire orchestration component is not installed at \"{path}\". The application cannot be run without it.", exception.Message);
        Assert.Equal(path, exception.FileName);
        Assert.Empty(runner.Commands);
    }

    [Theory]
    [InlineData(false, "version")]
    [InlineData(true, "info --container-runtime custom runtime")]
    public async Task NonzeroExitPreservesLegacyLineConcatenationAndStreamOrder(bool fullInfo, string command)
    {
        using var fileSystem = new TestFileSystemService();
        using var executable = fileSystem.TempDirectory.CreateTempFile();
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(42),
            Emit<byte>.Stdout("out-1\r\n \r\nout-2\n"u8),
            Emit<byte>.Stderr("err-1\n\n \nerr-2"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(17)
        ]));
        var dependencyCheck = new DcpDependencyCheck(Options.Create(new DcpOptions
        {
            CliPath = executable.Path,
            ContainerRuntime = "custom runtime"
        }), runner);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => fullInfo
            ? dependencyCheck.GetDcpInfoAsync(cancellationToken: TestContext.Current.CancellationToken)
            : dependencyCheck.GetDcpVersionAsync(TestContext.Current.CancellationToken));

        Assert.Equal(string.Format(CultureInfo.InvariantCulture, MessageStrings.DcpDependencyCheckFailedMessage,
            $"'dcp {command}' returned exit code 17. err-1 err-2{Environment.NewLine}out-1 out-2"), exception.Message);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("not JSON")]
    public async Task InvalidJsonRetainsCauseAndDoesNotPreventRetry(string output)
    {
        using var fileSystem = new TestFileSystemService();
        using var executable = fileSystem.TempDirectory.CreateTempFile();
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(42),
            Emit<byte>.Stdout(Encoding.UTF8.GetBytes(output)),
            Emit<byte>.Stderr("warning\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        runner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(43),
            Emit<byte>.Stdout("{\"version\":\"dev\"}"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var dependencyCheck = new DcpDependencyCheck(Options.Create(new DcpOptions { CliPath = executable.Path }), runner);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => dependencyCheck.GetDcpVersionAsync(TestContext.Current.CancellationToken));

        var cause = Assert.IsType<JsonException>(exception.InnerException);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, MessageStrings.DcpDependencyCheckFailedMessage,
            $"{cause.Message} warning{Environment.NewLine}{output}"), exception.Message);
        var info = await dependencyCheck.GetDcpVersionAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.Equal(DcpVersion.Dev, info.Version);
        Assert.Same(info, await dependencyCheck.GetDcpVersionAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, runner.Commands.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailureDoesNotPublishInfoOrReplaceLastSuccess(bool cached)
    {
        using var fileSystem = new TestFileSystemService();
        using var executable = fileSystem.TempDirectory.CreateTempFile();
        var runner = new TestProcessRunner();
        if (cached)
        {
            runner.Enqueue(new FakeEventStream<byte>(
            [
                Emit<byte>.Started(41),
                Emit<byte>.Stdout("{\"version\":\"99.1.0\",\"containers\":{}}"u8),
                Emit<byte>.StdoutCompleted(),
                Emit<byte>.StderrCompleted(),
                Emit<byte>.Exited(0)
            ]));
        }
        var cleanupFailure = new IOException("probe cleanup failed");
        runner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(42),
            Emit<byte>.Stdout("{\"version\":\"99.2.0\",\"containers\":{}}"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ], cleanupFailure: cleanupFailure));
        runner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(43),
            Emit<byte>.Stdout("{\"version\":\"99.3.0\",\"containers\":{}}"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var dependencyCheck = new DcpDependencyCheck(Options.Create(new DcpOptions { CliPath = executable.Path }), runner);
        var previous = cached ? await dependencyCheck.GetDcpInfoAsync(cancellationToken: TestContext.Current.CancellationToken) : null;
        if (cached)
        {
            Assert.NotNull(previous);
            Assert.Equal(new Version(99, 1, 0), previous.Version);
        }

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => dependencyCheck.GetDcpInfoAsync(force: true, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(cleanupFailure, exception.InnerException);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, MessageStrings.DcpDependencyCheckFailedMessage,
            $"probe cleanup failed {Environment.NewLine}{{\"version\":\"99.2.0\",\"containers\":{{}}}}"), exception.Message);
        if (cached)
        {
            Assert.Same(previous, await dependencyCheck.GetDcpVersionAsync(TestContext.Current.CancellationToken));
            Assert.Same(previous, await dependencyCheck.GetDcpInfoAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(2, runner.Commands.Count);
        }

        var refreshed = await dependencyCheck.GetDcpInfoAsync(force: cached, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(refreshed);
        Assert.Equal(new Version(99, 3, 0), refreshed.Version);
        Assert.Same(refreshed, await dependencyCheck.GetDcpVersionAsync(TestContext.Current.CancellationToken));
        Assert.Equal(cached ? 3 : 2, runner.Commands.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(25)]
    public async Task CallerCancellationKeepsOriginalTokenAndAllowsRetry(int timeout)
    {
        using var fileSystem = new TestFileSystemService();
        using var executable = fileSystem.TempDirectory.CreateTempFile();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new TestProcessRunner();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(42),
            Emit<byte>.Wait(pending.Task)
        ]));
        runner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(43),
            Emit<byte>.Stdout("{\"version\":\"dev\"}"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]));
        var dependencyCheck = new DcpDependencyCheck(Options.Create(new DcpOptions
        {
            CliPath = executable.Path,
            DependencyCheckTimeout = timeout
        }), runner);

        var probe = dependencyCheck.GetDcpVersionAsync(caller.Token);
        Assert.Single(runner.Commands);
        Assert.False(probe.IsCompleted);
        caller.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => probe).DefaultTimeout();
        Assert.Equal(caller.Token, exception.CancellationToken);
        var info = await dependencyCheck.GetDcpVersionAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.Equal(DcpVersion.Dev, info.Version);
        Assert.Equal(2, runner.Commands.Count);
    }

    [Fact]
    public async Task ProbeDeadlineKeepsTimeoutDiagnostic()
    {
        using var fileSystem = new TestFileSystemService();
        using var executable = fileSystem.TempDirectory.CreateTempFile();
        var runner = new TestProcessRunner();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Started(42),
            Emit<byte>.Stderr("waiting\n"u8),
            Emit<byte>.Wait(pending.Task)
        ]));
        var dependencyCheck = new DcpDependencyCheck(Options.Create(new DcpOptions
        {
            CliPath = executable.Path,
            DependencyCheckTimeout = 1
        }), runner);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => dependencyCheck.GetDcpInfoAsync(cancellationToken: TestContext.Current.CancellationToken)).DefaultTimeout();

        var cause = Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.IsType<OperationCanceledException>(cause.InnerException);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, MessageStrings.DcpDependencyCheckFailedMessage,
            $"{new TimeoutException().Message} waiting{Environment.NewLine}"), exception.Message);
    }

    [Fact]
    public void CheckDcpInfoAndLogErrors_UnhealthyRuntimeSurfacesErrorAtWarningAndInException()
    {
        const string error = "docker CLI version 23.0.6 is not supported; DCP requires Docker CLI version 25.0.0 or newer";
        var logger = new FakeLogger();
        var options = new DcpOptions { ContainerRuntime = "docker" };
        var dcpInfo = new DcpInfo
        {
            Containers = new DcpContainersInfo
            {
                Installed = true,
                Running = false,
                Error = error
            }
        };
        var (message, linkUrl) = DcpDependencyCheck.BuildContainerRuntimeUnhealthyMessage("docker");

        var exception = Assert.Throws<DistributedApplicationException>(
            () => DcpDependencyCheck.CheckDcpInfoAndLogErrors(logger, options, dcpInfo, throwIfUnhealthy: true));

        Assert.Equal(
            $"{message}{Environment.NewLine}The error from the container runtime check was: {error}",
            exception.Message);
        var warning = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(
            $"{message} For more information, visit: {linkUrl}{Environment.NewLine}The error from the container runtime check was: {error}",
            warning.Message);
    }

    [Fact]
    public void CheckDcpInfoAndLogErrors_MissingRuntimeSurfacesErrorAtWarningAndInException()
    {
        const string error = "exec: \"docker\": executable file not found in PATH";
        const string message = "Container runtime 'docker' could not be found. See https://aka.ms/aspire/containers for more details on supported container runtimes.";
        var logger = new FakeLogger();
        var options = new DcpOptions { ContainerRuntime = "docker" };
        var dcpInfo = new DcpInfo
        {
            Containers = new DcpContainersInfo
            {
                Installed = false,
                Running = false,
                Error = error
            }
        };

        var exception = Assert.Throws<DistributedApplicationException>(
            () => DcpDependencyCheck.CheckDcpInfoAndLogErrors(logger, options, dcpInfo, throwIfUnhealthy: true));

        Assert.Equal(
            $"{message}{Environment.NewLine}The error from the container runtime check was: {error}",
            exception.Message);
        var warning = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(
            $"{message}{Environment.NewLine}The error from the container runtime check was: {error}",
            warning.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CheckDcpInfoAndLogErrors_WithoutErrorPreservesGenericDiagnostic(string? error)
    {
        var logger = new FakeLogger();
        var options = new DcpOptions { ContainerRuntime = "docker" };
        var dcpInfo = new DcpInfo
        {
            Containers = new DcpContainersInfo
            {
                Installed = true,
                Running = false,
                Error = error
            }
        };
        var (message, linkUrl) = DcpDependencyCheck.BuildContainerRuntimeUnhealthyMessage("docker");

        var exception = Assert.Throws<DistributedApplicationException>(
            () => DcpDependencyCheck.CheckDcpInfoAndLogErrors(logger, options, dcpInfo, throwIfUnhealthy: true));

        Assert.Equal(message, exception.Message);
        var warning = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal($"{message} For more information, visit: {linkUrl}", warning.Message);
    }

    [Theory]
    // DCP reports an empty runtime and host name when the container runtime query times out.
    [InlineData("""{"runtime":"","hostName":"","installed":false,"running":false,"error":"context deadline exceeded"}""", "host.docker.internal")]
    [InlineData("""{"runtime":"podman","hostName":"","installed":true,"running":true}""", "host.containers.internal")]
    [InlineData("""{"runtime":"docker","installed":true,"running":true}""", "host.docker.internal")]
    [InlineData("""{"runtime":"docker","hostName":"custom.host","installed":true,"running":true}""", "custom.host")]
    public void ContainerHostName_FallsBackWhenDcpReportsNoHostName(string containersJson, string expectedHostName)
    {
        var dcpInfo = JsonSerializer.Deserialize<DcpInfo>($$"""{"version":"dev","containers":{{containersJson}}}""");

        Assert.NotNull(dcpInfo?.Containers);
        Assert.Equal(expectedHostName, dcpInfo.Containers.ContainerHostName);
    }
}
