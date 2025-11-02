// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Kusto.Cloud.Platform.Data;
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

            return await _queryClient.ExecuteQueryAsync(_queryClient.DefaultDatabaseName, _workerOptions.CurrentValue.TableName, crp, ct);
        }, stoppingToken);

        var results = string.Join(",", reader.ToJObjects());
        _logger.LogInformation("Query Results: {results}", results);
    }
}

internal sealed class KustoListener : Kusto.Cloud.Platform.Utils.ITraceListener
{
    private const string InstrumentationName = "Kusto.Client";
    private const string InstrumentationVersion = "1.0.0";
    private const string DbSystem = "kusto";

    // Tag keys for storing operation metadata on the Activity
    private const string OperationNameTagKey = "kusto.operation_name";
    private const string ClientRequestIdTagKey = "kusto.client_request_id";

    private static readonly ActivitySource s_activitySource = new(InstrumentationName, InstrumentationVersion);
    private static readonly Meter s_meter = new(InstrumentationName, InstrumentationVersion);

    // Metrics following OpenTelemetry database semantic conventions
    private static readonly Histogram<double> s_operationDurationHistogram = s_meter.CreateHistogram<double>(
        "db.client.operation.duration",
        unit: "s",
        description: "Duration of database client operations");

    private static readonly Counter<long> s_operationCounter = s_meter.CreateCounter<long>(
        "db.client.operation.count",
        unit: "{operation}",
        description: "Number of database client operations");

    public override void Flush()
    {
        // No buffering, nothing to flush
    }

    public override void Write(Kusto.Cloud.Platform.Utils.TraceRecord record)
    {
        if (record?.Message == null)
        {
            return;
        }

        var message = record.Message.AsSpan();

        // Handle HTTP request start
        if (message.StartsWith("$$HTTPREQUEST["))
        {
            HandleHttpRequestStart(message);
        }
        // Handle HTTP response received
        else if (message.StartsWith("$$HTTPREQUEST_RESPONSEHEADERRECEIVED["))
        {
            HandleHttpResponseReceived(message);
        }

        // For debugging, write to console
        Console.WriteLine(record.Message);
    }

    private static void HandleHttpRequestStart(ReadOnlySpan<char> message)
    {
        // Parse: $$HTTPREQUEST[RestClient2]: Verb=POST, Uri=http://localhost:55952/v1/rest/ingest/testdb/TestTable?streamFormat=csv
        var operationName = ExtractValueBetween(message, "$$HTTPREQUEST[", "]:");
        var verb = ExtractKeyValue(message, "Verb=", ',');
        var uri = ExtractKeyValue(message, "Uri=", ',');
        var clientRequestId = ExtractKeyValue(message, "ClientRequestId=", default);

        if (operationName.IsEmpty)
        {
            return;
        }

        var activityName = $"kusto {GetOperationFromUri(uri)}";
        var activity = s_activitySource.StartActivity(activityName, ActivityKind.Client);

        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag("db.system", DbSystem);

            // Store operation metadata on the Activity for later retrieval
            activity.SetTag(OperationNameTagKey, operationName.ToString());

            if (!verb.IsEmpty)
            {
                activity.SetTag("http.request.method", verb.ToString());
            }

            if (!uri.IsEmpty)
            {
                var uriString = uri.ToString();
                activity.SetTag("url.full", uriString);
                activity.SetTag("server.address", GetServerAddress(uri));

                // Extract database and operation details from URI
                var operation = GetOperationFromUri(uri);
                if (!string.IsNullOrEmpty(operation))
                {
                    activity.SetTag("db.operation.name", operation);
                }

                var database = GetDatabaseFromUri(uri);
                if (!string.IsNullOrEmpty(database))
                {
                    activity.SetTag("db.namespace", database);
                }
            }

            if (!clientRequestId.IsEmpty)
            {
                activity.SetTag(ClientRequestIdTagKey, clientRequestId.ToString());
            }
        }
    }

    private static void HandleHttpResponseReceived(ReadOnlySpan<char> message)
    {
        // Parse: $$HTTPREQUEST_RESPONSEHEADERRECEIVED[RestClient2]: ... StatusCode=OK
        var operationName = ExtractValueBetween(message, "$$HTTPREQUEST_RESPONSEHEADERRECEIVED[", "]:");
        var statusCode = ExtractKeyValue(message, "StatusCode=", '\r');
        var clientRequestId = ExtractKeyValue(message, "x-ms-client-request-id=", '\r');

        if (operationName.IsEmpty)
        {
            return;
        }

        // Find the matching activity by looking at current activity and its baggage
        var activity = Activity.Current;
        
        // Walk up the activity chain to find matching activity
        while (activity != null)
        {
            var activityOperationName = activity.GetTagItem(OperationNameTagKey) as string;
            var activityClientRequestId = activity.GetTagItem(ClientRequestIdTagKey) as string;

            // Match by client request ID if available, otherwise by operation name
            var matches = !clientRequestId.IsEmpty
                ? clientRequestId.ToString().Equals(activityClientRequestId, StringComparison.Ordinal)
                : operationName.ToString().Equals(activityOperationName, StringComparison.Ordinal);

            if (matches)
            {
                CompleteHttpActivity(activity, statusCode);
                break;
            }

            activity = activity.Parent;
        }
    }

    private static void CompleteHttpActivity(Activity activity, ReadOnlySpan<char> statusCode)
    {
        if (!statusCode.IsEmpty)
        {
            var statusCodeStr = statusCode.ToString();
            activity.SetTag("http.response.status_code", statusCodeStr);

            // Set error status for non-2xx responses
            if (!statusCodeStr.Equals("OK", StringComparison.OrdinalIgnoreCase) &&
                !statusCodeStr.StartsWith("2"))
            {
                activity.SetStatus(ActivityStatusCode.Error);
            }
        }

        var duration = activity.Duration.TotalSeconds;

        // Record metrics
        var tags = new TagList
        {
            { "db.system", DbSystem },
            { "db.operation.name", activity.DisplayName }
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
            return ReadOnlySpan<char>.Empty;
        }

        return remaining.Slice(0, endIndex);
    }

    private static ReadOnlySpan<char> ExtractKeyValue(ReadOnlySpan<char> source, string key, char delimiter)
    {
        var keyIndex = source.IndexOf(key);
        if (keyIndex < 0)
        {
            return ReadOnlySpan<char>.Empty;
        }

        var valueStart = keyIndex + key.Length;
        var remaining = source.Slice(valueStart);

        if (delimiter == default)
        {
            return remaining.Trim();
        }

        var delimiterIndex = remaining.IndexOf(delimiter);
        if (delimiterIndex < 0)
        {
            return remaining.Trim();
        }

        return remaining.Slice(0, delimiterIndex).Trim();
    }

    private static string GetOperationFromUri(ReadOnlySpan<char> uri)
    {
        // Extract operation from URI like: http://localhost:55952/v1/rest/ingest/testdb/TestTable
        // Should return "ingest"

        var pathStart = uri.IndexOf("://");
        if (pathStart >= 0)
        {
            var afterScheme = uri.Slice(pathStart + 3);
            var pathIndex = afterScheme.IndexOf('/');
            if (pathIndex >= 0)
            {
                var path = afterScheme.Slice(pathIndex + 1);

                // Skip version (v1)
                var nextSlash = path.IndexOf('/');
                if (nextSlash >= 0)
                {
                    path = path.Slice(nextSlash + 1);

                    // Skip rest
                    nextSlash = path.IndexOf('/');
                    if (nextSlash >= 0)
                    {
                        path = path.Slice(nextSlash + 1);

                        // Get operation (ingest, query, etc.)
                        nextSlash = path.IndexOf('/');
                        if (nextSlash >= 0)
                        {
                            return path.Slice(0, nextSlash).ToString();
                        }
                    }
                }
            }
        }

        return string.Empty;
    }

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

        // Remove port if present
        var portIndex = host.IndexOf(':');
        if (portIndex >= 0)
        {
            host = host.Slice(0, portIndex);
        }

        return host.ToString();
    }
}
