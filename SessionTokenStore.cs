using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HSHONAD_Launcher
{
    /// <summary>
    /// Stores the opaque launcher session token encrypted for the current Windows user.
    /// The token is never exposed to WebView localStorage.
    /// </summary>
    internal static class SessionTokenStore
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("HSHONAD.Launcher.Session.v1");

        private static string SessionPath
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HSHONAD Launcher");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "session.bin");
            }
        }

        public static void Save(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Session token is empty.", nameof(token));

            byte[] clearBytes = Encoding.UTF8.GetBytes(token);
            byte[] encrypted = ProtectedData.Protect(
                clearBytes, Entropy, DataProtectionScope.CurrentUser);

            string path = SessionPath;
            string tempPath = path + ".tmp";
            File.WriteAllBytes(tempPath, encrypted);
            File.Move(tempPath, path, true);
            CryptographicOperations.ZeroMemory(clearBytes);
        }

        public static string? Load()
        {
            string path = SessionPath;
            if (!File.Exists(path))
                return null;

            byte[]? clearBytes = null;
            try
            {
                byte[] encrypted = File.ReadAllBytes(path);
                clearBytes = ProtectedData.Unprotect(
                    encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clearBytes);
            }
            catch (CryptographicException)
            {
                Clear();
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            finally
            {
                if (clearBytes != null)
                    CryptographicOperations.ZeroMemory(clearBytes);
            }
        }

        public static void Clear()
        {
            try
            {
                string path = SessionPath;
                if (File.Exists(path))
                    File.Delete(path);
                string tempPath = path + ".tmp";
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (IOException)
            {
                // A later startup can retry cleanup.
            }
        }
    }
}
