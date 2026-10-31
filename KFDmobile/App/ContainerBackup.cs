using Android.Content;
using Android.OS;
using Android.Provider;
namespace KfdMobile.AndroidApp;
static class ContainerBackup
{
    public static string Save(Context context, string name, byte[] encrypted)
    {
        if(Build.VERSION.SdkInt>=BuildVersionCodes.Q)
        {
            var resolver=context.ContentResolver;using var values=new ContentValues();
            values.Put("_display_name",name);values.Put("mime_type","application/octet-stream");values.Put("relative_path","Download/KFDmobile/Backups/");values.Put("is_pending",1);
            var uri=resolver.Insert(MediaStore.Downloads.ExternalContentUri,values)??throw new IOException("Cannot create encrypted backup in Downloads.");
            try {
                using(var output=resolver.OpenOutputStream(uri,"w")??throw new IOException("Cannot write encrypted backup.")){output.Write(encrypted);output.Flush();}
                using(var input=resolver.OpenInputStream(uri)??throw new IOException("Cannot verify encrypted backup."))
                    if(!System.Security.Cryptography.SHA256.HashData(input).SequenceEqual(System.Security.Cryptography.SHA256.HashData(encrypted)))throw new IOException("Encrypted backup verification failed.");
                using var published=new ContentValues();published.Put("is_pending",0);if(resolver.Update(uri,published,null,null)!=1)throw new IOException("Cannot publish encrypted backup.");
                using var cursor=resolver.Query(uri,new[]{"_display_name"},null,null,null);return cursor!=null && cursor.MoveToFirst()?cursor.GetString(0):name;
            }catch{resolver.Delete(uri,null,null);throw;}
        }
        var folder=Path.Combine(Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads).AbsolutePath,"KFDmobile","Backups");Directory.CreateDirectory(folder);
        string candidate=name;for(int n=1;File.Exists(Path.Combine(folder,candidate));n++)candidate=Path.GetFileNameWithoutExtension(name)+$"_{n}.ekc";
        string path=Path.Combine(folder,candidate);
        try{
            using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write)){output.Write(encrypted);output.Flush(true);}
            using(var input=File.OpenRead(path))if(!System.Security.Cryptography.SHA256.HashData(input).SequenceEqual(System.Security.Cryptography.SHA256.HashData(encrypted)))throw new IOException("Encrypted backup verification failed.");
        }catch{if(File.Exists(path))File.Delete(path);throw;}
        Android.Media.MediaScannerConnection.ScanFile(context,new[]{path},new[]{"application/octet-stream"},null);return candidate;
    }
}
