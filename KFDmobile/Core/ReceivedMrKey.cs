namespace KfdMobile;

public sealed record ReceivedMrKey(int Sln, int KeyId, int KeysetId, int AlgorithmId, string KeyHex)
{
    public string FileName => $"SLN_{Sln}_KeyID_{KeyId}.txt";
    public string ToText() => $"SLN: {Sln}\r\nKey ID: {KeyId}\r\nKeyset ID: {KeysetId}\r\nAlgorithm ID: 0x{AlgorithmId:X2}\r\nKey data: {KeyHex}\r\n";
}
