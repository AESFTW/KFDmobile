using System.Security.Cryptography;

namespace KfdMobile;

public enum StorageKind { Local, Smb, Sftp, WebDav }
public sealed class StorageProfile
{
    public StorageKind Kind { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Share { get; set; } = "";
    public string Folder { get; set; } = "/";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Domain { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string KeyPassphrase { get; set; } = "";
    public string HostFingerprint { get; set; } = "";
    public bool RemoteOnly { get; set; } = true;
    public bool AlsoSaveLocally { get; set; }
    public string Identity => RemoteStorage.Hash(System.Text.Encoding.UTF8.GetBytes($"{Kind}|{Host.TrimEnd('/')}|{Port}|{Share}|{Domain}|{Username}"));
    public StorageProfile Copy() => (StorageProfile)MemberwiseClone();
    public void Validate()
    {
        if (!Enum.IsDefined(Kind)) throw new ArgumentException("Choose a storage type.");
        if (Kind == StorageKind.Local) return;
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("Enter the server address.");
        RemotePath.Normalize(Folder);
        if (Kind == StorageKind.WebDav)
        {
            if (!Uri.TryCreate(Host, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Enter an HTTPS WebDAV URL without credentials, query or fragment.");
        }
        else
        {
            if (Host.Any(c => char.IsWhiteSpace(c) || "/\\@".Contains(c))) throw new ArgumentException("Enter a hostname or IP address, without a URL or credentials.");
            if (string.IsNullOrWhiteSpace(Username)) throw new ArgumentException("Enter a username.");
        }
        if (Kind == StorageKind.Smb) RemotePath.Name(Share);
        if (Kind == StorageKind.Sftp)
        {
            if (Port < 1 || Port > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
            if (!HostFingerprint.StartsWith("SHA256:", StringComparison.Ordinal) || HostFingerprint.Length < 40)
                throw new ArgumentException("Enter the server's SHA256 SSH fingerprint, verified with its administrator.");
        }
    }
}
public static class RemotePath
{
    public static string Name(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 240 || name.Any(c => char.IsControl(c) || "/\\:*?\"<>|".Contains(c)) || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("Use a plain file or folder name without path separators or special characters.");
        return name;
    }
    public static string Normalize(string path)
    {
        var parts = (path ?? "/").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts) Name(part);
        return "/" + string.Join('/', parts);
    }
    public static string Join(string folder, string name) => Normalize(folder).TrimEnd('/') + "/" + Name(name);
    public static string Parent(string path) { path = Normalize(path); int i = path.LastIndexOf('/'); return i <= 0 ? "/" : path[..i]; }
}
public record RemoteEntry(string Name, bool IsDirectory);
public record RemoteFile(byte[] Bytes, string Revision);
public sealed class StorageConflictException : IOException
{
    public StorageConflictException() : base("The remote file changed or another save is in progress. Your edits are retained. Save a separate copy, or reopen the remote file after preserving your edits.") { }
}
public interface IRemoteStore : IDisposable
{
    IReadOnlyList<RemoteEntry> List(string folder);
    RemoteFile Read(string path); // null only when the file does not exist
    void Create(string path, byte[] bytes); // exclusive create
    void Replace(string path, byte[] bytes, string expectedRevision); // conflict checked
    void Delete(string path);
}
public static class RemoteStorage
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static byte[] ReadLimited(Stream stream, CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream(); var buffer = new byte[32768]; int n;
        while ((n = cancellationToken.CanBeCanceled ? stream.ReadAsync(buffer.AsMemory(), cancellationToken).AsTask().GetAwaiter().GetResult() : stream.Read(buffer, 0, buffer.Length)) > 0) { if (output.Length + n > MaxBytes) throw new IOException("Remote file exceeds the 8 MB limit."); output.Write(buffer, 0, n); }
        return output.ToArray();
    }
    public static IRemoteStore Connect(StorageProfile profile)
    {
        profile.Validate();
        return profile.Kind switch { StorageKind.Smb => new SmbStore(profile), StorageKind.Sftp => new SftpStore(profile), StorageKind.WebDav => new WebDavStore(profile), _ => throw new InvalidOperationException("Choose remote storage first.") };
    }
    public static RemoteFile Save(IRemoteStore store, string path, byte[] encrypted, string expectedRevision)
    {
        if (encrypted.Length > MaxBytes) throw new IOException("Container exceeds the 8 MB limit.");
        // Check before creating a backup; Replace must also check at publication time.
        var current = store.Read(path);
        if (current?.Revision != expectedRevision) throw new StorageConflictException();
        string name = path[(path.LastIndexOf('/') + 1)..];
        string backup = RemotePath.Join(RemotePath.Parent(path), BackupNaming.Name(name, DateTimeOffset.Now).Replace(".ekc", "_" + Guid.NewGuid().ToString("N")[..8] + ".ekc"));
        store.Create(backup, encrypted);
        Verify(store.Read(backup), encrypted);
        if (expectedRevision == null) store.Create(path, encrypted); else store.Replace(path, encrypted, expectedRevision);
        var saved = store.Read(path); Verify(saved, encrypted); return saved;
    }
    public static void Verify(RemoteFile file, byte[] expected)
    {
        if (file == null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(file.Bytes), SHA256.HashData(expected)))
            throw new IOException("Remote read-back verification failed. Changes remain unsaved; check the server before retrying.");
    }
    public static void Test(IRemoteStore store, string folder)
    {
        store.List(folder);
        string path = RemotePath.Join(folder, ".kfd-test-" + Guid.NewGuid().ToString("N"));
        byte[] first = RandomNumberGenerator.GetBytes(32), next = RandomNumberGenerator.GetBytes(32);
        bool created = false;
        try { store.Create(path, first); created = true; var read = store.Read(path); Verify(read, first); store.Replace(path, next, read.Revision); Verify(store.Read(path), next); }
        finally { if (created) store.Delete(path); }
    }
}
