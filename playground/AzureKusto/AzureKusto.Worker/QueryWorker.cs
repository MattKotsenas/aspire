// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Kusto.Data.Common;
using Microsoft.Extensions.Options;
using Polly;

namespace AzureKusto.Worker;

internal sealed class QueryWorker : BackgroundService
{
    private static readonly ActivitySource s_activitySource = new ActivitySource("AzureKusto.Worker.QueryWorker");

    private readonly ICslQueryProvider _queryClient;
    private readonly ResiliencePipeline _pipeline;
    private readonly IOptionsMonitor<WorkerOptions> _workerOptions;
    private readonly ILogger<QueryWorker> _logger;

    public QueryWorker(
        ICslQueryProvider queryClient,
        [FromKeyedServices("kusto-resilience")] ResiliencePipeline pipeline,
        IOptionsMonitor<WorkerOptions> workerOptions,
        ILogger<QueryWorker> logger)
    {
        _queryClient = queryClient;
        _pipeline = pipeline;
        _workerOptions = workerOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = s_activitySource.StartActivity(nameof(ExecuteAsync), ActivityKind.Consumer);

        while (!stoppingToken.IsCancellationRequested && !_workerOptions.CurrentValue.IsIngestionComplete)
        {
            // Wait for ingestion to complete
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }

        using var reader = await _pipeline.ExecuteAsync(async ct =>
        {
            var crp = new ClientRequestProperties();

            if (activity is not null)
            {
                crp.ClientRequestId = activity.TraceId.ToHexString();
            }
            crp.SetOption(ClientRequestProperties.OptionRequestDescription, "My sample description");

            return await _queryClient.ExecuteQueryV2Async(_queryClient.DefaultDatabaseName, $"{_workerOptions.CurrentValue.TableName} | where 1 == 1", crp, ct);
        }, stoppingToken);

        var results = ""; // string.Join(",", reader.ToJObjects());
        _logger.LogInformation("Query Results: {results}", results);
    }
}
