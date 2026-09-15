using SeaPower.Workshop;
using Steamworks;

namespace SeaPower.GameBridge;

internal class WorkshopControls : IDisposable
{
    private Upload? current;

    internal object Execute(GameCommand command)
    {
        if (command.Action == "workshop-status")
        {
            if (current == null) throw new InvalidOperationException("No Workshop operation in this game session.");
            if (command.Text.Length > 0 && command.Text != current.Id)
                throw new InvalidOperationException("That Workshop operation is no longer the current operation.");
            return current.Status();
        }
        if (current?.Busy == true) throw new InvalidOperationException($"Workshop operation {current.Id} is still active. Use workshop-status.");
        if (!SteamManager.Initialized || !SteamUser.BLoggedOn() || SteamUtils.GetAppID().m_AppId != WorkshopManifest.AppId)
            throw new InvalidOperationException("Sea Power must be running with Steam signed in and the updated bridge enabled.");
        var manifest = new WorkshopManifest(command.ModDirectory);
        var files = manifest.ContentFiles();
        current?.Dispose();
        current = new Upload(manifest, files, command);
        current.Start();
        return current.Status();
    }

    internal void Tick() => current?.Tick();
    public void Dispose() => current?.Dispose();

    private class Upload(WorkshopManifest manifest, FileInfo[] files, GameCommand command) : IDisposable
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal bool Busy => state is "checking" or "uploading";
        private string state = "checking";
        private string? failure;
        private string? remoteTitle;
        private string? remoteDescription;
        private ulong owner;
        private readonly long bytes = files.Sum(file => file.Length);
        private readonly DateTime queryDeadline = DateTime.UtcNow.AddSeconds(60);
        private UGCQueryHandle_t query = UGCQueryHandle_t.Invalid;
        private UGCUpdateHandle_t update = UGCUpdateHandle_t.Invalid;
        private CallResult<SteamUGCQueryCompleted_t>? queryCall;
        private CallResult<SubmitItemUpdateResult_t>? uploadCall;

        internal void Start()
        {
            try
            {
                query = SteamUGC.CreateQueryUGCDetailsRequest(new[] { new PublishedFileId_t(manifest.ItemId) }, 1);
                if (query == UGCQueryHandle_t.Invalid) throw new InvalidOperationException("Steam rejected the Workshop lookup.");
                SteamUGC.SetAllowCachedResponse(query, 0);
                Require(SteamUGC.SetLanguage(query, "english"), "query language");
                Require(SteamUGC.SetReturnLongDescription(query, true), "description query");
                queryCall = CallResult<SteamUGCQueryCompleted_t>.Create(Queried);
                var call = SteamUGC.SendQueryUGCRequest(query);
                if (call == SteamAPICall_t.Invalid) throw new InvalidOperationException("Steam could not start the Workshop lookup.");
                queryCall.Set(call);
            }
            catch (Exception error) { Fail(error.Message); ReleaseQuery(); }
        }

        private void Queried(SteamUGCQueryCompleted_t result, bool ioFailure)
        {
            try
            {
                if (state != "checking") return;
                if (DateTime.UtcNow >= queryDeadline) throw new TimeoutException("Workshop lookup expired; nothing uploaded.");
                if (ioFailure || result.m_eResult != EResult.k_EResultOK || result.m_unNumResultsReturned != 1
                    || !SteamUGC.GetQueryUGCResult(query, 0, out var item) || item.m_eResult != EResult.k_EResultOK)
                    throw new InvalidOperationException($"Could not verify the existing Workshop item: {result.m_eResult}.");
                remoteTitle = item.m_rgchTitle;
                remoteDescription = item.m_rgchDescription;
                owner = item.m_ulSteamIDOwner;
                if (item.m_eFileType != EWorkshopFileType.k_EWorkshopFileTypeFirst || item.m_bBanned)
                    throw new InvalidOperationException("URL does not identify an updateable Sea Power Workshop item.");
                manifest.ValidateTarget(item.m_nPublishedFileId.m_PublishedFileId, item.m_nConsumerAppID.m_AppId,
                    owner, SteamUser.GetSteamID().m_SteamID, remoteTitle);
                if (!command.Confirm) { state = "preview"; return; }
                Submit();
            }
            catch (Exception error) { Fail(error.Message); }
            finally { ReleaseQuery(); }
        }

        private void Submit()
        {
            var fresh = new WorkshopManifest(manifest.Directory);
            if (fresh.ItemId != manifest.ItemId || fresh.Title != manifest.Title
                || fresh.Name != manifest.Name || fresh.Description != manifest.Description)
                throw new InvalidOperationException("Local Workshop metadata changed during verification; nothing uploaded.");
            fresh.ContentFiles();
            if (!SteamUser.BLoggedOn() || SteamUser.GetSteamID().m_SteamID != owner)
                throw new InvalidOperationException("Steam account changed during verification; nothing uploaded.");
            string preview = Path.Combine(manifest.Directory, "thumbnail.jpg");
            if (File.Exists(preview) && new FileInfo(preview).Length > 1000000)
                throw new ArgumentException("thumbnail.jpg must be at most 1,000,000 bytes.");
            update = SteamUGC.StartItemUpdate(new AppId_t(WorkshopManifest.AppId), new PublishedFileId_t(manifest.ItemId));
            if (update == UGCUpdateHandle_t.Invalid) throw new InvalidOperationException("Steam rejected the item update.");
            Require(SteamUGC.SetItemUpdateLanguage(update, "english"), "listing language");
            Require(SteamUGC.SetItemTitle(update, manifest.Name), "name");
            Require(SteamUGC.SetItemDescription(update, manifest.Description), "description");
            Require(SteamUGC.SetItemContent(update, manifest.Directory), "content folder");
            if (File.Exists(preview)) Require(SteamUGC.SetItemPreview(update, preview), "thumbnail");
            var ini = Utils.openIniFile(Path.Combine(manifest.Directory, "_info.ini"), logErrors: false, useCaching: false);
            if (ini == null) throw new InvalidOperationException("Cannot read mod compatibility metadata.");
            var compatibility = ModCompatibility.FromIni(ini);
            if (compatibility.HasInfo) Require(SteamUGC.SetItemMetadata(update, compatibility.ToMetadataString()), "compatibility metadata");
            uploadCall = CallResult<SubmitItemUpdateResult_t>.Create(Submitted);
            var call = SteamUGC.SubmitItemUpdate(update, command.ChangeLog);
            if (call == SteamAPICall_t.Invalid) throw new InvalidOperationException("Steam rejected submission of the update.");
            state = "uploading";
            uploadCall.Set(call);
        }

        private void Submitted(SubmitItemUpdateResult_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK || result.m_nPublishedFileId.m_PublishedFileId != manifest.ItemId)
                Fail($"Steam upload failed or could not be confirmed: {result.m_eResult}, IO failure={ioFailure}. Check the Workshop item before retrying.");
            else state = result.m_bUserNeedsToAcceptWorkshopLegalAgreement ? "agreement-required" : "updated";
        }

        internal object Status()
        {
            ulong processed = 0, total = 0;
            string? progress = state == "uploading" ? SteamUGC.GetItemUpdateProgress(update, out processed, out total).ToString() : null;
            return new
            {
                ok = state is "preview" or "updated", action = "workshop-update", operation = Id, state,
                url = manifest.Url, publishedFileId = manifest.ItemId.ToString(), appId = WorkshopManifest.AppId,
                title = manifest.Name, description = manifest.Description,
                currentTitle = remoteTitle, currentDescription = remoteDescription,
                ownerSteamId = owner == 0 ? null : owner.ToString(),
                modDirectory = manifest.Directory, fileCount = files.Length, bytes,
                changeLog = command.ChangeLog, progress, bytesProcessed = processed, bytesTotal = total, failure,
                agreementUrl = state == "agreement-required" ? "https://steamcommunity.com/sharedfiles/workshoplegalagreement" : null
            };
        }

        internal void Tick()
        {
            if (state != "checking" || DateTime.UtcNow < queryDeadline) return;
            Fail("Steam Workshop lookup timed out; nothing uploaded.");
            queryCall?.Cancel();
            ReleaseQuery();
        }

        private void Fail(string message) { failure = message; state = "failed"; }
        private static void Require(bool accepted, string field)
        {
            if (!accepted) throw new InvalidOperationException($"Steam rejected the {field}; nothing submitted.");
        }

        private void ReleaseQuery()
        {
            if (query == UGCQueryHandle_t.Invalid) return;
            SteamUGC.ReleaseQueryUGCRequest(query);
            query = UGCQueryHandle_t.Invalid;
        }

        public void Dispose()
        {
            queryCall?.Dispose();
            uploadCall?.Dispose();
            ReleaseQuery();
        }
    }
}
