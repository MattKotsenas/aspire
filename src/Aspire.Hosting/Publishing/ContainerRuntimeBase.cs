// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Base class for container runtime implementations that provides common process execution,
/// logging, and error handling patterns.
/// </summary>
internal abstract class ContainerRuntimeBase<TLogger> : IContainerRuntime where TLogger : class
{
    private readonly ILogger<TLogger> _logger;
    private readonly IProcessRunner _processRunner;

    protected ContainerRuntimeBase(ILogger<TLogger> logger, IProcessRunner processRunner)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    /// <summary>
    /// Gets the logger instance for use in derived classes.
    /// </summary>
    protected ILogger<TLogger> Logger => _logger;

    /// <summary>
    /// Gets the process runner used for container runtime commands.
    /// </summary>
    protected IProcessRunner ProcessRunner => _processRunner;

    /// <summary>
    /// Gets the name of the container runtime executable (e.g., "docker", "podman").
    /// </summary>
    protected abstract string RuntimeExecutable { get; }

    public abstract string Name { get; }

    public abstract Task<bool> CheckIfRunningAsync(CancellationToken cancellationToken);

    public abstract Task BuildImageAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken);

    public virtual async Task TagImageAsync(string localImageName, string targetImageName, CancellationToken cancellationToken)
    {
        await ExecuteContainerCommandAsync(
            ["tag", localImageName, targetImageName],
            $"{Name} tag for {{LocalImageName}} -> {{TargetImageName}} failed with exit code {{ExitCode}}.",
            $"{Name} tag for {{LocalImageName}} -> {{TargetImageName}} succeeded.",
            $"{Name} tag failed with exit code {{0}}.",
            cancellationToken,
            localImageName, targetImageName).ConfigureAwait(false);
    }

    public virtual async Task RemoveImageAsync(string imageName, CancellationToken cancellationToken)
    {
        await ExecuteContainerCommandAsync(
            ["rmi", imageName],
            $"{Name} rmi for {{ImageName}} failed with exit code {{ExitCode}}.",
            $"{Name} rmi for {{ImageName}} succeeded.",
            $"{Name} rmi failed with exit code {{0}}.",
            cancellationToken,
            imageName).ConfigureAwait(false);
    }

    public virtual async Task PushImageAsync(IResource resource, CancellationToken cancellationToken)
    {
        var localImageName = resource.TryGetContainerImageName(out var imageName)
            ? imageName
            : resource.Name.ToLowerInvariant();

        var remoteImageName = await resource.GetFullRemoteImageNameAsync(cancellationToken).ConfigureAwait(false);

        await TagImageAsync(localImageName, remoteImageName, cancellationToken).ConfigureAwait(false);

        await ExecuteContainerCommandAsync(
            ["push", remoteImageName],
            $"{Name} push for {{ImageName}} failed with exit code {{ExitCode}}.",
            $"{Name} push for {{ImageName}} succeeded.",
            $"{Name} push failed with exit code {{0}}.",
            cancellationToken,
            remoteImageName).ConfigureAwait(false);
    }

    public virtual async Task<ContainerImageConfigInspectionResult> InspectImageConfigAsync(string imageName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);

        string output;
        try
        {
            output = await ExecuteContainerCommandForOutputAsync(
                [
                    "image",
                    "inspect",
                    imageName,
                    "--format",
                    """{"Entrypoint":{{json .Config.Entrypoint}},"Cmd":{{json .Config.Cmd}},"WorkingDir":{{json .Config.WorkingDir}}}"""
                ],
                "inspect image config",
                imageName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DistributedApplicationException ex)
        {
            return new ContainerImageConfigInspectionResult(
                ContainerImageInspectionStatus.Failed,
                rawJson: null,
                ex.Message,
                configAccessor: null);
        }

        if (!TryParseImageConfig(output, out var config))
        {
            return new ContainerImageConfigInspectionResult(
                ContainerImageInspectionStatus.Failed,
                output,
                $"Container runtime returned invalid image configuration for '{imageName}'.",
                configAccessor: null);
        }

        return new ContainerImageConfigInspectionResult(
            ContainerImageInspectionStatus.Succeeded,
            output,
            errorMessage: null,
            () => config);
    }

    public virtual async Task<ContainerImageManifestInspectionResult> InspectImageManifestAsync(string imageName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);

        string output;
        try
        {
            output = await ExecuteContainerCommandForOutputAsync(
                ["manifest", "inspect", "--verbose", imageName],
                "inspect image manifest",
                imageName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DistributedApplicationException ex)
        {
            return new ContainerImageManifestInspectionResult(
                ContainerImageInspectionStatus.Failed,
                rawJson: null,
                ex.Message,
                manifestAccessor: null);
        }

        if (!IsJsonObjectOrArray(output))
        {
            return new ContainerImageManifestInspectionResult(
                ContainerImageInspectionStatus.Failed,
                output,
                $"Container runtime returned invalid image manifest for '{imageName}'.",
                manifestAccessor: null);
        }

        return new ContainerImageManifestInspectionResult(
            ContainerImageInspectionStatus.Succeeded,
            output,
            errorMessage: null,
            (operatingSystem, architecture) => FindManifest(output, operatingSystem, architecture));
    }

    private static bool TryParseImageConfig(string output, [NotNullWhen(true)] out ContainerImageConfig? config)
    {
        config = null;

        try
        {
            var root = JsonNode.Parse(output) as JsonObject;
            if (root is null)
            {
                return false;
            }

            config = new ContainerImageConfig(
                ReadStringArray(root["Entrypoint"]),
                ReadStringArray(root["Cmd"]),
                root["WorkingDir"]?.GetValue<string>());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item?.GetValue<string>() is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    protected static bool IsJsonObjectOrArray(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    protected static ContainerImageManifest? FindManifest(string output, string operatingSystem, string architecture)
    {
        try
        {
            var root = JsonNode.Parse(output);
            if (root is JsonArray verboseManifests)
            {
                foreach (var item in verboseManifests.OfType<JsonObject>())
                {
                    if (TryCreateManifest(item["Descriptor"] as JsonObject, operatingSystem, architecture, out var manifest))
                    {
                        return manifest;
                    }
                }

                return null;
            }

            if (root is not JsonObject manifestObject)
            {
                return null;
            }

            if (manifestObject["manifests"] is JsonArray manifests)
            {
                foreach (var item in manifests.OfType<JsonObject>())
                {
                    if (TryCreateManifest(item, operatingSystem, architecture, out var manifest))
                    {
                        return manifest;
                    }
                }

                return null;
            }

            var descriptor = manifestObject["Descriptor"] as JsonObject ??
                manifestObject["descriptor"] as JsonObject ??
                manifestObject;
            return TryCreateManifest(descriptor, operatingSystem, architecture, out var singleManifest)
                ? singleManifest
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryCreateManifest(
        JsonObject? descriptor,
        string operatingSystem,
        string architecture,
        [NotNullWhen(true)] out ContainerImageManifest? manifest)
    {
        manifest = null;
        if (descriptor is null)
        {
            return false;
        }

        var platform = descriptor["platform"] as JsonObject;
        var actualOperatingSystem = platform?["os"]?.GetValue<string>();
        var actualArchitecture = platform?["architecture"]?.GetValue<string>();
        var digest = descriptor["digest"]?.GetValue<string>();
        if (digest is null ||
            !ContainerImageManifest.IsValidDigest(digest) ||
            actualOperatingSystem is null ||
            actualArchitecture is null ||
            !string.Equals(actualOperatingSystem, operatingSystem, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actualArchitecture, architecture, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        manifest = new ContainerImageManifest(digest, actualOperatingSystem, actualArchitecture);
        return true;
    }

    public virtual async Task LoginToRegistryAsync(string registryServer, string username, string password, CancellationToken cancellationToken)
    {
        string[] arguments = ["login", "--username", username, "--password-stdin", registryServer];
        var command = new ProcessCommand(RuntimeExecutable)
        {
            Arguments = arguments,
            // ProcessInput does not add the newline previously supplied by WriteLine.
            Input = ProcessInput.FromText(password + Environment.NewLine, Encoding.UTF8)
        };
        var diagnostics = new ProcessOutputCapture(maxRetainedLineCount: 50);

        _logger.LogDebug("Running {RuntimeName} with arguments: {Arguments}", RuntimeExecutable, arguments);
        _logger.LogDebug("Password length being passed to stdin: {PasswordLength}", password?.Length ?? 0);
        var exitCode = await _processRunner.CreateExecution(command)
            .DecodeChars(Encoding.UTF8)
            .FrameLines()
            .OnStandardOutput(output =>
            {
                if (output.Length > 0)
                {
                    diagnostics.Add(output);
                    _logger.LogDebug("{RuntimeName} (stdout): {Output}", RuntimeExecutable, output);
                }
            })
            .OnStandardError(error =>
            {
                if (error.Length > 0)
                {
                    diagnostics.Add(error);
                    _logger.LogDebug("{RuntimeName} (stderr): {Error}", RuntimeExecutable, error);
                }
            })
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            _logger.LogError("{RuntimeName} login to {RegistryServer} failed with exit code {ExitCode}.", Name, registryServer, exitCode);

            var message = $"{Name} login failed with exit code {exitCode}.";
            if (diagnostics.TotalLineCount > 0)
            {
                message = $"{message}{Environment.NewLine}{diagnostics.GetFormattedOutput()}";
            }

            throw new DistributedApplicationException(message);
        }

        _logger.LogInformation("{RuntimeName} login to {RegistryServer} succeeded.", Name, registryServer);
    }

    /// <summary>
    /// Executes a container runtime command with standard logging and error handling.
    /// </summary>
    /// <param name="arguments">The command arguments to pass to the container runtime.</param>
    /// <param name="errorLogTemplate">Log template for error messages (must include {ExitCode} placeholder).</param>
    /// <param name="successLogTemplate">Log template for success messages.</param>
    /// <param name="exceptionMessageTemplate">Exception message template (must include {ExitCode} placeholder).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="logArguments">Arguments to pass to the log templates.</param>
    protected async Task ExecuteContainerCommandAsync(
        IReadOnlyList<string> arguments,
        string errorLogTemplate,
        string successLogTemplate,
        string exceptionMessageTemplate,
        CancellationToken cancellationToken,
        params object[] logArguments)
    {
        var command = new ProcessCommand(RuntimeExecutable)
        {
            Arguments = arguments
        };
        var diagnostics = new ProcessOutputCapture(maxRetainedLineCount: 50);

        _logger.LogDebug("Running {RuntimeName} with arguments: {ArgumentList}", Name, arguments);
        var exitCode = await _processRunner.CreateExecution(command)
            .DecodeChars(Encoding.UTF8)
            .FrameLines()
            .OnStandardOutput(output =>
            {
                if (output.Length > 0)
                {
                    diagnostics.Add(output);
                    _logger.LogDebug("{RuntimeName} (stdout): {Output}", RuntimeExecutable, output);
                }
            })
            .OnStandardError(error =>
            {
                if (error.Length > 0)
                {
                    diagnostics.Add(error);
                    _logger.LogDebug("{RuntimeName} (stderr): {Error}", RuntimeExecutable, error);
                }
            })
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            var errorArgs = logArguments.Concat(new object[] { exitCode }).ToArray();
            _logger.LogError(errorLogTemplate, errorArgs);

            var message = string.Format(System.Globalization.CultureInfo.InvariantCulture, exceptionMessageTemplate, exitCode);
            if (diagnostics.TotalLineCount > 0)
            {
                message = $"{message}{Environment.NewLine}{diagnostics.GetFormattedOutput(outputDescription: "Command output")}";
            }

            throw new DistributedApplicationException(message);
        }

        _logger.LogInformation(successLogTemplate, logArguments);
    }

    /// <summary>
    /// Executes a container runtime command and returns the exit code without throwing exceptions.
    /// </summary>
    /// <param name="arguments">The command arguments to pass to the container runtime.</param>
    /// <param name="errorLogTemplate">Log template for error messages (must include {ExitCode} placeholder).</param>
    /// <param name="successLogTemplate">Log template for success messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="logArguments">Arguments to pass to the log templates.</param>
    /// <param name="environmentVariables">Optional environment variables to set for the process.</param>
    /// <returns>The exit code of the process.</returns>
    protected async Task<int> ExecuteContainerCommandWithExitCodeAsync(
        IReadOnlyList<string> arguments,
        string errorLogTemplate,
        string successLogTemplate,
        CancellationToken cancellationToken,
        object[] logArguments,
        Dictionary<string, string>? environmentVariables = null)
    {
        var processResult = await ExecuteContainerCommandWithResultAsync(
            arguments,
            errorLogTemplate,
            successLogTemplate,
            cancellationToken,
            logArguments,
            environmentVariables).ConfigureAwait(false);

        return processResult.ExitCode;
    }

    /// <summary>
    /// Builds arguments for container build commands without command-line quoting.
    /// </summary>
    /// <param name="buildArguments">The build arguments to include.</param>
    /// <returns>The build argument switches and values.</returns>
    protected static List<string> BuildArguments(Dictionary<string, string?> buildArguments)
    {
        var arguments = new List<string>();
        foreach (var buildArg in buildArguments)
        {
            arguments.Add("--build-arg");
            arguments.Add(buildArg.Value is not null
                ? $"{buildArg.Key}={buildArg.Value}"
                : buildArg.Key);
        }

        return arguments;
    }

    /// <summary>
    /// Builds secret arguments without command-line quoting.
    /// </summary>
    /// <param name="buildSecrets">The build secrets to include.</param>
    /// <param name="useCsv">Whether the runtime parses secret specifications as CSV records.</param>
    /// <param name="requireValue">Whether to require a non-null value for secrets (default: false).</param>
    /// <returns>The secret switches and specifications.</returns>
    internal static List<string> BuildSecrets(Dictionary<string, BuildImageSecretValue> buildSecrets, bool useCsv, bool requireValue = false)
    {
        var arguments = new List<string>();
        foreach (var buildSecret in buildSecrets)
        {
            arguments.Add("--secret");
            var id = FormatSecretField($"id={buildSecret.Key}", useCsv);
            if (buildSecret.Value.Type == BuildImageSecretType.File)
            {
                var source = FormatSecretField($"src={buildSecret.Value.Value}", useCsv);
                arguments.Add($"{id},type=file,{source}");
            }
            else if (requireValue && buildSecret.Value.Value is null)
            {
                arguments.Add($"{id},type=env");
            }
            else
            {
                var environment = FormatSecretField($"env={buildSecret.Key.ToUpperInvariant()}", useCsv);
                arguments.Add($"{id},type=env,{environment}");
            }
        }

        return arguments;
    }

    private static string FormatSecretField(string field, bool useCsv)
    {
        // Docker Buildx and WSLc parse a CSV record: id=npmrc,type=file,"src=C:\team,a\.npmrc".
        // These quotes belong to the secret specification, not to process argument escaping.
        // https://github.com/docker/buildx/blob/master/util/buildflags/secrets.go
        if (useCsv && field.AsSpan().ContainsAny(",\"\r\n"))
        {
            return $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return field;
    }

    /// <summary>
    /// Builds arguments for the target stage.
    /// </summary>
    /// <param name="stage">The target stage to include.</param>
    /// <returns>The target switches and value, or an empty collection.</returns>
    protected static List<string> BuildStage(string? stage)
    {
        return !string.IsNullOrEmpty(stage) ? ["--target", stage] : [];
    }

    /// <summary>
    /// Executes a command with argument boundaries preserved by the process runner.
    /// </summary>
    protected async Task<ProcessResult> ExecuteContainerCommandWithResultAsync(
        IReadOnlyList<string> arguments,
        string errorLogTemplate,
        string successLogTemplate,
        CancellationToken cancellationToken,
        object[] logArguments,
        Dictionary<string, string>? environmentVariables = null,
        bool retainOutput = false)
    {
        var command = new ProcessCommand(RuntimeExecutable)
        {
            Arguments = arguments,
            EnvironmentVariables = environmentVariables is null ? new Dictionary<string, string>() : new Dictionary<string, string>(environmentVariables)
        };
        var diagnostics = retainOutput ? new ProcessOutputCapture(maxRetainedLineCount: 50) : null;

        _logger.LogDebug("Running {RuntimeName} with arguments: {ArgumentList}", Name, arguments);
        var exitCode = await _processRunner.CreateExecution(command)
            .DecodeChars(Encoding.UTF8)
            .FrameLines()
            .OnStandardOutput(output =>
            {
                if (output.Length > 0)
                {
                    diagnostics?.Add(output);
                    _logger.LogDebug("{RuntimeName} (stdout): {Output}", RuntimeExecutable, output);
                }
            })
            .OnStandardError(error =>
            {
                if (error.Length > 0)
                {
                    diagnostics?.Add(error);
                    _logger.LogDebug("{RuntimeName} (stderr): {Error}", RuntimeExecutable, error);
                }
            })
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);

        // The terminal has drained and disposed the execution before caller policy runs.
        if (exitCode != 0)
        {
            var errorArgs = logArguments.Concat(new object[] { exitCode }).ToArray();
            _logger.LogError(errorLogTemplate, errorArgs);
        }
        else
        {
            _logger.LogDebug(successLogTemplate, logArguments);
        }

        return new ProcessResult(exitCode, diagnostics?.ToArray(), diagnostics?.TotalLineCount);
    }

    protected async Task<string> ExecuteContainerCommandForOutputAsync(
        IReadOnlyList<string> argumentList,
        string operationName,
        string imageName,
        CancellationToken cancellationToken)
    {
        var command = new ProcessCommand(RuntimeExecutable)
        {
            Arguments = argumentList
        };
        var stdout = new StringBuilder();
        var diagnostics = new ProcessOutputCapture(maxRetainedLineCount: 50);

        _logger.LogDebug("Running {RuntimeName} with arguments: {ArgumentList}", Name, argumentList);
        // Capture before framing so query results, including inspection RawJson, retain
        // empty records, original line endings, and final newlines in the same enumeration.
        var exitCode = await _processRunner.CreateExecution(command)
            .DecodeChars(Encoding.UTF8)
            .AppendStandardOutput(stdout)
            .FrameLines()
            .OnStandardOutput(output =>
            {
                if (output.Length > 0)
                {
                    diagnostics.Add(output);
                    _logger.LogDebug("{RuntimeName} (stdout): {Output}", RuntimeExecutable, output);
                }
            })
            .OnStandardError(error =>
            {
                if (error.Length > 0)
                {
                    diagnostics.Add(error);
                    _logger.LogDebug("{RuntimeName} (stderr): {Error}", RuntimeExecutable, error);
                }
            })
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            _logger.LogError("{RuntimeName} {OperationName} for {ImageName} failed with exit code {ExitCode}.", Name, operationName, imageName, exitCode);
            throw new DistributedApplicationException($"{Name} {operationName} for '{imageName}' failed with exit code {exitCode}.{Environment.NewLine}{diagnostics.GetFormattedOutput()}");
        }

        _logger.LogDebug("{RuntimeName} {OperationName} for {ImageName} succeeded.", Name, operationName, imageName);
        return stdout.ToString();
    }

    public virtual async Task ComposeUpAsync(ComposeOperationContext context, CancellationToken cancellationToken)
    {
        await EnsureRuntimeAvailableAsync().ConfigureAwait(false);

        var arguments = BuildComposeArguments(context);
        arguments.AddRange(["up", "-d", "--remove-orphans"]);

        _logger.LogInformation("Using container runtime '{Runtime}' for compose operations.", RuntimeExecutable);
        _logger.LogDebug("Running {Runtime} compose up with arguments: {Arguments}", RuntimeExecutable, arguments);

        var command = new ProcessCommand(RuntimeExecutable)
        {
            Arguments = arguments,
            WorkingDirectory = context.WorkingDirectory
        };
        var diagnostics = new ProcessOutputCapture(maxRetainedLineCount: 50);

        var exitCode = await _processRunner.CreateExecution(command)
            .DecodeChars(Encoding.UTF8)
            .FrameLines()
            .OnStandardOutput(output =>
            {
                if (output.Length > 0)
                {
                    diagnostics.Add(output);
                    _logger.LogDebug("{Runtime} compose up (stdout): {Output}", RuntimeExecutable, output);
                }
            })
            .OnStandardError(error =>
            {
                if (error.Length > 0)
                {
                    diagnostics.Add(error);
                    _logger.LogDebug("{Runtime} compose up (stderr): {Error}", RuntimeExecutable, error);
                }
            })
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            var envHint = Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME") is not null
                ? $"The container runtime is configured via ASPIRE_CONTAINER_RUNTIME (current: '{RuntimeExecutable}')."
                : $"The container runtime was auto-detected as '{RuntimeExecutable}'. Set ASPIRE_CONTAINER_RUNTIME to override (e.g., 'docker' or 'podman').";

            var message =
                $"'{RuntimeExecutable} compose up' failed with exit code {exitCode}. " +
                $"Ensure '{RuntimeExecutable}' is installed and available on PATH. " +
                envHint;

            if (diagnostics.TotalLineCount > 0)
            {
                message = $"{message}{Environment.NewLine}{diagnostics.GetFormattedOutput()}";
            }

            throw new DistributedApplicationException(message);
        }
    }

    public virtual async Task ComposeDownAsync(ComposeOperationContext context, CancellationToken cancellationToken)
    {
        await EnsureRuntimeAvailableAsync().ConfigureAwait(false);

        var arguments = BuildComposeArguments(context);
        arguments.Add("down");

        _logger.LogDebug("Running {Runtime} compose down with arguments: {Arguments}", RuntimeExecutable, arguments);

        var stderrLines = new List<string>();
        var command = new ProcessCommand(RuntimeExecutable)
        {
            Arguments = arguments,
            WorkingDirectory = context.WorkingDirectory
        };

        var exitCode = await _processRunner.CreateExecution(command)
            .DecodeChars(Encoding.UTF8)
            .FrameLines()
            .OnStandardOutput(output =>
            {
                if (output.Length > 0)
                {
                    _logger.LogDebug("{Runtime} compose down (stdout): {Output}", RuntimeExecutable, output);
                }
            })
            .OnStandardError(error =>
            {
                if (error.Length > 0)
                {
                    _logger.LogDebug("{Runtime} compose down (stderr): {Error}", RuntimeExecutable, error);
                }

                // Compose-down failures include every non-whitespace stderr record,
                // rather than the bounded, combined-stream tail used by other commands.
                if (!string.IsNullOrWhiteSpace(error))
                {
                    stderrLines.Add(error);
                }
            })
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            var stderrOutput = stderrLines.Count > 0
                ? " " + string.Join(" ", stderrLines)
                : "";

            throw new DistributedApplicationException(
                $"'{RuntimeExecutable} compose down' failed with exit code {exitCode}.{stderrOutput}");
        }
    }

    public virtual async Task<IReadOnlyList<ComposeServiceInfo>?> ComposeListServicesAsync(ComposeOperationContext context, CancellationToken cancellationToken)
    {
        await EnsureRuntimeAvailableAsync().ConfigureAwait(false);

        var arguments = BuildComposeArguments(context);
        arguments.AddRange(["ps", "--format", "json"]);

        var outputLines = new List<string>();
        var command = new ProcessCommand(RuntimeExecutable)
        {
            Arguments = arguments,
            WorkingDirectory = context.WorkingDirectory
        };

        var exitCode = await _processRunner.CreateExecution(command)
            .DecodeChars(Encoding.UTF8)
            .FrameLines()
            .OnStandardOutput(output =>
            {
                if (!string.IsNullOrWhiteSpace(output))
                {
                    outputLines.Add(output);
                }
            })
            .OnStandardError(error =>
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logger.LogDebug("{Runtime} compose ps (stderr): {Error}", RuntimeExecutable, error);
                }
            })
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            _logger.LogDebug("{Runtime} compose ps failed with exit code {ExitCode}", RuntimeExecutable, exitCode);
            return null;
        }

        return ParseComposeServiceEntries(outputLines);
    }

    /// <summary>
    /// Parses Docker Compose ps JSON output, handling both NDJSON (one object per line) and JSON array formats.
    /// </summary>
    /// <example>
    /// NDJSON (Docker Compose v2+):
    /// <code>
    /// {"Service":"web","Publishers":[{"URL":"","TargetPort":80,"PublishedPort":8080,"Protocol":"tcp"}]}
    /// {"Service":"cache","Publishers":[{"TargetPort":6379,"PublishedPort":6379}]}
    /// </code>
    /// JSON array (older versions):
    /// <code>
    /// [{"Service":"web","Publishers":[{"TargetPort":80,"PublishedPort":8080}]}]
    /// </code>
    /// </example>
    internal static List<ComposeServiceInfo> ParseComposeServiceEntries(List<string> outputLines)
    {
        var results = new List<ComposeServiceInfo>();

        foreach (var line in outputLines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // Try parsing as JSON array first (older Docker Compose versions)
            if (trimmed.StartsWith('['))
            {
                try
                {
                    var entries = JsonSerializer.Deserialize(trimmed, ComposeJsonContext.Default.ListDockerComposePsEntry);
                    if (entries is not null)
                    {
                        foreach (var entry in entries)
                        {
                            results.Add(MapDockerComposeEntry(entry));
                        }
                    }
                }
                catch (JsonException)
                {
                    // Skip unparseable lines
                }
                continue;
            }

            // Parse as single JSON object (NDJSON format)
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize(trimmed, ComposeJsonContext.Default.DockerComposePsEntry);
                    if (entry is not null)
                    {
                        results.Add(MapDockerComposeEntry(entry));
                    }
                }
                catch (JsonException)
                {
                    // Skip unparseable lines
                }
            }
        }

        return results;
    }

    private static ComposeServiceInfo MapDockerComposeEntry(DockerComposePsEntry entry)
    {
        return new ComposeServiceInfo
        {
            Service = entry.Service,
            Publishers = entry.Publishers?.Select(p => new ComposeServicePort
            {
                PublishedPort = p.PublishedPort,
                TargetPort = p.TargetPort
            }).ToList()
        };
    }

    /// <summary>
    /// Builds the compose CLI arguments from a <see cref="ComposeOperationContext"/>.
    /// </summary>
    private static List<string> BuildComposeArguments(ComposeOperationContext context)
    {
        var arguments = new List<string> { "compose" };
        if (context.ComposeFilePath is not null)
        {
            arguments.AddRange(["-f", context.ComposeFilePath]);
        }
        arguments.AddRange(["--project-name", context.ProjectName]);

        if (context.EnvFilePath is not null && File.Exists(context.EnvFilePath))
        {
            arguments.AddRange(["--env-file", context.EnvFilePath]);
        }

        return arguments;
    }

    /// <summary>
    /// Validates that the container runtime binary is available on the system PATH.
    /// Fails fast with an actionable error message instead of a cryptic exit code.
    /// </summary>
    protected async Task EnsureRuntimeAvailableAsync()
    {
        try
        {
            var whichCommand = OperatingSystem.IsWindows() ? "where" : "which";
            var command = new ProcessCommand(whichCommand)
            {
                Arguments = [RuntimeExecutable]
            };

            // Availability checks deliberately have no caller cancellation token.
            var exitCode = await _processRunner.CreateExecution(command)
                .ExecuteAsync(CancellationToken.None).ConfigureAwait(false);

            if (exitCode != 0)
            {
                throw new DistributedApplicationException(
                    $"Container runtime '{RuntimeExecutable}' was not found on PATH. " +
                    $"Install {Name} or set ASPIRE_CONTAINER_RUNTIME to a different runtime (e.g., 'docker' or 'podman').");
            }
        }
        catch (DistributedApplicationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check if {Runtime} is available on PATH", RuntimeExecutable);
        }
    }
}

/// <summary>
/// Internal DTO for deserializing Docker Compose ps JSON output.
/// </summary>
internal sealed class DockerComposePsEntry
{
    public string? Service { get; set; }
    public List<DockerComposePsPublisher>? Publishers { get; set; }
}

/// <summary>
/// Internal DTO for deserializing Docker Compose ps publisher entries.
/// </summary>
internal sealed class DockerComposePsPublisher
{
    public int? PublishedPort { get; set; }
    public int? TargetPort { get; set; }
}

[JsonSerializable(typeof(DockerComposePsEntry))]
[JsonSerializable(typeof(List<DockerComposePsEntry>))]
internal sealed partial class ComposeJsonContext : JsonSerializerContext
{
}
