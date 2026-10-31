using System.Globalization;
using System.Text.RegularExpressions;
namespace KfdMobile;
public static class BackupNaming
{
    public static string Name(string containerName, DateTimeOffset savedAt)
    {
        string name=Regex.Replace(containerName ?? "container", "[\\x00-\\x1f<>:\"/\\\\|?*]", "_").Trim().TrimEnd('.');
        if(name.EndsWith(".ekc",StringComparison.OrdinalIgnoreCase))name=name[..^4];
        if(string.IsNullOrWhiteSpace(name))name="container";
        if(name.Length>80)name=name[..80];
        return name+"_"+savedAt.ToString("yyyy-MM-dd_HH-mm-ss-fff",CultureInfo.InvariantCulture)+".ekc";
    }
}
