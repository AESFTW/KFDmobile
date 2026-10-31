using KFDtool.Adapter.Protocol.Adapter;
using KFDtool.P25.Constant;
using KFDtool.P25.DeviceProtocol;
using KFDtool.P25.Kmm;
using KFDtool.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KFDtool.P25.ThreeWire
{
    public class ThreeWireProtocol : IDeviceProtocol
    {
        public event Action<KfdMobile.ReceivedMrKey> KeyReceived;
        private static NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        private const int TIMEOUT_NONE = 0; // no timeout
        private const int TIMEOUT_STD = 5000; // 5 second timeout

        private const byte OPCODE_READY_REQ = 0xC0;
        private const byte OPCODE_READY_GENERAL_MODE_MR = 0xD0;
        private const byte OPCODE_READY_GENERAL_MODE_KVL = 0xD1;
        private const byte OPCODE_TRANSFER_DONE = 0xC1;
        private const byte OPCODE_KMM = 0xC2;
        private const byte OPCODE_DISCONNECT_ACK = 0x90;
        private const byte OPCODE_DISCONNECT = 0x92;

        private AdapterProtocol Protocol;

        public ThreeWireProtocol(AdapterProtocol ap)
        {
            Protocol = ap;
        }

        public void SendKeySignature()
        {
            if (Protocol.FeatureAvailableSendKeySignatureAndReadyReq)
            {
                Log.Debug("kfd: key signature and ready req");
                Protocol.SendKeySignatureAndReadyReq();
            }
            else
            {
                Log.Debug("kfd: key signature");
                Protocol.SendKeySignature();
            }
        }

        public DeviceType InitSession()
        {
            if (!Protocol.FeatureAvailableSendKeySignatureAndReadyReq)
            {
                // send ready req opcode
                List<byte> cmd = new List<byte>();
                cmd.Add(OPCODE_READY_REQ);
                Log.Debug("kfd: ready req");
                Log.Debug("kfd -> mr: {0}", Utility.DataFormat(cmd));
                Protocol.SendData(cmd);
            }

            // receive ready general mode opcode
            Log.Debug("mr: ready general mode");
            byte rsp = Protocol.GetByte(TIMEOUT_STD);
            Log.Debug("mr -> kfd: {0}", Utility.DataFormat(rsp));
            switch (rsp)
            {
                case OPCODE_READY_GENERAL_MODE_MR: return DeviceType.Mr;
                case OPCODE_READY_GENERAL_MODE_KVL: return DeviceType.Kvl;
                default: throw new Exception($"Radio handshake failed: expected ready D0/D1, received {rsp:X2}. The USB adapter is connected, but the radio did not complete its ready response.");
            }
        }

        public void CheckTargetMrConnection()
        {
            SendKeySignature();
            InitSession();
            EndSession();
        }

        private List<byte> CreateKmmFrame(List<byte> kmm)
        {
            // create body
            List<byte> body = new List<byte>();

            body.Add(0x00); // control
            body.Add(0xFF); // destination RSI high byte
            body.Add(0xFF); // destination RSI mid byte
            body.Add(0xFF); // destination RSI low byte
            body.AddRange(kmm); // kmm

            // calculate crc
            byte[] crc = CRC16.CalculateCrc(body.ToArray());

            // create frame
            List<byte> frame = new List<byte>();

            int length = body.Count + 2; // control + dest rsi + kmm + crc

            frame.Add(OPCODE_KMM); // kmm opcode

            frame.Add((byte)((length >> 8) & 0xFF)); // length high byte
            frame.Add((byte)(length & 0xFF)); // length low byte

            frame.AddRange(body); // kmm body

            frame.Add(crc[0]); // crc high byte
            frame.Add(crc[1]); // crc low byte

            return frame;
        }

        private List<byte> ParseKmmFrame(bool receivingKeys = false)
        {
            string context = receivingKeys ? "MR frame" : "Radio response frame";
            string result = receivingKeys ? "No key imported." : "Radio response was not confirmed.";
            int received=0, expected=2;
            byte Read()
            {
                try { byte value=Protocol.GetByte(TIMEOUT_STD); received++; return value; }
                catch (TimeoutException) { throw new TimeoutException($"{context} timeout: received {received} of {expected} bytes after the frame marker. {result}"); }
            }
            int length=(Read()<<8)|Read();
            if(length<16) throw new FormatException($"{context} length invalid ({length} bytes). {result}");
            expected=2+length;
            var body=new byte[length-2];
            for(int i=0;i<body.Length;i++)body[i]=Read();
            byte first=Read(),second=Read();
            byte[] crc=CRC16.CalculateCrc(body);
            if(crc[0]!=first || crc[1]!=second)
                throw new FormatException($"{context} checksum mismatch after {received} bytes. {result}");
            return body.Skip(4).ToList();
        }

        public void EndSession()
        {
            // send transfer done opcode
            List<byte> cmd1 = new List<byte>();
            cmd1.Add(OPCODE_TRANSFER_DONE);
            Log.Debug("kfd: transfer done");
            Log.Debug("kfd -> mr: {0}", Utility.DataFormat(cmd1));
            Protocol.SendData(cmd1);

            // receive transfer done opcode
            Log.Debug("mr: transfer done");
            byte rsp1 = Protocol.GetByte(TIMEOUT_STD);
            Log.Debug("mr -> kfd: {0}", Utility.DataFormat(rsp1));
            if (rsp1 != OPCODE_TRANSFER_DONE)
            {
                throw new Exception("mr: unexpected opcode");
            }

            // send disconnect opcode
            List<byte> cmd2 = new List<byte>();
            cmd2.Add(OPCODE_DISCONNECT);
            Log.Debug("kfd: disconnect");
            Log.Debug("kfd -> mr: {0}", Utility.DataFormat(cmd2));
            Protocol.SendData(cmd2);

            // receive disconnect ack opcode
            Log.Debug("mr: disconnect ack");
            byte rsp2 = Protocol.GetByte(TIMEOUT_STD);
            Log.Debug("mr -> kfd: {0}", Utility.DataFormat(rsp2));
            if (rsp2 != OPCODE_DISCONNECT_ACK)
            {
                throw new Exception("mr: unexpected opcode");
            }
        }

        private void SendKmm(byte[] inKmm)
        {
            if (inKmm.Length > 512)
            {
                throw new Exception("kmm exceeds max size");
            }

            List<byte> txFrame = CreateKmmFrame(inKmm.ToList());
            Log.Debug("out: {0}", Utility.DataFormat(txFrame));
            Protocol.SendData(txFrame);
        }

        public byte[] PerformKmmTransfer(byte[] inKmm)
        {
            Log.Debug("KFD -> MR KMM FRAME: {0}", BitConverter.ToString(inKmm));

            // send kmm frame
            SendKmm(inKmm);

            byte rx;

            // receive kmm opcode
            try
            {
                Log.Debug("in: waiting for kmm opcode");
                rx = Protocol.GetByte(TIMEOUT_STD);
                Log.Trace("in: {0}", Utility.DataFormat(rx));
            }
            catch (TimeoutException ex)
            {
                string msg = "Radio response timeout waiting for the frame marker.";
                Log.Warn(msg);
                throw new TimeoutException(msg, ex);
            }

            if (rx == OPCODE_KMM)
            {
                Log.Debug("in: got kmm opcode");
            }
            else
            {
                string msg = string.Format("in: unexpected kmm opcode, expected ({0}) got ({1})", Utility.DataFormat(OPCODE_KMM), Utility.DataFormat(rx));
                Log.Warn(msg);
                throw new Exception(msg);
            }

            // receive kmm frame
            byte[] rxFrame = ParseKmmFrame().ToArray();

            Log.Debug("MR -> KFD KMM FRAME: {0}", BitConverter.ToString(rxFrame));

            return rxFrame;
        }

        public event EventHandler StatusChanged;

        private string _status;

        public string Status
        {
            get { return _status; }
            set
            {
                if (_status != value)
                {
                    _status = value != null && value.Length > 65536 ? value.Substring(value.Length - 65536) : value;
                    OnStatusChanged(new EventArgs());
                }
            }
        }

        private void OnStatusChanged(EventArgs e)
        {
            EventHandler handler = StatusChanged;
            if (handler != null)
                handler(this, e);
        }

        public void MrRunProducer()
        {
            try
            {
                while (true)
                {
                    try
                    {
                    // The adapter exposes the physical signature only as zero bytes.
                    // Zeros alone are not proof of a key or even a valid transfer.
                    // Synchronize on the actual ready request, allowing repeated or
                    // absent signature bytes, and remain cancellable while idle.
                    byte rx;
                    do { rx = Protocol.GetByte(TIMEOUT_NONE); }
                    while (rx != OPCODE_READY_REQ);
                    Status += "Ready request received; accepting transfer." + Environment.NewLine;

                    /* TX: READY GENERAL MODE */

                    Log.Debug("out: ready general mode opcode");
                    Log.Trace("out: {0}", Utility.DataFormat(OPCODE_READY_GENERAL_MODE_MR));
                    Protocol.SendByte(OPCODE_READY_GENERAL_MODE_MR);

                    while (true)
                    {
                        /* RX: FRAME TYPE */

                        // Algorithm batches can be separated by a long operator/KVL pause.
                        // Stay in the established session until a real protocol event arrives.
                        // Payload reads retain TIMEOUT_STD; this idle wait is cancellable.
                        rx = Protocol.GetByte(TIMEOUT_NONE);
                        if (rx == 0x00) continue; // optional physical signature before next batch
                        if (rx == OPCODE_READY_REQ)
                        {
                            Protocol.SendByte(OPCODE_READY_GENERAL_MODE_MR);
                            Status += "Next batch ready; listening for keys." + Environment.NewLine;
                            continue;
                        }

                        if (rx == OPCODE_KMM)
                        {
                            Log.Debug("in: got kmm opcode");

                            List<byte> rxFrame;

                            try
                            {
                                rxFrame = ParseKmmFrame(receivingKeys: true);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not System.IO.IOException)
                            {
                                Log.Warn(ex.Message);
                                Status += (ex is TimeoutException || ex is FormatException ? ex.Message : "MR frame could not be decoded. No key imported.") + " Listening for a new ready request." + Environment.NewLine;
                                break;
                            }

                            Log.Debug("kmm frame in: {0}", BitConverter.ToString(rxFrame.ToArray()));

                            KmmFrame kfdKmmFrame = null;

                            try
                            {
                                kfdKmmFrame = new KmmFrame(false, rxFrame.ToArray());
                            }
                            catch (Exception ex)
                            {
                                Log.Warn(ex.Message);

                                byte[] message = rxFrame.ToArray();

                                if (message.Length != 0)
                                {
                                    Log.Warn("unexpected message id: {0} (dec), {0:X} (hex)", message[0]);

                                    NegativeAcknowledgment kmm = new NegativeAcknowledgment();

                                    kmm.AcknowledgedMessageId = (MessageId)message[0];
                                    kmm.Status = OperationStatus.InvalidMessageId;

                                    KmmFrame frame = new KmmFrame(kmm);

                                    SendKmm(frame.ToBytes());
                                    Status += "Unsupported or malformed key-management request rejected; still listening." + Environment.NewLine;
                                }

                                continue;
                            }

                            KmmBody kfdKmmBody = kfdKmmFrame.KmmBody;

                            if (kfdKmmBody is InventoryCommandListActiveKsetIds)
                            {
                                InventoryResponseListActiveKsetIds mrKmm = new InventoryResponseListActiveKsetIds();

                                // do not return any keysets, to match factory Motorola SU behavior

                                KmmFrame commandKmmFrame = new KmmFrame(mrKmm);

                                SendKmm(commandKmmFrame.ToBytes());
                            }
                            else if (kfdKmmBody is InventoryCommandListRsiItems)
                            {
                                InventoryResponseListRsiItems mrKmm = new InventoryResponseListRsiItems();

                                RsiItem item = new RsiItem();

                                // set RSI and message number to match factory Motorola SU behavior

                                item.RSI = 0x000001;
                                item.MessageNumber = 0x0000;

                                mrKmm.RsiItems.Add(item);

                                KmmFrame commandKmmFrame = new KmmFrame(mrKmm);

                                SendKmm(commandKmmFrame.ToBytes());
                            }
                            else if (kfdKmmBody is ModifyKeyCommand)
                            {
                                ModifyKeyCommand cmdKmm = kfdKmmBody as ModifyKeyCommand;

                                Log.Debug("keyset id: {0} (dec), {0:X} (hex)", cmdKmm.KeysetId);
                                Log.Debug("algorithm id: {0} (dec), {0:X} (hex)", cmdKmm.AlgorithmId);

                                RekeyAcknowledgment rspKmm = new RekeyAcknowledgment();

                                rspKmm.MessageIdAcknowledged = MessageId.ModifyKeyCommand;
                                rspKmm.NumberOfItems = cmdKmm.KeyItems.Count;

                                for (int i = 0; i < cmdKmm.KeyItems.Count; i++)
                                {
                                    KeyItem item = cmdKmm.KeyItems[i];

                                    Log.Debug("* key item {0} *", i);
                                    Log.Debug("erase: {0}", item.Erase);
                                    Log.Debug("sln: {0} (dec), {0:X} (hex)", item.SLN);
                                    Log.Debug("key id: {0} (dec), {0:X} (hex)", item.KeyId);
                                    Log.Debug("key: {0}", BitConverter.ToString(item.Key));

                                    KeyStatus status = new KeyStatus();

                                    status.AlgorithmId = cmdKmm.AlgorithmId;
                                    status.KeyId = item.KeyId;
                                    status.Status = 0x00; // command was performed

                                    rspKmm.Keys.Add(status);
                                }

                                KmmFrame cmdKmmFrame = new KmmFrame(rspKmm);

                                SendKmm(cmdKmmFrame.ToBytes());
                                // Acknowledge on the wire before scheduling UI/import work.
                                foreach (KeyItem item in cmdKmm.KeyItems)
                                    if (!item.Erase)
                                    {
                                        Status += $"Key received: CKR {item.SLN} · KID {item.KeyId} · Keyset {cmdKmm.KeysetId} · {KfdMobile.KeyPresentation.Algorithm(cmdKmm.AlgorithmId)}" + Environment.NewLine;
                                        KeyReceived?.Invoke(new KfdMobile.ReceivedMrKey(item.SLN, item.KeyId, cmdKmm.KeysetId, cmdKmm.AlgorithmId, Convert.ToHexString(item.Key)));
                                    }
                            }
                            else
                            {
                                var rejection = new NegativeAcknowledgment {
                                    AcknowledgedMessageId = kfdKmmBody.MessageId,
                                    Status = OperationStatus.InvalidMessageId
                                };
                                SendKmm(new KmmFrame(rejection).ToBytes());
                                Status += "Unsupported key-management request rejected; still listening." + Environment.NewLine;
                            }
                        }
                        else if (rx == OPCODE_TRANSFER_DONE)
                        {
                            Log.Debug("in: got transfer done opcode");

                            /* TX: TRANSFER DONE */

                            Log.Debug("out: transfer done opcode");
                            Log.Trace("out: {0}", Utility.DataFormat(OPCODE_TRANSFER_DONE));
                            Protocol.SendByte(OPCODE_TRANSFER_DONE);

                            Status += "Batch completed; listening for the next algorithm or disconnect." + Environment.NewLine;
                            // Do not consume the next ready/KMM as an expected disconnect.
                            continue;
                        }
                        else if (rx == OPCODE_DISCONNECT)
                        {
                            /* TX: DISCONNECT ACKNOWLEDGE */

                            Log.Debug("out: disconnect acknowledge opcode");
                            Log.Trace("out: {0}", Utility.DataFormat(OPCODE_DISCONNECT_ACK));
                            Protocol.SendByte(OPCODE_DISCONNECT_ACK);
                            Status += "Transfer completed; listening for another transfer." + Environment.NewLine;
                            break;
                        }
                        else
                        {
                            string msg = string.Format("in: unexpected frame type opcode ({0})", Utility.DataFormat(rx));
                            Log.Warn(msg);
                            Status += "Transfer interrupted; waiting for the sending keyloader to retry." + Environment.NewLine;
                            break;
                        }
                    }
                    }
                    catch (TimeoutException)
                    {
                        Status += "Transfer interrupted; listening for another ready request." + Environment.NewLine;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log.Debug("operation cancelled");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("error in mr emulation: {0}", ex.Message);
                throw;
            }
        }
    }
}
