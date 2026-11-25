using System;
using Microsoft.Win32;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>The current user's HKCU Run key behind <see cref="IRunKeyStore"/>. No admin, no other key, no other user.</summary>
    public sealed class RegistryRunKeyStore : IRunKeyStore
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public string? Get(string name)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(name) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return null; }
        }

        public void Set(string name, string value)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            key.SetValue(name, value, RegistryValueKind.String);
        }

        public void Delete(string name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key?.GetValue(name) != null) key.DeleteValue(name, false);
        }

        /// <summary>The sign-in registration used by Settings, with its restore journal inside PaRuXData.</summary>
        public static LogonRegistration CreateRegistration() =>
            new(new RegistryRunKeyStore(), PaRuXDataRoot.Combine("server", "logon-registration.json"));
    }
}
