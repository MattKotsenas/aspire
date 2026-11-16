// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Text;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Azure.Tests;

public class BicepCliCompilerStreamingTests
{
    [Theory]
    [InlineData("bicep")]
    [InlineData("az")]
    public async Task LogsEachNonemptyLineBeforeCompilationCompletes(string tool)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeEventStream<byte>([
            Emit<byte>.Started(1),
            Emit<byte>.Stdout(Encoding.UTF8.GetBytes("  first 日本語\r\n\n \rsecond\n")),
            Emit<byte>.Stderr(Encoding.UTF8.GetBytes("warning\n\n  \r\n")),
            Emit<byte>.Wait(release.Task),
            Emit<byte>.Stdout(Encoding.UTF8.GetBytes("last")),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]);
        var sink = new TestSink();
        var (compiler, runner) = CreateCompiler(source, sink, tool);
        Assert.Empty(runner.ExecutedCommands);
        Assert.Equal(0, source.MoveNextCount);
        Assert.Empty(OutputLogs(sink));
        Assert.False(release.Task.IsCompleted);

        var compilation = compiler.CompileBicepToArmAsync("template.bicep", TestContext.Current.CancellationToken);
        try
        {
            // This synchronous scripted source is now blocked on Wait, so all preceding
            // payloads have crossed the adapters. No scheduler delay is used as evidence.
            Assert.True(source.MoveNextCount >= 4);
            Assert.False(compilation.IsCompleted);
            Assert.Equal(0, source.DisposeCount);
            Assert.Equal(new[]
            {
                (LogLevel.Debug, $"{tool} (stdout):   first 日本語"),
                (LogLevel.Debug, $"{tool} (stdout):  "),
                (LogLevel.Debug, $"{tool} (stdout): second"),
                (LogLevel.Debug, $"{tool} (stderr): warning"),
                (LogLevel.Debug, $"{tool} (stderr):   ")
            }, OutputLogs(sink));
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => compilation);
        }

        Assert.Equal("  first 日本語\r\n\n \rsecond\nlast", await compilation);
        Assert.Equal((LogLevel.Debug, $"{tool} (stdout): last"), OutputLogs(sink)[^1]);
        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(tool == "bicep" ? 1 : 2, runner.ExecutedCommands.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\r\n\n  \"name\": \"日本語 😀\",\n  \t\r\n  \"resources\": []\r\n}\n")]
    public async Task CapturesExactStdout(string output)
    {
        var source = new FakeEventStream<byte>([
            Emit<byte>.Started(1),
            Emit<byte>.Stdout(Encoding.UTF8.GetBytes(output)),
            Emit<byte>.Stderr(Encoding.UTF8.GetBytes("診断 😀\r\n")),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(0)
        ]);
        var sink = new TestSink();
        var (compiler, runner) = CreateCompiler(source, sink);
        Assert.Equal(0, source.MoveNextCount);
        Assert.Empty(runner.ExecutedCommands);

        Assert.Equal(output, await compiler.CompileBicepToArmAsync("template.bicep", TestContext.Current.CancellationToken));

        Assert.Equal(1, source.DisposeCount);
        Assert.Single(runner.ExecutedCommands);
        Assert.Equal((LogLevel.Debug, "bicep (stderr): 診断 😀"), Assert.Single(OutputLogs(sink), log => log.Message.StartsWith("bicep (stderr):", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PartialLogsSurviveReadFailure()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("read failed");
        var source = new FakeEventStream<byte>([
            Emit<byte>.Started(1),
            Emit<byte>.Stdout(Encoding.UTF8.GetBytes("partial output\n")),
            Emit<byte>.Stderr(Encoding.UTF8.GetBytes("partial diagnostic\n")),
            Emit<byte>.Wait(release.Task),
            Emit<byte>.Fail(failure)
        ]);
        var sink = new TestSink();
        var (compiler, runner) = CreateCompiler(source, sink);
        Assert.Empty(OutputLogs(sink));
        Assert.Equal(0, source.MoveNextCount);
        Assert.False(release.Task.IsCompleted);

        var compilation = compiler.CompileBicepToArmAsync("template.bicep", TestContext.Current.CancellationToken);
        try
        {
            Assert.True(source.MoveNextCount >= 4);
            Assert.False(compilation.IsCompleted);
            Assert.Equal(new[]
            {
                (LogLevel.Debug, "bicep (stdout): partial output"),
                (LogLevel.Debug, "bicep (stderr): partial diagnostic")
            }, OutputLogs(sink));
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => compilation);
        }

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => compilation));
        Assert.Equal(2, OutputLogs(sink).Length);
        Assert.Single(runner.ExecutedCommands);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData("bicep")]
    [InlineData("az")]
    public async Task NonzeroDecisionWaitsForDisposalAndIncludesBoundedCombinedDiagnosticTail(string tool)
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new List<StreamStep<byte>> { Emit<byte>.Started(1) };
        var error = new StringBuilder();
        var lines = new List<string>();
        for (var index = 0; index < 300; index++)
        {
            var line = $"{(index % 2 == 0 ? "out" : "err")}-{index:D3} 日本語";
            lines.Add(line);
            if (index % 2 == 0)
            {
                steps.Add(Emit<byte>.Stdout(Encoding.UTF8.GetBytes(line + "\r\n")));
            }
            else
            {
                error.Append(line).Append("\r\n");
                steps.Add(Emit<byte>.Stderr(Encoding.UTF8.GetBytes(line + "\r\n")));
            }
        }
        steps.Add(Emit<byte>.StdoutCompleted());
        steps.Add(Emit<byte>.StderrCompleted());
        steps.Add(Emit<byte>.Exited(17));
        var source = new FakeEventStream<byte>(steps, cleanup: cleanup.Task);
        var sink = new TestSink();
        var (compiler, runner) = CreateCompiler(source, sink, tool);
        Assert.Equal(300, lines.Count);
        Assert.Equal(0, source.DisposeCount);
        Assert.False(cleanup.Task.IsCompleted);

        var compilation = compiler.CompileBicepToArmAsync("broken template.bicep", TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(1, source.DisposeCount);
            Assert.False(compilation.IsCompleted);
            Assert.DoesNotContain(sink.Writes, write => write.LogLevel == LogLevel.Error);
        }
        finally
        {
            cleanup.TrySetResult();
            await Record.ExceptionAsync(() => compilation);
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => compilation);
        Assert.Equal($"Failed to compile bicep file: broken template.bicep{Environment.NewLine}{error.ToString().Trim()}", failure.Message);
        var cause = Assert.IsAssignableFrom<InvalidOperationException>(failure.InnerException);
        Assert.Contains(tool, cause.Message, StringComparison.Ordinal);
        Assert.Contains("build", cause.Message, StringComparison.Ordinal);
        Assert.Contains("broken template.bicep", cause.Message, StringComparison.Ordinal);
        Assert.Contains("--stdout", cause.Message, StringComparison.Ordinal);
        Assert.Contains("17", cause.Message, StringComparison.Ordinal);
        if (tool == "az")
        {
            Assert.Contains("--file", cause.Message, StringComparison.Ordinal);
        }
        var expectedTail = string.Join(Environment.NewLine, lines.Skip(250));
        var truncation = $"truncated: showing last 50 of 300 lines.{Environment.NewLine}";
        var tailStart = cause.Message.IndexOf(truncation, StringComparison.Ordinal);
        Assert.True(tailStart >= 0, cause.Message);
        Assert.Equal(expectedTail, cause.Message[(tailStart + truncation.Length)..]);
        Assert.Equal(300, OutputLogs(sink).Length);
        Assert.Equal(tool == "bicep" ? 1 : 2, runner.ExecutedCommands.Count);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task CleanupFailureKeepsSameInnerReferenceAndFullStderrContext()
    {
        var original = new InvalidOperationException("Original process context", new IOException("Underlying transfer"));
        var stderr = "\r\n  first diagnostic\rsecond diagnostic 日本語\n  \r\n";
        var source = new FakeEventStream<byte>([
            Emit<byte>.Started(1),
            Emit<byte>.Stdout(Encoding.UTF8.GetBytes("partial output\n")),
            Emit<byte>.Stderr(Encoding.UTF8.GetBytes(stderr)),
            Emit<byte>.StdoutCompleted(),
            Emit<byte>.StderrCompleted(),
            Emit<byte>.Exited(17)
        ], cleanupFailure: original);
        var (compiler, runner) = CreateCompiler(source, new TestSink());
        Assert.Equal(0, source.MoveNextCount);
        Assert.Empty(runner.ExecutedCommands);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => compiler.CompileBicepToArmAsync("broken template.bicep", TestContext.Current.CancellationToken));

        Assert.Same(original, failure.InnerException);
        Assert.Equal($"Failed to compile bicep file: broken template.bicep{Environment.NewLine}{stderr.Trim()}", failure.Message);
        Assert.Single(runner.ExecutedCommands);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("win32")]
    [InlineData("input")]
    [InlineData("native")]
    public async Task NativeFailuresPropagateWithoutCompilationWrapperOrFallback(string kind)
    {
        Exception original = kind switch
        {
            "io" => new IOException("Pipe read failed"),
            "win32" => new Win32Exception(5, "Access denied"),
            "input" => new FileNotFoundException("Input disappeared", "template.bicep"),
            _ => new NotSupportedException("Native launch not supported")
        };
        var source = new FakeEventStream<byte>([Emit<byte>.Started(1), Emit<byte>.Fail(original)]);
        var (compiler, runner) = CreateCompiler(source, new TestSink());
        Assert.Empty(runner.ExecutedCommands);
        Assert.Equal(0, source.DisposeCount);

        var failure = await Record.ExceptionAsync(() => compiler.CompileBicepToArmAsync("template.bicep", TestContext.Current.CancellationToken));

        Assert.Same(original, failure);
        Assert.Single(runner.ExecutedCommands);
        Assert.Equal(1, source.DisposeCount);
    }

    private static (BicepCliCompiler Compiler, MockProcessRunner Runner) CreateCompiler(
        FakeEventStream<byte> source, TestSink sink, string tool = "bicep")
    {
        var runner = new MockProcessRunner
        {
            CreateEvents = command => command.ExecutablePath == tool
                ? source
                : throw new FileNotFoundException("Not on PATH.", command.ExecutablePath)
        };
        var compiler = new BicepCliCompiler(new TestLogger<BicepCliCompiler>(new TestLoggerFactory(sink, enabled: true)), runner);
        return (compiler, runner);
    }

    private static (LogLevel Level, string Message)[] OutputLogs(TestSink sink)
        => sink.Writes.Where(write => write.Message is { } message &&
            (message.Contains(" (stdout):", StringComparison.Ordinal) || message.Contains(" (stderr):", StringComparison.Ordinal)))
            .Select(write => (write.LogLevel, write.Message!)).ToArray();
}
