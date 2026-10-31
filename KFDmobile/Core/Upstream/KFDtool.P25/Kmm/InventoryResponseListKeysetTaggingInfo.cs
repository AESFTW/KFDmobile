using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KFDtool.P25.Kmm
{
    public class InventoryResponseListKeysetTaggingInfo : KmmBody
    {
        public List<KeysetItem> KeysetItems { get; private set; }

        public override MessageId MessageId
        {
            get
            {
                return MessageId.InventoryResponse;
            }
        }

        public InventoryType InventoryType
        {
            get
            {
                return InventoryType.ListKeysetTaggingInfo;
            }
        }

        public override ResponseKind ResponseKind
        {
            get
            {
                return ResponseKind.None;
            }
        }

        public InventoryResponseListKeysetTaggingInfo()
        {
            KeysetItems = new List<KeysetItem>();
        }

        public override byte[] ToBytes()
        {
            throw new NotImplementedException();
        }

        public override void Parse(byte[] contents)
        {
            if (contents == null || contents.Length < 3)
                throw new FormatException("Keyset response header is truncated.");
            if (contents[0] != (byte)InventoryType)
                throw new FormatException("Keyset inventory type mismatch.");
            int count = (contents[1] << 8) | contents[2];
            int pos = 3;
            var items = new List<KeysetItem>();
            void Require(int size)
            {
                if (size > contents.Length - pos)
                    throw new FormatException("Keyset response item is truncated.");
            }
            for (int i = 0; i < count; i++)
            {
                Require(3);
                int format = contents[pos++];
                var item = new KeysetItem { KeysetType = (format & 0x80) != 0 ? "KEK" : "TEK", KeysetId = contents[pos++] };
                pos++; // Deprecated reserved octet.
                if ((format & 0x40) != 0)
                {
                    Require(3);
                    item.ReservedField = (contents[pos] << 16) | (contents[pos + 1] << 8) | contents[pos + 2];
                    pos += 3;
                }
                if ((format & 0x20) != 0)
                {
                    Require(5);
                    if (contents.Skip(pos).Take(5).Any(b => b != 0))
                    {
                        int month = contents[pos] >> 4;
                        int day = ((contents[pos] & 15) << 1) | (contents[pos + 1] >> 7);
                        int year = 2000 + (contents[pos + 1] & 127);
                        int hour = contents[pos + 2] >> 3;
                        int minute = ((contents[pos + 2] & 7) << 3) | (contents[pos + 3] >> 5);
                        int second = ((contents[pos + 3] & 31) << 1) | (contents[pos + 4] >> 7);
                        try { item.ActivationDateTime = new DateTime(year, month, day, hour, minute, second); }
                        catch (ArgumentOutOfRangeException ex) { throw new FormatException("Keyset activation date is invalid.", ex); }
                    }
                    pos += 5;
                }
                int nameLength = format & 15;
                Require(nameLength);
                item.KeysetName = Encoding.ASCII.GetString(contents, pos, nameLength);
                pos += nameLength;
                items.Add(item);
            }
            if (pos != contents.Length) throw new FormatException("Keyset response has unexpected trailing data.");
            KeysetItems = items;
        }
    }
}
