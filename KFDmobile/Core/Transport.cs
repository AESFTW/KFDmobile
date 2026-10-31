using System.Diagnostics;
namespace KfdMobile;

public interface IByteTransport : IDisposable
{
    void Open();
    int Read(byte[] buffer, int timeoutMs);
    void Write(byte[] buffer, int timeoutMs);
    void Purge();
}

public static class TransportRegistry
{
    public static Func<string, IByteTransport> Factory { get; set; }
}

public sealed class FrameCodec
{
    readonly bool shield;
    readonly List<byte> frame = new();
    bool started, escaped;
    public FrameCodec(bool shield) => this.shield = shield;
    byte End => (byte)(shield ? 0x63 : 0x61);
    byte Escape => (byte)(shield ? 0x70 : 0x63);
    public byte[] Encode(IEnumerable<byte> payload)
    {
        var result = new List<byte> { 0x61 };
        foreach (byte b in payload)
        {
            if (b == 0x61) result.AddRange(new byte[] { Escape, 0x62 });
            else if (b == Escape) result.AddRange(new byte[] { Escape, (byte)(shield ? 0x71 : 0x64) });
            else if (shield && b == End) result.AddRange(new byte[] { Escape, 0x64 });
            else result.Add(b);
        }
        result.Add(End);
        return result.ToArray();
    }
    public void Reset() { frame.Clear(); started = escaped = false; }
    public IEnumerable<byte[]> Feed(byte[] data, int count)
    {
        var packets = new List<byte[]>();
        for (int i = 0; i < count; i++)
        {
            byte b = data[i];
            if (!started) { if (b == 0x61) { started = true; frame.Clear(); escaped = false; } continue; }
            if (escaped)
            {
                escaped = false;
                if (b == 0x62) frame.Add(0x61);
                else if (b == (shield ? 0x71 : 0x64)) frame.Add(Escape);
                else if (shield && b == 0x64) frame.Add(End);
                else { Reset(); throw new IOException("Invalid USB frame escape."); }
            }
            else if (b == Escape) escaped = true;
            else if (b == End) { if (frame.Count > 0) packets.Add(frame.ToArray()); Reset(); if (!shield) started = true; }
            else if (shield && b == 0x61) { frame.Clear(); escaped = false; }
            else frame.Add(b);
            if (frame.Count > 8192) { Reset(); throw new IOException("USB frame exceeds limit."); }
        }
        return packets;
    }
}
