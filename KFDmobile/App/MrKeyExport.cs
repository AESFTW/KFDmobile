using Android.Content;
using Android.OS;
using Android.Provider;
using System.Text;

namespace KfdMobile.AndroidApp;

static class MrKeyExport
{
    static readonly object gate = new();
    public static string Save(Context context, ReceivedMrKey key)
    {
        lock (gate)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                var resolver = context.ContentResolver;
                using var values = new ContentValues();
                values.Put("_display_name", key.FileName);
                values.Put("mime_type", "text/plain");
                values.Put("relative_path", "Download/");
                values.Put("is_pending", 1);
                var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values)
                    ?? throw new IOException("Cannot create Downloads file.");
                try
                {
                    using (var stream = resolver.OpenOutputStream(uri, "w") ?? throw new IOException("Cannot open Downloads file."))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(key.ToText());
                    using var published = new ContentValues(); published.Put("is_pending", 0);
                    if (resolver.Update(uri, published, null, null) != 1) throw new IOException("Cannot publish Downloads file.");
                    using var cursor = resolver.Query(uri, new[] { "_display_name" }, null, null, null);
                    return cursor != null && cursor.MoveToFirst() ? cursor.GetString(0) : key.FileName;
                }
                catch { resolver.Delete(uri, null, null); throw; }
            }
            var folder = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads).AbsolutePath;
            Directory.CreateDirectory(folder);
            string name = key.FileName;
            for (int suffix = 1; File.Exists(Path.Combine(folder, name)); suffix++)
                name = $"{Path.GetFileNameWithoutExtension(key.FileName)} ({suffix}).txt";
            string path = Path.Combine(folder, name);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(key.ToText());
            }
            catch { if (File.Exists(path)) File.Delete(path); throw; }
            Android.Media.MediaScannerConnection.ScanFile(context, new[] { path }, new[] { "text/plain" }, null);
            return name;
        }
    }
}
