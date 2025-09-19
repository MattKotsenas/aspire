// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using System.Text;
using Kusto.Data.Common;
using Kusto.Ingest;
using Microsoft.Extensions.Options;

namespace AzureKusto.Worker;

internal sealed class IngestionWorker : BackgroundService
{
    private readonly ICslAdminProvider _adminClient;
    private readonly IKustoIngestClient _ingestClient;
    private readonly IOptionsMonitor<WorkerOptions> _workerOptions;
    private readonly ILogger<QueryWorker> _logger;

    public IngestionWorker(
        ICslAdminProvider adminClient,
        IKustoIngestClient ingestClient,
        IOptionsMonitor<WorkerOptions> workerOptions,
        ILogger<QueryWorker> logger)
    {
        _adminClient = adminClient;
        _ingestClient = ingestClient;
        _workerOptions = workerOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await IngestFromStream();

        _logger.LogInformation("Ingestion complete");
        _workerOptions.CurrentValue.IsIngestionComplete = true;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0051:Remove unused private members", Justification = "<Pending>")]
    private async Task IngestFromAdminCommand()
    {
        // Option 2: Seed as part of worker startup
        // This is another approach that is likely more versatile, where seeding occurs as part of startup (optionally only in development).
        var command =
            $"""
            .execute database script with (ThrowOnErrors=true) <|
                 .create-merge table {_workerOptions.CurrentValue.TableName} (Id: int, Name: string, Timestamp: datetime)
                 .ingest inline into table {_workerOptions.CurrentValue.TableName} <|
                     11,"Alice",datetime(2024-01-01T10:00:00Z)
                     22,"Bob",datetime(2024-01-01T11:00:00Z)
                     33,"Charlie",datetime(2024-01-01T12:00:00Z)
            """;

        await _adminClient.ExecuteControlCommandAsync(_adminClient.DefaultDatabaseName, command);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1305:Specify IFormatProvider", Justification = "<Pending>")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0051:Remove unused private members", Justification = "<Pending>")]
    private async Task IngestFromDataReader()
    {
        // Build a DataTable matching your Kusto table schema
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Timestamp", typeof(DateTime));

        // Add the rows (same as your inline data)
        table.Rows.Add(11, "Alice", DateTime.Parse("2024-01-01T10:00:00Z"));
        table.Rows.Add(22, "Bob", DateTime.Parse("2024-01-01T11:00:00Z"));
        table.Rows.Add(33, "Charlie", DateTime.Parse("2024-01-01T12:00:00Z"));

        // Create ingestion properties
        var ingestionProps = new KustoIngestionProperties
        {
            DatabaseName = _adminClient.DefaultDatabaseName,
            TableName = _workerOptions.CurrentValue.TableName,
            Format = DataSourceFormat.csv
        };

        // Ingest using DataReader
        using var reader = table.CreateDataReader();
        await _ingestClient.IngestFromDataReaderAsync(reader, ingestionProps);
    }

    private async Task IngestFromStream()
    {
        // CSV payload matching the target table schema
        // (no header line needed unless you enabled it in ingestion mapping)
        var csv = new StringBuilder();
        csv.AppendLine("11,Alice,2024-01-01T10:00:00Z");
        csv.AppendLine("22,Bob,2024-01-01T11:00:00Z");
        csv.AppendLine("33,Charlie,2024-01-01T12:00:00Z");

        // Turn it into a Stream
        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        using var stream = new MemoryStream(bytes);

        // Configure ingestion properties
        var ingestionProps = new KustoIngestionProperties(
            databaseName: _adminClient.DefaultDatabaseName,
            tableName: _workerOptions.CurrentValue.TableName
        )
        {
            Format = DataSourceFormat.csv
        };

        // Ingest from the stream
        await _ingestClient.IngestFromStreamAsync(stream, ingestionProps);
    }
}
