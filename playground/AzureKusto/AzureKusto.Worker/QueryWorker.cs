// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Kusto.Cloud.Platform.Data;
using KustoUtils = Kusto.Cloud.Platform.Utils;
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

            return await _queryClient.ExecuteQueryAsync(_queryClient.DefaultDatabaseName, _workerOptions.CurrentValue.TableName, crp, ct);
        }, stoppingToken);

        var results = string.Join(",", reader.ToJObjects());
        _logger.LogInformation("Query Results: {results}", results);
    }
}

internal static class SemanticConventions
{
    public const string AttributeDbOperationName = "db.operation.name";
    public const string AttributeDbSystemName = "db.system.name";
    public const string AttributeHttpResponseStatusCode = "http.response.status_code";
    public const string AttributeDbNamespace = "db.namespace";
    public const string AttributeUrlFull = "url.full";
    public const string AttributeServerAddress = "server.address";
    public const string AttributeDbClientOperationDuration = "db.client.operation.duration";
    public const string AttributeDbQuerySummary = "db.query.summary";
    public const string AttributeDbQueryText = "db.query.text";
}

internal static class TraceRecordExtensions
{
    public static bool IsRequestStart(this KustoUtils.TraceRecord record)
    {
        return record.Message.StartsWith("$$HTTPREQUEST[", StringComparison.Ordinal);
    }

    public static bool IsResponseStart(this KustoUtils.TraceRecord record)
    {
        return record.Message.StartsWith("$$HTTPREQUEST_RESPONSEHEADERRECEIVED[", StringComparison.Ordinal);
    }

    public static bool IsExcention(this KustoUtils.TraceRecord record)
    {
        return record.TraceSourceName == "KD.Exceptions";
    }
}

internal sealed class KustoListener : KustoUtils.ITraceListener
{
    private const string InstrumentationName = "Kusto.Client";
    private const string InstrumentationVersion = "1.0.0";
    private const string DbSystem = "kusto";

    private const string ClientRequestIdTagKey = "kusto.client_request_id";

    private static readonly ActivitySource s_activitySource = new(InstrumentationName, InstrumentationVersion);
    private static readonly Meter s_meter = new(InstrumentationName, InstrumentationVersion);

    // Metrics following OpenTelemetry database semantic conventions
    private static readonly Histogram<double> s_operationDurationHistogram = s_meter.CreateHistogram<double>(
        SemanticConventions.AttributeDbClientOperationDuration,
        unit: "s",
        advice: new InstrumentAdvice<double>() { HistogramBucketBoundaries = [0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10] },
        description: "Duration of database client operations");

    private static readonly Counter<long> s_operationCounter = s_meter.CreateCounter<long>(
        "db.client.operation.count",
        description: "Number of database client operations");

    public override bool IsThreadSafe => true;

    public override void Flush()
    {
        // No buffering, nothing to flush
    }

    public override void Write(KustoUtils.TraceRecord record)
    {
        if (record?.Message is null)
        {
            return;
        }

        if (record.IsRequestStart())
        {
            HandleHttpRequestStart(record);
        }
        else if (record.IsResponseStart())
        {
            HandleHttpResponseReceived(record);
        }
        else if (record.IsExcention())
        {
            HandleException(record);
        }
    }

    private static void HandleException(KustoUtils.TraceRecord record)
    {
        var activity = Activity.Current;

        activity?.SetStatus(ActivityStatusCode.Error, record.Message);
    }

    private static void HandleHttpRequestStart(KustoUtils.TraceRecord record)
    {
        // Parse: $$HTTPREQUEST[RestClient2]: Verb=POST, Uri=http://localhost:55952/v1/rest/ingest/testdb/TestTable?streamFormat=csv
        var operationName = record.Activity.ActivityType;

        var activity = s_activitySource.StartActivity(operationName, ActivityKind.Client);

        if (activity?.IsAllDataRequested is true)
        {
            activity.SetTag(SemanticConventions.AttributeDbSystemName, DbSystem);
            activity.SetTag(ClientRequestIdTagKey, record.Activity.ClientRequestId.ToString());
            activity.SetTag(SemanticConventions.AttributeDbOperationName, operationName);

            var message = record.Message.AsSpan();

            var uri = ExtractValueBetween(message, "Uri=", ",");
            if (!uri.IsEmpty)
            {
                var uriString = uri.ToString();
                activity.SetTag(SemanticConventions.AttributeUrlFull, uriString);
                activity.SetTag(SemanticConventions.AttributeServerAddress, GetServerAddress(uri));

                var database = GetDatabaseFromUri(uri);
                if (!string.IsNullOrEmpty(database))
                {
                    activity.SetTag(SemanticConventions.AttributeDbNamespace, database);
                }
            }

            // TODO: Consider making text optional
            // TODO: Consider adding summary
            var text = ExtractValueBetween(message, "text=", Environment.NewLine);
            if (!text.IsEmpty)
            {
                activity.SetTag(SemanticConventions.AttributeDbQueryText, text.ToString());
            }
        }
    }

    private static void HandleHttpResponseReceived(KustoUtils.TraceRecord record)
    {
        // Parse: $$HTTPREQUEST_RESPONSEHEADERRECEIVED[RestClient2]: ... StatusCode=OK
        var activity = Activity.Current;

        if (activity is null)
        {
            return;
        }

        var clientRequestId = record.Activity.ClientRequestId;
        var activityClientRequestId = activity.GetTagItem(ClientRequestIdTagKey) as string;

        if (clientRequestId.Equals(activityClientRequestId, StringComparison.Ordinal))
        {
            var message = record.Message.AsSpan();
            var statusCode = ExtractValueBetween(message, "StatusCode=", Environment.NewLine);
            CompleteHttpActivity(activity, statusCode);
        }
    }

    private static void CompleteHttpActivity(Activity activity, ReadOnlySpan<char> statusCode)
    {
        if (!statusCode.IsEmpty)
        {
            var statusCodeStr = statusCode.ToString();
            activity.SetTag(SemanticConventions.AttributeHttpResponseStatusCode, statusCodeStr);

            // Set error status for non-2xx responses
            if (!statusCodeStr.Equals("OK", StringComparison.OrdinalIgnoreCase) &&
                !statusCodeStr.StartsWith('2'))
            {
                activity.SetStatus(ActivityStatusCode.Error);
            }
        }

        var duration = activity.Duration.TotalSeconds;

        // Record metrics
        var tags = new TagList
        {
            { SemanticConventions.AttributeDbSystemName, DbSystem },
            { SemanticConventions.AttributeDbOperationName, activity.DisplayName }
        };

        s_operationDurationHistogram.Record(duration, tags);
        s_operationCounter.Add(1, tags);

        activity.Stop();
    }

    private static ReadOnlySpan<char> ExtractValueBetween(ReadOnlySpan<char> source, string start, string end)
    {
        var startIndex = source.IndexOf(start);
        if (startIndex < 0)
        {
            return ReadOnlySpan<char>.Empty;
        }

        startIndex += start.Length;
        var remaining = source.Slice(startIndex);

        var endIndex = remaining.IndexOf(end);
        if (endIndex < 0)
        {
            endIndex = remaining.Length;
        }

        return remaining.Slice(0, endIndex);
    }

    // TODO: This doesn't work for queries, as they aren't in the Uri
    private static string GetDatabaseFromUri(ReadOnlySpan<char> uri)
    {
        // Extract database from URI like: http://localhost:55952/v1/rest/ingest/testdb/TestTable
        // Should return "testdb"

        var pathStart = uri.IndexOf("://");
        if (pathStart >= 0)
        {
            var afterScheme = uri.Slice(pathStart + 3);
            var pathIndex = afterScheme.IndexOf('/');
            if (pathIndex >= 0)
            {
                var path = afterScheme.Slice(pathIndex + 1);

                // Skip version and rest and operation
                for (int i = 0; i < 3; i++)
                {
                    var nextSlash = path.IndexOf('/');
                    if (nextSlash >= 0)
                    {
                        path = path.Slice(nextSlash + 1);
                    }
                    else
                    {
                        return string.Empty;
                    }
                }

                // Get database name
                var dbEnd = path.IndexOf('/');
                if (dbEnd >= 0)
                {
                    return path.Slice(0, dbEnd).ToString();
                }

                var queryIndex = path.IndexOf('?');
                if (queryIndex >= 0)
                {
                    return path.Slice(0, queryIndex).ToString();
                }

                return path.ToString();
            }
        }

        return string.Empty;
    }

    private static string GetServerAddress(ReadOnlySpan<char> uri)
    {
        var schemeEnd = uri.IndexOf("://");
        if (schemeEnd < 0)
        {
            return string.Empty;
        }

        var hostStart = schemeEnd + 3;
        var remaining = uri.Slice(hostStart);

        var pathStart = remaining.IndexOf('/');
        var host = pathStart >= 0 ? remaining.Slice(0, pathStart) : remaining;

        return host.ToString();
    }
}
