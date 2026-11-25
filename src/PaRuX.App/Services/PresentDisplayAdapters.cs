using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaRuX.Services
{
    /// <summary>
    /// Read-only list of the display adapters that are present right now (SetupAPI, present devices only), for the optional "required
    /// graphics card" start rule. An adapter that is unplugged is not listed. Changes nothing and starts no process.
    /// </summary>
    public static class PresentDisplayAdapters
    {
        private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
        private const uint DigcfPresent = 0x2;
        private const uint SpdrpDeviceDesc = 0x0;

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevInfoData { public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevInfoData data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SpDevInfoData data, uint property, out uint type, byte[]? buffer, uint size, out uint required);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        public static IReadOnlyList<string> Read()
        {
            var names = new List<string>();
            try
            {
                Guid cls = DisplayClass;
                IntPtr set = SetupDiGetClassDevsW(ref cls, null, IntPtr.Zero, DigcfPresent);
                if (set == IntPtr.Zero || set == new IntPtr(-1)) return names;
                try
                {
                    for (uint i = 0; i < 64; i++)
                    {
                        var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                        if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;
                        var buffer = new byte[1024];
                        if (SetupDiGetDeviceRegistryPropertyW(set, ref data, SpdrpDeviceDesc, out _, buffer, (uint)buffer.Length, out _))
                        {
                            string text = System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
                            if (text.Length > 0) names.Add(text);
                        }
                    }
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            return names;
        }
    }
}
