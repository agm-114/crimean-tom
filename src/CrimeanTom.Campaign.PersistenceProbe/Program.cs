using System.Diagnostics;
using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Engine;
using CrimeanTom.Campaign.Service;

if (args.Length == 6 && args[0] == "hold-writer")
{
    var holderJournal = new SqliteCampaignJournal(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
    await holderJournal.InitializeAsync();
    var campaignId = new CampaignId(Guid.Parse(args[3]));
    var holderNow = DateTimeOffset.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
    var holderLease = await AcquireAsync(holderJournal, campaignId, holderNow);
    var readyPath = Path.GetFullPath(args[5]);
    await File.WriteAllTextAsync(readyPath, holderLease.WriterEpoch.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (args.Length < 6 || !Enum.TryParse<CampaignPersistenceBoundary>(args[1], out var boundary))
{
    Console.Error.WriteLine(
        "Usage: <append|snapshot|blob|backup> <boundary> <database> <blobs> <payload> <utc-now> [backup-directory]");
    return 64;
}

var operation = args[0];
var databasePath = Path.GetFullPath(args[2]);
var blobDirectory = Path.GetFullPath(args[3]);
var payloadPath = Path.GetFullPath(args[4]);
var now = DateTimeOffset.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
var journal = new SqliteCampaignJournal(databasePath, blobDirectory, new AbruptExitFaultInjector(boundary));
await journal.InitializeAsync();

switch (operation)
{
    case "append":
    {
        var command = CampaignContractJson.DeserializeCommand(await File.ReadAllBytesAsync(payloadPath));
        var recovered = await journal.RecoverAsync(command.CampaignId);
        var campaignEvent = CampaignKernel.Decide(recovered.State, command, recovered.State.Head.Next());
        var lease = await AcquireAsync(journal, command.CampaignId, now);
        await journal.AppendAsync(lease, command, campaignEvent, now);
        break;
    }
    case "snapshot":
    {
        var state = CampaignCanonicalSerializer.Deserialize(await File.ReadAllBytesAsync(payloadPath));
        var lease = await AcquireAsync(journal, state.Metadata.CampaignId, now);
        await journal.SaveSnapshotAsync(lease, state, now);
        break;
    }
    case "blob":
    {
        var state = CampaignCanonicalSerializer.Deserialize(await File.ReadAllBytesAsync(payloadPath));
        var lease = await AcquireAsync(journal, state.Metadata.CampaignId, now);
        await journal.PutBlobAsync(
            lease,
            CampaignBlobPurpose.TacticalCheckpoint,
            "process-crash-checkpoint",
            state.Head,
            "process-crash-checkpoint-payload"u8.ToArray(),
            now);
        break;
    }
    case "backup":
    {
        if (args.Length != 7)
        {
            return 64;
        }

        await journal.CreateBackupAsync(Path.GetFullPath(args[6]));
        break;
    }
    default:
        return 64;
}

Console.Error.WriteLine($"Boundary {boundary} did not terminate the probe.");
return 65;

static Task<CampaignWriterLease> AcquireAsync(
    SqliteCampaignJournal journal,
    CampaignId campaignId,
    DateTimeOffset now) =>
    journal.AcquireWriterAsync(
        campaignId,
        new CoordinatorId(Guid.Parse("d0000000-0000-0000-0000-000000000001")),
        now,
        TimeSpan.FromMinutes(2));

file sealed class AbruptExitFaultInjector(CampaignPersistenceBoundary armedBoundary)
    : ICampaignPersistenceFaultInjector
{
    public void Reach(CampaignPersistenceBoundary boundary)
    {
        if (boundary == armedBoundary)
        {
            Process.GetCurrentProcess().Kill(entireProcessTree: false);
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
