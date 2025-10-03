// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using System.Globalization;
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
        _logger.LogInformation("Starting ingestion worker");

        // Option 2: Seed as part of worker startup
        // This is another approach that is likely more versatile, where seeding occurs as part of startup (optionally only in development).
        await ExecuteViaAdminClientAsync();
        await ExecuteViaIngestClientAsync();

        _logger.LogInformation("Ingestion complete");
        _workerOptions.CurrentValue.IsIngestionComplete = true;
    }

    private async Task ExecuteViaIngestClientAsync()
    {
        var command =
            $$"""
                .create-merge table {{_workerOptions.CurrentValue.TableName}} (Id: int, Name: string, Timestamp: datetime)
            """;
        await _adminClient.ExecuteControlCommandAsync(_adminClient.DefaultDatabaseName, command);

        command =
            $$"""
                .alter table {{_workerOptions.CurrentValue.TableName}} policy streamingingestion '{"IsEnabled": true}'
            """;
        await _adminClient.ExecuteControlCommandAsync(_adminClient.DefaultDatabaseName, command);

        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Timestamp", typeof(DateTime));

        table.Rows.Add(1111, "Dave", DateTime.Parse("2024-01-01T10:00:00Z", CultureInfo.InvariantCulture));
        table.Rows.Add(2222, "Eve", DateTime.Parse("2024-01-01T11:00:00Z", CultureInfo.InvariantCulture));
        table.Rows.Add(3333, "Frank", DateTime.Parse("2024-01-01T12:00:00Z", CultureInfo.InvariantCulture));

        var ingestionProps = new KustoIngestionProperties
        {
            DatabaseName = _adminClient.DefaultDatabaseName, // TODO: Default database name
            TableName = _workerOptions.CurrentValue.TableName,
            Format = DataSourceFormat.csv
        };

        using var reader = table.CreateDataReader();
        try
        {
            await _ingestClient.IngestFromDataReaderAsync(reader, ingestionProps);
        }
        catch (Exception e)
        {
            Console.Write(e);
            throw;
        }
    }

    private async Task ExecuteViaAdminClientAsync()
    {
        var command =
            $"""
                .create-merge table {_workerOptions.CurrentValue.TableName} (Id: int, Name: string, Timestamp: datetime)
            """;
        await _adminClient.ExecuteControlCommandAsync(_adminClient.DefaultDatabaseName, command);

        command =
            $"""
                .ingest inline into table {_workerOptions.CurrentValue.TableName} <|
                    11,"George",datetime(2024-02-01T10:00:00Z)
                    22,"Henry",datetime(2024-02-02T11:00:00Z)
                    33,"Ian",datetime(2024-02-03T12:00:00Z)
            """;
        await _adminClient.ExecuteControlCommandAsync(_adminClient.DefaultDatabaseName, command);
    }
}
