using System.Security.Cryptography;
using System.Text.Json;
using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Engine;
using Microsoft.Data.Sqlite;

namespace CrimeanTom.Campaign.Service;

public sealed class SqliteCampaignJournal
{
    private const int DatabaseSchemaVersion = 2;
    private readonly string _connectionString;

    public SqliteCampaignJournal(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A campaign database path is required.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA synchronous=FULL;", cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA foreign_keys=ON;", cancellationToken);

        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        var current = Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken));
        if (current < 0 || current > DatabaseSchemaVersion)
        {
            throw Conflict("unsupported-database-schema", $"Database schema {current} is not supported.");
        }

        if (current < DatabaseSchemaVersion)
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            if (current == 0)
            {
                await ExecuteAsync(connection, transaction, SchemaSql, cancellationToken);
                current = 1;
            }
            if (current == 1)
            {
                await ExecuteAsync(connection, transaction, SnapshotSchemaSql, cancellationToken);
            }
            await ExecuteAsync(connection, transaction, $"PRAGMA user_version={DatabaseSchemaVersion};", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task CreateCampaignAsync(CampaignMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _ = CampaignState.Create(metadata);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO campaigns (
                campaign_id, name, seed, catalog_fingerprint, campaign_schema_version,
                algorithm_version, head, writer_epoch)
            VALUES ($campaign_id, $name, $seed, $catalog, $schema, $algorithm, 0, 0)
            ON CONFLICT(campaign_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$campaign_id", metadata.CampaignId.ToString());
        command.Parameters.AddWithValue("$name", metadata.Name);
        command.Parameters.AddWithValue("$seed", metadata.Seed);
        command.Parameters.AddWithValue("$catalog", metadata.CatalogFingerprint);
        command.Parameters.AddWithValue("$schema", metadata.SchemaVersion);
        command.Parameters.AddWithValue("$algorithm", metadata.AlgorithmVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var stored = await ReadMetadataAsync(metadata.CampaignId, cancellationToken);
        if (stored != metadata)
        {
            throw Conflict("campaign-metadata-conflict", "The campaign ID already exists with different metadata.");
        }
    }

    public async Task<CampaignMetadata> ReadMetadataAsync(
        CampaignId campaignId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, seed, catalog_fingerprint, campaign_schema_version, algorithm_version
            FROM campaigns WHERE campaign_id = $campaign_id;
            """;
        command.Parameters.AddWithValue("$campaign_id", campaignId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Conflict("campaign-missing", "The campaign does not exist.");
        }

        return new CampaignMetadata(campaignId, reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
            reader.GetInt32(3), reader.GetInt32(4));
    }

    public async Task<CampaignWriterLease> AcquireWriterAsync(
        CampaignId campaignId,
        CoordinatorId coordinatorId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        CampaignInvariants.RequireId(campaignId);
        CampaignInvariants.RequireId(coordinatorId);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var expiresAt = now.Add(leaseDuration);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE campaigns
            SET writer_epoch = writer_epoch + 1,
                writer_owner = $owner,
                writer_lease_expires_unix_ms = $expires
            WHERE campaign_id = $campaign_id
              AND (writer_owner IS NULL OR writer_lease_expires_unix_ms <= $now OR writer_owner = $owner)
            RETURNING writer_epoch;
            """;
        command.Parameters.AddWithValue("$owner", coordinatorId.ToString());
        command.Parameters.AddWithValue("$expires", expiresAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$campaign_id", campaignId.ToString());
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
        {
            if (!await CampaignExistsAsync(connection, campaignId, cancellationToken))
            {
                throw Conflict("campaign-missing", "The campaign does not exist.");
            }

            throw Conflict("writer-lease-unavailable", "Another coordinator holds the campaign writer lease.");
        }

        return new CampaignWriterLease(campaignId, coordinatorId, new WriterEpoch(Convert.ToInt64(result)), expiresAt);
    }

    public async Task<CampaignWriterLease> RenewWriterAsync(
        CampaignWriterLease lease,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var expiresAt = now.Add(leaseDuration);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE campaigns SET writer_lease_expires_unix_ms = $expires
            WHERE campaign_id = $campaign_id AND writer_owner = $owner
              AND writer_epoch = $epoch AND writer_lease_expires_unix_ms > $now;
            """;
        command.Parameters.AddWithValue("$expires", expiresAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$campaign_id", lease.CampaignId.ToString());
        command.Parameters.AddWithValue("$owner", lease.CoordinatorId.ToString());
        command.Parameters.AddWithValue("$epoch", lease.WriterEpoch.Value);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("writer-fenced", "The writer lease is expired or no longer current.");
        }

        return lease with { ExpiresAt = expiresAt };
    }

    public async Task ReleaseWriterAsync(
        CampaignWriterLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE campaigns SET writer_owner = NULL, writer_lease_expires_unix_ms = NULL
            WHERE campaign_id = $campaign_id AND writer_owner = $owner AND writer_epoch = $epoch;
            """;
        command.Parameters.AddWithValue("$campaign_id", lease.CampaignId.ToString());
        command.Parameters.AddWithValue("$owner", lease.CoordinatorId.ToString());
        command.Parameters.AddWithValue("$epoch", lease.WriterEpoch.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CampaignAppendResult> AppendAsync(
        CampaignWriterLease lease,
        CampaignCommand command,
        CommittedCampaignEvent campaignEvent,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(campaignEvent);
        var commandJson = CampaignContractJson.Serialize(command);
        var eventJson = CampaignContractJson.Serialize(campaignEvent);
        var commandHash = Hash(commandJson);
        var eventHash = Hash(eventJson);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await RequireCurrentWriterAsync(connection, transaction, lease, now, cancellationToken);

        var duplicate = await ReadDuplicateAsync(connection, transaction, command, commandHash, cancellationToken);
        if (duplicate is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CampaignAppendResult(duplicate, Duplicate: true);
        }

        if (campaignEvent.CampaignId != lease.CampaignId
            || command.CampaignId != lease.CampaignId
            || campaignEvent.CausalCommandId != command.CommandId
            || campaignEvent.Subject != command.Subject
            || campaignEvent.EffectiveTime != command.EffectiveAt
            || campaignEvent.SchemaVersion != CampaignSchema.Version
            || campaignEvent.AlgorithmVersion != CampaignSchema.AlgorithmVersion
            || command.SchemaVersion != CampaignSchema.Version
            || campaignEvent.CommitPosition.Value <= 0
            || campaignEvent.NewSubjectRevision != command.ExpectedSubjectRevision.Next())
        {
            throw Conflict("append-envelope-conflict", "The command, event, and writer lease do not describe one append.");
        }

        var expectedHead = campaignEvent.CommitPosition.Value - 1;
        await RequireHeadAndRevisionAsync(connection, transaction, command, expectedHead, cancellationToken);
        await InsertCommandAsync(connection, transaction, command, campaignEvent.CommitPosition, commandJson, commandHash, cancellationToken);
        await InsertEventAsync(connection, transaction, campaignEvent, eventJson, eventHash, cancellationToken);
        await UpdateSubjectRevisionAsync(connection, transaction, campaignEvent, cancellationToken);
        await AdvanceHeadAsync(connection, transaction, lease, now, expectedHead, campaignEvent.CommitPosition, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CampaignAppendResult(campaignEvent, Duplicate: false);
    }

    public async Task<CampaignSnapshotInfo> SaveSnapshotAsync(
        CampaignWriterLease lease,
        CampaignState state,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(state);
        CampaignStateValidator.Validate(state);
        var snapshotJson = CampaignCanonicalSerializer.Serialize(state);
        var stateHash = Hash(snapshotJson);
        var decoded = CampaignCanonicalSerializer.Deserialize(snapshotJson);
        if (CampaignCanonicalSerializer.ComputeHash(decoded) != stateHash)
        {
            throw Conflict("snapshot-roundtrip", "The canonical snapshot did not survive its own decoder.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await RequireCurrentWriterAsync(connection, transaction, lease, now, cancellationToken);
        var durableHead = await ReadHeadAsync(connection, transaction, state.Metadata.CampaignId, cancellationToken);
        if (lease.CampaignId != state.Metadata.CampaignId || durableHead != state.Head)
        {
            throw Conflict("snapshot-head-conflict", "A snapshot must describe the current durable campaign head.");
        }

        var recovered = await RecoverAsync(connection, transaction, state.Metadata.CampaignId, durableHead, cancellationToken);
        if (recovered.CanonicalHash != stateHash)
        {
            throw Conflict("snapshot-state-conflict", "The snapshot does not match replay of the durable journal.");
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO snapshots (campaign_id, commit_position, state_hash, snapshot_json)
            VALUES ($campaign_id, $position, $hash, $json)
            ON CONFLICT(campaign_id, commit_position) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$campaign_id", state.Metadata.CampaignId.ToString());
        insert.Parameters.AddWithValue("$position", state.Head.Value);
        insert.Parameters.AddWithValue("$hash", stateHash);
        insert.Parameters.AddWithValue("$json", snapshotJson);
        var inserted = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;

        if (!inserted)
        {
            await using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT state_hash, snapshot_json FROM snapshots
                WHERE campaign_id = $campaign_id AND commit_position = $position;
                """;
            existing.Parameters.AddWithValue("$campaign_id", state.Metadata.CampaignId.ToString());
            existing.Parameters.AddWithValue("$position", state.Head.Value);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || reader.GetString(0) != stateHash
                || !((byte[])reader[1]).AsSpan().SequenceEqual(snapshotJson))
            {
                throw Conflict("snapshot-idempotency-conflict", "The snapshot position already contains different state.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new CampaignSnapshotInfo(state.Metadata.CampaignId, state.Head, stateHash, Duplicate: !inserted);
    }

    public async Task<CampaignRecoveryResult> RecoverAsync(
        CampaignId campaignId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var head = await ReadHeadAsync(connection, transaction, campaignId, cancellationToken);
        var result = await RecoverAsync(connection, transaction, campaignId, head, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<CommittedCampaignEvent>> ReadEventsAsync(
        CampaignId campaignId,
        CommitPosition after,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_json, event_hash FROM events
            WHERE campaign_id = $campaign_id AND commit_position > $after
            ORDER BY commit_position;
            """;
        command.Parameters.AddWithValue("$campaign_id", campaignId.ToString());
        command.Parameters.AddWithValue("$after", after.Value);
        var result = new List<CommittedCampaignEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var json = (byte[])reader[0];
            VerifyHash(json, reader.GetString(1), "event-corrupt");
            result.Add(CampaignContractJson.DeserializeEvent(json));
        }
        return result;
    }

    public async Task<CommitPosition> ReadHeadAsync(
        CampaignId campaignId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadHeadAsync(connection, null, campaignId, cancellationToken);
    }

    private static async Task<CampaignRecoveryResult> RecoverAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CampaignId campaignId,
        CommitPosition durableHead,
        CancellationToken cancellationToken)
    {
        var metadata = await ReadMetadataAsync(connection, transaction, campaignId, cancellationToken);
        var state = CampaignState.Create(metadata);
        var snapshotPosition = CommitPosition.Origin;

        await using (var snapshot = connection.CreateCommand())
        {
            snapshot.Transaction = transaction;
            snapshot.CommandText = """
                SELECT commit_position, state_hash, snapshot_json FROM snapshots
                WHERE campaign_id = $campaign_id AND commit_position <= $head
                ORDER BY commit_position DESC LIMIT 1;
                """;
            snapshot.Parameters.AddWithValue("$campaign_id", campaignId.ToString());
            snapshot.Parameters.AddWithValue("$head", durableHead.Value);
            await using var reader = await snapshot.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                snapshotPosition = new CommitPosition(reader.GetInt64(0));
                var expectedHash = reader.GetString(1);
                var json = (byte[])reader[2];
                VerifyHash(json, expectedHash, "snapshot-corrupt");
                try
                {
                    state = CampaignCanonicalSerializer.Deserialize(json);
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
                {
                    throw Conflict("snapshot-corrupt", $"The newest snapshot is invalid: {exception.Message}");
                }
                if (state.Metadata != metadata || state.Head != snapshotPosition
                    || CampaignCanonicalSerializer.ComputeHash(state) != expectedHash)
                {
                    throw Conflict("snapshot-corrupt", "The newest snapshot identity, position, metadata, or hash is invalid.");
                }
            }
        }

        await using (var events = connection.CreateCommand())
        {
            events.Transaction = transaction;
            events.CommandText = """
                SELECT commit_position, event_json, event_hash FROM events
                WHERE campaign_id = $campaign_id AND commit_position > $after AND commit_position <= $head
                ORDER BY commit_position;
                """;
            events.Parameters.AddWithValue("$campaign_id", campaignId.ToString());
            events.Parameters.AddWithValue("$after", state.Head.Value);
            events.Parameters.AddWithValue("$head", durableHead.Value);
            await using var reader = await events.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var position = new CommitPosition(reader.GetInt64(0));
                if (position != state.Head.Next())
                {
                    throw Conflict("journal-gap", "The durable journal contains a commit-position gap.");
                }
                var json = (byte[])reader[1];
                VerifyHash(json, reader.GetString(2), "event-corrupt");
                CommittedCampaignEvent campaignEvent;
                try
                {
                    campaignEvent = CampaignContractJson.DeserializeEvent(json);
                    state = CampaignKernel.Apply(state, campaignEvent);
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
                {
                    throw Conflict("event-corrupt", $"Event {position.Value} cannot replay: {exception.Message}");
                }
            }
        }

        if (state.Head != durableHead)
        {
            throw Conflict("journal-gap", "The durable journal does not reach the campaign head.");
        }
        CampaignStateValidator.Validate(state);
        return new CampaignRecoveryResult(state, snapshotPosition, CampaignCanonicalSerializer.ComputeHash(state));
    }

    private static async Task<CampaignMetadata> ReadMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CampaignId campaignId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT name, seed, catalog_fingerprint, campaign_schema_version, algorithm_version
            FROM campaigns WHERE campaign_id = $campaign_id;
            """;
        command.Parameters.AddWithValue("$campaign_id", campaignId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Conflict("campaign-missing", "The campaign does not exist.");
        }
        return new CampaignMetadata(campaignId, reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
            reader.GetInt32(3), reader.GetInt32(4));
    }

    private static async Task<CommitPosition> ReadHeadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CampaignId campaignId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT head FROM campaigns WHERE campaign_id = $campaign_id;";
        command.Parameters.AddWithValue("$campaign_id", campaignId.ToString());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null
            ? throw Conflict("campaign-missing", "The campaign does not exist.")
            : new CommitPosition(Convert.ToInt64(result));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA foreign_keys=ON;", cancellationToken);
        return connection;
    }

    private static async Task RequireCurrentWriterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CampaignWriterLease lease,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM campaigns WHERE campaign_id = $campaign_id AND writer_owner = $owner
              AND writer_epoch = $epoch AND writer_lease_expires_unix_ms > $now;
            """;
        command.Parameters.AddWithValue("$campaign_id", lease.CampaignId.ToString());
        command.Parameters.AddWithValue("$owner", lease.CoordinatorId.ToString());
        command.Parameters.AddWithValue("$epoch", lease.WriterEpoch.Value);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw Conflict("writer-fenced", "The writer lease is expired or no longer current.");
        }
    }

    private static async Task<CommittedCampaignEvent?> ReadDuplicateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CampaignCommand commandValue,
        string commandHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT command_id, idempotency_key, command_hash, event_json, event_hash
            FROM commands JOIN events USING (campaign_id, commit_position)
            WHERE campaign_id = $campaign_id AND (command_id = $command_id OR idempotency_key = $idempotency_key)
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$campaign_id", commandValue.CampaignId.ToString());
        command.Parameters.AddWithValue("$command_id", commandValue.CommandId.ToString());
        command.Parameters.AddWithValue("$idempotency_key", commandValue.IdempotencyKey.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var matches = reader.GetString(0) == commandValue.CommandId.ToString()
            && reader.GetString(1) == commandValue.IdempotencyKey.Value
            && reader.GetString(2) == commandHash;
        var json = (byte[])reader[3];
        var storedEventHash = reader.GetString(4);
        if (await reader.ReadAsync(cancellationToken) || !matches)
        {
            throw Conflict("idempotency-conflict", "The command ID or idempotency key was reused with different content.");
        }
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(storedEventHash),
                SHA256.HashData(json)))
        {
            throw Conflict("event-corrupt", "The duplicate command's stored event failed its content hash.");
        }
        return CampaignContractJson.DeserializeEvent(json);
    }

    private static async Task RequireHeadAndRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CampaignCommand commandValue,
        long expectedHead,
        CancellationToken cancellationToken)
    {
        await using var head = connection.CreateCommand();
        head.Transaction = transaction;
        head.CommandText = "SELECT head FROM campaigns WHERE campaign_id = $campaign_id;";
        head.Parameters.AddWithValue("$campaign_id", commandValue.CampaignId.ToString());
        if (Convert.ToInt64(await head.ExecuteScalarAsync(cancellationToken)) != expectedHead)
        {
            throw Conflict("head-conflict", "The campaign durable head changed before append.");
        }

        var current = expectedHead;
        if (commandValue.Subject.Kind != CampaignSubjectKind.Campaign)
        {
            await using var revision = connection.CreateCommand();
            revision.Transaction = transaction;
            revision.CommandText = """
                SELECT revision FROM subject_revisions
                WHERE campaign_id = $campaign_id AND subject_kind = $kind AND subject_id = $subject_id;
                """;
            revision.Parameters.AddWithValue("$campaign_id", commandValue.CampaignId.ToString());
            revision.Parameters.AddWithValue("$kind", (int)commandValue.Subject.Kind);
            revision.Parameters.AddWithValue("$subject_id", commandValue.Subject.Value.ToString("N"));
            var stored = await revision.ExecuteScalarAsync(cancellationToken);
            current = stored is null ? 0 : Convert.ToInt64(stored);
        }
        if (current != commandValue.ExpectedSubjectRevision.Value)
        {
            throw Conflict("revision-conflict", $"Expected revision {commandValue.ExpectedSubjectRevision.Value}, durable revision {current}.");
        }
    }

    private static async Task InsertCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CampaignCommand commandValue,
        CommitPosition position,
        byte[] json,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO commands (campaign_id, command_id, idempotency_key, command_hash, command_json, commit_position)
            VALUES ($campaign_id, $command_id, $idempotency_key, $hash, $json, $position);
            """;
        command.Parameters.AddWithValue("$campaign_id", commandValue.CampaignId.ToString());
        command.Parameters.AddWithValue("$command_id", commandValue.CommandId.ToString());
        command.Parameters.AddWithValue("$idempotency_key", commandValue.IdempotencyKey.Value);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$position", position.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CommittedCampaignEvent eventValue,
        byte[] json,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO events (
                campaign_id, commit_position, event_id, event_hash, event_json, subject_kind,
                subject_id, new_subject_revision, causal_command_id)
            VALUES ($campaign_id, $position, $event_id, $hash, $json, $kind, $subject_id, $revision, $causal);
            """;
        command.Parameters.AddWithValue("$campaign_id", eventValue.CampaignId.ToString());
        command.Parameters.AddWithValue("$position", eventValue.CommitPosition.Value);
        command.Parameters.AddWithValue("$event_id", eventValue.EventId.ToString());
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$kind", (int)eventValue.Subject.Kind);
        command.Parameters.AddWithValue("$subject_id", eventValue.Subject.Value.ToString("N"));
        command.Parameters.AddWithValue("$revision", eventValue.NewSubjectRevision.Value);
        command.Parameters.AddWithValue("$causal", eventValue.CausalCommandId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateSubjectRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CommittedCampaignEvent eventValue,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO subject_revisions (campaign_id, subject_kind, subject_id, revision)
            VALUES ($campaign_id, $kind, $subject_id, $revision)
            ON CONFLICT(campaign_id, subject_kind, subject_id) DO UPDATE SET revision = excluded.revision;
            """;
        command.Parameters.AddWithValue("$campaign_id", eventValue.CampaignId.ToString());
        command.Parameters.AddWithValue("$kind", (int)eventValue.Subject.Kind);
        command.Parameters.AddWithValue("$subject_id", eventValue.Subject.Value.ToString("N"));
        command.Parameters.AddWithValue("$revision", eventValue.NewSubjectRevision.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AdvanceHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CampaignWriterLease lease,
        DateTimeOffset now,
        long expectedHead,
        CommitPosition newHead,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE campaigns SET head = $new_head
            WHERE campaign_id = $campaign_id AND head = $expected_head AND writer_owner = $owner
              AND writer_epoch = $epoch AND writer_lease_expires_unix_ms > $now;
            """;
        command.Parameters.AddWithValue("$new_head", newHead.Value);
        command.Parameters.AddWithValue("$campaign_id", lease.CampaignId.ToString());
        command.Parameters.AddWithValue("$expected_head", expectedHead);
        command.Parameters.AddWithValue("$owner", lease.CoordinatorId.ToString());
        command.Parameters.AddWithValue("$epoch", lease.WriterEpoch.Value);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("writer-fenced", "The writer was fenced before the durable head advanced.");
        }
    }

    private static async Task<bool> CampaignExistsAsync(
        SqliteConnection connection,
        CampaignId campaignId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM campaigns WHERE campaign_id = $campaign_id;";
        command.Parameters.AddWithValue("$campaign_id", campaignId.ToString());
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static void VerifyHash(byte[] content, string expectedHash, string errorCode)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHash);
        }
        catch (FormatException)
        {
            throw Conflict(errorCode, "A stored content hash is malformed.");
        }
        if (!CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(content)))
        {
            throw Conflict(errorCode, "Stored content does not match its hash.");
        }
    }

    private static CampaignStoreConflict Conflict(string code, string message) => new(code, message);

    private const string SnapshotSchemaSql = """
        CREATE TABLE snapshots (
            campaign_id TEXT NOT NULL,
            commit_position INTEGER NOT NULL CHECK (commit_position >= 0),
            state_hash TEXT NOT NULL,
            snapshot_json BLOB NOT NULL,
            PRIMARY KEY (campaign_id, commit_position),
            FOREIGN KEY (campaign_id) REFERENCES campaigns(campaign_id)
        ) STRICT;
        """;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS campaigns (
            campaign_id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            seed INTEGER NOT NULL,
            catalog_fingerprint TEXT NOT NULL,
            campaign_schema_version INTEGER NOT NULL,
            algorithm_version INTEGER NOT NULL,
            head INTEGER NOT NULL CHECK (head >= 0),
            writer_epoch INTEGER NOT NULL CHECK (writer_epoch >= 0),
            writer_owner TEXT NULL,
            writer_lease_expires_unix_ms INTEGER NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS commands (
            campaign_id TEXT NOT NULL,
            command_id TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            command_hash TEXT NOT NULL,
            command_json BLOB NOT NULL,
            commit_position INTEGER NOT NULL CHECK (commit_position > 0),
            PRIMARY KEY (campaign_id, command_id),
            UNIQUE (campaign_id, idempotency_key),
            UNIQUE (campaign_id, commit_position),
            FOREIGN KEY (campaign_id) REFERENCES campaigns(campaign_id)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS events (
            campaign_id TEXT NOT NULL,
            commit_position INTEGER NOT NULL CHECK (commit_position > 0),
            event_id TEXT NOT NULL,
            event_hash TEXT NOT NULL,
            event_json BLOB NOT NULL,
            subject_kind INTEGER NOT NULL,
            subject_id TEXT NOT NULL,
            new_subject_revision INTEGER NOT NULL CHECK (new_subject_revision > 0),
            causal_command_id TEXT NOT NULL,
            PRIMARY KEY (campaign_id, commit_position),
            UNIQUE (campaign_id, event_id),
            FOREIGN KEY (campaign_id, causal_command_id) REFERENCES commands(campaign_id, command_id),
            FOREIGN KEY (campaign_id) REFERENCES campaigns(campaign_id)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS subject_revisions (
            campaign_id TEXT NOT NULL,
            subject_kind INTEGER NOT NULL,
            subject_id TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK (revision > 0),
            PRIMARY KEY (campaign_id, subject_kind, subject_id),
            FOREIGN KEY (campaign_id) REFERENCES campaigns(campaign_id)
        ) STRICT;
        """;
}
