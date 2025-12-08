using Azure.Identity;
using AzureKusto.Worker;
using Kusto.Cloud.Platform.Utils;
using Kusto.Data;
using Kusto.Data.Net.Client;
using Kusto.Ingest;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Polly;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

// Configure OpenTelemetry for Kusto
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddKustoInstrumentation(o =>
        {
            o.Enrich = (activity, record) =>
            {
                // Example of adding custom tags to the Activity based on Kusto trace records
                Console.WriteLine($"Kusto Trace Record: {record.Message}");
            };
            o.RecordQueryText = true;
        });
    })
    .WithMetrics(metrics =>
    {
        metrics.AddKustoInstrumentation();
    });

var connectionString = builder.Configuration.GetConnectionString("testdb");

var connectionStringBuilder = new KustoConnectionStringBuilder(connectionString);
if (connectionStringBuilder.DataSourceUri.Contains("kusto.windows.net"))
{
    connectionStringBuilder = connectionStringBuilder.WithAadAzureTokenCredentialsAuthentication(new DefaultAzureCredential());
}

builder.Services.AddSingleton(sp =>
{
    return KustoClientFactory.CreateCslQueryProvider(connectionStringBuilder);
});
builder.Services.AddSingleton(sp =>
{
    return KustoClientFactory.CreateCslAdminProvider(connectionStringBuilder);
});
builder.Services.AddSingleton(sp =>
{
    return KustoIngestFactory.CreateStreamingIngestClient(connectionStringBuilder);
});

builder.Services.AddResiliencePipeline("kusto-resilience", builder =>
    builder.AddRetry(new()
    {
        // Retry any non-permanent exceptions
        MaxRetryAttempts = 10,
        Delay = TimeSpan.FromMilliseconds(100),
        BackoffType = DelayBackoffType.Exponential,
        ShouldHandle = new PredicateBuilder().Handle<Exception>(e => e is ICloudPlatformException cpe && !cpe.IsPermanent),
    })
);

builder.Services.AddOptions<WorkerOptions>();

builder.Services.AddHostedService<QueryWorker>();
builder.Services.AddHostedService<IngestionWorker>();

var app = builder.Build();

app.Run();
