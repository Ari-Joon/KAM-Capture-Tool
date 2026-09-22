using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KamCapture.Services
{
    /// <summary>
    /// For the end-to-end update check only. When KAM_CAPTURE_SANDBOX names a
    /// folder, a copy started with it keeps its install, registry entries,
    /// settings, log and single-instance lock in there instead of the real
    /// ones — so a whole update, old copy to new, can run beside the real
    /// install without touching it or talking to it.
    /// </summary>
    internal static class Sandbox
    {
        public const string Variable = "KAM_CAPTURE_SANDBOX";

        public static readonly string? Root = Read();

        public static bool Active => Root != null;

        /// <summary>Short and stable for one folder, to keep names apart.</summary>
        public static readonly string Id = Root == null ? "" : Hash(Root);

        /// <summary>Appended to machine-wide names, so a sandboxed copy never meets the real one.</summary>
        public static readonly string Suffix = Root == null ? "" : "." + Id;

        private static string? Read()
        {
            var value = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(value)) return null;
            try { return Path.GetFullPath(value.Trim()); }
            catch { return null; }
        }

        private static string Hash(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant())))[..8];
    }
}
