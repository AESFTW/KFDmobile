using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using System.Text;
using System.Text.Json;

namespace KfdMobile.AndroidApp;

// Only the encrypted connection profile is persisted. Container bytes stay in memory.
static class StorageCredentials
{
    const string Alias = "kfdmobile-storage-v1";
    static IKey Key()
    {
        using var store = KeyStore.GetInstance("AndroidKeyStore"); store.Load(null);
        if (store.ContainsAlias(Alias)) return store.GetKey(Alias, null);
        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore");
        using var spec = new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm).SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone).Build();
        generator.Init(spec); return generator.GenerateKey();
    }
    public static StorageProfile Load(Context context)
    {
        string saved = context.GetSharedPreferences("storage", FileCreationMode.Private).GetString("profile", null);
        if (saved == null) return new();
        byte[] packed = Convert.FromBase64String(saved);
        if (packed.Length < 28) throw new IOException("Saved storage settings are unreadable. Reconfigure Storage; no local fallback was selected.");
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding"); using var key = Key();
        using var spec = new GCMParameterSpec(128, packed[..12]); cipher.Init(CipherMode.DecryptMode, key, spec);
        byte[] plain = cipher.DoFinal(packed[12..]);
        try { return JsonSerializer.Deserialize<StorageProfile>(plain) ?? throw new IOException("Storage settings are empty."); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }
    public static void Save(Context context, StorageProfile profile)
    {
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding"); using var key = Key(); cipher.Init(CipherMode.EncryptMode, key);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(profile);
        try
        {
            byte[] encrypted = cipher.DoFinal(plain); byte[] iv = cipher.GetIV();
            if (iv.Length != 12) throw new IOException("Unexpected credential encryption nonce.");
            bool saved = context.GetSharedPreferences("storage", FileCreationMode.Private).Edit().PutString("profile", Convert.ToBase64String(iv.Concat(encrypted).ToArray())).Commit();
            if (!saved) throw new IOException("Cannot save storage settings.");
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }
}
