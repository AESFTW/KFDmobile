using Android.Hardware.Usb;
using Java.Nio;

namespace KfdMobile.AndroidApp;

sealed class UsbBslTransport : IBslTransport
{
    readonly UsbDeviceConnection connection;
    readonly UsbInterface iface;
    readonly UsbEndpoint input, output;
    public static bool IsBootloader(UsbDevice device) => device.VendorId == 0x2047 && device.ProductId == 0x0200;
    public UsbBslTransport(UsbManager manager, UsbDevice device)
    {
        if (!IsBootloader(device)) throw new IOException("This is not the KFDtool MSP430 bootloader.");
        for (int i = 0; i < device.InterfaceCount; i++)
        {
            var candidate = device.GetInterface(i);
            if ((int)candidate.InterfaceClass != 3) continue;
            for (int j = 0; j < candidate.EndpointCount; j++)
            {
                var endpoint = candidate.GetEndpoint(j);
                if ((int)endpoint.Type != 3) continue;
                if ((int)endpoint.Direction == 0x80) { iface = candidate; input = endpoint; }
                else output = endpoint;
            }
            if (input != null) break;
        }
        if (iface == null || input == null) throw new IOException("Bootloader HID interface was not found.");
        connection = manager.OpenDevice(device) ?? throw new IOException("Grant USB access to the bootloader.");
        if (!connection.ClaimInterface(iface, true)) { connection.Close(); throw new IOException("Bootloader interface is busy."); }
    }
    int Transfer(UsbEndpoint endpoint, byte[] data)
    {
        using var buffer = ByteBuffer.AllocateDirect(data.Length);
        if ((int)endpoint.Direction != 0x80) { buffer.Put(data); buffer.Flip(); }
        using var request = new UsbRequest();
        if (!request.Initialize(connection, endpoint) || !request.Queue(buffer)) throw new IOException("Could not queue bootloader USB transfer.");
        try
        {
            var finished = connection.RequestWait(5000);
            if (finished == null || !finished.Equals(request)) throw new IOException("Bootloader USB transfer failed.");
            int count = buffer.Position();
            if ((int)endpoint.Direction == 0x80) { buffer.Flip(); buffer.Get(data, 0, count); }
            return count;
        }
        catch { request.Cancel(); throw; }
    }
    public void Send(byte[] command)
    {
        byte[] data = BslUpdate.EncodeReport(command);
        int written = output != null ? Transfer(output, data) : connection.ControlTransfer((UsbAddressing)0x21, 9, 0x023f, iface.Id, data, data.Length, 5000);
        if (written != data.Length) throw new IOException("Incomplete bootloader write.");
    }
    public byte[] Receive() { var data = new byte[64]; return BslUpdate.DecodeReport(data, Transfer(input, data)); }
    public void Dispose() { connection.ReleaseInterface(iface); connection.Close(); connection.Dispose(); }
}
