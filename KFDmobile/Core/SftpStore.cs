using Renci.SshNet;
using Renci.SshNet.Common;
using System.Text;

namespace KfdMobile;

public sealed class SftpStore : IRemoteStore
{
    readonly SftpClient client;
    readonly PrivateKeyFile key;
    public SftpStore(StorageProfile profile)
    {
        if (!string.IsNullOrEmpty(profile.PrivateKey))
        {
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(profile.PrivateKey));
            key = new PrivateKeyFile(bytes, profile.KeyPassphrase);
            client = new SftpClient(profile.Host, profile.Port, profile.Username, key);
        }
        else client = new SftpClient(profile.Host, profile.Port, profile.Username, profile.Password);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        client.OperationTimeout = TimeSpan.FromSeconds(20);
        client.HostKeyReceived += (_, e) => e.CanTrust = FingerprintMatches(profile.HostFingerprint, e.FingerPrintSHA256);
        try { client.Connect(); } catch { Dispose(); throw; }
    }
    public static bool FingerprintMatches(string expected, string actual) =>
        !string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(actual) &&
        expected.Trim().Replace("SHA256:", "").TrimEnd('=') == actual.Trim().Replace("SHA256:", "").TrimEnd('=');
    static string Path(string path) => RemotePath.Normalize(path);
    public IReadOnlyList<RemoteEntry> List(string folder) => client.ListDirectory(Path(folder))
        .Where(f => f.Name != "." && f.Name != ".." && !f.IsSymbolicLink && (f.IsDirectory || f.IsRegularFile))
        .Take(2001).Select(f => new RemoteEntry(f.Name, f.IsDirectory)).ToArray();
    public RemoteFile Read(string path)
    {
        try { using var stream = client.OpenRead(Path(path)); var bytes = RemoteStorage.ReadLimited(stream); return new(bytes, RemoteStorage.Hash(bytes)); }
        catch (SftpPathNotFoundException) { return null; }
    }
    public void Create(string path, byte[] bytes)
    {
        // Open with exclusive creation: never truncate an existing remote document.
        using var stream = client.Open(Path(path), FileMode.CreateNew, FileAccess.Write);
        stream.Write(bytes); stream.Flush();
    }
    public void Replace(string path, byte[] bytes, string expectedRevision)
    {
        path = Path(path); string lockPath = path + ".kfd-lock";
        string temp = path + ".kfd-" + Guid.NewGuid().ToString("N") + ".tmp";
        Stream lease;
        try { lease = client.Open(lockPath, FileMode.CreateNew, FileAccess.Write); }
        catch (SshException) { throw new StorageConflictException(); }
        using (lease)
        {
            bool staged = false;
            try
            {
                if (Read(path)?.Revision != expectedRevision) throw new StorageConflictException();
                Create(temp, bytes); staged = true; RemoteStorage.Verify(Read(temp), bytes);
                if (Read(path)?.Revision != expectedRevision) throw new StorageConflictException();
                // Require OpenSSH atomic replacement; never delete the old file as a fallback.
                client.RenameFile(temp, path, true); staged = false;
            }
            finally
            {
                if (staged) { try { client.DeleteFile(temp); } catch { } }
                lease.Dispose();
                try { client.DeleteFile(lockPath); } catch { /* A stale lock fails closed on the next save. */ }
            }
        }
    }
    public void Delete(string path) => client.DeleteFile(Path(path));
    public void Dispose() { client?.Dispose(); key?.Dispose(); }
}
