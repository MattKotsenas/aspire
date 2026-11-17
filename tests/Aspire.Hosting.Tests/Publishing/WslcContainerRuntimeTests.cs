// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPROJECTS001
#pragma warning disable ASPIRECOMPUTE003

using System.ComponentModel;
using System.Text;
using Aspire.Hosting.Dcp;
using Microsoft.AspNetCore.InternalTesting;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Publishing;

[SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "WSLc is Windows-only.")]
public class WslcContainerRuntimeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task CheckIfRunningAsync_UsesContainerList(int exitCode, bool expected)
    {
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(exitCode)]));
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        Assert.Equal(expected, await runtime.CheckIfRunningAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["container", "ls", "-n", "1"], Assert.Single(runner.Commands).Arguments);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Fact]
    public async Task CheckIfRunningAsync_ReturnsFalseForWin32Exception()
    {
        var runner = new TestProcessRunner();
        var script = new FakeEventStream<byte>([Emit<byte>.Fail(new Win32Exception("wslc not found"))]);
        runner.Enqueue(script);
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        Assert.False(await runtime.CheckIfRunningAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["container", "ls", "-n", "1"], Assert.Single(runner.Commands).Arguments);
        Assert.Equal(1, script.DisposeCount);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Fact]
    public async Task CheckIfRunningAsync_PropagatesNonWin32Exception()
    {
        var runner = new TestProcessRunner();
        var failure = new InvalidOperationException("probe failed");
        var script = new FakeEventStream<byte>([Emit<byte>.Fail(failure)]);
        runner.Enqueue(script);
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.CheckIfRunningAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(["container", "ls", "-n", "1"], Assert.Single(runner.Commands).Arguments);
        Assert.Equal(1, script.DisposeCount);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Fact]
    public async Task BuildImageAsync_PassesBuildArgumentsSecretsAndStage()
    {
        var runner = new TestProcessRunner();
        var script = new FakeEventStream<byte>([Emit<byte>.Exited(0)]);
        runner.Enqueue(script);
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);
        var unsetVariable = $"ASPIRE_WSLC_BUILD_ARG_{Guid.NewGuid():N}";

        await runtime.BuildImageAsync(
            "context",
            "Dockerfile",
            new ContainerImageBuildOptions { ImageName = "myapp", Tag = "latest", ImageFormat = ContainerImageFormat.Docker },
            new() { ["VERSION"] = "1.0", [unsetVariable] = null },
            new()
            {
                ["token"] = new("secret-value", BuildImageSecretType.Environment),
                ["file"] = new("secret.txt", BuildImageSecretType.File)
            },
            "final",
            TestContext.Current.CancellationToken);

        var command = Assert.Single(runner.Commands);
        Assert.Equal("wslc", command.ExecutablePath);
        Assert.Equal(
            ["build", "--file", "Dockerfile", "--tag", "myapp:latest", "--build-arg", "VERSION=1.0",
                "--secret", "id=token,type=env,env=TOKEN", "--secret", "id=file,type=file,src=secret.txt", "--target", "final",
                Path.GetFullPath("context")],
            command.Arguments);
        Assert.Equal("secret-value", command.EnvironmentVariables["TOKEN"]);
        Assert.True(command.InheritEnvironmentVariables);
        Assert.Equal(1, script.DisposeCount);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("host-value", null, "host-value")]
    [InlineData("", null, "")]
    [InlineData("""say "hello world" C:\path with spaces\trailing\ a=b""", null, """say "hello world" C:\path with spaces\trailing\ a=b""")]
    [InlineData("host-value", "explicit-value", "explicit-value")]
    [InlineData("host-value", "", "")]
    [InlineData(null, "", "")]
    public async Task BuildImageAsync_ResolvesInheritedBuildArguments(string? hostValue, string? explicitValue, string? expectedValue)
    {
        var variable = $"ASPIRE_WSLC_BUILD_ARG_{Guid.NewGuid():N}";
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);
        try
        {
            Environment.SetEnvironmentVariable(variable, hostValue);
            Assert.Equal(hostValue, Environment.GetEnvironmentVariable(variable));
            var buildArguments = new Dictionary<string, string?> { [variable] = explicitValue };

            await runtime.BuildImageAsync(
                "context", "Dockerfile", new ContainerImageBuildOptions { ImageName = "myapp" },
                buildArguments, [], null, TestContext.Current.CancellationToken);

            var expected = new List<string> { "build", "--file", "Dockerfile", "--tag", "myapp" };
            if (expectedValue is not null)
            {
                expected.AddRange(["--build-arg", $"{variable}={expectedValue}"]);
            }
            expected.Add(Path.GetFullPath("context"));
            Assert.Equal(expected, Assert.Single(runner.Commands).Arguments);
            Assert.Empty(runner.ProcessSpecs);
            Assert.Equal(explicitValue, buildArguments[variable]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData(ContainerTargetPlatform.LinuxAmd64)]
    [InlineData(ContainerTargetPlatform.LinuxArm64)]
    [InlineData(ContainerTargetPlatform.AllLinux)]
    [InlineData(ContainerTargetPlatform.WindowsAmd64)]
    public async Task BuildImageAsync_RejectsSelectedPlatformBeforeStartingProcess(ContainerTargetPlatform platform)
    {
        var runner = new TestProcessRunner();
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.BuildImageAsync(
            "context", "Dockerfile",
            new ContainerImageBuildOptions { ImageName = "myapp", TargetPlatform = platform },
            [], [], null, TestContext.Current.CancellationToken));

        Assert.Contains("WSLc does not support selecting a platform", exception.Message);
        Assert.Empty(runner.Commands);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Theory]
    [InlineData(null, "out", null)]
    [InlineData(ContainerImageFormat.Docker, "out", null)]
    [InlineData(ContainerImageFormat.Oci, "out", null)]
    [InlineData(ContainerImageFormat.Oci, null, null)]
    [InlineData(null, null, ContainerImageDestination.Archive)]
    public async Task BuildImageAsync_RejectsArchiveAndOciOutputBeforeStartingProcess(
        ContainerImageFormat? format, string? outputPath, ContainerImageDestination? destination)
    {
        var runner = new TestProcessRunner();
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.BuildImageAsync(
            "context", "Dockerfile",
            new ContainerImageBuildOptions { ImageName = "myapp", ImageFormat = format, OutputPath = outputPath, Destination = destination },
            [], [], null, TestContext.Current.CancellationToken));

        Assert.Contains("WSLc does not support Dockerfile image archive output or OCI image format selection.", exception.Message);
        Assert.Empty(runner.Commands);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Fact]
    public async Task BuildImageAsync_PreservesProcessFailureOutput()
    {
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stderr("build failed\n"u8), Emit<byte>.Exited(42)]));
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(() => runtime.BuildImageAsync(
            "context", "Dockerfile",
            new ContainerImageBuildOptions { ImageName = "myapp" },
            [], [], null, TestContext.Current.CancellationToken));

        Assert.Equal(42, exception.ExitCode);
        Assert.Equal(["build failed"], exception.ProcessOutput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeOperations_PropagateCancellationAndDisposeProcess(bool build)
    {
        var runner = new TestProcessRunner();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Stdout("running\n"u8),
            Emit<byte>.Wait(release.Task),
            Emit<byte>.Exited(0)
        ]);
        runner.Enqueue(script.OnStandardOutput(_ => started.TrySetResult()));
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);
        using var cancellation = new CancellationTokenSource();

        var operation = build
            ? runtime.BuildImageAsync("context", "Dockerfile", new ContainerImageBuildOptions { ImageName = "myapp" }, [], [], null, cancellation.Token)
            : runtime.CheckIfRunningAsync(cancellation.Token);
        try
        {
            await started.Task.DefaultTimeout();
            Assert.False(cancellation.IsCancellationRequested);
            Assert.False(operation.IsCompleted);
            Assert.Equal(0, script.DisposeCount);

            await cancellation.CancelAsync();

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation).DefaultTimeout();
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(1, script.DisposeCount);
            Assert.Single(runner.Commands);
            Assert.Empty(runner.ProcessSpecs);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DockerfilePublishing_RequiresOptingOutOfDefaultPlatform(bool hostPlatform)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.Configure<DcpOptions>(options => options.ContainerRuntime = "wslc");
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var resource = builder.AddDockerfile("program", workspace.WorkspaceRoot.FullName);
        if (hostPlatform)
        {
            resource.WithContainerBuildOptions(options => options.TargetPlatform = null);
        }
        using var app = builder.Build();
        var manager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        if (hostPlatform)
        {
            runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
            await manager.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken);
            Assert.Equal(2, runner.Commands.Count);
            Assert.Equal("build", runner.Commands[1].Arguments[0]);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
                () => manager.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken));
            Assert.Contains("WSLc does not support selecting a platform", exception.Message);
            Assert.Single(runner.Commands);
        }
        Assert.Equal(["container", "ls", "-n", "1"], runner.Commands[0].Arguments);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Fact]
    public async Task DockerfilePublishing_RejectsArchiveDestinationWithoutOutputPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.Configure<DcpOptions>(options => options.ContainerRuntime = "wslc");
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var resource = builder.AddDockerfile("program", workspace.WorkspaceRoot.FullName)
            .WithContainerBuildOptions(options =>
            {
                options.TargetPlatform = null;
                options.Destination = ContainerImageDestination.Archive;
            });
        using var app = builder.Build();
        var manager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => manager.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken));

        Assert.Contains("WSLc does not support Dockerfile image archive output", exception.Message);
        Assert.Equal(["container", "ls", "-n", "1"], Assert.Single(runner.Commands).Arguments);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Fact]
    public async Task ImageOperations_UseSharedWslcCommands()
    {
        var runner = new TestProcessRunner();
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var resource = builder.AddContainer("myapp", "myapp", "latest").WithContainerRegistry(registry);
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        const string password = "pässwörd🔑";

        await runtime.TagImageAsync("myapp:latest", "myapp:next", TestContext.Current.CancellationToken);
        await runtime.PushImageAsync(resource.Resource, TestContext.Current.CancellationToken);
        await runtime.LoginToRegistryAsync("registry.example.com", "user", password, TestContext.Current.CancellationToken);
        await runtime.RemoveImageAsync("myapp:next", TestContext.Current.CancellationToken);

        Assert.Collection(runner.Commands,
            command => Assert.Equal(["tag", "myapp:latest", "myapp:next"], command.Arguments),
            command => Assert.Equal(["tag", "myapp:latest", "registry.example.com/myapp:latest"], command.Arguments),
            command => Assert.Equal(["push", "registry.example.com/myapp:latest"], command.Arguments),
            command =>
            {
                Assert.Equal(["login", "--username", "user", "--password-stdin", "registry.example.com"], command.Arguments);
                Assert.Equal(Encoding.UTF8.GetBytes(password + Environment.NewLine), Assert.IsType<ProcessInput>(command.Input).Bytes.ToArray());
            },
            command => Assert.Equal(["rmi", "myapp:next"], command.Arguments));
        Assert.All(runner.Commands, command => Assert.Equal("wslc", command.ExecutablePath));
        Assert.Empty(runner.ProcessSpecs);
    }

    [Fact]
    public async Task InspectImageConfigAsync_ParsesNativeJsonAndPreservesExactRawJson()
    {
        const string json = "[\r\n  {\n" +
            "    \"Id\": \"sha256:0123456789abcdef\",\r\n\r\n" +
            "    \"Architecture\": \"amd64\",\n" +
            "    \"Os\": \"linux\",\r\n" +
            "    \"Config\": {\n" +
            "      \"Entrypoint\": [\"dotnet\", \"app.dll\"],\r\n" +
            "      \"Cmd\": [\"--urls\"],\n" +
            "      \"WorkingDir\": \"/app\"\r\n" +
            "    }\n  }\r\n]\n";
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes(json)), Emit<byte>.Exited(0)]));
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);
        const string imageName = "registry/image\\\" --help";

        var result = await runtime.InspectImageConfigAsync(imageName, TestContext.Current.CancellationToken);

        Assert.Equal(["image", "inspect", imageName, "--format", "json"], Assert.Single(runner.Commands).Arguments);
        Assert.Empty(runner.ProcessSpecs);
        Assert.Equal(ContainerImageInspectionStatus.Succeeded, result.Status);
        Assert.Equal(json, result.RawJson);
        Assert.True(result.TryGetConfig(out var config));
        Assert.Equal(["dotnet", "app.dll"], config.Entrypoint);
        Assert.Equal(["--urls"], config.Command);
        Assert.Equal("/app", config.WorkingDirectory);
    }

    [Fact]
    public async Task InspectImageConfigAsync_AllowsNullEntrypointAndCommand()
    {
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout("""[{"Config":{"Entrypoint":null,"Cmd":null,"WorkingDir":""}}]"""u8), Emit<byte>.Exited(0)]));
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        var result = await runtime.InspectImageConfigAsync("image", TestContext.Current.CancellationToken);

        Assert.True(result.TryGetConfig(out var config));
        Assert.Empty(config.Entrypoint);
        Assert.Empty(config.Command);
        Assert.Equal("", config.WorkingDirectory);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("""[{"Config":{}},{"Config":{}}]""")]
    [InlineData("""[{"Config":{"Cmd":42}}]""")]
    public async Task InspectImageConfigAsync_ReportsInvalidMetadata(string json)
    {
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes(json)), Emit<byte>.Exited(0)]));
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        var result = await runtime.InspectImageConfigAsync("image", TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Failed, result.Status);
        Assert.Equal("WSLc returned invalid image configuration for 'image'.", result.ErrorMessage);
        Assert.False(result.TryGetConfig(out _));
    }

    [Fact]
    public async Task InspectImageConfigAsync_ReportsCommandFailure()
    {
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stderr("image not found\n"u8), Emit<byte>.Exited(1)]));
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        var result = await runtime.InspectImageConfigAsync("image", TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Failed, result.Status);
        Assert.Contains("image not found", result.ErrorMessage);
    }

    [Fact]
    public async Task UnsupportedOperations_DoNotStartProcesses()
    {
        var runner = new TestProcessRunner();
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);
        var context = new ComposeOperationContext { ProjectName = "app", WorkingDirectory = "out" };

        var up = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.ComposeUpAsync(context, TestContext.Current.CancellationToken));
        var down = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.ComposeDownAsync(context, TestContext.Current.CancellationToken));
        var list = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.ComposeListServicesAsync(context, TestContext.Current.CancellationToken));
        Assert.Equal("WSLc does not support Docker Compose deployment or management. Set ASPIRE_CONTAINER_RUNTIME to docker or podman for Compose operations.", up.Message);
        Assert.Equal(up.Message, down.Message);
        Assert.Equal(up.Message, list.Message);

        var inspection = await runtime.InspectImageManifestAsync("image", TestContext.Current.CancellationToken);
        Assert.Equal(ContainerImageInspectionStatus.Unsupported, inspection.Status);
        Assert.Empty(runner.Commands);
        Assert.Empty(runner.ProcessSpecs);
    }

}
