using System.Text.Json;

namespace KfdMobile;

public sealed record RecoveryRecord(string FileName, string ContainerName, string ProfileIdentity, string RemotePath, string RemoteRevision, string Hash, DateTimeOffset SavedAt);
public sealed class LocalRecoveryStore
{
    readonly string directory;
    public LocalRecoveryStore(string directory) => this.directory = directory;
    public RecoveryRecord Save(string name, byte[] encrypted, string profileIdentity, string remotePath, string remoteRevision)
    {
        Directory.CreateDirectory(directory);
        string file = BackupNaming.Name(name, DateTimeOffset.Now).Replace(".ekc", "_" + Guid.NewGuid().ToString("N")[..8] + ".ekc");
        string path = System.IO.Path.Combine(directory, file);
        string staging = path + ".writing";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write)) { stream.Write(encrypted); stream.Flush(true); }
            if (RemoteStorage.Hash(File.ReadAllBytes(staging)) != RemoteStorage.Hash(encrypted)) throw new IOException("Local recovery verification failed.");
            File.Move(staging, path, false);
        }
        finally { if (File.Exists(staging)) { try { File.Delete(staging); } catch { } } }
        var record = new RecoveryRecord(file, name, profileIdentity, remotePath, remoteRevision, RemoteStorage.Hash(encrypted), DateTimeOffset.Now);
        // Container is already durable if metadata writing fails; List reconstructs an entry.
        try { File.WriteAllText(path + ".json", JsonSerializer.Serialize(record)); } catch (IOException) { }
        return record;
    }
    public IReadOnlyList<RecoveryRecord> List()
    {
        if (!Directory.Exists(directory)) return Array.Empty<RecoveryRecord>();
        return Directory.EnumerateFiles(directory, "*.ekc").OrderByDescending(File.GetLastWriteTimeUtc).Select(path => {
            try
            {
                var record = JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllText(path + ".json"));
                if (record != null && record.FileName == System.IO.Path.GetFileName(path)) return record;
            } catch { }
            return new RecoveryRecord(System.IO.Path.GetFileName(path), System.IO.Path.GetFileName(path), null, null, null, null, File.GetLastWriteTimeUtc(path));
        }).ToArray();
    }
    public void MarkRemoteSaved(RecoveryRecord record, string revision)
    {
        string path = System.IO.Path.Combine(directory, RemotePath.Name(record.FileName)) + ".json";
        // Failure only leaves an older baseline, which safely prompts a conflict on reopen.
        try { File.WriteAllText(path, JsonSerializer.Serialize(record with { RemoteRevision = revision })); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public byte[] Read(RecoveryRecord record)
    {
        string path = System.IO.Path.Combine(directory, RemotePath.Name(record.FileName));
        using var stream = File.OpenRead(path); byte[] bytes = RemoteStorage.ReadLimited(stream);
        if (record.Hash != null && record.Hash != RemoteStorage.Hash(bytes)) throw new IOException("The local recovery copy failed its integrity check.");
        return bytes;
    }
}

public record StorageSaveResult(RemoteFile Remote, RecoveryRecord Local, Exception RemoteFailure, Exception LocalFailure);
public static class StorageSaveCoordinator
{
    public static StorageSaveResult Save(bool both, Func<RemoteFile> saveRemote, Func<RecoveryRecord> saveLocal)
    {
        RemoteFile remote = null; RecoveryRecord local = null; Exception remoteFailure = null, localFailure = null;
        if (both) { try { local = saveLocal(); } catch (Exception ex) { localFailure = ex; } }
        // A failed local mirror must not prevent a working remote destination from saving.
        try { remote = saveRemote(); } catch (Exception ex) { remoteFailure = ex; }
        if (remoteFailure != null && local == null && !both) { try { local = saveLocal(); } catch (Exception ex) { localFailure = ex; } }
        return new(remote, local, remoteFailure, localFailure);
    }
}
