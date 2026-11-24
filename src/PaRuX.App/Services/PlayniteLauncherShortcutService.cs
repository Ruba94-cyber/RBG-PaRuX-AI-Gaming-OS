using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>One line of the outcome the launcher shows after the owner confirmed.</summary>
    public sealed record PlayniteShortcutOutcome(string Path, string Message, bool Ok);

    /// <summary>
    /// Writes and removes the optional Console Mode shortcuts that the owner confirmed. The plan comes from
    /// <see cref="PlayniteLauncherShortcuts"/> (pure, tested); this class only performs it. It never overwrites an existing file, never
    /// writes outside the desktop folder the caller names, and removal only deletes files that
    /// <see cref="PlayniteLauncherShortcuts.PlanRemove"/> proved to be PaRuX's own launcher shortcuts.
    /// </summary>
    public static class PlayniteLauncherShortcutService
    {
        public static string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        /// <summary>The PaRuX.exe a shortcut can point at, or null with an English reason.</summary>
        public static string? ExecutableOrReason(out string? reason)
        {
            string? exe = PaRuX.Contracts.PaRuXDataRoot.LaunchTargetFor(Environment.ProcessPath);
            reason = GameShortcuts.ExecutableProblem(exe);
            return reason is null ? Path.GetFullPath(exe!) : null;
        }

        public static IReadOnlyList<PlannedShortcut> Plan(string folder, string exe, IEnumerable<PlayniteLauncherAction> wanted) =>
            PlayniteLauncherShortcuts.PlanCreate(folder, exe, wanted, p => File.Exists(p) || Directory.Exists(p), p => WindowsShortcutFile.TryRead(p));

        public static IReadOnlyList<string> PlanRemoval(string folder, string exe) =>
            PlayniteLauncherShortcuts.PlanRemove(folder, exe, EnumerateShortcuts, p => WindowsShortcutFile.TryRead(p));

        private static IEnumerable<string> EnumerateShortcuts(string folder)
        {
            try { return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.lnk").Take(500).ToArray() : Array.Empty<string>(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        public static IReadOnlyList<PlayniteShortcutOutcome> Create(IEnumerable<PlannedShortcut> plan, string exe)
        {
            var results = new List<PlayniteShortcutOutcome>();
            foreach (PlannedShortcut item in plan)
            {
                if (item.State == PlannedShortcutState.AlreadyExists) { results.Add(new(item.Path, "already there", true)); continue; }
                try
                {
                    if (File.Exists(item.Path) || Directory.Exists(item.Path)) { results.Add(new(item.Path, "skipped: a file with that name appeared", false)); continue; }
                    WindowsShortcutFile.Create(item.Path, new ShortcutTarget(exe, item.Arguments, Path.GetDirectoryName(exe) ?? string.Empty, item.Description, exe, 0));
                    results.Add(new(item.Path, "created", true));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
                {
                    results.Add(new(item.Path, "not created: " + ex.Message, false));
                }
            }
            return results;
        }

        public static IReadOnlyList<PlayniteShortcutOutcome> Remove(IEnumerable<string> proven)
        {
            var results = new List<PlayniteShortcutOutcome>();
            foreach (string path in proven)
            {
                try { File.Delete(path); results.Add(new(path, "removed", true)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { results.Add(new(path, "not removed: " + ex.Message, false)); }
            }
            return results;
        }
    }

    /// <summary>
    /// Jump List tasks (the right-click menu of PaRuX's taskbar icon): Console, Desktop, Streaming, Settings. Written only when the
    /// owner confirmed; "Remove" deletes the whole custom list again. Windows does not allow an app to pin itself to the taskbar, so
    /// this is the supported way to reach the four actions from the taskbar. NOT exercised without a real shell (see the report).
    /// </summary>
    public static class PlayniteJumpList
    {
        public static string? TryApply(string exe, IReadOnlyList<(PlayniteLauncherAction Action, string Name, string Description)> specs)
        {
            ICustomDestinationList? list = null;
            IObjectCollection? tasks = null;
            try
            {
                list = (ICustomDestinationList)new CDestinationList();
                Marshal.ThrowExceptionForHR(list.BeginList(out uint _, ref IidIObjectArray, out object _));
                tasks = (IObjectCollection)new CEnumerableObjectCollection();
                foreach (var spec in specs)
                {
                    var link = (IShellLinkW)new CShellLink();
                    link.SetPath(exe);
                    link.SetArguments(PlayniteLauncherArguments.ArgumentsFor(spec.Action));
                    link.SetIconLocation(exe, 0);
                    link.SetDescription(spec.Description);
                    var store = (IPropertyStore)link;
                    var key = new PropertyKey(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);   // PKEY_Title
                    using var title = PropVariant.FromString(spec.Name);
                    store.SetValue(ref key, ref title.Ref);
                    store.Commit();
                    tasks.AddObject(link);
                }
                Marshal.ThrowExceptionForHR(list.AddUserTasks((IObjectArray)tasks));
                list.CommitList();
                return null;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
            {
                return "The taskbar menu could not be written: " + ex.Message;
            }
            finally
            {
                if (tasks != null) Marshal.FinalReleaseComObject(tasks);
                if (list != null) Marshal.FinalReleaseComObject(list);
            }
        }

        public static string? TryRemove()
        {
            ICustomDestinationList? list = null;
            try
            {
                list = (ICustomDestinationList)new CDestinationList();
                list.DeleteList(null);
                return null;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                return "The taskbar menu could not be cleared: " + ex.Message;
            }
            finally { if (list != null) Marshal.FinalReleaseComObject(list); }
        }

        private static Guid IidIObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");

        [ComImport, Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")] private class CDestinationList { }
        [ComImport, Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")] private class CEnumerableObjectCollection { }
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
        private interface ICustomDestinationList
        {
            void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
            [PreserveSig] int BeginList(out uint pcMaxSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
            [PreserveSig] int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);
            void AppendKnownCategory(int category);
            [PreserveSig] int AddUserTasks(IObjectArray poa);
            void CommitList();
            void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
            void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? pszAppID);
            void AbortList();
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
        private interface IObjectArray
        {
            void GetCount(out uint pcObjects);
            void GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
        private interface IObjectCollection : IObjectArray
        {
            new void GetCount(out uint pcObjects);
            new void GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
            void AddObject([MarshalAs(UnmanagedType.Interface)] object punk);
            void AddFromArray(IObjectArray poaSource);
            void RemoveObjectAt(uint uiIndex);
            void Clear();
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PropertyKey pkey);
            void GetValue(ref PropertyKey key, out PropVariantNative pv);
            void SetValue(ref PropertyKey key, ref PropVariantNative pv);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public uint PropertyId;
            public PropertyKey(Guid formatId, uint propertyId) { FormatId = formatId; PropertyId = propertyId; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariantNative
        {
            public ushort Vt;
            public ushort Reserved1, Reserved2, Reserved3;
            public IntPtr Pointer;
            public IntPtr Extra;
        }

        /// <summary>A VT_LPWSTR PROPVARIANT whose string is freed when disposed.</summary>
        private sealed class PropVariant : IDisposable
        {
            private PropVariantNative _value;
            private PropVariant() { }
            public ref PropVariantNative Ref => ref _value;
            public static PropVariant FromString(string text)
            {
                var pv = new PropVariant();
                pv._value.Vt = 31;   // VT_LPWSTR
                pv._value.Pointer = Marshal.StringToCoTaskMemUni(text);
                return pv;
            }
            public void Dispose()
            {
                if (_value.Pointer != IntPtr.Zero) { Marshal.FreeCoTaskMem(_value.Pointer); _value.Pointer = IntPtr.Zero; }
            }
        }
    }
}
