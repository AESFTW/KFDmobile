using KfdMobile;
using KFDtool.Adapter.Device;
using KFDtool.P25.Kmm;
using KFDtool.P25.TransferConstructs;

static class RadioLoadTests
{
    static void Assert(bool value) { if (!value) throw new Exception("Radio load assertion failed"); }
    static Exception Failure(Action action) { try { action(); } catch (Exception ex) { return ex; } throw new Exception("Expected radio load failure"); }
    public static void Run(Action<string, Action> test)
    {
        foreach (bool shield in new[] { false, true })
        {
            void Load(RadioTransport transport, Action<int, int> progress = null)
            {
                TransportRegistry.Factory = _ => transport;
                var device = new BaseDevice { KfdDeviceType = shield ? TwiKfdDevice.KfdShield : TwiKfdDevice.KfdTool, TwiKfdtoolDevice = new() { ComPort = "synthetic" } };
                var keys = new[] { (0xaa, 5), (0x84, 32), (0x81, 8) }.Select((k, i) => new CmdKeyItem { UseActiveKeyset = true, Sln = i + 1, KeyId = i + 10, AlgorithmId = k.Item1, Key = Enumerable.Repeat((byte)0x11, k.Item2).ToList() }).ToList();
                InteractTwiKfdtool.Keyload(device, keys, progress: progress);
            }
            test($"Key loading progress counts only confirmed batches ({shield})", () => {
                var updates = new List<(int, int)>();
                Load(new RadioTransport(shield) { Fault = "inventory-first" }, (n, total) => updates.Add((n, total)));
                Assert(updates.SequenceEqual(new[] { (1, 3), (2, 3), (3, 3) }));
                updates.Clear();
                Failure(() => Load(new RadioTransport(shield) { Fault = "write-second" }, (n, total) => updates.Add((n, total))));
                Assert(updates.SequenceEqual(new[] { (1, 3) }));
                updates.Clear();
                Failure(() => Load(new RadioTransport(shield) { Fault = "wrong-ack" }, (n, total) => updates.Add((n, total))));
                Assert(updates.Count == 0);
            });
            test($"Radio loading recovers a first 22-of-23 inventory response ({shield})", () => {
                var t = new RadioTransport(shield) { Fault = "inventory-first" }; Load(t);
                Assert(t.Opens == 2 && t.Closed == 2 && t.InventoryReads == 2 && t.Modifications == 3 && t.TruncatedExpected == 23);
            });
            test($"Radio loading restarts a failed initial handshake ({shield})", () => {
                var t = new RadioTransport(shield) { Fault = "handshake-first" }; Load(t); Assert(t.Opens == 2 && t.Modifications == 3);
            });
            test($"Radio preparation retries stop after three attempts ({shield})", () => {
                var t = new RadioTransport(shield) { Fault = "handshake-always" }; var ex = Failure(() => Load(t));
                Assert(t.Opens == 3 && t.Closed == 3 && t.Modifications == 0 && ex.Message.Contains("before any keys were sent"));
            });
            test($"Radio rejection and USB removal are never retried ({shield})", () => {
                foreach (string fault in new[] { "rejection", "usb" }) { var t = new RadioTransport(shield) { Fault = fault }; var ex = Failure(() => Load(t)); if (t.Opens != 1 || t.Modifications != 0) throw new Exception($"{fault}: opens={t.Opens}, writes={t.Modifications}, {ex.Message}"); }
            });
            test($"Missing key-write reply does not replay any algorithm ({shield})", () => {
                var t = new RadioTransport(shield) { Fault = "write-second" }; var ex = Failure(() => Load(t));
                Assert(t.Opens == 1 && t.Modifications == 2 && ex.Message.Contains("1 of 3 keys acknowledged") && ex.Message.Contains("Radio response frame timeout") && !ex.Message.Contains("No key imported"));
            });
            test($"Mismatched radio acknowledgment cannot report loading success ({shield})", () => {
                var t = new RadioTransport(shield) { Fault = "wrong-ack" }; var ex = Failure(() => Load(t));
                Assert(t.Opens == 1 && t.Modifications == 1 && ex.Message.Contains("did not match"));
            });
        }
    }
    sealed class RadioTransport(bool shield) : IByteTransport
    {
        readonly FrameCodec codec = new(shield);
        readonly Queue<byte> response = new();
        readonly List<byte> incoming = new();
        public string Fault; public int Opens, Closed, InventoryReads, Modifications, TruncatedExpected;
        public void Open() => Opens++;
        public void Dispose() => Closed++;
        public void Purge() { response.Clear(); incoming.Clear(); codec.Reset(); }
        public int Read(byte[] bytes, int timeout)
        {
            int count = 0; while (count < Math.Min(7, bytes.Length) && response.Count > 0) bytes[count++] = response.Dequeue();
            if (count == 0) Thread.Sleep(Math.Min(timeout, 10)); return count;
        }
        void Reply(params byte[] bytes) { foreach (byte b in codec.Encode(bytes)) response.Enqueue(b); }
        void Broadcast(byte b) => Reply(0x31, 0, b);
        void Frame(KmmBody kmm, bool truncate = false)
        {
            byte[] body = new byte[] { 0, 255, 255, 255 }.Concat(new KmmFrame(kmm).ToBytes()).ToArray();
            ushort checksum = 65535;
            foreach (byte b in body) { checksum ^= b; for (int bit = 0; bit < 8; bit++) checksum = (ushort)((checksum >> 1) ^ ((checksum & 1) != 0 ? 0x8408 : 0)); }
            byte[] crc = { (byte)checksum, (byte)(checksum >> 8) };
            byte[] wire = new byte[] { 0xc2, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2) }.Concat(body).Concat(crc).ToArray();
            if (truncate) TruncatedExpected = wire.Length - 1;
            foreach (byte b in wire.Take(wire.Length - (truncate ? 1 : 0))) Broadcast(b);
        }
        public void Write(byte[] bytes, int timeout)
        {
            foreach (var packet in codec.Feed(bytes, bytes.Length))
            {
                if (packet[0] == 0x11) Reply(0x21, 1, 2, 2, 0);
                else if (packet[0] == 0x12) Reply(0x22);
                else if (packet[0] == 0x19)
                {
                    if (Fault == "usb") throw new IOException("USB disconnected");
                    Reply(0x29); Broadcast(Fault == "handshake-always" || (Fault == "handshake-first" && Opens == 1) ? (byte)0x99 : (byte)0xd0);
                }
                else if (packet[0] == 0x18)
                {
                    Reply(0x28); incoming.AddRange(packet.Skip(4));
                    if (incoming[0] == 0xc1) { incoming.Clear(); Broadcast(0xc1); continue; }
                    if (incoming[0] == 0x92) { incoming.Clear(); Broadcast(0x90); continue; }
                    if (incoming.Count < 3 || incoming.Count < 3 + (incoming[1] << 8) + incoming[2]) continue;
                    var kmm = new KmmFrame(false, incoming.Skip(7).Take(incoming.Count - 9).ToArray()).KmmBody; incoming.Clear();
                    if (kmm is InventoryCommandListActiveKsetIds)
                    {
                        InventoryReads++;
                        if (Fault == "rejection") { Frame(new NegativeAcknowledgment { AcknowledgedMessageId = MessageId.InventoryCommand, Status = (OperationStatus)1 }); continue; }
                        var inventory = new InventoryResponseListActiveKsetIds(); inventory.KsetIds.AddRange(new byte[] { 1, 2 });
                        Frame(inventory, Fault == "inventory-first" && Opens == 1);
                    }
                    else if (kmm is ModifyKeyCommand modify)
                    {
                        Modifications++; var ack = new RekeyAcknowledgment { MessageIdAcknowledged = MessageId.ModifyKeyCommand, NumberOfItems = modify.KeyItems.Count };
                        foreach (var key in modify.KeyItems) ack.Keys.Add(new KeyStatus { AlgorithmId = modify.AlgorithmId, KeyId = Fault == "wrong-ack" ? 999 : key.KeyId, Status = 0 });
                        Frame(ack, Fault == "write-second" && Modifications == 2);
                    }
                    else throw new Exception("Unexpected synthetic KMM");
                }
                else throw new Exception("Unexpected synthetic adapter command");
            }
        }
    }
}
