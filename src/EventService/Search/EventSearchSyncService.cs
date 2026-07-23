using Elastic.Clients.Elasticsearch;
using EventService.Data;
using EventService.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.Replication;
using Npgsql.Replication.PgOutput;
using Npgsql.Replication.PgOutput.Messages;

namespace EventService.Search;

// Keeps the Elasticsearch "events" index in sync with Postgres via CDC
// (logical replication / WAL streaming) instead of dual-writing from the
// controller on every change -- the controller doesn't know or care that a
// search index exists at all. Scope is deliberately narrow: only the
// "Events" table is replicated, and only the changed row's id is actually
// read off the wire. On insert/update, that id is used to re-read the full
// Event+Venue+Performer join from Postgres via EF Core and upsert the
// resulting denormalized document into Elasticsearch -- reconstructing the
// join purely from WAL deltas across three separate tables would be far
// more complex for no real benefit at this project's scale.
public class EventSearchSyncService(
    IServiceScopeFactory scopeFactory,
    ElasticsearchClient esClient,
    IConfiguration config,
    ILogger<EventSearchSyncService> logger) : BackgroundService
{
    public const string IndexName = "events";
    private const string PublicationName = "event_search_publication";
    private const string SlotName = "event_search_slot";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = config.GetConnectionString("EventDb")!;

        var slotAlreadyExisted = await EnsurePublicationAndSlotAsync(connectionString, stoppingToken);
        await BackfillAsync(stoppingToken);

        await using var replicationConnection = new LogicalReplicationConnection(connectionString);
        await replicationConnection.Open(stoppingToken);

        var slot = slotAlreadyExisted
            ? new PgOutputReplicationSlot(new ReplicationSlotOptions(SlotName, (string?)null))
            : await replicationConnection.CreatePgOutputReplicationSlot(
                SlotName, false, null, false, stoppingToken);

        logger.LogInformation("Starting logical replication stream for event search sync (slot: {Slot}).", SlotName);

        var options = new PgOutputReplicationOptions(PublicationName, PgOutputProtocolVersion.V1);

        await foreach (var message in replicationConnection.StartReplication(slot, options, stoppingToken))
        {
            switch (message)
            {
                case InsertMessage insert:
                    await UpsertAsync(await ReadEventIdAsync(insert.NewRow, stoppingToken), stoppingToken);
                    break;

                case UpdateMessage update:
                    await UpsertAsync(await ReadEventIdAsync(update.NewRow, stoppingToken), stoppingToken);
                    break;

                case KeyDeleteMessage keyDelete:
                    await DeleteAsync(await ReadEventIdAsync(keyDelete.Key, stoppingToken), stoppingToken);
                    break;

                case FullDeleteMessage fullDelete:
                    await DeleteAsync(await ReadEventIdAsync(fullDelete.OldRow, stoppingToken), stoppingToken);
                    break;
            }

            replicationConnection.SetReplicationStatus(message.WalEnd);
        }
    }

    // Publications/slots are plain Postgres server objects, not something Npgsql's
    // replication API creates from config -- checked/created here over a regular SQL
    // connection, since the replication-protocol connection used above can only run
    // a handful of dedicated replication commands, not arbitrary SQL.
    private async Task<bool> EnsurePublicationAndSlotAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using (var pubCheck = new NpgsqlCommand(
            "SELECT 1 FROM pg_publication WHERE pubname = @name", conn))
        {
            pubCheck.Parameters.AddWithValue("name", PublicationName);
            if (await pubCheck.ExecuteScalarAsync(ct) is null)
            {
                await using var create = new NpgsqlCommand(
                    $"CREATE PUBLICATION \"{PublicationName}\" FOR TABLE \"Events\"", conn);
                await create.ExecuteNonQueryAsync(ct);
                logger.LogInformation("Created Postgres publication {Publication}.", PublicationName);
            }
        }

        await using var slotCheck = new NpgsqlCommand(
            "SELECT 1 FROM pg_replication_slots WHERE slot_name = @name", conn);
        slotCheck.Parameters.AddWithValue("name", SlotName);
        return await slotCheck.ExecuteScalarAsync(ct) is not null;
    }

    private async Task BackfillAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventDbContext>();

        var events = await db.Events.AsNoTracking()
            .Include(e => e.Venue)
            .Include(e => e.Performer)
            .ToListAsync(ct);

        foreach (var e in events)
        {
            await esClient.IndexAsync(ToDocument(e), IndexName, e.Id, ct);
        }

        logger.LogInformation("Backfilled {Count} events into the search index.", events.Count);
    }

    private async Task UpsertAsync(Guid eventId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventDbContext>();

        var e = await db.Events.AsNoTracking()
            .Include(x => x.Venue)
            .Include(x => x.Performer)
            .FirstOrDefaultAsync(x => x.Id == eventId, ct);

        if (e is null)
        {
            // Deleted again before we got to processing this change -- nothing to index.
            return;
        }

        await esClient.IndexAsync(ToDocument(e), IndexName, e.Id, ct);
        logger.LogInformation("Upserted event {EventId} into search index.", eventId);
    }

    private async Task DeleteAsync(Guid eventId, CancellationToken ct)
    {
        await esClient.DeleteAsync(IndexName, eventId, ct);
        logger.LogInformation("Removed event {EventId} from search index.", eventId);
    }

    private static EventSearchDocument ToDocument(Event e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Description = e.Description,
        Date = e.Date,
        VenueId = e.VenueId,
        VenueName = e.Venue!.Name,
        VenueAddress = e.Venue.Address,
        VenueCapacity = e.Venue.Capacity,
        PerformerId = e.PerformerId,
        PerformerName = e.Performer!.Name,
        PerformerType = e.Performer.Type,
    };

    // Only the row's id is read off the WAL -- everything else about the change is
    // re-fetched from Postgres (see UpsertAsync) rather than reconstructed here.
    // Read as string and parsed rather than Get<Guid>() directly: pgoutput streams
    // values in text format by default, so Npgsql reports the field's type as
    // 'text' at this layer regardless of the column's real Postgres type, and a
    // direct Guid read throws over that mismatch.
    private static async Task<Guid> ReadEventIdAsync(ReplicationTuple tuple, CancellationToken ct)
    {
        await foreach (var value in tuple.WithCancellation(ct))
        {
            if (value.GetFieldName() == "Id")
            {
                var raw = await value.Get<string>(ct);
                return Guid.Parse(raw);
            }
        }

        throw new InvalidOperationException("Replicated row did not contain an Id column.");
    }
}
