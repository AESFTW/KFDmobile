namespace KfdMobile;

public static class KeyEntry
{
    public static int HexLength(int algorithm) => algorithm switch
    {
        0x84 => 64, 0x81 or 0x9f => 16, 0xaa => 10,
        _ => throw new ArgumentException("Supported algorithms: AES-256, DES-OFB, DES-XL, ARC4.")
    };

    public static bool AcceptsReplacement(int algorithm, string current, int start, int end, string inserted)
    {
        if (start < 0 || end < start || end > current.Length) return false;
        return current.Length - (end - start) + inserted.Length <= HexLength(algorithm)
            && inserted.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    }
}
