// Host-side doubles for the Android USB calls used by the linked production transport.
// These test lifecycle behavior, not Android's USB stack or physical timing.
namespace Android.Hardware.Usb;
public enum UsbAddressing { }
public sealed class UsbEndpoint
{
    public int Type => 2;
    public int Direction { get; init; }
    public int MaxPacketSize => 64;
}
public sealed class UsbInterface
{
    public int Id => 0;
    public int InterfaceClass => 2;
    public int EndpointCount => 2;
    public UsbEndpoint GetEndpoint(int i) => new() { Direction = i == 0 ? 0x80 : 0 };
}
public sealed class UsbDevice
{
    public int InterfaceCount => 1;
    public int VendorId => 0;
    public int ProductId => 0;
    public string DeviceName => "test-usb";
    public UsbInterface GetInterface(int i) => new();
}
public sealed class UsbManager
{
    public Dictionary<string, UsbDevice> DeviceList { get; } = new();
    public UsbDeviceConnection Connection = new();
    public bool HasPermission(UsbDevice device) => true;
    public UsbDeviceConnection OpenDevice(UsbDevice device) => Connection;
}
public sealed class UsbDeviceConnection : IDisposable
{
    public bool ClaimSucceeds = true;
    public int Closes, Releases, Disposals, Reads;
    public bool ClaimInterface(UsbInterface iface, bool force) => ClaimSucceeds;
    public bool ReleaseInterface(UsbInterface iface) { Releases++; return true; }
    public int ControlTransfer(UsbAddressing type, int request, int value, int index, byte[] data, int length, int timeout) => length;
    public int BulkTransfer(UsbEndpoint endpoint, byte[] buffer, int length, int timeout)
    { if (endpoint.Direction == 0x80) { Reads++; return -1; } return length; }
    public void Close() => Closes++;
    public void Dispose() => Disposals++;
}
