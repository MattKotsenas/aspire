// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.VersionChecking;

internal sealed class PackageFetcher : IPackageFetcher
{
    public const string PackageId = "Aspire.Hosting.AppHost";

    // Limit the number of packages fetched per search to avoid overwhelming the output. This should never happen unless there is a bug in the API.
    // Package search returns the latest version per source and few packages will match "Aspire.Hosting.AppHost" search string.
    private const int SearchPageSize = 1000;

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<PackageFetcher> _logger;

    public PackageFetcher(IProcessRunner processRunner, ILogger<PackageFetcher> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<List<NuGetPackage>> TryFetchPackagesAsync(string appHostDirectory, CancellationToken cancellationToken)
    {
        var outputJson = new StringBuilder();
        var command = new ProcessCommand("dotnet")
        {
            Arguments = ["package", "search", PackageId, "--format", "json", "--prerelease", "--take", SearchPageSize.ToString(CultureInfo.InvariantCulture)],
            InheritEnvironmentVariables = true,
            WorkingDirectory = appHostDirectory
        };

        _logger.LogDebug("Running dotnet CLI to check for latest version of {PackageId} with arguments: {ArgumentList}", PackageId, string.Join(" ", command.Arguments));
        // Preserve the exact JSON, including line endings, before framing diagnostic records.
        var exitCode = await _processRunner.CreateExecution(command)
            .DecodeChars(Encoding.UTF8)
            .AppendStandardOutput(outputJson)
            .FrameLines()
            .OnStandardOutput(output =>
            {
                if (output.Length > 0)
                {
                    _logger.LogDebug("dotnet (stdout): {Output}", output);
                }
            })
            .OnStandardError(error =>
            {
                if (error.Length > 0)
                {
                    _logger.LogDebug("dotnet (stderr): {Error}", error);
                }
            })
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            _logger.LogDebug("The dotnet CLI call to check for latest version failed with exit code {ExitCode}.", exitCode);
            return [];
        }

        // Filter packages to only consider "Aspire.Hosting.AppHost".
        // Although the CLI command 'dotnet package search Aspire.Hosting.AppHost --format json' 
        // should already limit results according to NuGet search syntax 
        // (https://learn.microsoft.com/en-us/nuget/consume-packages/finding-and-choosing-packages#search-syntax),
        // we add this extra check for robustness in case the CLI output includes unexpected packages.
        var packages = PackageUpdateHelpers.ParsePackageSearchResults(outputJson.ToString(), PackageId);
        _logger.LogDebug("Found {PackageCount} packages.", packages.Count);

        return packages;
    }
}
