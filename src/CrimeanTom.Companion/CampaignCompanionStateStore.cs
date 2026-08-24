using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Transport.V1;
using CrimeanTom.Protocol;
using TransportCommittedCampaignEvent = CrimeanTom.Campaign.Transport.V1.CommittedCampaignEvent;

using Google.Protobuf;

namespace CrimeanTom.Companion;

public sealed class CampaignCompanionStateStore
{
    private const int AtomicReplaceAttempts = 25;
    private static ReadOnlySpan<byte> TacticalIslandMigratedDiscriminator =>
        "\"eventType\":\"tactical-island-migrated/v1\""u8;

    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CampaignCompanionStateStore(string root) => _root = Path.GetFullPath(root);

    public long ReadCursor()
    {
        var path = Path.Combine(_root, "cursor");
        return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var value) && value >= 0
            ? value
            : 0;
    }

    public async Task AcceptCommittedEventAsync(
        TransportCommittedCampaignEvent campaignEvent,
        CancellationToken cancellationToken)
    {
        var content = campaignEvent.EventJson.ToByteArray();
        var actualHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(actualHash, campaignEvent.EventSha256, StringComparison.Ordinal)
            || campaignEvent.CommitPosition != ReadCursor() + 1)
        {
            throw new InvalidDataException("Committed campaign catch-up is corrupt or non-contiguous.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (content.AsSpan().IndexOf(TacticalIslandMigratedDiscriminator) >= 0)
            {
                var committed = CampaignContractJson.DeserializeEvent(content);
                if (committed.CommitPosition.Value != campaignEvent.CommitPosition
                    || committed.Data is not TacticalIslandMigratedEvent migrated)
                {
                    throw new InvalidDataException("A committed tactical migration proof is malformed.");
                }

                foreach (var member in migrated.Migration.Members)
                {
                    await WriteAtomicallyAsync(
                        CommittedMigrationPath(
                            campaignEvent.CommitPosition,
                            member.DestinationAssignmentId,
                            member.DestinationAuthorityEpoch.Value),
                        "committed"u8.ToArray(),
                        cancellationToken);
                }
            }

            await WriteAtomicallyAsync(
                Path.Combine(_root, "cursor"),
                Encoding.UTF8.GetBytes(campaignEvent.CommitPosition.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StageOutboundAsync(string key, CompanionFrame frame, CancellationToken cancellationToken)
    {
        var safeKey = string.Concat(key.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        if (safeKey.Length is 0 or > 180)
        {
            throw new InvalidDataException("A bounded durable outbound key is required.");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(_root, "outbound");
            Directory.CreateDirectory(directory);
            var normalized = frame.Clone();
            normalized.Sequence = 0;
            var destination = Path.Combine(directory, $"{safeKey}.frame");
            var content = normalized.ToByteArray();
            if (File.Exists(destination))
            {
                if (!File.ReadAllBytes(destination).AsSpan().SequenceEqual(content))
                {
                    throw new InvalidDataException("A durable outbound key was reused with different bytes.");
                }
                return;
            }
            await WriteAtomicallyAsync(destination, content, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<StagedCompanionFrame> ReadOutbound()
    {
        var directory = Path.Combine(_root, "outbound");
        if (!Directory.Exists(directory))
        {
            return [];
        }
        return Directory.EnumerateFiles(directory, "*.frame")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new StagedCompanionFrame(
                Path.GetFileNameWithoutExtension(path),
                CompanionFrame.Parser.ParseFrom(File.ReadAllBytes(path))))
            .ToArray();
    }

    public async Task SaveAssignmentAsync(TacticalAssignment assignment, CancellationToken cancellationToken)
    {
        var safeId = SafeKey(assignment.AssignmentId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = Path.Combine(_root, "assignments", $"{safeId}.assignment");
            var content = assignment.ToByteArray();
            if (File.Exists(path))
            {
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                {
                    throw new InvalidDataException("A durable assignment identity was reused with different bytes.");
                }
                return;
            }
            await WriteAtomicallyAsync(path, content, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public TacticalAssignment ReadAssignment(string assignmentId)
    {
        var path = Path.Combine(_root, "assignments", $"{SafeKey(assignmentId)}.assignment");
        return File.Exists(path)
            ? TacticalAssignment.Parser.ParseFrom(File.ReadAllBytes(path))
            : throw new InvalidDataException("The durable tactical assignment baseline is missing.");
    }

    public IReadOnlyList<TacticalAssignment> ReadAssignments()
    {
        var directory = Path.Combine(_root, "assignments");
        if (!Directory.Exists(directory))
        {
            return [];
        }
        return Directory.EnumerateFiles(directory, "*.assignment")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => TacticalAssignment.Parser.ParseFrom(File.ReadAllBytes(path)))
            .ToArray();
    }

    public bool HasCompletedPlayerFocus(string assignmentId, long authorityEpoch) =>
        File.Exists(PlayerFocusCompletedPath(assignmentId, authorityEpoch));

    public async Task SaveCompletedPlayerFocusAsync(
        string assignmentId,
        long authorityEpoch,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAtomicallyAsync(
                PlayerFocusCompletedPath(assignmentId, authorityEpoch),
                "completed"u8.ToArray(),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveGameRevisionAsync(
        string assignmentId,
        long revision,
        CancellationToken cancellationToken)
    {
        if (revision < 0)
        {
            throw new InvalidDataException("A game entity revision cannot be negative.");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAtomicallyAsync(
                Path.Combine(_root, "assignments", $"{SafeKey(assignmentId)}.revision"),
                Encoding.UTF8.GetBytes(revision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public long ReadGameRevision(string assignmentId)
    {
        var path = Path.Combine(_root, "assignments", $"{SafeKey(assignmentId)}.revision");
        return File.Exists(path)
            && long.TryParse(File.ReadAllText(path).Trim(), out var revision)
            && revision >= 0
                ? revision
                : throw new InvalidDataException("The durable game entity revision is missing or invalid.");
    }

    public async Task SaveMaterializedGameSessionAsync(
        string assignmentId,
        string gameSessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gameSessionId) || gameSessionId.Length > 128)
        {
            throw new InvalidDataException("A bounded game session id is required.");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAtomicallyAsync(
                Path.Combine(_root, "assignments", $"{SafeKey(assignmentId)}.game-session"),
                Encoding.UTF8.GetBytes(gameSessionId),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? TryReadMaterializedGameSession(string assignmentId)
    {
        var path = Path.Combine(
            _root,
            "assignments",
            $"{SafeKey(assignmentId)}.game-session");
        if (!File.Exists(path))
        {
            return null;
        }
        var value = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(value) || value.Length > 128 ? null : value;
    }

    public async Task AcknowledgeOutboundAsync(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = Path.Combine(_root, "outbound", $"{key}.frame");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveStagedCheckpointAsync(
        string assignmentId,
        long authorityEpoch,
        string checkpointSha256,
        CancellationToken cancellationToken)
    {
        if (checkpointSha256.Length != 64 || checkpointSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("A staged checkpoint hash is invalid.");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAtomicallyAsync(
                StagedCheckpointPath(assignmentId, authorityEpoch),
                Encoding.UTF8.GetBytes(checkpointSha256.ToLowerInvariant()),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string ReadStagedCheckpoint(string assignmentId, long authorityEpoch)
    {
        var path = StagedCheckpointPath(assignmentId, authorityEpoch);
        return File.Exists(path)
            ? File.ReadAllText(path).Trim()
            : throw new InvalidDataException("The durable staged checkpoint hash is missing.");
    }

    public async Task SaveStagedIdempotencyKeyAsync(
        string assignmentId,
        long authorityEpoch,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!OwnedMutationContracts.IsValidIdempotencyKey(idempotencyKey))
        {
            throw new InvalidDataException("A staged checkpoint idempotency key is invalid.");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAtomicallyAsync(
                StagedIdempotencyPath(assignmentId, authorityEpoch),
                Encoding.UTF8.GetBytes(idempotencyKey),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? TryReadStagedIdempotencyKey(string assignmentId, long authorityEpoch)
    {
        var path = StagedIdempotencyPath(assignmentId, authorityEpoch);
        if (!File.Exists(path))
        {
            return null;
        }

        var value = File.ReadAllText(path).Trim();
        return OwnedMutationContracts.IsValidIdempotencyKey(value) ? value : null;
    }

    public async Task SaveActivationGrantAsync(
        string assignmentId,
        long authorityEpoch,
        long migrationCommitPosition,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAtomicallyAsync(
                ActivationGrantPath(assignmentId, authorityEpoch),
                Encoding.UTF8.GetBytes(migrationCommitPosition.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool HasActivationGrant(string assignmentId, long authorityEpoch) =>
        File.Exists(ActivationGrantPath(assignmentId, authorityEpoch));

    public async Task SaveSupersededActivationGrantAsync(
        string assignmentId,
        long authorityEpoch,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidDataException("A superseded activation grant reason is required.");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAtomicallyAsync(
                SupersededActivationGrantPath(assignmentId, authorityEpoch),
                Encoding.UTF8.GetBytes(reason.Trim()),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool HasSupersededActivationGrant(string assignmentId, long authorityEpoch) =>
        File.Exists(SupersededActivationGrantPath(assignmentId, authorityEpoch));

    public string? TryReadSupersededActivationGrantReason(string assignmentId, long authorityEpoch)
    {
        var path = SupersededActivationGrantPath(assignmentId, authorityEpoch);
        if (!File.Exists(path))
        {
            return null;
        }

        var value = File.ReadAllText(path).Trim();
        return value.Length > 0 ? value : null;
    }

    public bool HasCommittedMigration(long commitPosition, string assignmentId, long authorityEpoch)
    {
        if (!TryParseAssignmentId(assignmentId, out var destinationAssignmentId))
        {
            return false;
        }

        if (File.Exists(CommittedMigrationPath(commitPosition, destinationAssignmentId, authorityEpoch)))
        {
            return true;
        }

        // Compatibility for state written before committed events were reduced to
        // bounded migration proofs. New state never writes these full JSON files.
        var events = Path.Combine(_root, "committed-events");
        if (!Directory.Exists(events))
        {
            return false;
        }
        var prefix = $"{commitPosition:D20}-";
        var match = Directory.EnumerateFiles(events, $"{prefix}*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (match is null)
        {
            return false;
        }

        var committed = CampaignContractJson.DeserializeEvent(File.ReadAllBytes(match));
        if (committed.CommitPosition.Value != commitPosition
            || committed.Data is not TacticalIslandMigratedEvent migrated)
        {
            return false;
        }

        return migrated.Migration.Members.Any(member =>
            member.DestinationAssignmentId == destinationAssignmentId
            && member.DestinationAuthorityEpoch.Value == authorityEpoch);
    }

    private static bool TryParseAssignmentId(string assignmentId, out AssignmentId parsed)
    {
        parsed = default;
        var value = assignmentId.StartsWith("assignment:", StringComparison.Ordinal)
            ? assignmentId["assignment:".Length..]
            : assignmentId;
        if (!Guid.TryParseExact(value, "N", out var guid)
            && !Guid.TryParse(value, out guid))
        {
            return false;
        }

        parsed = new AssignmentId(guid);
        return true;
    }

    private string StagedCheckpointPath(string assignmentId, long authorityEpoch) =>
        Path.Combine(_root, "assignments", $"{SafeKey(assignmentId)}.{authorityEpoch}.staged-checkpoint");

    private string StagedIdempotencyPath(string assignmentId, long authorityEpoch) =>
        Path.Combine(_root, "assignments", $"{SafeKey(assignmentId)}.{authorityEpoch}.staged-idempotency");

    private string ActivationGrantPath(string assignmentId, long authorityEpoch) =>
        Path.Combine(_root, "assignments", $"{SafeKey(assignmentId)}.{authorityEpoch}.activation-grant");

    private string SupersededActivationGrantPath(string assignmentId, long authorityEpoch) =>
        Path.Combine(_root, "assignments", $"{SafeKey(assignmentId)}.{authorityEpoch}.activation-grant-superseded");

    private string PlayerFocusCompletedPath(string assignmentId, long authorityEpoch) =>
        Path.Combine(_root, "assignments", $"{SafeKey(assignmentId)}.{authorityEpoch}.player-focus-completed");

    private string CommittedMigrationPath(
        long commitPosition,
        AssignmentId assignmentId,
        long authorityEpoch) =>
        Path.Combine(
            _root,
            "committed-migrations",
            $"{commitPosition:D20}-{assignmentId.Value:N}.{authorityEpoch}.migration");

    private static async Task WriteAtomicallyAsync(
        string destination,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            await MoveIntoPlaceAsync(temporary, destination, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task MoveIntoPlaceAsync(
        string temporary,
        string destination,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException
                && attempt < AtomicReplaceAttempts)
            {
                // Windows indexers and malware scanners can briefly open the tiny cursor
                // file without delete sharing while a large catch-up stream replaces it
                // thousands of times. Keep the already-flushed temporary file and retry
                // the atomic rename; never acknowledge an event from an in-memory cursor.
                await Task.Delay(Math.Min(100, attempt * 5), cancellationToken);
            }
        }
    }

    private static string SafeKey(string key)
    {
        var safe = string.Concat(key.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        return safe.Length is > 0 and <= 180
            ? safe
            : throw new InvalidDataException("A bounded durable key is required.");
    }
}

public sealed record StagedCompanionFrame(string Key, CompanionFrame Frame);
