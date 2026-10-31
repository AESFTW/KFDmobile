using KFDtool.Container;
using KFDtool.P25.TransferConstructs;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace KfdMobile;

public sealed class ContainerSession : IDisposable
{
    public InnerContainer Inner { get; private set; }
    OuterContainer outer;
    byte[] key;
    public bool Dirty { get; set; }
    public bool IsOpen => Inner != null;
    public void Create(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Enter a container password.");
        var created = ContainerUtilities.CreateOuterContainer(password);
        Dispose(); outer = created.Container; key = created.Key; Inner = ContainerUtilities.CreateInnerContainer(); Dirty = true;
    }
    public void Open(byte[] file, string password)
    {
        if (file.Length < 5 || file.Length > 8 * 1024 * 1024) throw new IOException("Invalid container size.");
        int expanded = BitConverter.ToInt32(file, 0);
        if (expanded < 1 || expanded > 16 * 1024 * 1024) throw new IOException("Invalid expanded container size.");
        var opened = ContainerUtilities.DecryptOuterContainer(file, password);
        if (opened.ContainerInner.Keys == null || opened.ContainerInner.Groups == null) throw new IOException("Invalid container content.");
        var inner = opened.ContainerInner;
        if (inner.Keys.Any(k => k == null || k.Id < 0) || inner.Keys.Select(k => k.Id).Distinct().Count() != inner.Keys.Count || inner.Groups.Any(g => g == null || g.Id < 0 || g.Keys == null) || inner.Groups.Select(g => g.Id).Distinct().Count() != inner.Groups.Count) throw new IOException("Container has invalid or duplicate record IDs.");
        if (inner.Groups.Any(g => g.Keys.Any(id => !inner.Keys.Any(k => k.Id == id)))) throw new IOException("Container group refers to a missing key.");
        inner.NextKeyNumber = checked(Math.Max(inner.NextKeyNumber, inner.Keys.Select(k => k.Id).DefaultIfEmpty(0).Max() + 1));
        inner.NextGroupNumber = checked(Math.Max(inner.NextGroupNumber, inner.Groups.Select(g => g.Id).DefaultIfEmpty(0).Max() + 1));
        Dispose(); outer = opened.ContainerOuter; key = opened.Key; Inner = opened.ContainerInner; Dirty = false;
    }
    public byte[] Save() => ContainerUtilities.EncryptOuterContainer(outer, Inner, key);
    public static bool DkfSupported(KeyItem item) => (item.AlgorithmId == 0x84 || item.AlgorithmId == 0x81) && !item.KeyTypeKek && !(item.KeyTypeAuto && item.Sln >= 61440);
    public byte[] ExportDkf(string password)
    {
        if (!IsOpen) throw new InvalidOperationException("Open a container first.");
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Enter a DKF export password.");
        var export = ContainerUtilities.CreateInnerContainer();
        export.Keys = new System.Collections.ObjectModel.ObservableCollection<KeyItem>(Inner.Keys.Where(DkfSupported));
        if (export.Keys.Count == 0) throw new InvalidOperationException("DKF export requires at least one AES-256 or DES-OFB TEK.");
        foreach (var item in export.Keys) Validate(item);
        return ContainerUtilities.EncryptOuterContainerDKF(outer, export, password);
    }
    public KeyItem ImportReceived(ReceivedMrKey received, int? replaceId = null)
    {
        if (!IsOpen) throw new InvalidOperationException("Open a container first.");
        var original = replaceId.HasValue ? Inner.Keys.Single(k => k.Id == replaceId.Value) : null;
        var item = new KeyItem {
            Id = original?.Id ?? Inner.NextKeyNumber,
            Name = original?.Name ?? $"MR SLN {received.Sln} KID {received.KeyId}",
            Sln = received.Sln, KeyId = received.KeyId, AlgorithmId = received.AlgorithmId,
            KeysetId = received.KeysetId, ActiveKeyset = received.KeysetId == 0,
            KeyTypeAuto = true, Key = received.KeyHex
        };
        Validate(item);
        if (original == null) { Inner.Keys.Add(item); Inner.NextKeyNumber++; }
        else { Inner.Keys[Inner.Keys.IndexOf(original)] = item; original.Key = ""; }
        Dirty = true;
        return item;
    }
    public void ChangePassword(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Enter a container password.");
        var created = ContainerUtilities.CreateOuterContainer(password);
        if (key != null) CryptographicOperations.ZeroMemory(key);
        outer = created.Container; key = created.Key; Dirty = true;
    }
    public static CmdKeyItem ToCommand(KeyItem item)
    {
        Validate(item);
        return new CmdKeyItem(item.ActiveKeyset, item.KeysetId, item.Sln,
            item.KeyTypeAuto ? item.Sln >= 61440 : item.KeyTypeKek,
            item.KeyId, item.AlgorithmId, Convert.FromHexString(item.Key).ToList());
    }
    public static void Validate(KeyItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Name)) throw new ArgumentException("A key name is required.");
        if (item.KeyId < 0 || item.KeyId > 65535 || item.Sln < 0 || item.Sln > 65535 || item.KeysetId < 0 || item.KeysetId > 255) throw new ArgumentException("KID and SLN must be 0–65535; keyset must be 0–255.");
        int length = KeyEntry.HexLength(item.AlgorithmId);
        if (item.Key == null || item.Key.Length != length || !Regex.IsMatch(item.Key, "\\A[0-9a-fA-F]+\\z")) throw new ArgumentException($"This algorithm requires {length} hexadecimal characters.");
        if (new[] { item.KeyTypeAuto, item.KeyTypeTek, item.KeyTypeKek }.Count(x => x) != 1) throw new ArgumentException("Select exactly one key type.");
        var validation = KFDtool.P25.Validator.FieldValidator.KeyloadValidate(item.ActiveKeyset ? 1 : item.KeysetId, item.Sln, item.KeyTypeAuto ? item.Sln >= 61440 : item.KeyTypeKek, item.KeyId, item.AlgorithmId, Convert.FromHexString(item.Key).ToList());
        if (validation.Item1 == KFDtool.Shared.ValidateResult.Error) throw new ArgumentException(validation.Item2);
    }
    public void Dispose()
    {
        if (key != null) CryptographicOperations.ZeroMemory(key);
        if (Inner != null) foreach (var item in Inner.Keys) item.Key = "";
        Inner = null; outer = null; key = null; Dirty = false;
    }
}
