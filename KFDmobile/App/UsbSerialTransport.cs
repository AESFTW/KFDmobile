using Android.Hardware.Usb;
using KfdMobile;
namespace KfdMobile.AndroidApp;

// Fixed 115200, 8N1 transport. Control requests follow usb-serial-for-android
// (MIT); see THIRD-PARTY-NOTICES.md. No Android serial-port or root access needed.
public sealed class UsbSerialTransport : IByteTransport
{
    readonly UsbManager manager;
    readonly UsbDevice device;
    UsbDeviceConnection connection;
    UsbInterface dataInterface, controlInterface;
    UsbEndpoint input, output;
    readonly List<UsbInterface> claimed = new();
    string driver;
    volatile bool disconnected;
    public string DeviceName => device.DeviceName;
    public void Disconnect() { disconnected = true; try { Dispose(); } catch { /* Device removal can invalidate the native handle before close. */ } }
    void CheckConnected()
    {
        if (disconnected || connection == null) throw new IOException("USB keyloader disconnected. Reconnect and select the device again.");
    }
    public UsbSerialTransport(UsbManager manager, UsbDevice device) { this.manager = manager; this.device = device; }
    public static string Driver(UsbDevice d)
    {
        for (int i = 0; i < d.InterfaceCount; i++) if ((int)d.GetInterface(i).InterfaceClass == 2) return "CDC ACM";
        if (d.VendorId == 0x1a86 && (d.ProductId == 0x7523 || d.ProductId == 0x5523)) return "CH340/CH341";
        if (d.VendorId == 0x10c4 && d.ProductId == 0xea60) return "CP210x";
        if (d.VendorId == 0x0403 && d.ProductId == 0x6001) return "FT232";
        return "Unsupported USB interface";
    }
    public void Open()
    {
        if (disconnected) throw new IOException("USB keyloader disconnected.");
        driver = Driver(device);
        if (driver.StartsWith("Unsupported")) throw new IOException($"Unsupported USB VID:PID {device.VendorId:X4}:{device.ProductId:X4}. Send these IDs with your test report.");
        if (!manager.HasPermission(device)) throw new IOException("USB permission has not been granted.");
        connection = manager.OpenDevice(device) ?? throw new IOException("Android could not open the USB device.");
        for (int i = 0; i < device.InterfaceCount; i++)
        {
            var iface = device.GetInterface(i);
            if ((int)iface.InterfaceClass == 2) controlInterface = iface;
            UsbEndpoint candidateIn = null, candidateOut = null;
            for (int j = 0; j < iface.EndpointCount; j++)
            {
                var ep = iface.GetEndpoint(j);
                if ((int)ep.Type != 2) continue;
                if ((int)ep.Direction == 0x80) candidateIn = ep; else candidateOut = ep;
            }
            if (candidateIn != null && candidateOut != null && dataInterface == null) { dataInterface = iface; input = candidateIn; output = candidateOut; }
        }
        if (dataInterface == null) throw new IOException("USB serial bulk endpoints were not found.");
        if (controlInterface != null && controlInterface.Id != dataInterface.Id) Claim(controlInterface);
        Claim(dataInterface);
        int index = dataInterface.Id;
        byte[] baud = { 0, 0xc2, 1, 0 };
        switch (driver)
        {
            case "CDC ACM":
                if (controlInterface == null) throw new IOException("CDC control interface is missing.");
                Control(0x21, 0x20, 0, controlInterface.Id, new byte[] { 0, 0xc2, 1, 0, 0, 0, 8 });
                Control(0x21, 0x22, 3, controlInterface.Id);
                break;
            case "CH340/CH341":
                Control(0xc0, 0x5f, 0, 0, new byte[2]);
                Control(0x40, 0xa1, 0, 0);
                ChBaud();
                Control(0xc0, 0x95, 0x2518, 0, new byte[2]);
                Control(0x40, 0x9a, 0x2518, 0xc3);
                Control(0xc0, 0x95, 0x0706, 0, new byte[2]);
                Control(0x40, 0xa1, 0x501f, 0xd90a);
                ChBaud();
                Control(0x40, 0xa4, 0xff9f, 0);
                break;
            case "CP210x":
                Control(0x41, 0, 1, index);
                Control(0x41, 0x1e, 0, index, baud);
                Control(0x41, 3, 0x0800, index);
                Control(0x41, 0x13, 0, index, new byte[] { 1,0,0,0, 0x40,0,0,0, 0,0,0,0, 0,0,0,0 });
                Control(0x41, 7, 0x0303, index);
                break;
            case "FT232":
                Control(0x40, 0, 0, index + 1);
                Control(0x40, 4, 8, index + 1);
                Control(0x40, 3, 26, index + 1);
                Control(0x40, 2, 0, index + 1);
                Control(0x40, 1, 0x0303, index + 1);
                Control(0x40, 9, 1, index + 1);
                break;
        }
        // Opening an Arduino-derived adapter may reset its MCU via DTR.
        Thread.Sleep(1600);
        Purge();
    }
    void ChBaud() { Control(0x40, 0x9a, 0x1312, 0xcc83); Control(0x40, 0x9a, 0x0f2c, 8); }
    void Claim(UsbInterface iface)
    {
        if (!connection.ClaimInterface(iface, true)) throw new IOException("USB interface is busy.");
        claimed.Add(iface);
    }
    void Control(int type, int request, int value, int index, byte[] data = null)
    {
        CheckConnected();
        int expected = data?.Length ?? 0;
        var opened = connection ?? throw new IOException("USB keyloader disconnected.");
        int n = opened.ControlTransfer((UsbAddressing)type, request, value, index, data, expected, 2000);
        if (n != expected) throw new IOException($"USB setup failed ({driver}, request {request:X2}, returned {n}).");
    }
    public int Read(byte[] buffer, int timeoutMs)
    {
        CheckConnected();
        var opened = connection ?? throw new IOException("USB keyloader disconnected.");
        int n = opened.BulkTransfer(input, buffer, buffer.Length, timeoutMs);
        CheckConnected();
        if (n <= 0)
        {
            if (!manager.DeviceList.ContainsKey(device.DeviceName)) { Disconnect(); CheckConnected(); }
            return 0;
        }
        if (driver != "FT232") return n;
        int used = 0;
        for (int start = 0; start < n; start += input.MaxPacketSize)
        {
            int size = Math.Min(input.MaxPacketSize, n - start);
            if (size < 2) throw new IOException("Truncated FTDI status header.");
            Buffer.BlockCopy(buffer, start + 2, buffer, used, size - 2); used += size - 2;
        }
        return used;
    }
    public void Write(byte[] buffer, int timeoutMs)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            CheckConnected();
            byte[] part = buffer.Skip(offset).Take(4096).ToArray();
            var opened = connection ?? throw new IOException("USB keyloader disconnected.");
            int n = opened.BulkTransfer(output, part, part.Length, timeoutMs);
            if (n <= 0) throw new IOException("USB write failed or keyloader disconnected.");
            offset += n;
        }
    }
    public void Purge()
    {
        var buffer = new byte[4096];
        for (int i = 0; i < 32; i++) if (Read(buffer, 20) == 0) return;
        throw new IOException("USB input did not become idle.");
    }
    public void Dispose()
    {
        var opened = Interlocked.Exchange(ref connection, null);
        if (opened == null) return;
        foreach (var iface in claimed.ToArray()) { try { opened.ReleaseInterface(iface); } catch { } }
        claimed.Clear();
        try { opened.Close(); } finally { opened.Dispose(); }
    }
}
