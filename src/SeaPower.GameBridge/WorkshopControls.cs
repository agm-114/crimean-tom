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
        if (command.ContentOnly && (command.Action != "workshop-update" || command.PreviewDirectory.Length > 0 || command.ChangeLog.Length > 0))
            throw new ArgumentException("Content-only updates cannot create items or include gallery changes or change notes.");
        if (current?.Busy == true) throw new InvalidOperationException($"Workshop operation {current.Id} is still active. Use workshop-status.");
        if (!SteamManager.Initialized || !SteamUser.BLoggedOn() || SteamUtils.GetAppID().m_AppId != WorkshopManifest.AppId)
            throw new InvalidOperationException("Sea Power must be running with Steam signed in and the updated bridge enabled.");
        var manifest = new WorkshopManifest(command.ModDirectory, command.Action == "workshop-create");
        var files = manifest.ContentFiles();
        FileInfo[] previews = command.PreviewDirectory.Length == 0 ? [] : new DirectoryInfo(command.PreviewDirectory)
            .EnumerateFiles().Where(file => file.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif")
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (command.PreviewDirectory.Length > 0 && previews.Length == 0)
            throw new ArgumentException("--preview-dir must contain at least one PNG, JPEG, or GIF image.");
        current?.Dispose();
        current = new Upload(manifest, files, previews, command);
        current.Start();
        return current.Status();
    }

    internal void Tick() => current?.Tick();
    public void Dispose() => current?.Dispose();

    private class Upload(WorkshopManifest manifest, FileInfo[] files, FileInfo[] previews, GameCommand command) : IDisposable
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal bool Busy => state is "checking" or "creating" or "adding-dependencies" or "uploading";
        private bool Creating => command.Action == "workshop-create";
        private string state = "checking";
        private bool agreementRequired;
        private string? failure;
        private string? remoteTitle;
        private string? remoteDescription;
        private ulong owner;
        private readonly ulong account = SteamUser.GetSteamID().m_SteamID;
        private readonly long bytes = files.Sum(file => file.Length);
        private readonly DateTime queryDeadline = DateTime.UtcNow.AddSeconds(60);
        private UGCQueryHandle_t query = UGCQueryHandle_t.Invalid;
        private UGCUpdateHandle_t update = UGCUpdateHandle_t.Invalid;
        private CallResult<SteamUGCQueryCompleted_t>? queryCall;
        private CallResult<SubmitItemUpdateResult_t>? uploadCall;
        private CallResult<CreateItemResult_t>? createCall;
        private readonly List<CallResult<AddUGCDependencyResult_t>> dependencyCalls = [];
        private int dependenciesRemaining;
        private readonly Dictionary<string, uint> currentPreviews = new(StringComparer.OrdinalIgnoreCase);

        internal void Start()
        {
            try
            {
                if (Creating) { Create(); return; }
                query = SteamUGC.CreateQueryUGCDetailsRequest(new[] { new PublishedFileId_t(manifest.ItemId) }, 1);
                if (query == UGCQueryHandle_t.Invalid) throw new InvalidOperationException("Steam rejected the Workshop lookup.");
                SteamUGC.SetAllowCachedResponse(query, 0);
                Require(SteamUGC.SetLanguage(query, "english"), "query language");
                Require(SteamUGC.SetReturnLongDescription(query, true), "description query");
                if (previews.Length > 0) Require(SteamUGC.SetReturnAdditionalPreviews(query, true), "gallery query");
                queryCall = CallResult<SteamUGCQueryCompleted_t>.Create(Queried);
                var call = SteamUGC.SendQueryUGCRequest(query);
                if (call == SteamAPICall_t.Invalid) throw new InvalidOperationException("Steam could not start the Workshop lookup.");
                queryCall.Set(call);
            }
            catch (Exception error) { Fail(error.Message); ReleaseQuery(); }
        }

        private ERemoteStoragePublishedFileVisibility Visibility => command.Visibility switch
        {
            "public" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic,
            "friends-only" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly,
            "private" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate,
            "unlisted" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityUnlisted,
            _ => throw new ArgumentException("Creation requires an explicit valid visibility."),
        };

        private void Create()
        {
            _ = Visibility;
            if (command.Dependencies.Any(id => id == 0)) throw new ArgumentException("Invalid dependency ID.");
            ValidateLocal();
            owner = account;
            if (!command.Confirm) { state = "preview"; return; }
            // Persist before contacting Steam: an uncertain outcome must never create a duplicate on retry.
            manifest.MarkCreationPending(Id);
            state = "creating";
            createCall = CallResult<CreateItemResult_t>.Create(Created);
            var call = SteamUGC.CreateItem(new AppId_t(WorkshopManifest.AppId), EWorkshopFileType.k_EWorkshopFileTypeFirst);
            if (call == SteamAPICall_t.Invalid) throw new InvalidOperationException("Steam rejected creation. Check the pending marker before retrying.");
            createCall.Set(call);
        }

        private void Created(CreateItemResult_t result, bool ioFailure)
        {
            try
            {
                if (ioFailure || result.m_eResult != EResult.k_EResultOK || result.m_nPublishedFileId.m_PublishedFileId == 0)
                    throw new InvalidOperationException($"Steam creation failed or is uncertain: {result.m_eResult}, IO failure={ioFailure}. Check your Workshop listings before retrying.");
                manifest.PinCreatedItem(result.m_nPublishedFileId.m_PublishedFileId);
                agreementRequired = result.m_bUserNeedsToAcceptWorkshopLegalAgreement;
                if (!SteamUser.BLoggedOn() || SteamUser.GetSteamID().m_SteamID != account)
                    throw new InvalidOperationException("Steam account changed after creation. The new URL is saved; nothing uploaded.");
                var dependencies = command.Dependencies.Distinct().ToArray();
                dependenciesRemaining = dependencies.Length;
                if (dependenciesRemaining == 0) { Submit(); return; }
                state = "adding-dependencies";
                foreach (ulong id in dependencies)
                {
                    var callback = CallResult<AddUGCDependencyResult_t>.Create(DependencyAdded);
                    dependencyCalls.Add(callback);
                    var call = SteamUGC.AddDependency(new PublishedFileId_t(manifest.ItemId), new PublishedFileId_t(id));
                    if (call == SteamAPICall_t.Invalid) throw new InvalidOperationException($"Steam rejected dependency {id}. The new URL is saved.");
                    callback.Set(call);
                }
            }
            catch (Exception error) { Fail(error.Message); }
        }

        private void DependencyAdded(AddUGCDependencyResult_t result, bool ioFailure)
        {
            if (state != "adding-dependencies") return;
            try
            {
                if (ioFailure || result.m_eResult != EResult.k_EResultOK || result.m_nPublishedFileId.m_PublishedFileId != manifest.ItemId)
                    throw new InvalidOperationException($"Steam could not add dependency {result.m_nChildPublishedFileId}: {result.m_eResult}. The new URL is saved.");
                if (--dependenciesRemaining == 0) Submit();
            }
            catch (Exception error) { Fail(error.Message); }
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
                manifest.ValidateTarget(item.m_nPublishedFileId.m_PublishedFileId, item.m_nConsumerAppID.m_AppId, remoteTitle);
                if (command.ContentOnly && !string.Equals(remoteTitle.Trim(), manifest.Title.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException($"Workshop title '{remoteTitle}' does not match pinned title '{manifest.Title}'.");
                if (previews.Length > 0) ReadPreviews();
                if (!command.Confirm) { state = "preview"; return; }
                Submit();
            }
            catch (Exception error) { Fail(error.Message); }
            finally { ReleaseQuery(); }
        }

        private void ReadPreviews()
        {
            uint count = SteamUGC.GetQueryUGCNumAdditionalPreviews(query, 0);
            for (uint index = 0; index < count; index++)
            {
                Require(SteamUGC.GetQueryUGCAdditionalPreview(query, 0, index, out _, 2048, out string name, 1024, out EItemPreviewType type),
                    $"gallery image {index}");
                if (type == EItemPreviewType.k_EItemPreviewType_Image) currentPreviews.TryAdd(name, index);
            }
        }

        private void Submit()
        {
            ValidateLocal();
            if (!SteamUser.BLoggedOn() || SteamUser.GetSteamID().m_SteamID != account)
                throw new InvalidOperationException("Steam account changed during verification; nothing uploaded.");
            update = SteamUGC.StartItemUpdate(new AppId_t(WorkshopManifest.AppId), new PublishedFileId_t(manifest.ItemId));
            if (update == UGCUpdateHandle_t.Invalid) throw new InvalidOperationException("Steam rejected the item update.");
            if (Creating)
            {
                Require(SteamUGC.SetItemVisibility(update, Visibility), "visibility");
                if (command.Tags.Length > 0) Require(SteamUGC.SetItemTags(update, command.Tags), "tags");
            }
            Require(SteamUGC.SetItemContent(update, manifest.Directory), "content folder");
            if (!command.ContentOnly) UpdateListing();
            uploadCall = CallResult<SubmitItemUpdateResult_t>.Create(Submitted);
            var call = SteamUGC.SubmitItemUpdate(update, command.ChangeLog);
            if (call == SteamAPICall_t.Invalid) throw new InvalidOperationException("Steam rejected submission of the update.");
            uploadCall.Set(call);
            state = "uploading";
        }

        private void UpdateListing()
        {
            Require(SteamUGC.SetItemUpdateLanguage(update, "english"), "listing language");
            Require(SteamUGC.SetItemTitle(update, manifest.Name), "name");
            Require(SteamUGC.SetItemDescription(update, manifest.Description), "description");
            string preview = Path.Combine(manifest.Directory, "thumbnail.jpg");
            if (File.Exists(preview)) Require(SteamUGC.SetItemPreview(update, preview), "thumbnail");
            foreach (FileInfo file in previews)
                Require(currentPreviews.TryGetValue(file.Name, out uint index)
                    ? SteamUGC.UpdateItemPreviewFile(update, index, file.FullName)
                    : SteamUGC.AddItemPreviewFile(update, file.FullName, EItemPreviewType.k_EItemPreviewType_Image), file.Name);
            var ini = Utils.openIniFile(Path.Combine(manifest.Directory, "_info.ini"), logErrors: false, useCaching: false);
            if (ini == null) throw new InvalidOperationException("Cannot read mod compatibility metadata.");
            var compatibility = ModCompatibility.FromIni(ini);
            if (compatibility.HasInfo) Require(SteamUGC.SetItemMetadata(update, compatibility.ToMetadataString()), "compatibility metadata");
        }

        private void ValidateLocal()
        {
            var fresh = new WorkshopManifest(manifest.Directory, Creating && manifest.ItemId == 0);
            if (fresh.ItemId != manifest.ItemId || fresh.Title != manifest.Title
                || fresh.Name != manifest.Name || fresh.Description != manifest.Description)
                throw new InvalidOperationException("Local Workshop metadata changed during verification; nothing uploaded.");
            fresh.ContentFiles();
            if (command.ContentOnly) return;
            string preview = Path.Combine(manifest.Directory, "thumbnail.jpg");
            if (File.Exists(preview) && new FileInfo(preview).Length > 1000000)
                throw new ArgumentException("thumbnail.jpg must be at most 1,000,000 bytes.");
        }

        private void Submitted(SubmitItemUpdateResult_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK || result.m_nPublishedFileId.m_PublishedFileId != manifest.ItemId)
                Fail($"Steam upload failed or could not be confirmed: {result.m_eResult}, IO failure={ioFailure}. Check the Workshop item before retrying.");
            else state = agreementRequired || result.m_bUserNeedsToAcceptWorkshopLegalAgreement ? "agreement-required" : Creating ? "created" : "updated";
        }

        internal object Status()
        {
            ulong processed = 0, total = 0;
            string? progress = state == "uploading" ? SteamUGC.GetItemUpdateProgress(update, out processed, out total).ToString() : null;
            return new
            {
                ok = state is "preview" or "updated" or "created", action = command.Action, operation = Id, state,
                url = manifest.Url, publishedFileId = manifest.ItemId == 0 ? null : manifest.ItemId.ToString(), appId = WorkshopManifest.AppId,
                contentOnly = command.ContentOnly,
                title = command.ContentOnly ? remoteTitle : manifest.Name,
                description = command.ContentOnly ? remoteDescription : manifest.Description,
                currentTitle = remoteTitle, currentDescription = remoteDescription,
                previewImages = previews.Select(file => file.Name).ToArray(), currentPreviewImages = currentPreviews.Keys.ToArray(),
                ownerSteamId = owner == 0 ? null : owner.ToString(),
                modDirectory = manifest.Directory, fileCount = files.Length, bytes,
                changeLog = command.ChangeLog, progress, bytesProcessed = processed, bytesTotal = total, failure,
                visibility = Creating ? command.Visibility : null, dependencies = command.Dependencies.Select(id => id.ToString()).ToArray(), tags = command.Tags,
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
            createCall?.Dispose();
            dependencyCalls.ForEach(callback => callback.Dispose());
            ReleaseQuery();
        }
    }
}
