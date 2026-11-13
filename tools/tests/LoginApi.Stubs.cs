// Standalone tests only. RSA uses .NET's actual PKCS#1 implementation. Storage
// and user protection are in-memory substitutes; they never access real cookies.
namespace Windows.Security.Cryptography.Core
{
    public static class AsymmetricAlgorithmNames { public const string RsaPkcs1 = "RSA_PKCS1"; }
    public enum CryptographicPublicKeyBlobType { X509SubjectPublicKeyInfo }
    public sealed class CryptographicKey { internal global::System.Security.Cryptography.RSA Rsa; }
    public sealed class AsymmetricKeyAlgorithmProvider
    {
        public static AsymmetricKeyAlgorithmProvider OpenAlgorithm(string algorithm)
        {
            if (algorithm != AsymmetricAlgorithmNames.RsaPkcs1) throw new Exception("Unexpected RSA algorithm");
            return new AsymmetricKeyAlgorithmProvider();
        }
        public CryptographicKey ImportPublicKey(byte[] bytes, CryptographicPublicKeyBlobType blobType)
        {
            var rsa = global::System.Security.Cryptography.RSA.Create();
            int read; rsa.ImportSubjectPublicKeyInfo(bytes, out read);
            if (read != bytes.Length) throw new Exception("Trailing RSA key data");
            return new CryptographicKey { Rsa = rsa };
        }
    }
    public static class CryptographicEngine
    {
        public static byte[] Encrypt(CryptographicKey key, byte[] bytes, object iv)
            => key.Rsa.Encrypt(bytes, global::System.Security.Cryptography.RSAEncryptionPadding.Pkcs1);
    }
}
namespace Windows.Security.Cryptography.DataProtection
{
    public sealed class DataProtectionProvider
    {
        private readonly string _descriptor;
        public DataProtectionProvider() { }
        public DataProtectionProvider(string descriptor) { _descriptor = descriptor; }
        public Task<byte[]> ProtectAsync(byte[] value)
        {
            if (_descriptor != "LOCAL=user") throw new Exception("Wrong user protection descriptor");
            if (Windows.Storage.LoginMemoryStorage.FailSave) throw new IOException("Fixture disk unavailable");
            Windows.Storage.LoginMemoryStorage.ProtectCalls++;
            var bytes = Encoding.UTF8.GetBytes("protected-test:").Concat(value).ToArray();
            return Task.FromResult(bytes);
        }
        public Task<byte[]> UnprotectAsync(byte[] value)
            => Task.FromResult(value.Skip(Encoding.UTF8.GetByteCount("protected-test:")).ToArray());
    }
}
namespace Windows.Storage
{
    public enum CreationCollisionOption { ReplaceExisting }
    public enum NameCollisionOption { FailIfExists }
    public enum StorageDeleteOption { PermanentDelete }
    public static class LoginMemoryStorage
    {
        public static readonly Dictionary<string, StorageFile> Files = new Dictionary<string, StorageFile>();
        public static bool FailSave;
        public static int ProtectCalls;
        public static void Reset() { Files.Clear(); FailSave = false; ProtectCalls = 0; }
    }
    public sealed class ApplicationData
    {
        public static ApplicationData Current { get; } = new ApplicationData();
        public StorageFolder LocalFolder { get; } = new StorageFolder();
    }
    public sealed class StorageFolder
    {
        public Task<object> TryGetItemAsync(string name)
        {
            StorageFile file; LoginMemoryStorage.Files.TryGetValue(name, out file);
            return Task.FromResult<object>(file);
        }
        public Task<StorageFile> CreateFileAsync(string name, CreationCollisionOption option)
        {
            var file = new StorageFile { Name = name };
            LoginMemoryStorage.Files[name] = file; return Task.FromResult(file);
        }
    }
    public sealed class StorageFile
    {
        internal string Name; internal byte[] Bytes;
        public Task DeleteAsync(StorageDeleteOption option) { LoginMemoryStorage.Files.Remove(Name); return Task.CompletedTask; }
        public Task MoveAndReplaceAsync(StorageFile file)
        {
            LoginMemoryStorage.Files.Remove(Name); Name = file.Name; LoginMemoryStorage.Files[Name] = this;
            return Task.CompletedTask;
        }
        public Task RenameAsync(string name, NameCollisionOption option)
        {
            if (LoginMemoryStorage.Files.ContainsKey(name)) throw new IOException("File already exists");
            LoginMemoryStorage.Files.Remove(Name); Name = name; LoginMemoryStorage.Files[name] = this;
            return Task.CompletedTask;
        }
    }
    public static class FileIO
    {
        public static Task<byte[]> ReadBufferAsync(StorageFile file) => Task.FromResult(file.Bytes);
        public static Task WriteBufferAsync(StorageFile file, byte[] value) { file.Bytes = value; return Task.CompletedTask; }
    }
}
