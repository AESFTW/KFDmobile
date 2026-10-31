using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace KfdMobile;

public sealed class WebDavStore : IRemoteStore
{
    readonly HttpClient client;
    readonly Uri root;
    public WebDavStore(StorageProfile profile) : this(profile, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = new NetworkCredential(profile.Username, profile.Password) }) { }
    public WebDavStore(StorageProfile profile, HttpMessageHandler handler)
    {
        profile.Validate(); root = new Uri(profile.Host.TrimEnd('/') + "/");
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
    }
    public Uri Address(string path) => new(root, string.Join('/', RemotePath.Normalize(path).TrimStart('/').Split('/').Select(Uri.EscapeDataString)));
    static byte[] ReadBody(HttpResponseMessage response)
    {
        // HttpClient's header timeout does not cover a streaming response body.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stream = response.Content.ReadAsStreamAsync(timeout.Token).GetAwaiter().GetResult();
        return RemoteStorage.ReadLimited(stream, timeout.Token);
    }
    HttpResponseMessage Send(HttpRequestMessage request)
    {
        var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.Locked)
        { response.Dispose(); throw new StorageConflictException(); }
        if ((int)response.StatusCode >= 300 && response.StatusCode != HttpStatusCode.NotFound)
        { int status = (int)response.StatusCode; response.Dispose(); throw new IOException($"WebDAV request failed (HTTP {status}). Check server address, access rights and certificate."); }
        return response;
    }
    public IReadOnlyList<RemoteEntry> List(string folder)
    {
        var address = Address(folder); address = new Uri(address.AbsoluteUri.TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), address);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent("<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
        using var response = Send(request);
        if ((int)response.StatusCode != 207) throw new IOException("The server must support WebDAV directory browsing (PROPFIND).");
        using var limited = new MemoryStream(ReadBody(response));
        using var reader = XmlReader.Create(limited, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = RemoteStorage.MaxBytes });
        var doc = XDocument.Load(reader); XNamespace dav = "DAV:"; var entries = new List<RemoteEntry>();
        foreach (var item in doc.Descendants(dav + "response"))
        {
            string href = (string)item.Element(dav + "href");
            if (!Uri.TryCreate(address, href, out var child) || child.Scheme != root.Scheme || child.Authority != root.Authority) continue;
            string prefix = address.AbsolutePath.TrimEnd('/') + "/", path = child.AbsolutePath.TrimEnd('/');
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string tail = path[prefix.Length..]; if (tail.Length == 0 || tail.Contains('/')) continue;
            string name = Uri.UnescapeDataString(tail); try { RemotePath.Name(name); } catch { continue; }
            var prop = item.Elements(dav + "propstat").FirstOrDefault(p => ((string)p.Element(dav + "status"))?.Contains(" 200 ") == true)?.Element(dav + "prop");
            if (prop == null) continue;
            entries.Add(new(name, prop.Element(dav + "resourcetype")?.Element(dav + "collection") != null));
            if (entries.Count > 2000) break;
        }
        return entries;
    }
    public RemoteFile Read(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Address(path)); using var response = Send(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode != HttpStatusCode.OK) throw new IOException("The server did not return a complete file.");
        byte[] data = ReadBody(response);
        var etag = response.Headers.ETag;
        // Weak or absent ETags cannot protect an overwrite. Opening remains available.
        string revision = etag != null && !etag.IsWeak ? etag.ToString() : "unversioned:" + RemoteStorage.Hash(data);
        return new(data, revision);
    }
    void Put(string path, byte[] bytes, string revision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Address(path));
        if (revision == null) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        else
        {
            if (!revision.StartsWith('"')) throw new IOException("This WebDAV server supplies no strong ETag. Use Save as with a new filename to avoid overwriting changes.");
            request.Headers.TryAddWithoutValidation("If-Match", revision);
        }
        request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = Send(request);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent)) throw new IOException("The server did not confirm the upload.");
    }
    public void Create(string path, byte[] bytes) => Put(path, bytes, null);
    public void Replace(string path, byte[] bytes, string expectedRevision) => Put(path, bytes, expectedRevision);
    public void Delete(string path) { using var request = new HttpRequestMessage(HttpMethod.Delete, Address(path)); using var response = Send(request); }
    public void Dispose() => client.Dispose();
}
