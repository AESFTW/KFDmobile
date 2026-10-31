using KfdMobile;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

static class StorageTests
{
    static void Assert(bool value) { if (!value) throw new Exception("Storage assertion failed"); }
    static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    public static void Run(Action<string, Action> test)
    {
        test("Remote paths reject traversal, separators and unsafe filenames", () => {
            foreach (var path in new[] { "/../secret", "/safe/../secret", "/./a", "/a:b" }) Throws<ArgumentException>(() => RemotePath.Normalize(path));
            foreach (var name in new[] { "../a", "a/b", "a\\b", "", "a.", "a\n" }) Throws<ArgumentException>(() => RemotePath.Name(name));
            Assert(RemotePath.Join("/safe", "Team Alpha.ekc") == "/safe/Team Alpha.ekc"); Assert(RemotePath.Parent("/one") == "/");
        });
        test("Storage profiles enforce HTTPS and pinned SFTP host identity", () => {
            foreach (var url in new[] { "http://server/", "https://user:pass@server/", "https://server/?token=a", "file:///tmp" }) Throws<ArgumentException>(() => new StorageProfile { Kind = StorageKind.WebDav, Host = url }.Validate());
            Throws<ArgumentException>(() => new StorageProfile { Kind = StorageKind.Sftp, Host = "host", Username = "test" }.Validate());
            Assert(SftpStore.FingerprintMatches("SHA256:abc=", "abc")); Assert(!SftpStore.FingerprintMatches("SHA256:abc", "abd")); Assert(!SftpStore.FingerprintMatches("", ""));
        });
        test("Remote save verifies encrypted backup and primary", () => {
            using var store = new MemoryStore(); byte[] bytes = { 1, 2, 3 };
            var saved = RemoteStorage.Save(store, "/keys.ekc", bytes, null);
            Assert(saved.Bytes.SequenceEqual(bytes)); Assert(store.Files.Count == 2); Assert(store.Files.Values.All(v => v.SequenceEqual(bytes)));
        });
        test("Remote changed before save never overwrites or creates a backup", () => {
            using var store = new MemoryStore(); store.Create("/keys.ekc", new byte[] { 1 });
            Throws<StorageConflictException>(() => RemoteStorage.Save(store, "/keys.ekc", new byte[] { 2 }, "old")); Assert(store.Files.Count == 1 && store.Files["/keys.ekc"][0] == 1);
        });
        test("Remote change during backup is rechecked before replacement", () => {
            using var store = new MemoryStore(); store.Create("/keys.ekc", new byte[] { 1 }); string expected = store.Read("/keys.ekc").Revision;
            store.AfterCreate = path => { if (path != "/keys.ekc") store.Files["/keys.ekc"] = new byte[] { 9 }; };
            Throws<StorageConflictException>(() => RemoteStorage.Save(store, "/keys.ekc", new byte[] { 2 }, expected)); Assert(store.Files["/keys.ekc"][0] == 9);
        });
        test("Failed backup verification leaves original remote file intact", () => {
            using var store = new MemoryStore(); store.Create("/keys.ekc", new byte[] { 1 }); string expected = store.Read("/keys.ekc").Revision;
            store.AfterCreate = path => { if (path != "/keys.ekc") store.Files[path] = new byte[] { 0 }; };
            Throws<IOException>(() => RemoteStorage.Save(store, "/keys.ekc", new byte[] { 2 }, expected)); Assert(store.Files["/keys.ekc"][0] == 1);
        });
        test("Read-back mismatch is never reported as a successful remote save", () => {
            using var store = new MemoryStore(); store.AfterCreate = path => { if (path == "/keys.ekc") store.Files[path] = new byte[] { 9 }; };
            Throws<IOException>(() => RemoteStorage.Save(store, "/keys.ekc", new byte[] { 2 }, null));
        });
        test("Local recovery copies preserve encrypted bytes, names and remote identity", () => {
            string folder = Path.Combine(Path.GetTempPath(), "kfd-recovery-test-" + Guid.NewGuid().ToString("N"));
            try {
                var recovery = new LocalRecoveryStore(folder); using var session = new ContainerSession(); session.Create("synthetic-password"); byte[] encrypted = session.Save();
                var record = recovery.Save("Team Alpha.ekc", encrypted, "server-id", "/keys.ekc", "revision");
                Assert(record.FileName.StartsWith("Team Alpha_")); Assert(recovery.Read(record).SequenceEqual(encrypted)); Assert(recovery.List().Single().RemoteRevision == "revision");
                using var reopened = new ContainerSession(); reopened.Open(recovery.Read(record), "synthetic-password"); Assert(reopened.IsOpen);
                recovery.MarkRemoteSaved(record, "new-revision"); Assert(recovery.List().Single().RemoteRevision == "new-revision");
                File.WriteAllBytes(Path.Combine(folder, record.FileName), new byte[] { 0 }); Throws<IOException>(() => recovery.Read(record));
            } finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });
        test("WebDAV uses conditional writes and rejects conflicting ETags", () => {
            using var handler = new DavHandler(); using var store = Dav(handler);
            var saved = RemoteStorage.Save(store, "/Team Alpha.ekc", new byte[] { 1 }, null); Assert(handler.LastNewCondition == "*");
            RemoteStorage.Save(store, "/Team Alpha.ekc", new byte[] { 2 }, saved.Revision); Assert(handler.LastMatch == saved.Revision);
            Throws<StorageConflictException>(() => store.Replace("/Team Alpha.ekc", new byte[] { 3 }, saved.Revision));
            Assert(store.Read("/Team Alpha.ekc").Bytes[0] == 2);
        });
        test("WebDAV weak ETag cannot authorize overwrite", () => {
            using var handler = new DavHandler { WeakTag = true }; using var store = Dav(handler); store.Create("/keys.ekc", new byte[] { 1 });
            Throws<IOException>(() => store.Replace("/keys.ekc", new byte[] { 2 }, store.Read("/keys.ekc").Revision)); Assert(store.Read("/keys.ekc").Bytes[0] == 1);
        });
        test("WebDAV rejects redirects and never follows an external listing", () => {
            using var handler = new DavHandler(); using var store = Dav(handler);
            handler.ListXml = "<d:multistatus xmlns:d='DAV:'><d:response><d:href>https://attacker.test/file.ekc</d:href><d:propstat><d:prop/><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>";
            Assert(store.List("/").Count == 0); handler.Redirect = true; Throws<IOException>(() => store.Read("/keys.ekc"));
        });
        test("WebDAV parser rejects external entity documents", () => {
            using var handler = new DavHandler(); using var store = Dav(handler);
            handler.ListXml = "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><x>&e;</x>"; Throws<System.Xml.XmlException>(() => store.List("/"));
        });
        test("WebDAV correctly escapes filenames and retains the configured base URL", () => {
            using var store = Dav(new DavHandler()); Assert(store.Address("/folder/Team #1.ekc").AbsoluteUri == "https://storage.test/dav/folder/Team%20%231.ekc");
        });
        test("Network reads enforce an 8 MB limit", () => { using var data = new MemoryStream(new byte[RemoteStorage.MaxBytes + 1]); Throws<IOException>(() => RemoteStorage.ReadLimited(data)); });
        test("Streaming response reads honor cancellation", () => {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)); using var stream = new StalledStream();
            Throws<OperationCanceledException>(() => RemoteStorage.ReadLimited(stream, timeout.Token));
        });
        test("Dual save writes locally before remotely and reports both", () => {
            var order = new List<string>();
            var result = StorageSaveCoordinator.Save(true, () => { order.Add("remote"); return new(new byte[] { 1 }, "r"); }, () => { order.Add("local"); return new("copy.ekc", "copy", "p", "/copy.ekc", null, "hash", DateTimeOffset.Now); });
            Assert(order.SequenceEqual(new[] { "local", "remote" }) && result.Remote != null && result.Local != null);
        });
        test("Remote preferred skips local on success and falls back on failure", () => {
            int local = 0; RecoveryRecord SaveLocal() { local++; return new("copy.ekc", "copy", "p", "/copy.ekc", null, "hash", DateTimeOffset.Now); }
            var good = StorageSaveCoordinator.Save(false, () => new(new byte[] { 1 }, "r"), SaveLocal); Assert(local == 0 && good.Remote != null);
            var failed = StorageSaveCoordinator.Save(false, () => throw new IOException("offline"), SaveLocal); Assert(local == 1 && failed.Local != null && failed.Remote == null && failed.RemoteFailure != null);
        });
        test("Failed local mirror does not block a working remote save", () => {
            var result = StorageSaveCoordinator.Save(true, () => new(new byte[] { 1 }, "r"), () => throw new IOException("disk full"));
            Assert(result.Remote != null && result.Local == null && result.LocalFailure != null);
        });
        test("Both destination failures remain distinguishable without false success", () => {
            var result = StorageSaveCoordinator.Save(false, () => throw new IOException("offline"), () => throw new IOException("disk full"));
            Assert(result.Remote == null && result.Local == null && result.RemoteFailure.Message == "offline" && result.LocalFailure.Message == "disk full");
        });
        string smoke = Environment.GetEnvironmentVariable("KFD_SFTP_SMOKE");
        string smbPort = Environment.GetEnvironmentVariable("KFD_SMB_SMOKE_PORT");
        if (!string.IsNullOrEmpty(smoke))
        {
            var profile = JsonSerializer.Deserialize<StorageProfile>(File.ReadAllText(smoke)); profile.Kind = StorageKind.Sftp;
            test("Real loopback SFTP verifies create, atomic replace, conflict and cleanup", () => {
                using var store = RemoteStorage.Connect(profile); RemoteStorage.Test(store, "/");
                string path = "/smoke-" + Guid.NewGuid().ToString("N") + ".ekc";
                try { store.Create(path, new byte[] { 1 }); var before = store.Read(path); store.Replace(path, new byte[] { 2 }, before.Revision); Assert(store.Read(path).Bytes[0] == 2); Throws<StorageConflictException>(() => store.Replace(path, new byte[] { 3 }, before.Revision)); }
                finally { store.Delete(path); }
            });
            test("Real loopback SFTP rejects an untrusted host fingerprint", () => {
                var wrong = profile.Copy(); wrong.HostFingerprint = "SHA256:" + new string('A', 43); Throws<Renci.SshNet.Common.SshConnectionException>(() => { using var store = RemoteStorage.Connect(wrong); });
            });
            if (!string.IsNullOrEmpty(smbPort)) test("Real loopback SMB verifies browse, create, replace, conflict and cleanup", () => {
                var client = new LoopbackSmbClient(); Assert(client.ConnectLoopback(int.Parse(smbPort)));
                using var store = new SmbStore(profile, client); RemoteStorage.Test(store, "/");
                string path = "/smb-smoke-" + Guid.NewGuid().ToString("N") + ".ekc";
                try { store.Create(path, new byte[] { 1 }); Assert(store.List("/").Any(e => e.Name == path[1..])); var before = store.Read(path); store.Replace(path, new byte[] { 2 }, before.Revision); Assert(store.Read(path).Bytes[0] == 2); Throws<StorageConflictException>(() => store.Replace(path, new byte[] { 3 }, before.Revision)); }
                finally { store.Delete(path); }
            });
        }
    }
    sealed class LoopbackSmbClient : SMBLibrary.Client.SMB2Client
    {
        public bool ConnectLoopback(int port) => Connect(IPAddress.Loopback, SMBLibrary.SMBTransportType.DirectTCPTransport, port);
    }
    sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { await Task.Delay(Timeout.Infinite, token); return 0; }
    }
    static WebDavStore Dav(DavHandler handler) => new(new StorageProfile { Kind = StorageKind.WebDav, Host = "https://storage.test/dav/" }, handler);
    sealed class MemoryStore : IRemoteStore
    {
        public readonly Dictionary<string, byte[]> Files = new(); public Action<string> AfterCreate;
        public IReadOnlyList<RemoteEntry> List(string folder) => Array.Empty<RemoteEntry>();
        public RemoteFile Read(string path) => Files.TryGetValue(path, out var bytes) ? new(bytes.ToArray(), RemoteStorage.Hash(bytes)) : null;
        public void Create(string path, byte[] bytes) { if (Files.ContainsKey(path)) throw new StorageConflictException(); Files[path] = bytes.ToArray(); AfterCreate?.Invoke(path); }
        public void Replace(string path, byte[] bytes, string revision) { if (Read(path)?.Revision != revision) throw new StorageConflictException(); Files[path] = bytes.ToArray(); }
        public void Delete(string path) => Files.Remove(path); public void Dispose() { }
    }
    sealed class DavHandler : HttpMessageHandler
    {
        readonly Dictionary<string, (byte[] Data, string Tag)> files = new(); int generation;
        public bool WeakTag, Redirect; public string LastNewCondition, LastMatch, ListXml = "<d:multistatus xmlns:d='DAV:'/>";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri.AbsolutePath;
            if (Redirect) return new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://attacker.test/") } };
            if (request.Method.Method == "PROPFIND") return new((HttpStatusCode)207) { Content = new StringContent(ListXml) };
            if (request.Method == HttpMethod.Get)
            {
                if (!files.TryGetValue(path, out var found)) return new(HttpStatusCode.NotFound);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(found.Data), Headers = { ETag = new EntityTagHeaderValue(found.Tag, WeakTag) } };
            }
            if (request.Method == HttpMethod.Put)
            {
                string match = request.Headers.IfMatch.FirstOrDefault()?.ToString(), fresh = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
                if (fresh != null) LastNewCondition = fresh; if (match != null) LastMatch = match;
                if ((fresh == "*" && files.ContainsKey(path)) || (match != null && (!files.TryGetValue(path, out var found) || found.Tag != match))) return new(HttpStatusCode.PreconditionFailed);
                files[path] = (await request.Content.ReadAsByteArrayAsync(token), "\"" + ++generation + "\""); return new(HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Delete) { files.Remove(path); return new(HttpStatusCode.NoContent); }
            throw new Exception("Unexpected test request");
        }
    }
}
