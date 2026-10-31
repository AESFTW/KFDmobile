using KfdMobile;
using System.Diagnostics;
namespace KFDtool.Adapter.Protocol.Serial;

public class KfdToolSerialProtocol : KfdSerialProtocol
{
    readonly string port;
    readonly FrameCodec codec;
    readonly Queue<byte[]> packets = new();
    readonly byte[] readBuffer = new byte[4096];
    IByteTransport transport;
    volatile bool canceled;
    public KfdToolSerialProtocol(string port) : this(port, false) { }
    protected KfdToolSerialProtocol(string port, bool shield) { this.port = port; codec = new(shield); }
    public void Open()
    {
        transport = (TransportRegistry.Factory ?? throw new InvalidOperationException("USB transport is unavailable."))(port);
        try { transport.Open(); canceled = false; } catch { transport.Dispose(); transport = null; throw; }
    }
    public void Close() { transport?.Dispose(); transport = null; }
    public void Clear() { codec.Reset(); packets.Clear(); canceled = false; transport.Purge(); }
    public void Cancel() => canceled = true;
    public void Send(List<byte> data) => transport.Write(codec.Encode(data), 2000);
    public List<byte> Read(int timeout)
    {
        var timer = Stopwatch.StartNew();
        var buffer = readBuffer;
        while (!canceled)
        {
            if (packets.Count > 0) return packets.Dequeue().ToList();
            // The desktop adapter contract uses zero for an indefinite, cancelable read.
            int remaining = timeout == 0 ? 100 : timeout - (int)timer.ElapsedMilliseconds;
            if (remaining <= 0) throw new TimeoutException("No response from keyloader. Check adapter selection and USB cable.");
            int n = transport.Read(buffer, Math.Min(remaining, 100));
            if (n > 0) foreach (var packet in codec.Feed(buffer, n)) packets.Enqueue(packet);
        }
        throw new OperationCanceledException();
    }
}
public sealed class KfdShieldSerialProtocol : KfdToolSerialProtocol
{
    public KfdShieldSerialProtocol(string port) : base(port, true) { }
}
