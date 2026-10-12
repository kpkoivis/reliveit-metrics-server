using Npgsql;

/// <summary>
/// Enforces the privacy policy's promise that gameplay statistics are kept for a limited
/// time (Metrics:StatisticsRetentionMonths, 24 by default). Runs once at startup and then
/// once a day.
///
/// Snapshots older than that are deleted. Purchase rows are sales records that accounting
/// law requires keeping, so they stay; only their statistics part - the install id, game and
/// age - is cleared once it is as old as a snapshot would be when deleted.
/// </summary>
sealed class RetentionService : BackgroundService
{
    public const int DefaultRetentionMonths = 24;

    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly NpgsqlDataSource db;
    private readonly ILogger<RetentionService> log;
    private readonly int retentionMonths;

    public RetentionService(NpgsqlDataSource db, IConfiguration configuration, ILogger<RetentionService> log)
    {
        this.db = db;
        this.log = log;
        this.retentionMonths = configuration.GetValue("Metrics:StatisticsRetentionMonths", DefaultRetentionMonths);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (this.retentionMonths <= 0)
        {
            this.log.LogWarning("Metrics:StatisticsRetentionMonths is {Months}: statistics are kept forever.", this.retentionMonths);
            return;
        }

        using PeriodicTimer timer = new(Interval);

        do
        {
            try
            {
                await this.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed run is retried tomorrow; it must not take the server down.
                this.log.LogError(ex, "Retention run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using NpgsqlConnection connection = await this.db.OpenConnectionAsync(ct);

        await using NpgsqlCommand deleteSnapshots = new(Sql.DeleteOldSnapshots, connection);
        deleteSnapshots.Parameters.AddWithValue(this.retentionMonths);
        int deleted = await deleteSnapshots.ExecuteNonQueryAsync(ct);

        await using NpgsqlCommand stripPurchases = new(Sql.StripOldPurchaseStatistics, connection);
        stripPurchases.Parameters.AddWithValue(this.retentionMonths);
        int stripped = await stripPurchases.ExecuteNonQueryAsync(ct);

        this.log.LogInformation("Retention ({Months} months): deleted {Deleted} snapshots, cleared statistics from {Stripped} purchase records",
            this.retentionMonths, deleted, stripped);
    }
}
