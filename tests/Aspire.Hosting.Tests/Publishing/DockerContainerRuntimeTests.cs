// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003

using System.Text;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Tests.Publishing;

public class DockerContainerRuntimeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(ContainerImageFormat.Docker)]
    [InlineData(ContainerImageFormat.Oci)]
    public async Task BuildImageAsync_ArchiveUsesIsolatedBuilder(ContainerImageFormat? imageFormat)
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = imageFormat
        };

        await runtime.BuildImageAsync(
            contextPath: "context",
            dockerfilePath: "Dockerfile",
            options: options,
            buildArguments: [],
            buildSecrets: [],
            stage: null,
            cancellationToken: CancellationToken.None);

        var archivePath = ResourceExtensions.GetContainerImageArchivePath("out", "myapp:latest");
        var outputType = imageFormat == ContainerImageFormat.Oci ? "type=oci" : "type=docker";
        var commands = processRunner.Commands;
        Assert.Equal(4, commands.Count);
        Assert.All(commands, command => Assert.Equal("docker", command.ExecutablePath));
        Assert.Equal(["buildx", "version"], commands[0].Arguments);
        var builderName = AssertAndGetBuilderName(commands[1].Arguments);
        Assert.Equal(
            ["buildx", "build", "--file", "Dockerfile", "--tag", "myapp:latest", "--builder", builderName,
                "--output", $"{outputType},dest={archivePath}", GetNormalizedContextPath()],
            commands[2].Arguments);
        Assert.Equal(["buildx", "rm", builderName], commands[3].Arguments);
    }

    [Fact]
    public async Task BuildImageAsync_ArchiveUsesUniqueIsolatedBuilders()
    {
        var processRunner = new TestProcessRunner();
        for (var i = 0; i < 8; i++)
        {
            processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        }

        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Docker
        };

        await BuildImageAsync(runtime, options);
        await BuildImageAsync(runtime, options);

        Assert.Equal(8, processRunner.Commands.Count);
        var builderNames = processRunner.Commands
            .Where(static command => command.Arguments.Take(2).SequenceEqual(["buildx", "create"]))
            .Select(command => AssertAndGetBuilderName(command.Arguments))
            .ToArray();
        Assert.Equal(2, builderNames.Length);
        Assert.Equal(2, builderNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task BuildImageAsync_CancellationWaitsForIsolatedBuilderCleanup()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var build = new FakeEventStream<byte>(
        [
            Emit<byte>.Stdout("building\n"u8),
            Emit<byte>.Wait(buildRelease.Task),
            Emit<byte>.Exited(0)
        ], cleanup: buildDisposed.Task);
        var cleanup = new FakeEventStream<byte>(
        [
            Emit<byte>.Stdout("removing builder\n"u8),
            Emit<byte>.Wait(cleanupRelease.Task),
            Emit<byte>.Exited(0)
        ]);
        processRunner.Enqueue(build.OnStandardOutput(_ => buildStarted.TrySetResult()));
        processRunner.Enqueue(cleanup.OnStandardOutput(_ => cleanupStarted.TrySetResult()));
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Docker
        };
        using var cancellation = new CancellationTokenSource();

        var buildTask = runtime.BuildImageAsync(
            contextPath: "context",
            dockerfilePath: "Dockerfile",
            options: options,
            buildArguments: [],
            buildSecrets: [],
            stage: null,
            cancellationToken: cancellation.Token);

        try
        {
            await buildStarted.Task.DefaultTimeout();
            Assert.False(cancellation.IsCancellationRequested);
            Assert.False(buildTask.IsCompleted);
            Assert.Equal(3, processRunner.Commands.Count);

            await cancellation.CancelAsync();
            Assert.True(cancellation.IsCancellationRequested);
            Assert.False(buildTask.IsCompleted);
            Assert.Equal(3, processRunner.Commands.Count);
            Assert.False(cleanupStarted.Task.IsCompleted);

            // Builder removal must wait for the canceled build's process cleanup to settle.
            buildDisposed.TrySetResult();
            await cleanupStarted.Task.DefaultTimeout();
            Assert.Equal(1, build.DisposeCount);
            var builderName = AssertAndGetBuilderName(processRunner.Commands[1].Arguments);
            Assert.Equal(["buildx", "rm", builderName], processRunner.Commands[3].Arguments);
            Assert.False(buildTask.IsCompleted);

            // The caller is already canceled; removal still has to finish with its own token.
            cleanupRelease.TrySetResult();
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => buildTask).DefaultTimeout();
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Equal(1, cleanup.DisposeCount);
            Assert.Equal(4, processRunner.Commands.Count);
        }
        finally
        {
            buildRelease.TrySetResult();
            buildDisposed.TrySetResult();
            cleanupRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task BuildImageAsync_CleanupFailureDoesNotMaskBuildFailure()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var build = new FakeEventStream<byte>([Emit<byte>.Stderr("build failed\n"u8), Emit<byte>.Exited(42)]);
        var cleanup = new FakeEventStream<byte>([Emit<byte>.Fail(new InvalidOperationException("cleanup failed"))]);
        processRunner.Enqueue(build);
        processRunner.Enqueue(cleanup);
        var logger = new FakeLogger<DockerContainerRuntime>();
        var runtime = new DockerContainerRuntime(logger, processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Docker
        };

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(
            () => BuildImageAsync(runtime, options));

        Assert.Equal(42, exception.ExitCode);
        Assert.Equal(["build failed"], exception.ProcessOutput);
        Assert.Equal(1, build.DisposeCount);
        Assert.Equal(1, cleanup.DisposeCount);
        Assert.Equal(4, processRunner.Commands.Count);
        var builderName = AssertAndGetBuilderName(processRunner.Commands[1].Arguments);
        Assert.Equal(["buildx", "rm", builderName], processRunner.Commands[3].Arguments);
        Assert.Contains(
            logger.Collector.GetSnapshot(),
            log => log.Level == LogLevel.Warning &&
                log.Message.Contains("Failed to remove buildkit instance aspire-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, ContainerTargetPlatform.LinuxAmd64, "linux/amd64")]
    [InlineData(null, ContainerTargetPlatform.LinuxArm64, "linux/arm64")]
    [InlineData(null, ContainerTargetPlatform.AllLinux, "linux/amd64,linux/arm64")]
    [InlineData(ContainerImageFormat.Docker, null, null)]
    [InlineData(ContainerImageFormat.Docker, ContainerTargetPlatform.LinuxAmd64, "linux/amd64")]
    [InlineData(ContainerImageFormat.Docker, ContainerTargetPlatform.LinuxArm64, "linux/arm64")]
    [InlineData(ContainerImageFormat.Docker, ContainerTargetPlatform.AllLinux, "linux/amd64,linux/arm64")]
    public async Task BuildImageAsync_LocalImageArchiveUsesActiveContextBuilderThenDockerSave(
        ContainerImageFormat? imageFormat, ContainerTargetPlatform? targetPlatform, string? expectedPlatforms)
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout("desktop-linux\n"u8), Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = imageFormat,
            TargetPlatform = targetPlatform,
            RequiresLocalImageStore = true
        };

        await BuildImageAsync(runtime, options);

        var archivePath = ResourceExtensions.GetContainerImageArchivePath("out", "myapp:latest");
        string[] platformArguments = expectedPlatforms is null ? [] : ["--platform", expectedPlatforms];
        Assert.Collection(
            processRunner.Commands,
            check => Assert.Equal(["buildx", "version"], check.Arguments),
            context => Assert.Equal(["context", "show"], context.Arguments),
            build => Assert.Equal(
                ["buildx", "build", "--file", "Dockerfile", "--tag", "myapp:latest", "--builder", "desktop-linux",
                    .. platformArguments, GetNormalizedContextPath()],
                build.Arguments),
            save => Assert.Equal(
                ["image", "save", "--output", archivePath, "myapp:latest"],
                save.Arguments));
    }

    [Fact]
    public async Task BuildImageAsync_LocalMultiPlatformBuildFailurePreservesDiagnosticWithoutSaving()
    {
        string[] diagnostic =
        [
            "Multi-platform build is not supported for the docker driver.",
            "Switch to a different driver, or turn on the containerd image store, and try again.",
            "Learn more at https://docs.docker.com/go/build-multi-platform/"
        ];
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout("desktop-linux\n"u8), Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>(
        [
            Emit<byte>.Stderr(Encoding.UTF8.GetBytes(string.Join('\n', diagnostic) + "\n")),
            Emit<byte>.Exited(1)
        ]));
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Docker,
            TargetPlatform = ContainerTargetPlatform.AllLinux,
            RequiresLocalImageStore = true
        };

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(() => BuildImageAsync(runtime, options));

        Assert.Equal(1, exception.ExitCode);
        Assert.Equal(diagnostic, exception.ProcessOutput);
        Assert.Equal(
            $"Docker build failed with exit code 1.{Environment.NewLine}{string.Join(Environment.NewLine, diagnostic)}",
            exception.Message);
        Assert.Collection(
            processRunner.Commands,
            check => Assert.Equal(["buildx", "version"], check.Arguments),
            context => Assert.Equal(["context", "show"], context.Arguments),
            build => Assert.Equal(
                ["buildx", "build", "--file", "Dockerfile", "--tag", "myapp:latest", "--builder", "desktop-linux",
                    "--platform", "linux/amd64,linux/arm64", GetNormalizedContextPath()],
                build.Arguments));
    }

    [Fact]
    public async Task BuildImageAsync_LocalImageWithoutArchiveUsesActiveContextBuilder()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout("desktop-linux\n"u8), Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            TargetPlatform = ContainerTargetPlatform.LinuxAmd64,
            RequiresLocalImageStore = true
        };

        await BuildImageAsync(runtime, options);

        Assert.Collection(
            processRunner.Commands,
            check => Assert.Equal(["buildx", "version"], check.Arguments),
            context => Assert.Equal(["context", "show"], context.Arguments),
            build => Assert.Equal(
                ["buildx", "build", "--file", "Dockerfile", "--tag", "myapp:latest", "--builder", "desktop-linux",
                    "--platform", "linux/amd64", GetNormalizedContextPath()],
                build.Arguments));
    }

    [Theory]
    [InlineData("\n \n\t desktop-linux \t\n\n")]
    [InlineData("\r\n \r\n\t desktop-linux \t\r\n\r\n")]
    [InlineData("\r \r\t desktop-linux \t\r\r")]
    public async Task BuildImageAsync_LocalImageAcceptsOneTrimmedActiveContext(string output)
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes(output)), Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            RequiresLocalImageStore = true
        };

        await BuildImageAsync(runtime, options);

        Assert.Collection(
            processRunner.Commands,
            check => Assert.Equal(["buildx", "version"], check.Arguments),
            context => Assert.Equal(["context", "show"], context.Arguments),
            build => Assert.Equal(
                ["buildx", "build", "--file", "Dockerfile", "--tag", "myapp:latest", "--builder", "desktop-linux",
                    GetNormalizedContextPath()],
                build.Arguments));
    }

    [Theory]
    [InlineData("desktop-linux\nother-context\n")]
    [InlineData("desktop-linux\r\nother-context\r\n")]
    [InlineData("desktop-linux\rother-context\r")]
    public async Task BuildImageAsync_LocalImageRejectsMultipleActiveContexts(string output)
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes(output)), Emit<byte>.Exited(0)]));
        // If validation regresses, let the build succeed so the failure reports the missing rejection.
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            RequiresLocalImageStore = true
        };

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => BuildImageAsync(runtime, options));

        Assert.Equal("Docker did not report exactly one active context.", exception.Message);
        Assert.Collection(
            processRunner.Commands,
            check => Assert.Equal(["buildx", "version"], check.Arguments),
            context => Assert.Equal(["context", "show"], context.Arguments));
    }

    [Fact]
    public async Task BuildImageAsync_LocalImageWithEmptyActiveContextFails()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(" \n"u8), Emit<byte>.Exited(0)]));
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            RequiresLocalImageStore = true
        };

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => BuildImageAsync(runtime, options));

        Assert.Equal("Docker did not report exactly one active context.", exception.Message);
        Assert.Collection(
            processRunner.Commands,
            check => Assert.Equal(["buildx", "version"], check.Arguments),
            context => Assert.Equal(["context", "show"], context.Arguments));
    }

    [Fact]
    public async Task BuildImageAsync_LocalImageContextDiscoveryFailureIsReported()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stderr("context failed\n"u8), Emit<byte>.Exited(42)]));
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            RequiresLocalImageStore = true
        };

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => BuildImageAsync(runtime, options));

        Assert.Equal(
            $"Docker context discovery for 'myapp:latest' failed with exit code 42.{Environment.NewLine}context failed",
            exception.Message);
        Assert.Collection(
            processRunner.Commands,
            check => Assert.Equal(["buildx", "version"], check.Arguments),
            context => Assert.Equal(["context", "show"], context.Arguments));
    }

    [Fact]
    public async Task BuildImageAsync_LocalImageOciArchiveFailsBeforeInvokingDocker()
    {
        var processRunner = new TestProcessRunner();
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Oci,
            RequiresLocalImageStore = true
        };

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => BuildImageAsync(runtime, options));

        Assert.Equal(
            "Docker cannot export an OCI archive when container-file layering references locally built images. " +
            "Use ContainerImageFormat.Docker for this archive or run the publish with Podman.",
            exception.Message);
        Assert.Empty(processRunner.Commands);
        Assert.Empty(processRunner.ProcessSpecs);
    }

    [Fact]
    public async Task BuildImageAsync_LocalImageArchiveRejectsInvalidFormat()
    {
        var processRunner = new TestProcessRunner();
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = (ContainerImageFormat)42,
            RequiresLocalImageStore = true
        };

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BuildImageAsync(runtime, options));

        Assert.Equal("options", exception.ParamName);
        Assert.Empty(processRunner.Commands);
        Assert.Empty(processRunner.ProcessSpecs);
    }

    [Fact]
    public async Task BuildImageAsync_WithoutArchiveUsesDefaultBuilder()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            ImageFormat = ContainerImageFormat.Docker
        };

        await runtime.BuildImageAsync(
            contextPath: "context",
            dockerfilePath: "Dockerfile",
            options: options,
            buildArguments: [],
            buildSecrets: [],
            stage: null,
            cancellationToken: CancellationToken.None);

        Assert.Collection(
            processRunner.Commands,
            check => Assert.Equal(["buildx", "version"], check.Arguments),
            build => Assert.Equal(
                ["buildx", "build", "--file", "Dockerfile", "--tag", "myapp:latest",
                    "--output", "type=docker", GetNormalizedContextPath()],
                build.Arguments));
    }

    private static Task BuildImageAsync(
        DockerContainerRuntime runtime,
        ContainerImageBuildOptions options)
    {
        return runtime.BuildImageAsync(
            contextPath: "context",
            dockerfilePath: "Dockerfile",
            options: options,
            buildArguments: [],
            buildSecrets: [],
            stage: null,
            cancellationToken: CancellationToken.None);
    }

    private static string AssertAndGetBuilderName(IReadOnlyList<string> arguments)
    {
        Assert.Equal(6, arguments.Count);
        var builderName = arguments[3];
        Assert.Equal(["buildx", "create", "--name", builderName, "--driver", "docker-container"], arguments);
        Assert.StartsWith("aspire-", builderName, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(builderName["aspire-".Length..], "N", out _));
        return builderName;
    }

    private static string GetNormalizedContextPath()
    {
        return Path.GetFullPath("context")
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
