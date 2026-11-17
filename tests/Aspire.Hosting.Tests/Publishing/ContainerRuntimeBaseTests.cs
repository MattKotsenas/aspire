// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Text;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Tests.Publishing;

[Trait("Partition", "4")]
public class ContainerRuntimeBaseTests
{
    public static TheoryData<string> BuildRuntimes => OperatingSystem.IsWindows()
        ? new() { KnownContainerRuntimes.Docker, KnownContainerRuntimes.Podman, KnownContainerRuntimes.Wslc }
        : new() { KnownContainerRuntimes.Docker, KnownContainerRuntimes.Podman };

    [Theory]
    [MemberData(nameof(BuildRuntimes))]
    public async Task BuildImageAsync_PreservesRootContext(string runtimeName)
    {
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        if (runtimeName == KnownContainerRuntimes.Docker)
        {
            runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        }
        var runtime = CreateRuntime(runtimeName, runner);
        var rootPath = Path.GetPathRoot(Path.GetFullPath("context"))!;

        await runtime.BuildImageAsync(rootPath, Path.Combine(rootPath, "Dockerfile"),
            new ContainerImageBuildOptions { ImageName = "myapp" },
            [], [], null, TestContext.Current.CancellationToken);

        Assert.Equal(rootPath, runner.Commands[^1].Arguments[^1]);
    }

    [Theory]
    [MemberData(nameof(BuildRuntimes))]
    public async Task BuildImageAsync_PreservesArgumentBoundaries(string runtimeName)
    {
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        if (runtimeName == KnownContainerRuntimes.Docker)
        {
            runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        }
        var runtime = CreateRuntime(runtimeName, runner);
        var contextPath = Path.GetFullPath("context with spaces");
        var dockerfile = Path.Combine(contextPath, "Dockerfile");
        const string secretPath = """C:\secrets with spaces\team,a\config "quoted".json""";
        const string stage = "stage with spaces";
        var values = new Dictionary<string, string?>
        {
            ["CONFIG"] = """{"mode":"prod"}""",
            ["MESSAGE"] = """say "hello world" """,
            ["PATH"] = """C:\path with spaces\trailing\""",
            ["QUOTED_PATH"] = """C:\path\"quoted"\file""",
            ["EMPTY"] = "",
            [$"ASPIRE_BUILD_ARG_{Guid.NewGuid():N}"] = null
        };

        await runtime.BuildImageAsync(contextPath, dockerfile,
            new ContainerImageBuildOptions { ImageName = "myapp", Tag = "latest" },
            values,
            new()
            {
                ["file"] = new(secretPath, BuildImageSecretType.File),
                ["token"] = new("secret-value", BuildImageSecretType.Environment),
                ["inherited"] = new(null, BuildImageSecretType.Environment)
            },
            stage, TestContext.Current.CancellationToken);

        var spec = runner.Commands[^1];
        var expected = new List<string>();
        if (runtimeName == KnownContainerRuntimes.Docker)
        {
            expected.Add("buildx");
        }
        expected.AddRange(["build", "--file", dockerfile, "--tag", "myapp:latest"]);
        foreach (var (key, value) in values)
        {
            if (runtimeName == KnownContainerRuntimes.Wslc && value is null)
            {
                continue;
            }
            expected.AddRange(["--build-arg", value is null ? key : $"{key}={value}"]);
        }
        expected.AddRange([
            "--secret", runtimeName == KnownContainerRuntimes.Podman
                ? $"id=file,type=file,src={secretPath}"
                : "id=file,type=file,\"src=C:\\secrets with spaces\\team,a\\config \"\"quoted\"\".json\"",
            "--secret", "id=token,type=env,env=TOKEN",
            "--secret", runtimeName == KnownContainerRuntimes.Podman ? "id=inherited,type=env" : "id=inherited,type=env,env=INHERITED",
            "--target", stage, contextPath]);
        Assert.Equal(expected, spec.Arguments);
        Assert.True(spec.InheritEnvironmentVariables);
        Assert.Equal("secret-value", spec.EnvironmentVariables["TOKEN"]);
    }

    [Theory]
    [InlineData(@"C:\team,a\.npmrc", "\"src=C:\\team,a\\.npmrc\"")]
    [InlineData("path\"quoted", "\"src=path\"\"quoted\"")]
    [InlineData("path\r\nnext", "\"src=path\r\nnext\"")]
    [InlineData(@"C:\plain\.npmrc", @"src=C:\plain\.npmrc")]
    public void BuildSecrets_EncodesCsvFieldsOnlyWhenRequested(string path, string expectedSource)
    {
        var secrets = new Dictionary<string, BuildImageSecretValue>
        {
            ["npmrc"] = new(path, BuildImageSecretType.File)
        };

        Assert.Equal(
            ["--secret", $"id=npmrc,type=file,{expectedSource}"],
            ContainerRuntimeBase<DockerContainerRuntime>.BuildSecrets(secrets, useCsv: true));
        Assert.Equal(
            ["--secret", $"id=npmrc,type=file,src={path}"],
            ContainerRuntimeBase<PodmanContainerRuntime>.BuildSecrets(secrets, useCsv: false));
    }

    private static IContainerRuntime CreateRuntime(string runtimeName, TestProcessRunner runner) => runtimeName switch
    {
        KnownContainerRuntimes.Docker => new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner),
        KnownContainerRuntimes.Podman => new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, runner),
        KnownContainerRuntimes.Wslc => new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner),
        _ => throw new ArgumentOutOfRangeException(nameof(runtimeName))
    };

    [Fact]
    public async Task ExecuteContainerCommandAsync_IncludesCapturedOutputInFailureMessage()
    {
        var runtime = new TestContainerRuntime();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            runtime.RunFailingCommandAsync()).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("Test command failed with exit code 1.", exception.Message);
        Assert.Contains("stdout-final-line", exception.Message);
        Assert.Contains("stderr-final-line", exception.Message);
    }

    [Fact]
    public async Task ExecuteContainerCommandForOutputAsync_ReturnsStdoutOnly()
    {
        var runtime = new TestContainerRuntime();

        var output = await runtime.RunCommandForOutputAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("stdout-only" + Environment.NewLine, output);
    }

    [Fact]
    public async Task ExecuteContainerCommandForOutputAsync_PreservesExactShellOutput()
    {
        var expected = $"first{Environment.NewLine}{Environment.NewLine} \t{Environment.NewLine}last{Environment.NewLine}";
        var directory = Directory.CreateTempSubdirectory("aspire output-");
        try
        {
            var path = Path.Combine(directory.FullName, "runtime output.txt");
            await File.WriteAllTextAsync(path, expected, TestContext.Current.CancellationToken);
            Assert.Equal(expected, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            var runtime = new TestContainerRuntime();

            var output = await runtime.RunFileForOutputAsync(path, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(expected, output);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task InspectImageCommandsPreserveUntrustedImageNameAsSingleArgument()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = new TestContainerRuntime(processRunner);
        const string imageName = "registry/image\\\" --help";

        await runtime.InspectImageConfigAsync(imageName, TestContext.Current.CancellationToken);
        await runtime.InspectImageManifestAsync(imageName, TestContext.Current.CancellationToken);

        Assert.Collection(
            processRunner.Commands.Select(command => command.Arguments),
            arguments =>
            {
                Assert.Equal(
                    [
                        "image",
                        "inspect",
                        imageName,
                        "--format",
                        """{"Entrypoint":{{json .Config.Entrypoint}},"Cmd":{{json .Config.Cmd}},"WorkingDir":{{json .Config.WorkingDir}}}"""
                    ],
                    arguments);
            },
            arguments => Assert.Equal(["manifest", "inspect", "--verbose", imageName], arguments));
    }

    [Fact]
    public async Task DockerInspectionReturnsTypedResults()
    {
        const string configJson = "{\r\n\r\n  \"Entrypoint\": [\"dotnet\", \"/app/app.dll\"],\n" +
            "  \"Cmd\": [\"--urls\"],\r\n  \"WorkingDir\": \"/app\"\n}\r\n";
        const string manifestJson = "[\n\n  {\r\n    \"Descriptor\": {\n" +
            "      \"digest\": \"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\r\n" +
            "      \"platform\": { \"os\": \"linux\", \"architecture\": \"amd64\" }\n" +
            "    }\r\n  }\n]\n";
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes(configJson)), Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes(manifestJson)), Emit<byte>.Exited(0)]));
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, processRunner);

        var configResult = await runtime.InspectImageConfigAsync(
            "example/image:tag",
            TestContext.Current.CancellationToken);
        var manifestResult = await runtime.InspectImageManifestAsync(
            "example/image:tag",
            TestContext.Current.CancellationToken);

        Assert.Equal(configJson, configResult.RawJson);
        Assert.Equal(manifestJson, manifestResult.RawJson);
        Assert.Equal(ContainerImageInspectionStatus.Succeeded, configResult.Status);
        Assert.True(configResult.TryGetConfig(out var config));
        Assert.Equal(["dotnet", "/app/app.dll"], config.Entrypoint);
        Assert.Equal(["--urls"], config.Command);
        Assert.Equal("/app", config.WorkingDirectory);
        Assert.Equal(ContainerImageInspectionStatus.Succeeded, manifestResult.Status);
        Assert.True(manifestResult.TryGetManifest("linux", "amd64", out var manifest));
        Assert.Equal("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", manifest.Digest);
    }

    [Fact]
    public async Task ContainerRuntimeInspectionDefaultsToUnsupported()
    {
        IContainerRuntime runtime = new UnsupportedInspectionContainerRuntime();

        var configResult = await runtime.InspectImageConfigAsync(
            "example/image:tag",
            TestContext.Current.CancellationToken);
        var manifestResult = await runtime.InspectImageManifestAsync(
            "example/image:tag",
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Unsupported, configResult.Status);
        Assert.False(configResult.TryGetConfig(out _));
        Assert.Equal(ContainerImageInspectionStatus.Unsupported, manifestResult.Status);
        Assert.False(manifestResult.TryGetManifest("linux", "amd64", out _));
    }

    [Fact]
    public void ContainerRuntimeImplementationsCanCreateInspectionResults()
    {
        var config = new ContainerImageConfig(["dotnet"], ["app.dll"], "/app");
        var configResult = ContainerImageConfigInspectionResult.Success(config, """{"config":true}""");
        var configFailure = ContainerImageConfigInspectionResult.Failure("config failed", """{"error":true}""");
        var manifest = new ContainerImageManifest(
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "linux",
            "amd64");
        var manifestResult = ContainerImageManifestInspectionResult.Success([manifest], """{"manifest":true}""");
        var manifestFailure = ContainerImageManifestInspectionResult.Failure("manifest failed", """{"error":true}""");

        Assert.Equal(ContainerImageInspectionStatus.Succeeded, configResult.Status);
        Assert.True(configResult.TryGetConfig(out var inspectedConfig));
        Assert.Same(config, inspectedConfig);
        Assert.Equal("""{"config":true}""", configResult.RawJson);
        Assert.Equal(ContainerImageInspectionStatus.Failed, configFailure.Status);
        Assert.Equal("config failed", configFailure.ErrorMessage);
        Assert.Equal("""{"error":true}""", configFailure.RawJson);
        Assert.False(configFailure.TryGetConfig(out _));
        Assert.Equal(ContainerImageInspectionStatus.Unsupported, ContainerImageConfigInspectionResult.Unsupported.Status);

        Assert.Equal(ContainerImageInspectionStatus.Succeeded, manifestResult.Status);
        Assert.True(manifestResult.TryGetManifest("LINUX", "AMD64", out var inspectedManifest));
        Assert.Same(manifest, inspectedManifest);
        Assert.Equal("""{"manifest":true}""", manifestResult.RawJson);
        Assert.Equal(ContainerImageInspectionStatus.Failed, manifestFailure.Status);
        Assert.Equal("manifest failed", manifestFailure.ErrorMessage);
        Assert.Equal("""{"error":true}""", manifestFailure.RawJson);
        Assert.False(manifestFailure.TryGetManifest("linux", "amd64", out _));
        Assert.Equal(ContainerImageInspectionStatus.Unsupported, ContainerImageManifestInspectionResult.Unsupported.Status);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("sha256:")]
    [InlineData("sha256:not-a-hex-digest")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha512:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ContainerImageManifestRejectsInvalidDigests(string digest)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ContainerImageManifest(digest, "linux", "amd64"));

        Assert.Equal("digest", exception.ParamName);
    }

    [Fact]
    public async Task DockerIgnoresManifestsWithInvalidDigests()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes("""
                [
                  {
                    "Descriptor": {
                      "digest": "sha256:not-a-hex-digest",
                      "platform": { "os": "linux", "architecture": "amd64" }
                    }
                  }
                ]
                """)), Emit<byte>.Exited(0)]));
        var runtime = new TestContainerRuntime(processRunner);

        var result = await runtime.InspectImageManifestAsync(
            "example/image:tag",
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Succeeded, result.Status);
        Assert.False(result.TryGetManifest("linux", "amd64", out _));
    }

    [Fact]
    public async Task PodmanInspectsRemoteImageManifestsUsingRegistryTransport()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, processRunner);
        const string maliciousImageName = "registry/image\\\" --help";

        await runtime.InspectImageManifestAsync(maliciousImageName, TestContext.Current.CancellationToken);
        await runtime.InspectImageManifestAsync("docker://registry/image:tag", TestContext.Current.CancellationToken);

        Assert.Collection(
            processRunner.Commands.Select(command => command.Arguments),
            arguments => Assert.Equal(["manifest", "inspect", $"docker://{maliciousImageName}"], arguments),
            arguments => Assert.Equal(["manifest", "inspect", "docker://registry/image:tag"], arguments));
    }

    [Fact]
    public async Task PodmanReturnsTypedManifestFromNativeIndex()
    {
        var processRunner = new TestProcessRunner();
        var json = """
                {
                  "manifests": [
                    {
                      "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                      "platform": { "os": "linux", "architecture": "amd64" }
                    },
                    {
                      "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                      "platform": { "os": "linux", "architecture": "arm64" }
                    }
                  ]
                }
                """ + "\r\n\n \t\n";
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes(json)), Emit<byte>.Exited(0)]));
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, processRunner);

        var result = await runtime.InspectImageManifestAsync(
            "registry/image:tag",
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Succeeded, result.Status);
        Assert.True(result.TryGetManifest("linux", "amd64", out var manifest));
        Assert.Equal("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", manifest.Digest);
        Assert.False(result.TryGetManifest("windows", "amd64", out _));
        Assert.Equal(json, result.RawJson);
    }

    [Fact]
    public async Task PodmanResolvesDigestForPlainSingleImageManifest()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes("""{ "schemaVersion": 2, "config": { "digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" }, "layers": [] }""")), Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes("""{ "Digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Os": "linux", "Architecture": "amd64" }""")), Emit<byte>.Exited(0)]));
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, processRunner);
        const string maliciousImageName = "registry/image\\\" --help:tag";

        var result = await runtime.InspectImageManifestAsync(
            $"docker://{maliciousImageName}",
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Succeeded, result.Status);
        Assert.True(result.TryGetManifest("linux", "amd64", out var manifest));
        Assert.Equal("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", manifest.Digest);
        Assert.Equal("linux", manifest.OperatingSystem);
        Assert.Equal("amd64", manifest.Architecture);
        Assert.Collection(
            processRunner.Commands.Select(command => command.Arguments),
            arguments => Assert.Equal(["manifest", "inspect", $"docker://{maliciousImageName}"], arguments),
            arguments => Assert.Equal(["pull", $"docker://{maliciousImageName}"], arguments),
            arguments => Assert.Equal(
                [
                    "image",
                    "inspect",
                    "--format",
                    """{"Digest":{{json .Digest}},"Os":{{json .Os}},"Architecture":{{json .Architecture}}}""",
                    maliciousImageName
                ],
                arguments));
    }

    [Fact]
    public async Task PodmanFailsInspectionWhenImageMetadataHasInvalidDigest()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes("""{ "schemaVersion": 2, "config": { "digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" }, "layers": [] }""")), Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes("""{ "Digest": "sha256:not-a-hex-digest", "Os": "linux", "Architecture": "amd64" }""")), Emit<byte>.Exited(0)]));
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, processRunner);

        var result = await runtime.InspectImageManifestAsync(
            "registry/image:tag",
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Failed, result.Status);
        Assert.Equal("Podman did not return an immutable digest for image 'registry/image:tag'.", result.ErrorMessage);
        Assert.False(result.TryGetManifest("linux", "amd64", out _));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "Digest": 123, "Os": "linux", "Architecture": "amd64" }""")]
    public async Task PodmanFailsInspectionWhenImageMetadataIsMalformed(string imageMetadata)
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes("""{ "schemaVersion": 2, "config": { "digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" }, "layers": [] }""")), Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout(Encoding.UTF8.GetBytes(imageMetadata)), Emit<byte>.Exited(0)]));
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, processRunner);

        var result = await runtime.InspectImageManifestAsync(
            "registry/image:tag",
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Failed, result.Status);
        Assert.StartsWith("Podman returned invalid image metadata for 'registry/image:tag':", result.ErrorMessage);
        Assert.False(result.TryGetManifest("linux", "amd64", out _));
    }

    [Fact]
    public async Task Commands_LogLiveNonemptyLinesBeforeDisposal()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Stdout("first\n\n \t\r\n"u8),
            Emit<byte>.Stderr("warning\n\n"u8),
            Emit<byte>.Wait(release.Task),
            Emit<byte>.Exited(0)
        ]);
        var runner = new TestProcessRunner();
        runner.Enqueue(script);
        var logger = new FakeLogger<PodmanContainerRuntime>();
        var runtime = new PodmanContainerRuntime(logger, runner);

        var operation = runtime.TagImageAsync("image", "next", TestContext.Current.CancellationToken);
        try
        {
            Assert.False(operation.IsCompleted);
            Assert.Equal(0, script.DisposeCount);
            Assert.Equal(
                ["podman (stdout): first", "podman (stdout):  \t", "podman (stderr): warning"],
                logger.Collector.GetSnapshot().Where(record => record.Message.Contains(" (std", StringComparison.Ordinal)).Select(record => record.Message));
        }
        finally
        {
            release.TrySetResult();
        }
        await operation.DefaultTimeout();
        Assert.Equal(1, script.DisposeCount);
    }

    [Fact]
    public async Task BuildFailure_RetainsLast50Of51NonemptyLinesIncludingPartialFinalLine()
    {
        var lines = Enumerable.Range(1, 49).Select(index => $"line-{index}").ToArray();
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Stdout(Encoding.UTF8.GetBytes(string.Join("\n\n", lines) + "\n")),
            Emit<byte>.Stderr("\n \t\npartial-final"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(23)
        ]);
        var runner = new TestProcessRunner();
        runner.Enqueue(script);
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, runner);

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(() => runtime.BuildImageAsync(
            "context", "Dockerfile", new ContainerImageBuildOptions { ImageName = "image" }, [], [], null,
            TestContext.Current.CancellationToken));

        Assert.Equal(1, script.DisposeCount);
        Assert.Equal(23, exception.ExitCode);
        Assert.Equal(51, exception.TotalProcessOutputLineCount);
        Assert.Equal([.. lines.Skip(1), " \t", "partial-final"], exception.ProcessOutput);
        Assert.Equal(
            "Podman build failed with exit code 23." + Environment.NewLine +
            "Process output truncated: showing last 50 of 51 lines." + Environment.NewLine +
            string.Join(Environment.NewLine, lines.Skip(1).Concat([" \t", "partial-final"])),
            exception.Message);
    }

    [Fact]
    public async Task Commands_ApplyNonzeroExitPolicyOnlyAfterDisposal()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeEventStream<byte>([Emit<byte>.Exited(17)], cleanup: release.Task);
        var runner = new TestProcessRunner();
        runner.Enqueue(script);
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, runner);

        var operation = runtime.RemoveImageAsync("image", TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(1, script.DisposeCount);
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => operation).DefaultTimeout();
        Assert.Equal("Podman rmi failed with exit code 17.", failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthProbes_PreserveFalseOnCancellation(bool docker)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeEventStream<byte>([Emit<byte>.Wait(release.Task), Emit<byte>.Exited(0)]);
        var runner = new TestProcessRunner();
        runner.Enqueue(script);
        var runtime = CreateRuntime(docker ? KnownContainerRuntimes.Docker : KnownContainerRuntimes.Podman, runner);
        using var cancellation = new CancellationTokenSource();

        var operation = runtime.CheckIfRunningAsync(cancellation.Token);
        try
        {
            Assert.Single(runner.Commands);
            Assert.False(operation.IsCompleted);
            await cancellation.CancelAsync();
            Assert.False(await operation.DefaultTimeout());
            Assert.Equal(1, script.DisposeCount);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Inspection_PropagatesCancellationTokenAfterDisposal()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeEventStream<byte>([Emit<byte>.Wait(release.Task), Emit<byte>.Exited(0)], cleanup: cleanup.Task);
        var runner = new TestProcessRunner();
        runner.Enqueue(script);
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, runner);
        using var cancellation = new CancellationTokenSource();

        var operation = runtime.InspectImageConfigAsync("image", cancellation.Token);
        try
        {
            Assert.Single(runner.Commands);
            Assert.False(operation.IsCompleted);
            Assert.Equal(0, script.DisposeCount);
            await cancellation.CancelAsync();
            Assert.False(operation.IsCompleted);
            cleanup.TrySetResult();
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation).DefaultTimeout();
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Equal(1, script.DisposeCount);
        }
        finally
        {
            release.TrySetResult();
            cleanup.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 13)]
    public async Task ComposeUp_PreservesProjectFilesAndFailureDiagnostics(bool envFileExists, int exitCode)
    {
        var directory = Directory.CreateTempSubdirectory("aspire-compose-");
        try
        {
            var envPath = Path.Combine(directory.FullName, "app env.txt");
            if (envFileExists)
            {
                await File.WriteAllTextAsync(envPath, "SETTING=value", TestContext.Current.CancellationToken);
            }
            Assert.Equal(envFileExists, File.Exists(envPath));
            var runner = new TestProcessRunner();
            runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
            var script = new FakeEventStream<byte>(
            [
                Emit<byte>.Stdout("creating service\n"u8),
                Emit<byte>.Stderr("compose diagnostic"u8),
                Emit<byte>.StdoutCompleted(),
                Emit<byte>.StderrCompleted(),
                Emit<byte>.Exited(exitCode)
            ]);
            runner.Enqueue(script);
            var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);
            var composePath = Path.Combine(directory.FullName, "app compose.yaml");
            var context = new ComposeOperationContext
            {
                ProjectName = "project name",
                WorkingDirectory = directory.FullName,
                ComposeFilePath = composePath,
                EnvFilePath = envPath
            };

            var operation = runtime.ComposeUpAsync(context, TestContext.Current.CancellationToken);
            if (exitCode == 0)
            {
                await operation;
            }
            else
            {
                var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => operation);
                Assert.StartsWith("'docker compose up' failed with exit code 13.", failure.Message);
                Assert.EndsWith(Environment.NewLine + "creating service" + Environment.NewLine + "compose diagnostic", failure.Message);
            }
            Assert.Equal(1, script.DisposeCount);
            var expected = new List<string> { "compose", "-f", composePath, "--project-name", "project name" };
            if (envFileExists)
            {
                expected.AddRange(["--env-file", envPath]);
            }
            expected.AddRange(["up", "-d", "--remove-orphans"]);
            Assert.Equal(expected, runner.Commands[1].Arguments);
            Assert.Equal(directory.FullName, runner.Commands[1].WorkingDirectory);
            Assert.Equal(2, runner.Commands.Count);
            Assert.Empty(runner.ProcessSpecs);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Login_SendsUtf8PasswordWithNewlineWithoutExposingItInArgumentsOrLogs()
    {
        const string password = "päss🔒 word";
        var script = new FakeEventStream<byte>([Emit<byte>.Stderr("registry unavailable\n"u8), Emit<byte>.Exited(7)]);
        var runner = new TestProcessRunner();
        runner.Enqueue(script);
        var logger = new FakeLogger<PodmanContainerRuntime>();
        var runtime = new PodmanContainerRuntime(logger, runner);

        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.LoginToRegistryAsync(
            "registry.example.com", "user with spaces", password, TestContext.Current.CancellationToken));

        var command = Assert.Single(runner.Commands);
        Assert.Equal(["login", "--username", "user with spaces", "--password-stdin", "registry.example.com"], command.Arguments);
        Assert.Equal(Encoding.UTF8.GetBytes(password + Environment.NewLine), Assert.IsType<ProcessInput>(command.Input).Bytes.ToArray());
        Assert.All(logger.Collector.GetSnapshot(), record => Assert.False(record.Message.Contains(password, StringComparison.Ordinal)));
        Assert.Equal("Podman login failed with exit code 7." + Environment.NewLine + "registry unavailable", failure.Message);
        Assert.Equal(1, script.DisposeCount);
    }

    [Fact]
    public async Task ComposeDown_FailureIncludesEveryNonWhitespaceStderrLineBeyond50()
    {
        var lines = Enumerable.Range(1, 51).Select(index => $"error-{index}").ToArray();
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Stdout("not an error\n"u8),
            Emit<byte>.Stderr(Encoding.UTF8.GetBytes("\n \t\n" + string.Join("\r\n\r\n", lines))),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(9)
        ]);
        runner.Enqueue(script);
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);
        var context = new ComposeOperationContext { ProjectName = "app", WorkingDirectory = "out" };

        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.ComposeDownAsync(context, TestContext.Current.CancellationToken));

        Assert.Equal("'docker compose down' failed with exit code 9. " + string.Join(" ", lines), failure.Message);
        Assert.Equal(1, script.DisposeCount);
        Assert.Equal(["compose", "--project-name", "app", "down"], runner.Commands[1].Arguments);
        Assert.Equal("out", runner.Commands[1].WorkingDirectory);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 19)]
    [InlineData(true, 0)]
    [InlineData(true, 19)]
    public async Task ComposeList_LogsNativeStderrLiveAndReturnsNullOnNonzero(bool podman, int exitCode)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var json = podman
            ? "[{\"Labels\":{\"com.docker.compose.service\":\"web\"},\"Ports\":[{\"container_port\":80,\"host_port\":8080}]}]\n"
            : "{\"Service\":\"web\",\"Publishers\":[{\"TargetPort\":80,\"PublishedPort\":8080}]}\n";
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Stdout(Encoding.UTF8.GetBytes(json)),
            Emit<byte>.Stderr("\n \t\nprovider warning\n"u8),
            Emit<byte>.Wait(release.Task),
            Emit<byte>.Exited(exitCode)
        ]);
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        runner.Enqueue(script);
        var podmanLogger = new FakeLogger<PodmanContainerRuntime>();
        var dockerLogger = new FakeLogger<DockerContainerRuntime>();
        IContainerRuntime runtime = podman ? new PodmanContainerRuntime(podmanLogger, runner) : new DockerContainerRuntime(dockerLogger, runner);
        var context = new ComposeOperationContext { ProjectName = "app", WorkingDirectory = "output dir" };

        var operation = runtime.ComposeListServicesAsync(context, TestContext.Current.CancellationToken);
        try
        {
            Assert.False(operation.IsCompleted);
            Assert.Equal(0, script.DisposeCount);
            var records = podman ? podmanLogger.Collector.GetSnapshot() : dockerLogger.Collector.GetSnapshot();
            Assert.Equal([podman ? "podman ps (stderr): provider warning" : "docker compose ps (stderr): provider warning"],
                records.Where(record => record.Message.Contains("(stderr)", StringComparison.Ordinal)).Select(record => record.Message));
        }
        finally
        {
            release.TrySetResult();
        }
        var result = await operation.DefaultTimeout();
        Assert.Equal(1, script.DisposeCount);
        if (exitCode == 0)
        {
            var service = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ComposeServiceInfo>>(result));
            Assert.Equal("web", service.Service);
            var port = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ComposeServicePort>>(service.Publishers));
            Assert.Equal(80, port.TargetPort);
            Assert.Equal(8080, port.PublishedPort);
        }
        else
        {
            Assert.Null(result);
        }
        Assert.Equal("output dir", runner.Commands[1].WorkingDirectory);
        Assert.Equal(podman ? ["ps", "--filter", "label=com.docker.compose.project=app", "--format", "json"] :
            new[] { "compose", "--project-name", "app", "ps", "--format", "json" }, runner.Commands[1].Arguments);
        Assert.Equal(OperatingSystem.IsWindows() ? "where" : "which", runner.Commands[0].ExecutablePath);
        Assert.Equal([podman ? "podman" : "docker"], runner.Commands[0].Arguments);
    }

    [Theory]
    [InlineData(" registry with spaces\t", " team user\t")]
    [InlineData("registry\\\"quoted", "DOMAIN\\\"user")]
    public async Task LoginToRegistryAsync_PreservesLiteralArgumentValues(string registryServer, string username)
    {
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);
        const string password = "päss word";

        await runtime.LoginToRegistryAsync(registryServer, username, password, TestContext.Current.CancellationToken);

        var command = Assert.Single(runner.Commands);
        Assert.Equal("docker", command.ExecutablePath);
        Assert.Equal(["login", "--username", username, "--password-stdin", registryServer], command.Arguments);
        Assert.Equal(Encoding.UTF8.GetBytes(password + Environment.NewLine), Assert.IsType<ProcessInput>(command.Input).Bytes.ToArray());
        Assert.Empty(runner.ProcessSpecs);
    }

    [Theory]
    [InlineData(" local image\t", " remote image\t")]
    [InlineData("local\\\"quoted", "remote\\\"quoted")]
    public async Task TagImageAsync_PreservesLiteralArgumentValues(string localImageName, string targetImageName)
    {
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);

        await runtime.TagImageAsync(localImageName, targetImageName, TestContext.Current.CancellationToken);

        var command = Assert.Single(runner.Commands);
        Assert.Equal("docker", command.ExecutablePath);
        Assert.Equal(["tag", localImageName, targetImageName], command.Arguments);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Theory]
    [InlineData(" image with spaces\t")]
    [InlineData("image\\\"quoted")]
    public async Task RemoveImageAsync_PreservesLiteralArgumentValue(string imageName)
    {
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);

        await runtime.RemoveImageAsync(imageName, TestContext.Current.CancellationToken);

        var command = Assert.Single(runner.Commands);
        Assert.Equal("docker", command.ExecutablePath);
        Assert.Equal(["rmi", imageName], command.Arguments);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Theory]
    [InlineData("up", "project\"quoted\"", "compose \"quoted\".yaml", true)]
    [InlineData("down", @"project trailing\", @"C:\compose files\", false)]
    [InlineData("list", "project with spaces\t", "compose file.yaml", false)]
    public async Task ComposeOperations_PreserveLiteralProjectAndFileArguments(string operation, string projectName, string composePath, bool includeEnvFile)
    {
        var directory = Directory.CreateTempSubdirectory("aspire-compose-argv-");
        try
        {
            var envPath = Path.Combine(directory.FullName, "app env.txt");
            if (includeEnvFile)
            {
                await File.WriteAllTextAsync(envPath, "SETTING=value", TestContext.Current.CancellationToken);
            }
            Assert.Equal(includeEnvFile, File.Exists(envPath));
            var runner = new TestProcessRunner();
            runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
            runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
            var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);
            var context = new ComposeOperationContext
            {
                ProjectName = projectName,
                // The fake records these literal paths without opening them; quotes are valid boundary data even on Windows.
                ComposeFilePath = composePath,
                WorkingDirectory = "working \"quoted\" directory\\",
                EnvFilePath = envPath
            };

            switch (operation)
            {
                case "up":
                    await runtime.ComposeUpAsync(context, TestContext.Current.CancellationToken);
                    break;
                case "down":
                    await runtime.ComposeDownAsync(context, TestContext.Current.CancellationToken);
                    break;
                case "list":
                    Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ComposeServiceInfo>>(
                        await runtime.ComposeListServicesAsync(context, TestContext.Current.CancellationToken)));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }

            var expected = new List<string> { "compose", "-f", composePath, "--project-name", projectName };
            if (includeEnvFile)
            {
                expected.AddRange(["--env-file", envPath]);
            }
            expected.AddRange(operation switch
            {
                "up" => ["up", "-d", "--remove-orphans"],
                "down" => ["down"],
                "list" => ["ps", "--format", "json"],
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            });
            Assert.Collection(runner.Commands,
                command =>
                {
                    Assert.Equal(OperatingSystem.IsWindows() ? "where" : "which", command.ExecutablePath);
                    Assert.Equal(["docker"], command.Arguments);
                },
                command =>
                {
                    Assert.Equal("docker", command.ExecutablePath);
                    Assert.Equal(expected, command.Arguments);
                    Assert.Equal(context.WorkingDirectory, command.WorkingDirectory);
                });
            Assert.Empty(runner.ProcessSpecs);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(" project with spaces\t")]
    [InlineData("project\\\"quoted")]
    public async Task PodmanComposeList_PreservesProjectLabelAsSingleLiteralFilter(string projectName)
    {
        var runner = new TestProcessRunner();
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        runner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout("[]\n"u8), Emit<byte>.StdoutCompleted(), Emit<byte>.Exited(0)]));
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, runner);
        var context = new ComposeOperationContext { ProjectName = projectName, WorkingDirectory = @"working directory\" };

        var result = await runtime.ComposeListServicesAsync(context, TestContext.Current.CancellationToken);

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ComposeServiceInfo>>(result));
        Assert.Collection(runner.Commands,
            command =>
            {
                Assert.Equal(OperatingSystem.IsWindows() ? "where" : "which", command.ExecutablePath);
                Assert.Equal(["podman"], command.Arguments);
            },
            command =>
            {
                Assert.Equal("podman", command.ExecutablePath);
                Assert.Equal(["ps", "--filter", $"label=com.docker.compose.project={projectName}", "--format", "json"], command.Arguments);
                Assert.Equal(context.WorkingDirectory, command.WorkingDirectory);
            });
        Assert.Empty(runner.ProcessSpecs);
    }

    private sealed class TestContainerRuntime(IProcessRunner? processRunner = null, string? runtimeExecutable = null) : ContainerRuntimeBase<TestContainerRuntime>(NullLogger<TestContainerRuntime>.Instance, processRunner ?? new DefaultProcessRunner())
    {
        protected override string RuntimeExecutable => runtimeExecutable ?? (OperatingSystem.IsWindows() ? "cmd" : "sh");

        public override string Name => "test-runtime";

        public override Task<bool> CheckIfRunningAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public override Task BuildImageAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task RunFailingCommandAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteContainerCommandAsync(
                OperatingSystem.IsWindows()
                    ? ["/c", "echo stdout-final-line & echo stderr-final-line 1>&2 & exit /b 1"]
                    : ["-c", "echo stdout-final-line; echo stderr-final-line 1>&2; exit 1"],
                "Test command failed with exit code {ExitCode}.",
                "Test command succeeded.",
                "Test command failed with exit code {0}.",
                cancellationToken);
        }

        public Task<string> RunFileForOutputAsync(string path, CancellationToken cancellationToken)
        {
            return ExecuteContainerCommandForOutputAsync(
                OperatingSystem.IsWindows() ? ["/c", "type", path] : ["-c", "cat -- \"$1\"", "sh", path],
                "test exact output",
                "test-image",
                cancellationToken);
        }

        public Task<string> RunCommandForOutputAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteContainerCommandForOutputAsync(
                OperatingSystem.IsWindows()
                    ? ["/c", "echo stdout-only& echo stderr-line 1>&2"]
                    : ["-c", "echo stdout-only; echo stderr-line 1>&2"],
                "test output",
                "test-image",
                cancellationToken);
        }
    }

    private sealed class UnsupportedInspectionContainerRuntime : IContainerRuntime
    {
        public string Name => "unsupported-inspection";

        public Task<bool> CheckIfRunningAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task BuildImageAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task TagImageAsync(string localImageName, string targetImageName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveImageAsync(string imageName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PushImageAsync(IResource resource, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task LoginToRegistryAsync(string registryServer, string username, string password, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ComposeUpAsync(ComposeOperationContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ComposeDownAsync(ComposeOperationContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ComposeServiceInfo>?> ComposeListServicesAsync(ComposeOperationContext context, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ComposeServiceInfo>?>(null);
    }

}
