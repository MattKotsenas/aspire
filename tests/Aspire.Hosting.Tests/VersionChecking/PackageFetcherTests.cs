// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Hosting.VersionChecking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using TestLoggerFactory = Microsoft.Extensions.Logging.Testing.TestLoggerFactory;

namespace Aspire.Hosting.Tests.VersionChecking;

[Trait("Partition", "4")]
public class PackageFetcherTests(ITestOutputHelper outputHelper)
{
    private const string PackageSearchJson = """
        {
          "version": 2,
          "problems": [],
          "searchResult": [{
            "sourceName": "https://例.example/nuget/v3/index.json",
            "packages": [
              { "id": "Aspire.Hosting.AppHost", "latestVersion": "13.4.0" },
              { "id": "Other.Package", "latestVersion": "99.0.0" }
            ]
          }]
        }
        """;

    [Fact]
    public async Task FetchUsesInjectedRunnerAndReturnsMatchingPackages()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create();
        var runner = new TestProcessRunner();
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout(Encoding.UTF8.GetBytes(PackageSearchJson)),
            Emit<byte>.Stderr("credential provider diagnostic, not JSON\n"u8),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]);
        runner.Enqueue(script);
        builder.Services.AddSingleton<IProcessRunner>(runner);
        using var app = builder.Build();
        var fetcher = app.Services.GetRequiredService<IPackageFetcher>();
        Assert.IsType<PackageFetcher>(fetcher);
        Assert.Empty(runner.Commands);
        Assert.Equal(0, script.DisposeCount);

        var packages = await fetcher.TryFetchPackagesAsync(workspace.Path, TestContext.Current.CancellationToken);

        Assert.Equal(
            [("Aspire.Hosting.AppHost", "13.4.0", "https://例.example/nuget/v3/index.json")],
            packages.Select(static package => (package.Id, package.Version, package.Source)));
        var command = Assert.Single(runner.Commands);
        Assert.Equal("dotnet", command.ExecutablePath);
        Assert.Equal(
            ["package", "search", PackageFetcher.PackageId, "--format", "json", "--prerelease", "--take", "1000"],
            command.Arguments);
        Assert.Equal(workspace.Path, command.WorkingDirectory);
        Assert.True(command.InheritEnvironmentVariables);
        Assert.Equal(1, script.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchLogsNonemptyLinesLiveWithoutDroppingWhitespace(bool stderr)
    {
        var sink = new TestSink();
        using var loggerFactory = new TestLoggerFactory(sink, enabled: true);
        var runner = new TestProcessRunner();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[] lines = ["", " \t", stderr ? "credential provider diagnostic" : "{}", ""];
        Assert.Equal(2, lines.Count(string.IsNullOrEmpty));
        var diagnostics = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            stderr ? Emit<byte>.Stderr(diagnostics) : Emit<byte>.Stdout(diagnostics),
            Emit<byte>.Wait(completion.Task),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(1)
        ]);
        runner.Enqueue(script);
        var fetcher = new PackageFetcher(runner, new TestLogger<PackageFetcher>(loggerFactory));
        Assert.Empty(sink.Writes);
        Assert.Equal(0, script.DisposeCount);

        var fetch = fetcher.TryFetchPackagesAsync(Environment.CurrentDirectory, TestContext.Current.CancellationToken);
        try
        {
            Assert.False(fetch.IsCompleted);
            var prefix = $"dotnet ({(stderr ? "stderr" : "stdout")}): ";
            Assert.Equal(lines.Where(line => !string.IsNullOrEmpty(line)).Select(line => prefix + line),
                sink.Writes.Where(write => write.Message?.StartsWith(prefix, StringComparison.Ordinal) == true).Select(write => write.Message));
        }
        finally
        {
            completion.SetResult();
        }

        Assert.Empty(await fetch);
        Assert.Equal(1, script.DisposeCount);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(null, false)]
    [InlineData(0, true)]
    public async Task FetchWaitsForTerminalCleanupBeforeParsingOrApplyingExitPolicy(int? exitCode, bool failCleanup)
    {
        var runner = new TestProcessRunner();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("package-search cleanup failed");
        List<StreamStep<byte>> steps =
        [
            Emit<byte>.Started(1),
            Emit<byte>.Stdout(Encoding.UTF8.GetBytes(exitCode == 1 ? "invalid JSON from failed command" : PackageSearchJson)),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted()
        ];
        if (exitCode is { } knownExit)
        {
            steps.Add(Emit<byte>.Exited(knownExit));
        }
        var script = new FakeEventStream<byte>(steps, cleanupFailure: failCleanup ? failure : null, cleanup: cleanup.Task);
        runner.Enqueue(script);
        var fetcher = new PackageFetcher(runner, NullLogger<PackageFetcher>.Instance);
        Assert.Empty(runner.Commands);
        Assert.Equal(0, script.DisposeCount);

        var fetch = fetcher.TryFetchPackagesAsync(Environment.CurrentDirectory, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(1, script.DisposeCount);
            Assert.False(fetch.IsCompleted);
        }
        finally
        {
            cleanup.SetResult();
        }

        if (failCleanup)
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => fetch));
        }
        else if (exitCode is null)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fetch);
            Assert.Equal("The process event stream completed without an exit code.", exception.Message);
        }
        else
        {
            var packages = await fetch;
            if (exitCode == 0)
            {
                Assert.Equal("13.4.0", Assert.Single(packages).Version);
            }
            else
            {
                Assert.Empty(packages);
            }
        }
        Assert.Equal(1, script.DisposeCount);
    }

    [Fact]
    public async Task CancellationPropagatesCallerTokenAfterDisposal()
    {
        var runner = new TestProcessRunner();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeEventStream<byte>(
        [
            Emit<byte>.Started(1),
            Emit<byte>.Wait(completion.Task)
        ]);
        runner.Enqueue(script);
        var fetcher = new PackageFetcher(runner, NullLogger<PackageFetcher>.Instance);
        using var cancellation = new CancellationTokenSource();
        Assert.Empty(runner.Commands);
        Assert.Equal(0, script.DisposeCount);

        var fetch = fetcher.TryFetchPackagesAsync(Environment.CurrentDirectory, cancellation.Token);
        Assert.False(fetch.IsCompleted);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(completion.Task.IsCompleted);
        Assert.Equal(1, script.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionAndParsingFailuresPropagateAfterDisposal(bool launchFailure)
    {
        var runner = new TestProcessRunner();
        var failure = new Win32Exception(2, "executable missing");
        var script = new FakeEventStream<byte>(launchFailure
            ? [Emit<byte>.Fail(failure)]
            : [Emit<byte>.Started(1), Emit<byte>.Stdout("not JSON"u8), Emit<byte>.StdoutCompleted(), Emit<byte>.StderrCompleted(), Emit<byte>.Exited(0)]);
        runner.Enqueue(script);
        var fetcher = new PackageFetcher(runner, NullLogger<PackageFetcher>.Instance);
        Assert.Empty(runner.Commands);
        Assert.Equal(0, script.DisposeCount);

        if (launchFailure)
        {
            Assert.Same(failure, await Assert.ThrowsAsync<Win32Exception>(
                () => fetcher.TryFetchPackagesAsync(Environment.CurrentDirectory, TestContext.Current.CancellationToken)));
        }
        else
        {
            await Assert.ThrowsAnyAsync<JsonException>(
                () => fetcher.TryFetchPackagesAsync(Environment.CurrentDirectory, TestContext.Current.CancellationToken));
        }

        Assert.Single(runner.Commands);
        Assert.Equal(1, script.DisposeCount);
    }
}
