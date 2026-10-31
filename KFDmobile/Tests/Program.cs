using KfdMobile;
using KFDtool.Container;
using KFDtool.Adapter.Protocol.Adapter;
using KFDtool.Adapter.Device;
using System.Security.Cryptography;

int passed = 0;
void Test(string name, Action action) { action(); Console.WriteLine("PASS " + name); passed++; }
if (args.Contains("--storage-only")) { StorageTests.Run(Test); Console.WriteLine($"{passed} storage tests passed."); return; }
if (args.Contains("--radio-only")) { RadioLoadTests.Run(Test); Console.WriteLine($"{passed} radio tests passed."); return; }
void Assert(bool condition, string text = "Assertion failed") { if (!condition) throw new Exception(text); }
void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected failure"); }
KeyItem Example() => new() { Id=1, Name="Test AES", AlgorithmId=0x84, Key=Convert.ToHexString(Enumerable.Range(0,32).Select(x => (byte)x).ToArray()), KeyId=1234, Sln=7, ActiveKeyset=true, KeysetId=1, KeyTypeAuto=true };

foreach (var (algorithm, length) in new[] { (0xaa, 10), (0x81, 16), (0x9f, 16), (0x84, 64) })
{
    Test($"Key entry limits and replacement for {algorithm:X2}", () => {
        string full = new('A', length);
        Assert(KeyEntry.HexLength(algorithm) == length);
        Assert(KeyEntry.AcceptsReplacement(algorithm, "", 0, 0, full));
        Assert(!KeyEntry.AcceptsReplacement(algorithm, full, length, length, "1"));
        Assert(!KeyEntry.AcceptsReplacement(algorithm, "", 0, 0, full + "1"));
        Assert(KeyEntry.AcceptsReplacement(algorithm, full, 2, 3, "f"));
        Assert(KeyEntry.AcceptsReplacement(algorithm, full, 2, 3, ""));
        Assert(!KeyEntry.AcceptsReplacement(algorithm, full, 2, 3, "FF"));
        Assert(!KeyEntry.AcceptsReplacement(algorithm, "", 0, 0, "G"));
        Assert(!KeyEntry.AcceptsReplacement(algorithm, "", 0, 0, "1 2"));
    });
}
Test("ARC4 rejects the reported 25-digit input", () => {
    Assert(!KeyEntry.AcceptsReplacement(0xaa, "", 0, 0, new string('1', 25)));
    var key = Example(); key.AlgorithmId = 0xaa; key.Key = new string('1', 25);
    Throws(() => ContainerSession.Validate(key));
});
if (args.Contains("--key-entry-only")) { Console.WriteLine($"{passed} key entry tests passed."); return; }

foreach (bool shield in new[] { false,true })
{
    Test($"Every byte survives framing ({shield}) across split USB reads", () => {
        var codec=new FrameCodec(shield); var payload=Enumerable.Range(0,256).Select(x=>(byte)x).ToArray(); var encoded=codec.Encode(payload);
        for (int split=0;split<=encoded.Length;split++) { codec.Reset(); var packets=codec.Feed(encoded.Take(split).ToArray(),split).Concat(codec.Feed(encoded.Skip(split).ToArray(),encoded.Length-split)).ToArray(); Assert(packets.Length==1 && packets[0].SequenceEqual(payload)); }
    });
    Test($"Multiple frames in one USB read ({shield})", () => { var codec=new FrameCodec(shield); byte[] data=codec.Encode(new byte[]{0x11,1}).Concat(codec.Encode(new byte[]{0x21,1,2,3,4})).ToArray(); Assert(codec.Feed(data,data.Length).Count()==2); });
    Test($"Adapter protocol over mock USB ({shield})", () => {
        TransportRegistry.Factory=_=>new MockTransport(shield);
        var ap=new AdapterProtocol("test",shield?TwiKfdDevice.KfdShield:TwiKfdDevice.KfdTool); ap.Open();
        try { ap.Clear(); Assert(ap.ReadAdapterProtocolVersion()==new Version(2,2,0)); Assert(ap.ReadFirmwareVersion().SequenceEqual(new byte[]{1,8,8})); } finally { ap.Close(); }
    });
}
Test("KFDtool shared frame delimiters survive every read boundary", () => {
    byte[] wire={0x61,0x31,0,0,0x61,0x31,0,0xc0,0x61};
    for(int split=0;split<=wire.Length;split++) {
        var codec=new FrameCodec(false);
        var packets=codec.Feed(wire.Take(split).ToArray(),split).Concat(codec.Feed(wire.Skip(split).ToArray(),wire.Length-split)).ToArray();
        Assert(packets.Length==2 && packets[1].SequenceEqual(new byte[]{0x31,0,0xc0}));
    }
});
Test("USB detach invalidates a reused device path and a fresh session reconnects", () => {
    var manager=new Android.Hardware.Usb.UsbManager();
    var device=new Android.Hardware.Usb.UsbDevice(); manager.DeviceList[device.DeviceName]=device;
    var oldConnection=manager.Connection;
    var transport=new KfdMobile.AndroidApp.UsbSerialTransport(manager,device);
    transport.Open(); transport.Disconnect();
    Assert(oldConnection.Closes==1 && oldConnection.Disposals==1,"Detach must release the USB handle immediately");
    // Android can reuse the same device path on rapid reattachment.
    manager.DeviceList[device.DeviceName]=new Android.Hardware.Usb.UsbDevice();
    Throws(()=>transport.Read(new byte[64],100)); Throws(()=>transport.Write(new byte[]{1},100));
    transport.Dispose(); transport.Dispose();
    Assert(oldConnection.Closes==1 && oldConnection.Disposals==1 && oldConnection.Releases==1);
    manager.Connection=new();
    using var fresh=new KfdMobile.AndroidApp.UsbSerialTransport(manager,manager.DeviceList[device.DeviceName]);
    fresh.Open(); fresh.Write(new byte[]{1},100);
});
Test("USB open failure releases the connection through the serial owner", () => {
    var manager=new Android.Hardware.Usb.UsbManager(); var device=new Android.Hardware.Usb.UsbDevice();
    manager.DeviceList[device.DeviceName]=device; manager.Connection.ClaimSucceeds=false;
    TransportRegistry.Factory=_=>new KfdMobile.AndroidApp.UsbSerialTransport(manager,device);
    var ap=new AdapterProtocol("test",TwiKfdDevice.KfdTool);
    Throws(ap.Open); ap.Close();
    Assert(manager.Connection.Closes==1 && manager.Connection.Disposals==1);
});
Test("Encrypted container keys, groups, Unicode and desktop schema round trip", () => {
    using var s=new ContainerSession(); s.Create("Pässword-测试"); s.Inner.Keys.Add(Example()); s.Inner.Groups.Add(new GroupItem{Id=1,Name="Test group",Keys=new(){1}}); s.Inner.NextKeyNumber=2; s.Inner.NextGroupNumber=2;
    byte[] bytes=s.Save(); Assert(!System.Text.Encoding.UTF8.GetString(bytes).Contains(Example().Key));
    using var copy=new ContainerSession(); copy.Open(bytes,"Pässword-测试"); Assert(copy.Inner.Keys.Single().Key==Example().Key); Assert(copy.Inner.Groups.Single().Keys.Single()==1); Assert(copy.Inner.NextKeyNumber==2);
    Directory.CreateDirectory("work/test-fixtures"); File.WriteAllBytes("work/test-fixtures/android.ekc",bytes);
});
Test("Wrong password preserves current container", () => { using var s=new ContainerSession(); s.Create("correct"); s.Inner.Keys.Add(Example()); byte[] bytes=s.Save(); Throws(()=>s.Open(bytes,"incorrect")); Assert(s.Inner.Keys.Count==1); });
Test("Original desktop 1.8.8 exported container opens on port", () => { using var s=new ContainerSession(); s.Open(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","desktop-1.8.8.ekc")),"Pässword-测试"); Assert(s.Inner.Keys.Single().KeyId==1234 && s.Inner.Groups.Single().Keys.Single()==1); });
Test("Password change invalidates old password", () => { using var s=new ContainerSession(); s.Create("old"); s.ChangePassword("new"); byte[] bytes=s.Save(); using var r=new ContainerSession(); Throws(()=>r.Open(bytes,"old")); r.Open(bytes,"new"); });
Test("Random IV produces distinct ciphertext", () => { using var s=new ContainerSession(); s.Create("password"); Assert(!s.Save().SequenceEqual(s.Save())); });
Test("Malformed and oversized containers rejected", () => { using var s=new ContainerSession(); Throws(()=>s.Open(new byte[]{1,2},"x")); Throws(()=>s.Open(new byte[]{255,255,255,127,0},"x")); });
Test("P25 key conversion preserves IDs and automatic KEK classification", () => { var k=Example(); var c=ContainerSession.ToCommand(k); Assert(c.KeyId==1234 && c.Sln==7 && !c.IsKek && c.UseActiveKeyset); k.Sln=61440; Assert(ContainerSession.ToCommand(k).IsKek); });
Test("Invalid key lengths and IDs rejected", () => { var k=Example(); k.Key="00"; Throws(()=>ContainerSession.Validate(k)); k=Example(); k.KeyId=65536; Throws(()=>ContainerSession.Validate(k)); });
Test("DES parity and explicit keyset rules enforced", () => { var k=Example(); k.AlgorithmId=0x81; k.Key="0000000000000000"; Throws(()=>ContainerSession.Validate(k)); k.Key="133457799BBCDFF1"; ContainerSession.Validate(k); k.ActiveKeyset=false; k.KeysetId=0; Throws(()=>ContainerSession.Validate(k)); k.ActiveKeyset=true; ContainerSession.Validate(k); });
Test("Lock removes accessible key material", () => { using var s=new ContainerSession(); s.Create("test"); var k=Example(); s.Inner.Keys.Add(k); s.Dispose(); Assert(!s.IsOpen && k.Key==""); });
Test("Keysets with optional fields and multiple entries", () => {
    var parser = new KFDtool.P25.Kmm.InventoryResponseListKeysetTaggingInfo();
    // 2026-09-25 14:35:42; independent packed timestamp fixture.
    parser.Parse(new byte[]{0xf9,0,2, 0x63,1,0, 0x12,0x34,0x56, 0x9c,0x9a,0x74,0x75,0, 65,66,67, 0x81,2,0,90});
    Assert(parser.KeysetItems.Count==2);
    var first=parser.KeysetItems[0]; Assert(first.ReservedField==0x123456 && first.ActivationDateTime==new DateTime(2026,9,25,14,35,42) && first.KeysetName=="ABC");
    Assert(parser.KeysetItems[1].KeysetId==2 && parser.KeysetItems[1].KeysetType=="KEK" && parser.KeysetItems[1].KeysetName=="Z");
    parser.Parse(new byte[]{0xf9,0,0}); Assert(parser.KeysetItems.Count==0);
});
Test("Truncated keyset response rejected at every boundary", () => {
    byte[] complete={0xf9,0,1,0x63,1,0,0,0,0,0x9c,0x9a,0x74,0x75,0,65,66,67};
    for(int n=0;n<complete.Length;n++) Throws(()=>new KFDtool.P25.Kmm.InventoryResponseListKeysetTaggingInfo().Parse(complete.Take(n).ToArray()));
});
foreach(bool shield in new[]{false,true}) Test($"MR waits, handshakes and cancels ({shield})", () => {
    var transport=new MrTransport(shield); TransportRegistry.Factory=_=>transport;
    var ap=new AdapterProtocol("test",shield?TwiKfdDevice.KfdShield:TwiKfdDevice.KfdTool); ap.Open(); ap.Clear();
    var task=Task.Run(()=>new KFDtool.P25.ThreeWire.ThreeWireProtocol(ap).MrRunProducer());
    try {
        Thread.Sleep(150); Assert(!task.IsCompleted,"MR listener exited while idle");
        transport.Broadcast(0); transport.Broadcast(0xc0);
        Assert(transport.Finished.Wait(3000),"MR handshake did not finish");
        ap.Cancel(); Assert(task.Wait(2000),"Cancel did not stop listener");
        Assert(transport.Sent.SequenceEqual(new byte[]{0xd0,0xc1,0x90}));
    } finally { ap.Cancel(); task.Wait(2000); ap.Close(); }
});
foreach(bool shield in new[]{false,true}) foreach(int zeros in new[]{0,3}) Test($"MR ready synchronization without false key notifications ({shield}, zeros={zeros})", () => {
    var transport=new MrTransport(shield); TransportRegistry.Factory=_=>transport;
    var ap=new AdapterProtocol("test",shield?TwiKfdDevice.KfdShield:TwiKfdDevice.KfdTool); ap.Open(); ap.Clear();
    var emulator=new KFDtool.P25.ThreeWire.ThreeWireProtocol(ap); int received=0; emulator.KeyReceived+=_=>received++;
    var task=Task.Run(emulator.MrRunProducer);
    try {
        for(int i=0;i<zeros;i++) transport.Broadcast(0);
        Thread.Sleep(100); Assert(received==0 && !(emulator.Status ?? "").Contains("signature received"));
        transport.Broadcast(0xc0); Assert(transport.Finished.Wait(3000));
        Assert(received==0);
    } finally { ap.Cancel(); task.Wait(2000); ap.Close(); }
});
Test("MR remains available after an adapter acknowledgment timeout", () => {
    var transport=new MrTransport(true){FailReadyOnce=true}; TransportRegistry.Factory=_=>transport;
    var ap=new AdapterProtocol("test",TwiKfdDevice.KfdShield); ap.Open(); ap.Clear();
    var emulator=new KFDtool.P25.ThreeWire.ThreeWireProtocol(ap); var task=Task.Run(emulator.MrRunProducer);
    try {
        transport.Broadcast(0xc0);
        Assert(SpinWait.SpinUntil(()=>(emulator.Status ?? "").Contains("interrupted"),2000));
        Assert(!task.IsCompleted); transport.Broadcast(0xc0); Assert(transport.Finished.Wait(3000));
    } finally { ap.Cancel(); task.Wait(2000); ap.Close(); }
});
Test("MR explicitly rejects unsupported inventory requests", () => {
    var transport=new MrTransport(true,true){Unsupported=true}; TransportRegistry.Factory=_=>transport;
    var ap=new AdapterProtocol("test",TwiKfdDevice.KfdShield); ap.Open(); ap.Clear();
    var emulator=new KFDtool.P25.ThreeWire.ThreeWireProtocol(ap); int keys=0; emulator.KeyReceived+=_=>keys++;
    var task=Task.Run(emulator.MrRunProducer);
    try { transport.Broadcast(0xc0); Assert(transport.Finished.Wait(3000)); Assert(keys==0 && transport.KmmResponses==1 && emulator.Status.Contains("Unsupported")); }
    finally { ap.Cancel(); task.Wait(2000); ap.Close(); }
});
Test("Malformed modify-key payloads cannot create received keys", () => {
    var command=new KFDtool.P25.Kmm.ModifyKeyCommand{KeysetId=1,AlgorithmId=0x84};
    command.KeyItems.Add(new KFDtool.P25.Kmm.KeyItem{SLN=1,KeyId=1,Key=new byte[32]});
    byte[] valid=command.ToBytes();
    for(int n=0;n<valid.Length;n++) Throws(()=>new KFDtool.P25.Kmm.ModifyKeyCommand().Parse(valid.Take(n).ToArray()));
    Throws(()=>new KFDtool.P25.Kmm.ModifyKeyCommand().Parse(valid.Concat(new byte[]{0}).ToArray()));
});
Test("Radio RSI validation rejects reserved addresses, group conversion and invalid counters", () => {
    RadioValidation.Rsi(false,0,1,0); RadioValidation.Rsi(true,1,9999998,65535); RadioValidation.Rsi(true,10000000,16777214,0);
    Throws(()=>RadioValidation.Rsi(false,0,0,0)); Throws(()=>RadioValidation.Rsi(false,0,10000000,0));
    Throws(()=>RadioValidation.Rsi(true,1,9999999,0)); Throws(()=>RadioValidation.Rsi(true,1,10000000,0));
    Throws(()=>RadioValidation.Rsi(true,16777215,1,0)); Throws(()=>RadioValidation.Rsi(false,0,1,65536));
});
Test("CKR sort is numeric and algorithm sorting uses displayed names", () => {
    var a=Example(); a.Id=1;a.Sln=10;a.AlgorithmId=0x84;
    var b=Example(); b.Id=2;b.Sln=2;b.AlgorithmId=0x81;
    var rows=new[]{a,b};
    Assert(KeyPresentation.Sort(rows,4).First().Id==2); Assert(KeyPresentation.Sort(rows,5).First().Id==1);
    Assert(KeyPresentation.Sort(rows,6).First().Id==1); Assert(KeyPresentation.Sort(rows,7).First().Id==2);
});
Test("MR key text export includes identifiers and key data", () => {
    var key=new ReceivedMrKey(7,1234,1,0x84,"001122AABB");
    Assert(key.FileName=="SLN_7_KeyID_1234.txt");
    Assert(key.ToText()=="SLN: 7\r\nKey ID: 1234\r\nKeyset ID: 1\r\nAlgorithm ID: 0x84\r\nKey data: 001122AABB\r\n");
});
foreach(bool shield in new[]{false,true}) foreach(bool early in new[]{false,true}) Test($"MR key receive with early radio response={early} ({shield})", () => {
    var transport=new MrTransport(shield,true,early); TransportRegistry.Factory=_=>transport;
    var ap=new AdapterProtocol("test",shield?TwiKfdDevice.KfdShield:TwiKfdDevice.KfdTool); ap.Open(); ap.Clear();
    var emulator=new KFDtool.P25.ThreeWire.ThreeWireProtocol(ap); ReceivedMrKey received=null;
    emulator.KeyReceived+=key=>{ Assert(transport.KmmResponses==1,"Key callback ran before wire acknowledgment"); received=key; };
    var task=Task.Run(emulator.MrRunProducer);
    try {
        transport.Broadcast(0); transport.Broadcast(0xc0);
        Assert(transport.Finished.Wait(3000),"Transfer failed");
        ap.Cancel(); Assert(task.Wait(2000));
        Assert(received != null && received.Sln==7 && received.KeyId==1234 && received.KeysetId==2);
        Assert(received.KeyHex==Convert.ToHexString(Enumerable.Range(0,32).Select(x=>(byte)x).ToArray()));
        Assert(emulator.Status.Contains("Key received") && !emulator.Status.Contains(received.KeyHex),"Status must not contain key material");
    } finally { ap.Cancel(); task.Wait(2000); ap.Close(); }
});
foreach(bool shield in new[]{false,true}) Test($"MR keyload-all retains session across algorithm gaps ({shield})", () => {
    var transport=new MrTransport(shield){ManualSequence=true}; TransportRegistry.Factory=_=>transport;
    var ap=new AdapterProtocol("test",shield?TwiKfdDevice.KfdShield:TwiKfdDevice.KfdTool);ap.Open();ap.Clear();
    var emulator=new KFDtool.P25.ThreeWire.ThreeWireProtocol(ap);var keys=new System.Collections.Concurrent.ConcurrentQueue<ReceivedMrKey>();
    emulator.KeyReceived+=keys.Enqueue;var task=Task.Run(emulator.MrRunProducer);
    try {
        transport.Broadcast(0xc0);Assert(SpinWait.SpinUntil(()=>transport.Readies==1,2000));
        transport.KeyBatch(0xaa,5);Assert(SpinWait.SpinUntil(()=>keys.Count==1,2000));
        // Longer than the original five-second frame wait, without restarting the handshake.
        Thread.Sleep(5500); transport.KeyBatch(0x84,32);
        Assert(SpinWait.SpinUntil(()=>keys.Count==2,2000),"AES batch lost after inter-algorithm gap");
        Thread.Sleep(5500);transport.KeyBatch(0x81,8);Assert(SpinWait.SpinUntil(()=>keys.Count==3,2000),"DES batch lost after inter-algorithm gap");
        // Some senders separate batches with transfer-done, then another ready request.
        transport.Broadcast(0xc1);Assert(SpinWait.SpinUntil(()=>transport.DoneReplies==1,2000));
        Thread.Sleep(5500);transport.Broadcast(0);transport.Broadcast(0xc0);
        Assert(SpinWait.SpinUntil(()=>transport.Readies==2,2000),"Next ready request was consumed as a disconnect");
        transport.KeyBatch(0x84,32);Assert(SpinWait.SpinUntil(()=>keys.Count==4,2000));
        transport.Broadcast(0xc1);Assert(SpinWait.SpinUntil(()=>transport.DoneReplies==2,2000));
        transport.Broadcast(0x92);Assert(transport.Finished.Wait(2000));
        Assert(keys.Select(k=>k.AlgorithmId).SequenceEqual(new[]{0xaa,0x84,0x81,0x84}));
        ap.Cancel();Assert(task.Wait(2000));
    } finally {ap.Cancel();task.Wait(2000);ap.Close();}
});
Test("MR Stop remains responsive while waiting between batches", () => {
    var transport=new MrTransport(true){ManualSequence=true};TransportRegistry.Factory=_=>transport;
    var ap=new AdapterProtocol("test",TwiKfdDevice.KfdShield);ap.Open();ap.Clear();
    var task=Task.Run(()=>new KFDtool.P25.ThreeWire.ThreeWireProtocol(ap).MrRunProducer());
    try {transport.Broadcast(0xc0);Assert(SpinWait.SpinUntil(()=>transport.Readies==1,2000));ap.Cancel();Assert(task.Wait(1000));}
    finally{ap.Cancel();task.Wait(2000);ap.Close();}
});
foreach(int fault in new[]{1,2,3}) Test($"MR frame diagnostic and retry contain no key material (fault {fault})", () => {
    var transport=new MrTransport(true){ManualSequence=true};TransportRegistry.Factory=_=>transport;
    var ap=new AdapterProtocol("test",TwiKfdDevice.KfdShield);ap.Open();ap.Clear();
    var emulator=new KFDtool.P25.ThreeWire.ThreeWireProtocol(ap);int keys=0;emulator.KeyReceived+=_=>Interlocked.Increment(ref keys);
    var task=Task.Run(emulator.MrRunProducer);
    try {
        transport.Broadcast(0xc0);Assert(SpinWait.SpinUntil(()=>transport.Readies==1,2000));transport.KeyBatch(0x84,32,fault);
        string expected=fault==1?"checksum mismatch":fault==2?"frame timeout":"length invalid";
        Assert(SpinWait.SpinUntil(()=>(emulator.Status??"").Contains(expected),6500));Assert(keys==0);
        Assert(!emulator.Status.Contains(Convert.ToHexString(Enumerable.Range(0,32).Select(i=>(byte)i).ToArray())));
        transport.Broadcast(0xc0);Assert(SpinWait.SpinUntil(()=>transport.Readies==2,2000));transport.KeyBatch(0x81,8);Assert(SpinWait.SpinUntil(()=>keys==1,2000));
    }finally{ap.Cancel();task.Wait(2000);ap.Close();}
});
Test("Radio read retries are bounded and do not retry USB removal or rejection", () => {
    int calls=0;int result=RadioReadRetry.Run(()=>{if(++calls<3)throw new TimeoutException();return 7;},_=>{});Assert(result==7 && calls==3);
    calls=0;Throws(()=>RadioReadRetry.Run<int>(()=>{calls++;throw new TimeoutException();},_=>{}));Assert(calls==3);
    calls=0;Throws(()=>RadioReadRetry.Run<int>(()=>{calls++;throw new IOException("USB disconnected");},_=>{}));Assert(calls==1);
    calls=0;Throws(()=>RadioReadRetry.Run<int>(()=>{calls++;throw new Exception("Radio rejected change status 1");},_=>{}));Assert(calls==1);
});
Test("RSI commands preserve addresses and radio status 1 remains rejection", () => {
    var kmf=new KFDtool.P25.Kmm.LoadConfigCommand{KmfRsi=0x123456,MessageNumberPeriod=0x789a};Assert(kmf.ToBytes().SequenceEqual(new byte[]{0x12,0x34,0x56,0x78,0x9a}));
    var radio=new KFDtool.P25.Kmm.ChangeRsiCommand{RsiOld=0x123456,RsiNew=0x654321,MessageNumber=0x789a};Assert(radio.ToBytes().SequenceEqual(new byte[]{1,0x12,0x34,0x56,0x65,0x43,0x21,0x78,0x9a}));
    var response=new KFDtool.P25.Kmm.LoadConfigResponse();response.Parse(new byte[]{0x12,0x34,0x56,0x78,0x9a,1});Assert(response.Status==1 && response.RSI==0x123456);
});
Test("Backup names preserve container name and save timestamp without path traversal", () => {
    var time=new DateTimeOffset(2026,10,15,16,27,31,123,TimeSpan.FromHours(-4));
    Assert(BackupNaming.Name("Team Alpha.ekc",time)=="Team Alpha_2026-10-15_16-27-31-123.ekc");
    Assert(BackupNaming.Name("",time).StartsWith("container_"));
    string name=BackupNaming.Name("../../bad:name.ekc",time);Assert(!name.Contains('/') && !name.Contains('\\') && !name.Contains(':'));
    Assert(BackupNaming.Name(new string('X',200),time).Length<120);
});
Test("MR import adds received key and survives encrypted save", () => {
    using var s=new ContainerSession(); s.Create("test");
    var received=new ReceivedMrKey(7,1234,2,0x84,Example().Key);
    var added=s.ImportReceived(received);
    Assert(s.Dirty && !added.ActiveKeyset && added.KeysetId==2 && added.Key==received.KeyHex);
    using var opened=new ContainerSession(); opened.Open(s.Save(),"test"); Assert(opened.Inner.Keys.Single().KeyId==1234);
});
Test("MR replacement preserves record identity, name and groups", () => {
    using var s=new ContainerSession(); s.Create("test"); s.Inner.Keys.Add(Example()); s.Inner.Groups.Add(new GroupItem{Id=1,Name="Group",Keys=new(){1}});
    var received=new ReceivedMrKey(7,1234,1,0x84,new string('A',64));
    s.ImportReceived(received,1);
    Assert(s.Inner.Keys.Count==1 && s.Inner.Keys[0].Id==1 && s.Inner.Keys[0].Name=="Test AES" && s.Inner.Keys[0].Key==received.KeyHex && s.Inner.Groups[0].Keys.Single()==1);
});
Test("Invalid MR import leaves container unchanged", () => {
    using var s=new ContainerSession(); s.Create("test"); s.Inner.Keys.Add(Example()); s.Dirty=false;
    Throws(()=>s.ImportReceived(new ReceivedMrKey(7,1234,1,0x84,"00"),1));
    Assert(!s.Dirty && s.Inner.Keys.Single().Key==Example().Key);
    s.Dispose(); Throws(()=>s.ImportReceived(new ReceivedMrKey(7,1234,1,0x84,Example().Key)));
});
Test("DKF decrypts with export password and preserves metadata", () => {
    using var session=new ContainerSession(); session.Create("container"); session.Inner.Keys.Add(Example());
    var excluded=Example(); excluded.Id=2; excluded.Sln=61440; session.Inner.Keys.Add(excluded);
    byte[] dkf=session.ExportDkf("export-password");
    var doc=System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(dkf));
    string Param(string id)=>doc.Descendants("param").Single(p=>(string)p.Attribute("id")==id).Attribute("value").Value;
    byte[] derived=Rfc2898DeriveBytes.Pbkdf2("export-password",Convert.FromBase64String(Param("salt")),10000,HashAlgorithmName.SHA1,32);
    using var aes=Aes.Create(); aes.Key=derived; aes.IV=Convert.FromBase64String(Param("iv"));
    using var decryptor=aes.CreateDecryptor(); byte[] encrypted=Convert.FromBase64String(Param("content"));
    byte[] compressed=decryptor.TransformFinalBlock(encrypted,0,encrypted.Length);
    using var gzip=new System.IO.Compression.GZipStream(new MemoryStream(compressed),System.IO.Compression.CompressionMode.Decompress);
    using var reader=new StreamReader(gzip); var inner=System.Xml.Linq.XDocument.Parse(reader.ReadToEnd());
    var key=inner.Descendants("key").Single(); Assert((string)key.Attribute("id")=="1234" && (string)key.Attribute("sln")=="7" && (string)key.Attribute("value")==Example().Key);
    Assert(session.Dirty && session.Inner.Keys.Count==2);
});
Test("DKF rejects missing password and empty supported selection", () => { using var s=new ContainerSession(); s.Create("test"); Throws(()=>s.ExportDkf("")); Throws(()=>s.ExportDkf("test")); });
KFDtool.Adapter.Bundle.Update FirmwareExample() => new() { AppVersion="1.0.0", RamBslVersion="test", AppData=System.Text.Encoding.ASCII.GetBytes("@8000\n01 02 03 04\n@FFFE\n00 80\nq"), RamBslData=System.Text.Encoding.ASCII.GetBytes("@2504\n01 02\nq"), ProductModel=new(){new(){Name="KFD100",Revision=new(){"1.0","1.1"}}} };
Test("Firmware revision matching includes previous revisions and blocks others", () => { var p=new FirmwarePlan(FirmwareExample()); Assert(p.Supports("KFD100","1.0") && p.Supports("KFD100","1.1") && !p.Supports("KFD100","2.0") && !p.Supports("KFDpico","1.0")); });
Test("Firmware rejects malformed, overlapping and unsafe images", () => {
    foreach(string text in new[]{"@8000 01", "@8000 0Z q", "@8000 01 02 @8001 03 q", "@FFFF 01 02 q", "@8000 01 q 02"}) Throws(()=>FirmwarePlan.ParseTiText(text));
    var p=FirmwareExample(); p.AppData=System.Text.Encoding.ASCII.GetBytes("@0120 A5 06 @FFFE 20 01 q"); Throws(()=>new FirmwarePlan(p));
});
Test("BSL HID framing and malformed report rejection", () => { byte[] data={0x10,0x00,0x80,0,1,2}; var report=BslUpdate.EncodeReport(data); Assert(report.Length==64 && BslUpdate.DecodeReport(report,64).SequenceEqual(data)); Throws(()=>BslUpdate.DecodeReport(new byte[]{0x3f,62,0},3)); });
Test("BSL update waits for acknowledgments and verifies all written bytes", () => {
    var plan=new FirmwarePlan(FirmwareExample()); using var device=new FakeBsl(); BslUpdate.LoadRam(device,plan); BslUpdate.Program(device,plan,_=>{}); Assert(device.Launched==0x8000 && device.Readbacks==2);
});
Test("BSL verification failure never launches firmware", () => { var plan=new FirmwarePlan(FirmwareExample()); using var device=new FakeBsl { Corrupt=true }; Throws(()=>BslUpdate.Program(device,plan,_=>{})); Assert(device.Launched==0); });
Test("Firmware package parser rejects DTD and malformed checksum", () => { Throws(()=>FirmwarePlan.ReadPackage(System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a 'x'>]><x/>"))); Throws(()=>FirmwarePlan.ReadPackage(System.Text.Encoding.UTF8.GetBytes("<FirmwarePackage version='1.0'/>"))); });
Test("Upstream UFP and CFP integrity accepts valid package and rejects tampering", () => {
    string fixtureDir=Path.Combine("work","firmware-test"); Directory.CreateDirectory(fixtureDir);
    var update=FirmwareExample();
    string app=Path.GetFullPath(Path.Combine(fixtureDir,"app.txt")); string ram=Path.GetFullPath(Path.Combine(fixtureDir,"ram.txt"));
    File.WriteAllBytes(app,update.AppData); File.WriteAllBytes(ram,update.RamBslData);
    var template=new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("Generate",new System.Xml.Linq.XAttribute("version","1.0"),
        new System.Xml.Linq.XElement("Update",new System.Xml.Linq.XElement("AppDataPath",app),new System.Xml.Linq.XElement("AppVersion","1.0.0"),new System.Xml.Linq.XElement("Audience","TEST"),new System.Xml.Linq.XElement("RamBslDataPath",ram),new System.Xml.Linq.XElement("RamBslVersion","test"),new System.Xml.Linq.XElement("ProductModel",new System.Xml.Linq.XElement("Model",new System.Xml.Linq.XElement("Name","KFD100"),new System.Xml.Linq.XElement("Revision",new System.Xml.Linq.XElement("string","1.0")))))));
    string source=Path.Combine(fixtureDir,"generate.xml"), output=Path.Combine(fixtureDir,"bundle"); template.Save(source);
    KFDtool.Adapter.Bundle.Firmware.GenerateUpdate(source,output);
    foreach(string extension in new[]{".ufp",".cfp"}) Assert(new FirmwarePlan(FirmwarePlan.ReadPackage(File.ReadAllBytes(output+extension)).Single()).Supports("KFD100","1.0"));
    string altered=File.ReadAllText(output+".ufp").Replace("<AppVersion>1.0.0</AppVersion>","<AppVersion>9.0.0</AppVersion>");
    Throws(()=>FirmwarePlan.ReadPackage(System.Text.Encoding.UTF8.GetBytes(altered)));
});
Test("KID base switching preserves every valid 16-bit ID", () => {
    for(int kid=0;kid<=65535;kid++) foreach(bool hex in new[]{false,true})
        Assert(KeyPresentation.ParseKid(KeyPresentation.Kid(kid,hex),hex)==kid);
    foreach(var bad in new[]{"-1","65536","1.0","","0x10"}) Throws(()=>KeyPresentation.ParseKid(bad,false));
    foreach(var bad in new[]{"-1","10000","GG",""}) Throws(()=>KeyPresentation.ParseKid(bad,true));
});
Test("Key sorting is numeric for KID and case-insensitive for names", () => {
    var keys=new[]{new KeyItem{Id=1,KeyId=16,Name="bravo"},new KeyItem{Id=2,KeyId=2,Name="Alpha"},new KeyItem{Id=3,KeyId=255,Name="charlie"}};
    Assert(KeyPresentation.Sort(keys,0).Select(k=>k.Id).SequenceEqual(new[]{2,1,3}));
    Assert(KeyPresentation.Sort(keys,1).Select(k=>k.Id).SequenceEqual(new[]{3,1,2}));
    Assert(KeyPresentation.Sort(keys,2).Select(k=>k.Id).SequenceEqual(new[]{2,1,3}));
    Assert(KeyPresentation.Sort(keys,3).Select(k=>k.Id).SequenceEqual(new[]{3,1,2}));
    Assert(keys[0].Id==1);
});
Test("Algorithm labels never identify unknown IDs as DES", () => {
    Assert(KeyPresentation.Algorithm(0x84)=="AES-256"); Assert(KeyPresentation.Algorithm(0x81)=="DES-OFB");
    Assert(KeyPresentation.Algorithm(0x44)=="Unknown (0x44)");
});
Test("MR apply-all new-key consent never silently authorizes replacements", () => {
    var policy=new MrImportPolicy(); policy.Apply(false,true);
    Assert(policy.Choice(false)==true && policy.Choice(true)==null);
    policy.Apply(true,false); Assert(policy.Choice(true)==false && policy.Choice(false)==true);
    policy.Reset(); Assert(policy.Choice(true)==null && policy.Choice(false)==null);
});
Test("A completed save cannot mark later edits or another container clean", () => {
    var container=new object(); var stamp=new SaveStamp(container,3);
    Assert(stamp.Matches(container,3)); Assert(!stamp.Matches(container,4)); Assert(!stamp.Matches(new object(),3));
});

foreach(bool shield in new[]{false,true}) Test($"MR receives multi-key batches across all three algorithms ({shield})",()=>{
    var transport=new MrTransport(shield){ManualSequence=true};TransportRegistry.Factory=_=>transport;
    var ap=new AdapterProtocol("test",shield?TwiKfdDevice.KfdShield:TwiKfdDevice.KfdTool);ap.Open();ap.Clear();
    var emulator=new KFDtool.P25.ThreeWire.ThreeWireProtocol(ap);int count=0;emulator.KeyReceived+=_=>Interlocked.Increment(ref count);
    var task=Task.Run(emulator.MrRunProducer);
    try {
        transport.Broadcast(0xc0);Assert(SpinWait.SpinUntil(()=>transport.Readies==1,2000));
        int expected=0;
        foreach(var pair in new[]{(0xaa,5),(0x84,32),(0x81,8)}){
            transport.KeyBatch(pair.Item1,pair.Item2,count:32);expected+=32;
            Assert(SpinWait.SpinUntil(()=>count==expected,5000),"Every key in each batch must arrive");
        }
    }finally{ap.Cancel();Assert(task.Wait(2000));ap.Close();}
});
Test("Bulk keyload preserves transfer failure when cleanup also fails", () => {
    var application=new KFDtool.P25.ManualRekey.ManualRekeyApplication(new AdapterProtocol("test",TwiKfdDevice.KfdShield));
    var protocol=new FailedKeyloadProtocol();
    typeof(KFDtool.P25.ManualRekey.ManualRekeyApplication).GetField("DeviceProtocol",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).SetValue(application,protocol);
    Exception caught=null;
    try { application.Keyload(new(){ContainerSession.ToCommand(Example())}); } catch(Exception ex){caught=ex;}
    Assert(ReferenceEquals(caught?.InnerException,protocol.TransferFailure) && caught.Message.Contains(protocol.TransferFailure.Message),"Cleanup must not hide the original failure");
    Assert(protocol.Transfers==1 && protocol.Ends==1,"Do not retry key writes");
});
StorageTests.Run(Test);
RadioLoadTests.Run(Test);
Console.WriteLine($"{passed} tests passed.");

sealed class MockTransport(bool shield) : IByteTransport
{
    readonly FrameCodec codec=new(shield); readonly Queue<byte> response=new();
    public void Open(){} public void Dispose(){} public void Purge()=>response.Clear();
    public int Read(byte[] buffer,int timeoutMs) { int n=0; while(n<3 && response.Count>0) buffer[n++]=response.Dequeue(); return n; }
    public void Write(byte[] bytes,int timeoutMs) {
        foreach(var packet in codec.Feed(bytes,bytes.Length)) {
            byte[] rsp=packet[0] switch { 0x11 when packet[1]==1=>new byte[]{0x21,1,2,2,0},0x11 when packet[1]==2=>new byte[]{0x21,2,1,8,8},0x12=>new byte[]{0x22},_=>throw new Exception("Unexpected command") };
            foreach(byte b in codec.Encode(rsp)) response.Enqueue(b);
        }
    }
}

sealed class MrTransport(bool shield, bool sendKey = false, bool early = false) : IByteTransport
{
    readonly FrameCodec codec=new(shield);
    readonly System.Collections.Concurrent.ConcurrentQueue<byte> response=new();
    public readonly ManualResetEventSlim Finished=new();
    public readonly List<byte> Sent=new();
    public bool FailReadyOnce, Unsupported, ManualSequence; public int KmmResponses; public volatile int Readies, DoneReplies;
    public void Open(){} public void Dispose(){} public void Purge(){while(response.TryDequeue(out _)) {}}
    void Reply(byte[] data) { foreach(byte b in codec.Encode(data)) response.Enqueue(b); }
    public void Broadcast(byte data)=>Reply(new byte[]{0x31,0,data});
    public void KeyBatch(int algorithm,int length,int fault=0,int count=1) {
        if(fault==3){Broadcast(0xc2);Broadcast(0);Broadcast(10);return;}
        var command=new KFDtool.P25.Kmm.ModifyKeyCommand{KeysetId=2,AlgorithmId=algorithm};
        for(int k=0;k<count;k++)command.KeyItems.Add(new KFDtool.P25.Kmm.KeyItem{SLN=7+k,KeyId=1234+k,Key=Enumerable.Range(0,length).Select(i=>(byte)i).ToArray()});
        var body=new byte[]{0,255,255,255}.Concat(new KFDtool.P25.Kmm.KmmFrame(command).ToBytes()).ToArray();
        ushort crc=65535;foreach(byte b in body){crc^=b;for(int bit=0;bit<8;bit++)crc=(ushort)((crc>>1)^((crc&1)!=0?0x8408:0));}
        if(fault==1)crc^=1;
        var wire=new byte[]{0xc2,(byte)((body.Length+2)>>8),(byte)(body.Length+2)}.Concat(body).Concat(new byte[]{(byte)crc,(byte)(crc>>8)}).ToArray();
        foreach(byte b in wire.Take(wire.Length-(fault==2?1:0)))Broadcast(b);
    }
    public int Read(byte[] buffer,int timeoutMs) { int n=0; while(n<buffer.Length && response.TryDequeue(out byte b)) buffer[n++]=b; if(n==0) Thread.Sleep(Math.Min(10,timeoutMs)); return n; }
    public void Write(byte[] bytes,int timeoutMs) {
        foreach(var packet in codec.Feed(bytes,bytes.Length)) {
            if(packet[0]==0x11) Reply(new byte[]{0x21,1,2,2,0});
            else if(packet[0]==0x12) Reply(new byte[]{0x22});
            else if(packet[0]==0x17) {
                if(packet[2]==0xd0 && FailReadyOnce) { FailReadyOnce=false; throw new TimeoutException("Synthetic adapter timeout"); }
                Sent.Add(packet[2]); if (!early) Reply(new byte[]{0x27});
                if(packet[2]==0xd0) {
                    Readies++;
                    if (ManualSequence) { if(early) Reply(new byte[]{0x27}); continue; }
                    if (!sendKey) Broadcast(0xc1);
                    else {
                        var command=new KFDtool.P25.Kmm.ModifyKeyCommand { KeysetId=2, AlgorithmId=0x84 };
                        command.KeyItems.Add(new KFDtool.P25.Kmm.KeyItem { SLN=7, KeyId=1234, Key=Enumerable.Range(0,32).Select(x=>(byte)x).ToArray() });
                        byte[] kmm=new KFDtool.P25.Kmm.KmmFrame(Unsupported ? new KFDtool.P25.Kmm.InventoryCommandListActiveKeys() : command).ToBytes();
                        var body=new byte[]{0,255,255,255}.Concat(kmm).ToArray();
                        ushort checksum=65535; foreach(byte octet in body) { checksum ^= octet; for(int bit=0;bit<8;bit++) checksum=(ushort)((checksum >> 1) ^ ((checksum & 1)!=0 ? 0x8408 : 0)); } var crc=new byte[]{(byte)checksum,(byte)(checksum>>8)};
                        foreach(byte b in new byte[]{0xc2,(byte)((body.Length+2)>>8),(byte)(body.Length+2)}.Concat(body).Concat(crc)) Broadcast(b);
                    }
                }
                else if(packet[2]==0xc1) { DoneReplies++; if(!ManualSequence) Broadcast(0x92); }
                else if(packet[2]==0x90) Finished.Set();
                if (early) Reply(new byte[]{0x27});
            } else if(packet[0]==0x18) { KmmResponses++; Reply(new byte[]{0x28}); if(!ManualSequence) Broadcast(0xc1); }
            else throw new Exception("Unexpected adapter command");
        }
    }
}

sealed class FakeBsl : IBslTransport
{
    readonly Dictionary<int,byte> memory=new(); readonly Queue<byte[]> replies=new();
    public int Launched, Readbacks; public bool Corrupt;
    public void Dispose() { }
    public void Send(byte[] command)
    {
        int address=command.Length>=4 ? command[1]|command[2]<<8|command[3]<<16 : 0;
        switch(command[0]) {
            case 0x11: case 0x15: replies.Enqueue(new byte[]{0x3b,0}); break;
            case 0x1b: break;
            case 0x10: for(int i=4;i<command.Length;i++) memory[address+i-4]=command[i]; replies.Enqueue(new byte[]{0x3b,0}); break;
            case 0x18: Readbacks++; replies.Enqueue(new byte[]{0x3a}.Concat(Enumerable.Range(0,command[4]|command[5]<<8).Select(i=>(byte)(memory[address+i] ^ (Corrupt?1:0)))).ToArray()); break;
            case 0x17: Launched=address; break;
            default: throw new Exception("Unexpected BSL command");
        }
    }
    public byte[] Receive()=>replies.Dequeue();
}

sealed class FailedKeyloadProtocol : KFDtool.P25.DeviceProtocol.IDeviceProtocol
{
    public readonly Exception TransferFailure=new IOException("Synthetic transfer failure");
    public int Transfers,Ends;
    public void SendKeySignature() { }
    public KFDtool.P25.DeviceProtocol.DeviceType InitSession()=>KFDtool.P25.DeviceProtocol.DeviceType.Kvl;
    public void CheckTargetMrConnection() { }
    public void EndSession(){Ends++;throw new IOException("Synthetic cleanup failure");}
    public byte[] PerformKmmTransfer(byte[] kmm){Transfers++;throw TransferFailure;}
}
