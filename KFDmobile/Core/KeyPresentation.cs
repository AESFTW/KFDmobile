using System.Globalization;
using KFDtool.Container;
namespace KfdMobile;

public static class KeyPresentation
{
    public static string Kid(int value, bool hex) => hex ? $"0x{value:X4}" : value.ToString(CultureInfo.InvariantCulture);
    public static int ParseKid(string value, bool hex)
    {
        value = value.Trim();
        if (hex && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        if (!int.TryParse(value, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int kid) || kid < 0 || kid > 65535)
            throw new ArgumentException(hex ? "KID must be hexadecimal 0000-FFFF." : "KID must be decimal 0-65535.");
        return kid;
    }
    public static string Algorithm(int id) => id switch {
        0x84 => "AES-256", 0x81 => "DES-OFB", 0x9f => "DES-XL", 0xaa => "ADP / ARC4",
        0x85 => "AES-128", 0x83 => "Triple DES", 0x80 => "Clear", _ => $"Unknown (0x{id:X2})"
    };
    public static IEnumerable<KeyItem> Sort(IEnumerable<KeyItem> keys, int order) => order switch {
        1 => keys.OrderByDescending(k => k.KeyId).ThenBy(k => k.Id),
        2 => keys.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Id),
        3 => keys.OrderByDescending(k => k.Name, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Id),
        4 => keys.OrderBy(k => k.Sln).ThenBy(k => k.KeyId).ThenBy(k => k.Id),
        5 => keys.OrderByDescending(k => k.Sln).ThenBy(k => k.KeyId).ThenBy(k => k.Id),
        6 => keys.OrderBy(k => Algorithm(k.AlgorithmId), StringComparer.OrdinalIgnoreCase).ThenBy(k => k.KeyId).ThenBy(k => k.Id),
        7 => keys.OrderByDescending(k => Algorithm(k.AlgorithmId), StringComparer.OrdinalIgnoreCase).ThenBy(k => k.KeyId).ThenBy(k => k.Id),
        _ => keys.OrderBy(k => k.KeyId).ThenBy(k => k.Id)
    };
}

// Bulk approval for new entries never silently authorizes replacement of existing keys.
public sealed class MrImportPolicy
{
    bool? add, replace;
    public bool? Choice(bool replacement) => replacement ? replace : add;
    public void Apply(bool replacement, bool import) { if (replacement) replace = import; else add = import; }
    public void Reset() { add = replace = null; }
}
