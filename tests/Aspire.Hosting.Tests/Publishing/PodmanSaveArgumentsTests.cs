// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Publishing;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Publishing;

public class PodmanSaveArgumentsTests
{
    [Fact]
    public void BuildSaveArguments_OciFormat_UsesOciArchive()
    {
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = Path.Combine("out", "archives"),
            ImageFormat = ContainerImageFormat.Oci
        };

        var arguments = PodmanContainerRuntime.BuildSaveArguments("myapp:latest", options);

        var expectedPath = Path.Combine("out", "archives", "myapp-latest.tar");
        Assert.Equal(["save", "--format", "oci-archive", "--output", expectedPath, "myapp:latest"], arguments);
    }

    [Fact]
    public void BuildSaveArguments_DockerFormat_UsesDockerArchive()
    {
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = Path.Combine("out", "archives"),
            ImageFormat = ContainerImageFormat.Docker
        };

        var arguments = PodmanContainerRuntime.BuildSaveArguments("myapp:latest", options);

        var expectedPath = Path.Combine("out", "archives", "myapp-latest.tar");
        Assert.Equal(["save", "--format", "docker-archive", "--output", expectedPath, "myapp:latest"], arguments);
    }

    [Fact]
    public void BuildSaveArguments_UnspecifiedFormat_DefaultsToDockerArchive()
    {
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = Path.Combine("out", "archives")
        };

        var arguments = PodmanContainerRuntime.BuildSaveArguments("myapp:latest", options);

        var expectedPath = Path.Combine("out", "archives", "myapp-latest.tar");
        Assert.Equal(["save", "--format", "docker-archive", "--output", expectedPath, "myapp:latest"], arguments);
    }

    [Fact]
    public void BuildSaveArguments_ArchivePathMatchesDockerRuntime()
    {
        // Both runtimes must write the same archive file for the same resource, otherwise consumers that
        // resolve the archive path cannot find what Podman produced.
        var outputPath = Path.Combine("out", "archives");
        var options = new ContainerImageBuildOptions
        {
            ImageName = "registry.example.com/team/myapp",
            Tag = "1.2.3",
            OutputPath = outputPath,
            ImageFormat = ContainerImageFormat.Oci
        };

        var arguments = PodmanContainerRuntime.BuildSaveArguments("registry.example.com/team/myapp:1.2.3", options);

        var dockerArchivePath = ResourceExtensions.GetContainerImageArchivePath(outputPath, "registry.example.com/team/myapp:1.2.3");
        Assert.Equal(["save", "--format", "oci-archive", "--output", dockerArchivePath, "registry.example.com/team/myapp:1.2.3"], arguments);
    }

    [Fact]
    public async Task BuildSaveArguments_ArchivePathMatchesContainerImageReference()
    {
        // The runtimes write the archive, but ContainerImageReference is what tells consumers where to
        // find it, so the producer and the consumer have to resolve the same file. A registry-qualified
        // image name is the case where they used to diverge: the runtimes flattened the repository
        // segments while ContainerImageReference left the '/' in place, pointing consumers at a nested
        // path nothing ever wrote.
        var outputPath = Path.Combine("out", "archives");
        const string ImageName = "registry.example.com/team/myapp";
        const string ImageTag = "1.2.3";

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var container = builder.AddContainer("myapp", ImageName)
            .WithContainerBuildOptions(ctx =>
            {
                ctx.Destination = ContainerImageDestination.Archive;
                ctx.ImageFormat = ContainerImageFormat.Oci;
                ctx.OutputPath = outputPath;
                ctx.LocalImageName = ImageName;
                ctx.LocalImageTag = ImageTag;
            });

        using var app = builder.Build();

        IValueProvider imageReference = new ContainerImageReference(container.Resource);
        var consumerPath = await imageReference.GetValueAsync(
            new ValueProviderContext { ExecutionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>() },
            CancellationToken.None);
        Assert.NotNull(consumerPath);

        var options = new ContainerImageBuildOptions
        {
            ImageName = ImageName,
            Tag = ImageTag,
            OutputPath = outputPath,
            ImageFormat = ContainerImageFormat.Oci
        };

        var arguments = PodmanContainerRuntime.BuildSaveArguments($"{ImageName}:{ImageTag}", options);

        Assert.Equal(
            ["save", "--format", "oci-archive", "--output", consumerPath, $"{ImageName}:{ImageTag}"],
            arguments);
    }

    [Fact]
    public void BuildSaveArguments_TagIsPreservedInArchiveName()
    {
        // Dropping the tag would place the archive at a path nothing else resolves to.
        var options = new ContainerImageBuildOptions
        {
            ImageName = "container",
            Tag = "abc123",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Oci
        };

        var arguments = PodmanContainerRuntime.BuildSaveArguments("container:abc123", options);

        Assert.Equal(["save", "--format", "oci-archive", "--output", Path.Combine("out", "container-abc123.tar"), "container:abc123"], arguments);
    }

    [Theory]
    [InlineData("registry.example.com/team/app \"quoted\":tag", "registry.example.com-team-app \"quoted\"-tag.tar")]
    [InlineData(@"registry.example.com/team/app\", @"registry.example.com-team-app\.tar")]
    public void BuildSaveArguments_PreservesLiteralImageAndArchivePath(string imageName, string archiveFileName)
    {
        var outputPath = Path.Combine("out", "archives with spaces") + Path.DirectorySeparatorChar;
        var options = new ContainerImageBuildOptions
        {
            ImageName = imageName,
            OutputPath = outputPath,
            ImageFormat = ContainerImageFormat.Docker
        };
        // OutputPath is a portable directory path. Image references are opaque boundary data;
        // these pure argument builders do not create the derived archive, even for quoted names.
        var expectedPath = Path.Combine(outputPath, archiveFileName);

        var dockerArguments = DockerContainerRuntime.BuildSaveArguments(imageName, options);
        var podmanArguments = PodmanContainerRuntime.BuildSaveArguments(imageName, options);

        Assert.Equal(["image", "save", "--output", expectedPath, imageName], dockerArguments);
        Assert.Equal(["save", "--format", "docker-archive", "--output", expectedPath, imageName], podmanArguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task BuildImageAsync_OciFormatWithoutOutputPath_ThrowsBeforeBuild(string? outputPath)
    {
        var processRunner = new TestProcessRunner();
        var runtime = CreateRuntime(processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = outputPath,
            ImageFormat = ContainerImageFormat.Oci
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => BuildImageAsync(runtime, options));

        Assert.Equal("options", exception.ParamName);
        Assert.Empty(processRunner.Commands);
        Assert.Empty(processRunner.ProcessSpecs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task BuildImageAsync_WithoutOutputPath_DoesNotSave(string? outputPath)
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = CreateRuntime(processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = outputPath,
            ImageFormat = ContainerImageFormat.Docker
        };

        await BuildImageAsync(runtime, options);

        var build = Assert.Single(processRunner.Commands);
        Assert.Equal("podman", build.ExecutablePath);
        Assert.Equal("build", build.Arguments[0]);
    }

    [Fact]
    public async Task BuildImageAsync_WithOutputPath_BuildsThenSaves()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        var runtime = CreateRuntime(processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Oci
        };

        await BuildImageAsync(runtime, options);

        Assert.All(processRunner.Commands, command => Assert.Equal("podman", command.ExecutablePath));
        Assert.Collection(
            processRunner.Commands,
            build => Assert.Equal("build", build.Arguments[0]),
            save => Assert.Equal(
                ["save", "--format", "oci-archive", "--output", Path.Combine("out", "myapp-latest.tar"), "myapp:latest"],
                save.Arguments));
    }

    [Fact]
    public async Task BuildImageAsync_WhenBuildFails_DoesNotSave()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout("build failed\n"u8), Emit<byte>.Exited(17)]));
        var runtime = CreateRuntime(processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Oci
        };

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(() => BuildImageAsync(runtime, options));

        Assert.Equal(17, exception.ExitCode);
        Assert.Contains("build failed", exception.Message);
        Assert.Equal(["build failed"], exception.ProcessOutput);
        var build = Assert.Single(processRunner.Commands);
        Assert.Equal("build", build.Arguments[0]);
    }

    [Fact]
    public async Task BuildImageAsync_WhenSaveFails_ThrowsProcessFailedException()
    {
        var processRunner = new TestProcessRunner();
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Exited(0)]));
        processRunner.Enqueue(new FakeEventStream<byte>([Emit<byte>.Stdout("save failed\n"u8), Emit<byte>.Exited(23)]));
        var runtime = CreateRuntime(processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Oci
        };

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(() => BuildImageAsync(runtime, options));

        Assert.Equal(23, exception.ExitCode);
        Assert.Contains("save failed", exception.Message);
        Assert.Equal(["save failed"], exception.ProcessOutput);
        Assert.Collection(
            processRunner.Commands,
            build => Assert.Equal("build", build.Arguments[0]),
            save => Assert.Equal(
                ["save", "--format", "oci-archive", "--output", Path.Combine("out", "myapp-latest.tar"), "myapp:latest"],
                save.Arguments));
    }

    private static PodmanContainerRuntime CreateRuntime(TestProcessRunner processRunner)
        => new(NullLogger<PodmanContainerRuntime>.Instance, processRunner);

    private static Task BuildImageAsync(PodmanContainerRuntime runtime, ContainerImageBuildOptions options)
        => runtime.BuildImageAsync(
            contextPath: "context",
            dockerfilePath: "Dockerfile",
            options: options,
            buildArguments: [],
            buildSecrets: [],
            stage: null,
            cancellationToken: CancellationToken.None);
}
