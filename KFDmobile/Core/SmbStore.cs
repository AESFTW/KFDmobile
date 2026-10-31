using SMBLibrary;
using SMBLibrary.Client;
using Attributes = SMBLibrary.FileAttributes;

namespace KfdMobile;

public sealed class SmbStore : IRemoteStore
{
    readonly SMB2Client client;
    ISMBFileStore files;
    public SmbStore(StorageProfile profile, SMB2Client connectedClient = null)
    {
        client = connectedClient ?? new SMB2Client(10000);
        try
        {
            if (connectedClient == null && !client.Connect(profile.Host, SMBTransportType.DirectTCPTransport)) throw new IOException("Cannot connect to SMB server on port 445.");
            Check(client.Login(profile.Domain, profile.Username, profile.Password), "login");
            files = client.TreeConnect(profile.Share, out var status); Check(status, "open share");
        }
        catch { Dispose(); throw; }
    }
    static string Path(string path) => RemotePath.Normalize(path).TrimStart('/').Replace('/', '\\');
    static void Check(NTStatus status, string operation)
    {
        if (status == NTStatus.STATUS_OBJECT_NAME_COLLISION || status == NTStatus.STATUS_SHARING_VIOLATION) throw new StorageConflictException();
        if (status != NTStatus.STATUS_SUCCESS) throw new IOException($"SMB {operation} failed ({status}). Check connection and permissions.");
    }
    object Open(string path, AccessMask access, CreateDisposition disposition, bool directory = false)
    {
        var status = files.CreateFile(out var handle, out _, Path(path), access | AccessMask.SYNCHRONIZE,
            directory ? Attributes.Directory : Attributes.Normal, directory ? ShareAccess.Read | ShareAccess.Write : ShareAccess.Read,
            disposition, (directory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE) | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        Check(status, "open file"); return handle;
    }
    public IReadOnlyList<RemoteEntry> List(string folder)
    {
        object handle = Open(folder, AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN, true);
        try
        {
            var status = files.QueryDirectory(out var entries, handle, "*", FileInformationClass.FileDirectoryInformation);
            // SMBLibrary returns the terminal status after collecting every directory page.
            if (status != NTStatus.STATUS_NO_MORE_FILES) Check(status, "list folder");
            return entries.Cast<FileDirectoryInformation>().Where(f => f.FileName != "." && f.FileName != ".." && (f.FileAttributes & Attributes.ReparsePoint) == 0)
                .Take(2001).Select(f => new RemoteEntry(f.FileName, (f.FileAttributes & Attributes.Directory) != 0)).ToArray();
        }
        finally { files.CloseFile(handle); }
    }
    public RemoteFile Read(string path)
    {
        var status = files.CreateFile(out var handle, out _, Path(path), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            Attributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        if (status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE) return null;
        Check(status, "read file");
        try
        {
            using var output = new MemoryStream();
            while (true)
            {
                status = files.ReadFile(out var bytes, handle, output.Length, (int)Math.Min(client.MaxReadSize, 65536));
                if (status == NTStatus.STATUS_END_OF_FILE) break;
                Check(status, "read file"); if (bytes.Length == 0) break;
                if (output.Length + bytes.Length > RemoteStorage.MaxBytes) throw new IOException("Remote file exceeds the 8 MB limit.");
                output.Write(bytes);
            }
            byte[] data = output.ToArray(); return new(data, RemoteStorage.Hash(data));
        }
        finally { files.CloseFile(handle); }
    }
    public void Create(string path, byte[] bytes)
    {
        object handle = Open(path, AccessMask.GENERIC_WRITE, CreateDisposition.FILE_CREATE);
        try
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                byte[] part = bytes.Skip(offset).Take((int)Math.Min(client.MaxWriteSize, 65536)).ToArray();
                Check(files.WriteFile(out int written, handle, offset, part), "write file");
                if (written <= 0 || written > part.Length) throw new IOException("SMB write did not complete."); offset += written;
            }
        }
        finally { Check(files.CloseFile(handle), "close file"); }
    }
    public void Replace(string path, byte[] bytes, string expectedRevision)
    {
        string lockPath = path + ".kfd-lock", temp = path + ".kfd-" + Guid.NewGuid().ToString("N") + ".tmp";
        Create(lockPath, Array.Empty<byte>()); bool staged = false;
        try
        {
            if (Read(path)?.Revision != expectedRevision) throw new StorageConflictException();
            Create(temp, bytes); staged = true; RemoteStorage.Verify(Read(temp), bytes);
            if (Read(path)?.Revision != expectedRevision) throw new StorageConflictException();
            object handle = Open(temp, AccessMask.DELETE | AccessMask.GENERIC_WRITE, CreateDisposition.FILE_OPEN);
            try { Check(files.SetFileInformation(handle, new FileRenameInformationType2 { FileName = Path(path), ReplaceIfExists = true }), "publish file"); staged = false; }
            finally { files.CloseFile(handle); }
        }
        finally
        {
            if (staged) { try { Delete(temp); } catch { } }
            try { Delete(lockPath); } catch { /* A stale lock fails closed on the next save. */ }
        }
    }
    public void Delete(string path)
    {
        object handle = Open(path, AccessMask.DELETE, CreateDisposition.FILE_OPEN);
        try { Check(files.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true }), "delete file"); }
        finally { files.CloseFile(handle); }
    }
    public void Dispose() { try { files?.Disconnect(); } catch { } try { client.Logoff(); } catch { } client.Disconnect(); }
}
