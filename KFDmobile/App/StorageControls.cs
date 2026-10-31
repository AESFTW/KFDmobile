using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using System.Text;

namespace KfdMobile.AndroidApp;

public partial class MainActivity
{
    StorageProfile storage = new();
    string storageLoadError, remotePath, remoteRevision;
    Action<string> importedSshKey;
    bool Remote => storage.Kind != StorageKind.Local;
    bool RemoteOnly => storageLoadError != null || (Remote && storage.RemoteOnly);
    bool HasContainerDestination => containerUri != null || remotePath != null;
    void RequireStorage()
    {
        if (storageLoadError != null) throw new IOException(storageLoadError);
    }
    void EditStorage()
    {
        var draft = storage.Copy(); var form = Column();
        Text(form, "Containers and timestamped backups use the selected destination. Existing files are not moved or deleted.", 14);
        if (storageLoadError != null) Text(form, storageLoadError, 14);
        var kind = Choices(form, "Storage type", new[] { "Local / Android file picker", "SMB", "SFTP", "HTTPS / WebDAV" }, (int)draft.Kind);
        var details = Column(); form.AddView(details);
        var hostLabel = Text(details, "Server hostname / HTTPS WebDAV URL", 13);
        var host = Field(details, "Server", draft.Host);
        var port = Field(details, "SFTP port", draft.Port.ToString(), numeric: true);
        var share = Field(details, "SMB share", draft.Share);
        var domain = Field(details, "SMB domain (optional)", draft.Domain);
        var folder = Field(details, "Folder (relative to WebDAV URL / SMB share; absolute for SFTP)", draft.Folder);
        var username = Field(details, "Username", draft.Username);
        var password = Field(details, "Password / WebDAV app password", draft.Password, secret: true);
        var ssh = Column(); details.AddView(ssh);
        var fingerprint = Field(ssh, "Verified SSH host fingerprint (SHA256:...)", draft.HostFingerprint);
        Text(ssh, "Obtain this fingerprint from your server administrator. A different host key is rejected.", 13);
        var keyStatus = Text(ssh, string.IsNullOrEmpty(draft.PrivateKey) ? "Password authentication" : "SSH private key configured", 14);
        var passphrase = Field(ssh, "SSH key passphrase (optional)", draft.KeyPassphrase, secret: true);
        Button(ssh, "Import SSH private key", () => {
            importedSshKey = value => { draft.PrivateKey = value; keyStatus.Text = "SSH private key configured"; };
            pickerActive = true; var intent = new Intent(Intent.ActionOpenDocument); intent.SetType("*/*"); intent.AddCategory(Intent.CategoryOpenable);
            try { StartActivityForResult(intent, 105); } catch { pickerActive = false; importedSshKey = null; throw; }
        });
        Button(ssh, "Use password instead", () => { draft.PrivateKey = ""; draft.KeyPassphrase = ""; passphrase.Text = ""; keyStatus.Text = "Password authentication"; });
        var only = Check(details, "Disable local plaintext key exports and DKF export", draft.RemoteOnly);
        var mirror = Check(details, "Also save locally on every remote save", draft.AlsoSaveLocally);
        Text(details, "Remote saves include a timestamped backup beside the container. If the remote save or backup fails, an encrypted local recovery copy is always saved automatically. Optional local copies use the same recovery area; open them from Container > Local recovery copies. Android removes this app-private area if the app is uninstalled. SMB uses port 445. HTTPS requires a trusted certificate. SFTP overwrites require atomic rename support.", 13);
        void UpdateFields()
        {
            var value = (StorageKind)kind.SelectedItemPosition;
            details.Visibility = value == StorageKind.Local ? ViewStates.Gone : ViewStates.Visible;
            ssh.Visibility = value == StorageKind.Sftp ? ViewStates.Visible : ViewStates.Gone;
            port.Enabled = value == StorageKind.Sftp; share.Enabled = domain.Enabled = value == StorageKind.Smb;
            hostLabel.Text = value == StorageKind.WebDav ? "HTTPS WebDAV base URL" : "Server hostname or IP address";
        }
        kind.ItemSelected += (_, _) => UpdateFields(); UpdateFields();
        StorageProfile ReadForm()
        {
            draft.Kind = (StorageKind)kind.SelectedItemPosition; draft.Host = host.Text.Trim();
            draft.Port = draft.Kind == StorageKind.Sftp ? int.Parse(port.Text) : 22;
            draft.Share = share.Text.Trim(); draft.Domain = domain.Text.Trim(); draft.Folder = RemotePath.Normalize(folder.Text);
            draft.Username = username.Text.Trim(); draft.Password = password.Text; draft.KeyPassphrase = passphrase.Text;
            draft.HostFingerprint = fingerprint.Text.Trim(); draft.RemoteOnly = only.Checked; draft.AlsoSaveLocally = mirror.Checked; draft.Validate(); return draft.Copy();
        }
        Button(form, "Browse server folders", () => { var profile = ReadForm(); if (profile.Kind == StorageKind.Local) return; BrowseRemote(profile, profile.Folder, true, value => folder.Text = value); });
        Button(form, "Test connection and save access", () => {
            var profile = ReadForm(); if (profile.Kind == StorageKind.Local) { Message("Local storage", "Choose a document location when opening or saving."); return; }
            Work("Testing remote storage", () => { using var store = RemoteStorage.Connect(profile); RemoteStorage.Test(store, profile.Folder); }, () => Message("Connection verified", "Folder browsing, upload, replacement, read-back and cleanup succeeded."));
        });
        var scroll = new ScrollView(this); scroll.AddView(form);
        var dialog = new AlertDialog.Builder(this).SetTitle("Storage").SetView(scroll).SetNegativeButton("Cancel", (_, _) => { }).Create();
        Button(form, "Save storage settings", () => {
            var profile = ReadForm(); dialog.Dismiss();
            // Save/close the existing container through its old profile before switching destinations.
            Discard(() => { StorageCredentials.Save(this, profile); if (session.IsOpen) Lock(); storage = profile; storageLoadError = null; status = "Storage settings saved"; Render(); });
        });
        dialog.DismissEvent += (_, _) => { password.Text = ""; passphrase.Text = ""; importedSshKey = null; draft.PrivateKey = ""; };
        ShowDialog(dialog); dialog.Window?.SetSoftInputMode(SoftInput.AdjustResize);
    }
    void BrowseRemote(StorageProfile profile, string folder, bool chooseFolder, Action<string> selectedPath)
    {
        IReadOnlyList<RemoteEntry> entries = null;
        Work("Browsing remote storage", () => { using var store = RemoteStorage.Connect(profile); entries = store.List(folder); }, () => {
            if (entries.Count > 2000) { Message("Folder too large", "Choose a smaller folder in Storage settings (maximum 2,000 entries). "); return; }
            var visible = entries.Where(e => e.IsDirectory || (!chooseFolder && e.Name.EndsWith(".ekc", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var labels = new List<string> { "↑ Parent folder" }; if (chooseFolder) labels.Add("Use this folder");
            labels.AddRange(visible.Select(e => (e.IsDirectory ? "Folder: " : "") + e.Name));
            ShowDialog(new AlertDialog.Builder(this).SetTitle(folder).SetItems(labels.ToArray(), (_, e) => {
                try {
                    if (e.Which == 0) { BrowseRemote(profile, RemotePath.Parent(folder), chooseFolder, selectedPath); return; }
                    if (chooseFolder && e.Which == 1) { selectedPath(folder); return; }
                    var entry = visible[e.Which - (chooseFolder ? 2 : 1)]; string path = RemotePath.Join(folder, entry.Name);
                    if (entry.IsDirectory) BrowseRemote(profile, path, chooseFolder, selectedPath); else selectedPath(path);
                } catch (Exception ex) { Error(ex); }
            }).SetNegativeButton("Cancel", (_, _) => { if (pendingExport != null) { pendingExport = null; afterSave = null; saveLocationCanceled = true; } }).Create());
        }, ex => {
            if (pendingExport == null) { Error(ex); return; }
            var bytes = pendingExport; var stamp = pendingStamp; var continuation = afterSave; pendingExport = null; afterSave = null;
            SaveRecoveryAfterFailure(bytes, stamp, profile, remotePath ?? RemotePath.Join(profile.Folder, containerName), remoteRevision, continuation, ex);
        });
    }
    void OpenContainerDocument()
    {
        RequireStorage();
        if (!Remote) { LaunchDocument(false); return; }
        var profile = storage.Copy();
        BrowseRemote(profile, profile.Folder, false, path => Password("Unlock remote container", false, password => {
            RemoteFile file = null;
            Work("Opening remote container", () => {
                using var store = RemoteStorage.Connect(profile); file = store.Read(path) ?? throw new IOException("The remote file no longer exists.");
                session.Open(file.Bytes, password);
            }, () => { containerUri = null; remotePath = path; remoteRevision = file.Revision; containerName = path[(path.LastIndexOf('/') + 1)..]; selected.Clear(); revision++; ResetListPosition(); Render(); });
        }));
    }
    void ChooseRemoteSave()
    {
        var profile = storage.Copy();
        BrowseRemote(profile, profile.Folder, true, folder => {
            var form = Column(); var name = Field(form, "Container filename", containerName);
            var dialog = new AlertDialog.Builder(this).SetTitle("Save remote container as").SetView(form).SetNegativeButton("Cancel", (_, _) => { pendingExport = null; afterSave = null; saveLocationCanceled = true; }).Create();
            Button(form, "Save new file", () => {
                string value = name.Text.Trim(); if (!value.EndsWith(".ekc", StringComparison.OrdinalIgnoreCase)) value += ".ekc";
                string path = RemotePath.Join(folder, value); dialog.Dismiss(); WriteRemoteContainer(path, null);
            }); ShowDialog(dialog);
        });
    }
    void WriteRemoteContainer(string path, string expectedRevision)
    {
        var bytes = pendingExport; var stamp = pendingStamp; var continuation = afterSave; var profile = storage.Copy();
        pendingExport = null; afterSave = null; StorageSaveResult result = null;
        Work("Saving and verifying container destinations", () => {
            result = StorageSaveCoordinator.Save(profile.AlsoSaveLocally,
                () => { using var store = RemoteStorage.Connect(profile); return RemoteStorage.Save(store, path, bytes, expectedRevision); },
                () => Recovery.Save(path[(path.LastIndexOf('/') + 1)..], bytes, profile.Identity, path, expectedRevision));
            if (result.Remote != null && result.Local != null) Recovery.MarkRemoteSaved(result.Local, result.Remote.Revision);
        }, () => {
            if (result.Remote == null)
            {
                if (result.Local == null) { Error(new IOException("Neither remote nor local recovery could be saved. Edits remain in memory. Check the connection and free phone storage before closing. " + SafeStorageError(result.LocalFailure ?? result.RemoteFailure))); return; }
                RecoverySaved(result.Local, stamp, path, expectedRevision, continuation, result.RemoteFailure); return;
            }
            if (stamp.Matches(session.Inner, revision))
            {
                remotePath = path; remoteRevision = result.Remote.Revision; containerUri = null; containerName = path[(path.LastIndexOf('/') + 1)..];
                session.Dirty = false; saveLocationCanceled = false;
                status = result.Local != null ? "Saved locally and remotely; all copies verified" : "Remote container and timestamped backup verified";
                AddEvent(status); Render();
                if (result.LocalFailure != null) Message("Remote saved", "The remote container and backup were verified, but the additional local copy could not be saved. Free phone storage and save again.");
                continuation?.Invoke();
            }
            else { status = "Remote save contains an earlier revision; newer changes remain unsaved."; ScheduleSave(); }
        });
    }
    LocalRecoveryStore Recovery => new(System.IO.Path.Combine(FilesDir.AbsolutePath, "container-recovery"));
    void SaveRecoveryAfterFailure(byte[] bytes, SaveStamp stamp, StorageProfile profile, string path, string expectedRevision, Action continuation, Exception failure, RecoveryRecord existing = null)
    {
        Work("Saving local encrypted recovery copy", () => { existing ??= Recovery.Save(containerName, bytes, profile.Identity, path, expectedRevision); },
            () => RecoverySaved(existing, stamp, path, expectedRevision, continuation, failure),
            localFailure => Error(new IOException("Remote save failed and local recovery could not be written. Edits remain in memory. Free phone storage and retry before closing the app. " + SafeStorageError(localFailure))));
    }
    void RecoverySaved(RecoveryRecord copy, SaveStamp stamp, string path, string expectedRevision, Action continuation, Exception failure)
    {
        if (!stamp.Matches(session.Inner, revision)) { ScheduleSave(); return; }
        remotePath = path; remoteRevision = expectedRevision; containerUri = null;
        session.Dirty = true; // Durable locally, but still pending remote synchronization.
        status = "Saved locally — remote save pending"; AddEvent(status + ": " + copy.FileName); Render();
        if (continuation != null) { continuation(); return; }
        if (failure is StorageConflictException) Error(failure);
        else Message("Saved locally", "The remote save or backup could not complete. An encrypted recovery copy was verified on this phone. Use Save container to retry the server, or Local recovery copies to reopen it.\n\n" + SafeStorageError(failure));
    }
    string SafeStorageError(Exception ex)
    {
        string text = ex.Message;
        foreach (string secret in new[] { storage.Password, storage.KeyPassphrase, storage.PrivateKey })
            if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return text;
    }
    void OpenRecoveryCopies()
    {
        var copies = Recovery.List();
        if (copies.Count == 0) { Message("Local recovery copies", "No local recovery copies have been saved yet."); return; }
        ShowDialog(new AlertDialog.Builder(this).SetTitle("Local encrypted recovery copies")
            .SetItems(copies.Select(c => c.FileName).ToArray(), (_, e) => {
                var copy = copies[e.Which];
                Discard(() => Password("Unlock local recovery copy", false, password => Work("Opening local recovery copy", () => session.Open(Recovery.Read(copy), password), () => {
                    containerUri = null; containerName = copy.ContainerName; selected.Clear(); revision++;
                    bool same = Remote && storage.Identity == copy.ProfileIdentity;
                    remotePath = same ? copy.RemotePath : null; remoteRevision = same ? copy.RemoteRevision : null;
                    session.Dirty = true; saveLocationCanceled = false; ResetListPosition();
                    status = "Local recovery opened. Save to sync, or Save as to keep a separate file."; Render();
                })));
            }).SetNegativeButton("Cancel", (_, _) => { }).Create());
    }
}
