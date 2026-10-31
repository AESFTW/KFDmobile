using KFDtool.Adapter.Bundle;
using System.Globalization;
using System.Xml;

namespace KfdMobile;

public sealed record FirmwareBlock(int Address, byte[] Bytes);
public sealed class FirmwarePlan
{
    public Update Package { get; }
    public List<FirmwareBlock> Application { get; }
    public List<FirmwareBlock> RamLoader { get; }
    public int ResetAddress { get; }
    public FirmwarePlan(Update package)
    {
        Package = package;
        if (!Version.TryParse(package.AppVersion, out _) || string.IsNullOrWhiteSpace(package.RamBslVersion)) throw new FormatException("Missing firmware version metadata.");
        Application = ParseTiText(package.GetAppDataString());
        RamLoader = ParseTiText(package.GetRamBslDataString());
        // Keep executable data out of peripheral registers, information memory and ROM BSL.
        if (Application.Any(b => b.Address < 0x4400 || b.Address + b.Bytes.Length > 0x10000)) throw new FormatException("Application addresses are outside KFD100 flash.");
        if (RamLoader.Any(b => b.Address < 0x2400 || b.Address + b.Bytes.Length > 0x4400) || !Contains(RamLoader, 0x2504)) throw new FormatException("RAM BSL addresses are invalid.");
        byte At(int address) { var b = Application.SingleOrDefault(b => address >= b.Address && address < b.Address + b.Bytes.Length) ?? throw new FormatException("Firmware reset vector is missing."); return b.Bytes[address - b.Address]; }
        ResetAddress = At(0xfffe) | (At(0xffff) << 8);
        if (ResetAddress == 0xffff || (ResetAddress & 1) != 0 || !Contains(Application, ResetAddress)) throw new FormatException("Firmware reset target is invalid.");
    }
    static bool Contains(List<FirmwareBlock> blocks, int address) => blocks.Any(b => address >= b.Address && address < b.Address + b.Bytes.Length);
    public bool Supports(string model, string revision) => Package.ProductModel?.Any(m => m.Name == model && m.Revision?.Contains(revision) == true) == true;
    public static List<Update> ReadPackage(byte[] data)
    {
        if (data.Length == 0 || data.Length > 8 * 1024 * 1024) throw new FormatException("Firmware package exceeds the file limit.");
        // CFP is a little-endian expanded length followed by gzip; UFP is XML.
        if (data.Length > 6 && data[4] == 0x1f && data[5] == 0x8b) data = KFDtool.Shared.Utility.Decompress(data);
        using var stream = new MemoryStream(data);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        while (reader.Read()) { }
        var updates = Firmware.OpenUpdatePackage(data);
        if (updates.Count == 0 || updates.Count > 64) throw new FormatException("Firmware package has an invalid number of updates.");
        return updates;
    }
    public static List<FirmwareBlock> ParseTiText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4 * 1024 * 1024) throw new FormatException("Invalid TI-TXT image size.");
        var blocks = new List<FirmwareBlock>(); int address = -1; var bytes = new List<byte>(); bool ended = false;
        void Flush() { if (address >= 0) { if (bytes.Count == 0) throw new FormatException("Empty TI-TXT section."); blocks.Add(new(address, bytes.ToArray())); bytes.Clear(); } }
        foreach (var token in text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ended) throw new FormatException("Data after TI-TXT terminator.");
            if (token.Equals("q", StringComparison.OrdinalIgnoreCase)) { Flush(); ended = true; continue; }
            if (token.StartsWith('@')) { Flush(); if (!int.TryParse(token.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address) || address < 0 || address > 0xffff) throw new FormatException("Invalid image address."); }
            else { if (address < 0 || token.Length != 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) throw new FormatException("Malformed TI-TXT byte."); bytes.Add(b); if (address + bytes.Count > 0x10000) throw new FormatException("Image exceeds memory range."); }
        }
        if (!ended || blocks.Count == 0) throw new FormatException("TI-TXT terminator or data is missing.");
        var sorted = blocks.OrderBy(b => b.Address).ToArray();
        for (int i = 1; i < sorted.Length; i++) if (sorted[i].Address < sorted[i-1].Address + sorted[i-1].Bytes.Length) throw new FormatException("Overlapping image sections.");
        return blocks;
    }
}

public interface IBslTransport : IDisposable
{
    void Send(byte[] command);
    byte[] Receive();
}

public static class BslUpdate
{
    public static byte[] AddressCommand(byte opcode, int address, byte[] data) => new[] { opcode, (byte)address, (byte)(address >> 8), (byte)(address >> 16) }.Concat(data).ToArray();
    public static byte[] EncodeReport(byte[] command)
    {
        if (command.Length is < 1 or > 62) throw new ArgumentException("BSL command must be 1–62 bytes.");
        var report = new byte[64]; report[0] = 0x3f; report[1] = (byte)command.Length; command.CopyTo(report, 2); return report;
    }
    public static byte[] DecodeReport(byte[] report, int length)
    {
        if (length < 3 || report[0] != 0x3f || report[1] is < 1 or > 62 || report[1] > length - 2) throw new IOException("Invalid BSL HID response.");
        return report.Skip(2).Take(report[1]).ToArray();
    }
    static int Status(IBslTransport device) { var rsp = device.Receive(); if (rsp.Length != 2 || rsp[0] != 0x3b) throw new IOException("Expected BSL status response."); return rsp[1]; }
    static void Success(IBslTransport device) { int status = Status(device); if (status != 0) throw new IOException($"BSL rejected command (status 0x{status:X2})."); }
    public static void LoadRam(IBslTransport device, FirmwarePlan plan)
    {
        byte[] password = new byte[] { 0x11 }.Concat(Enumerable.Repeat((byte)255,32)).ToArray();
        device.Send(password); int status = Status(device);
        // The first wrong password erases main flash, as in the upstream updater.
        if (status == 5) { device.Send(password); Success(device); }
        else if (status != 0) throw new IOException($"BSL unlock failed (status 0x{status:X2}).");
        foreach (var block in plan.RamLoader) for (int offset = 0; offset < block.Bytes.Length; offset += 58)
            device.Send(AddressCommand(0x1b, block.Address + offset, block.Bytes.Skip(offset).Take(58).ToArray()));
        device.Send(AddressCommand(0x17, 0x2504, Array.Empty<byte>()));
    }
    public static void Program(IBslTransport device, FirmwarePlan plan, Action<string> progress)
    {
        device.Send(new byte[] { 0x15 }); Success(device);
        int total = plan.Application.Sum(b => b.Bytes.Length), done = 0;
        foreach (var block in plan.Application) for (int offset = 0; offset < block.Bytes.Length; offset += 58)
        {
            byte[] data = block.Bytes.Skip(offset).Take(58).ToArray();
            device.Send(AddressCommand(0x10, block.Address + offset, data)); Success(device);
            done += data.Length; progress($"Writing firmware: {done * 100 / total}%");
        }
        done = 0;
        foreach (var block in plan.Application) for (int offset = 0; offset < block.Bytes.Length; offset += 58)
        {
            byte[] expected = block.Bytes.Skip(offset).Take(58).ToArray();
            device.Send(AddressCommand(0x18, block.Address + offset, new byte[] { (byte)expected.Length, 0 }));
            var response = device.Receive();
            if (response.Length != expected.Length + 1 || response[0] != 0x3a || !response.Skip(1).SequenceEqual(expected)) throw new IOException("Firmware readback verification failed. Device remains in bootloader mode.");
            done += expected.Length; progress($"Verifying firmware: {done * 100 / total}%");
        }
        device.Send(AddressCommand(0x17, plan.ResetAddress, Array.Empty<byte>()));
        progress("Firmware written and verified; reconnect the adapter and read its firmware version.");
    }
}
