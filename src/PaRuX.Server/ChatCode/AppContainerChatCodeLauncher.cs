using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace PaRuX.Server.ChatCode;

/// <summary>
/// OS-enforced sandbox for chat code execution on Windows. The interpreter runs as a Windows AppContainer identity that
/// is created WITHOUT any capability: no network (not even loopback), and no access to anything the identity was not
/// explicitly granted (the user's files stay unreachable). Inside a per-run Job Object it is also limited in committed
/// memory, CPU time and number of processes (so a fork bomb cannot start a second process), has no UI/clipboard/global-atom
/// access, and the whole job is killed on timeout, output/disk overflow, cancellation, Gaming start, or when PaRuX exits.
/// The identity is only ever granted: read on the interpreter's own folder, modify on PaRuX's private run root, and
/// traverse (not read) on the parent folders of both. If any step fails the code is NOT run - there is no fallback.
/// </summary>
internal sealed class AppContainerChatCodeLauncher : IChatCodeProcessLauncher
{
    internal const string ProfileName = "PaRuX.ChatCode.Python";

    private static readonly object AccessGate = new();
    private static readonly HashSet<string> GrantedFolders = new(StringComparer.OrdinalIgnoreCase);
    private static int _desktopAccessGranted;

    public async Task<ChatCodeProcessResult> RunAsync(ChatCodeLaunch launch, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Failed("Code execution needs the Windows AppContainer sandbox, which is not available on this system.");

        // Gaming hard-off: refused (typed) while Gaming is active; a Gaming start cancels the token linked below, which kills the job.
        GamingChildScope? gamingScope = GamingChildGate.Ambient;
        long gamingTicket = gamingScope?.Capture() ?? 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, gamingScope?.Token ?? CancellationToken.None);

        IntPtr appContainerSid = IntPtr.Zero, attributes = IntPtr.Zero, capabilitiesPtr = IntPtr.Zero, handleList = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero, job = IntPtr.Zero, outHandle = IntPtr.Zero, nulHandle = IntPtr.Zero;
        PROCESS_INFORMATION process = default;
        bool resumed = false;
        string outputPath = Path.Combine(launch.WorkDirectory, "..", "output.log");
        outputPath = Path.GetFullPath(outputPath);

        try
        {
            int hr = CreateAppContainerProfile(ProfileName, "PaRuX Chat Code", "PaRuX isolated chat code execution",
                IntPtr.Zero, 0, out _);
            if (hr < 0 && hr != unchecked((int)0x800700B7)) // ERROR_ALREADY_EXISTS
                return Failed("Windows refused to create the sandbox identity (0x" + hr.ToString("X8") + ").");
            hr = DeriveAppContainerSidFromAppContainerName(ProfileName, out appContainerSid);
            if (hr < 0 || appContainerSid == IntPtr.Zero)
                return Failed("Windows could not resolve the sandbox identity (0x" + hr.ToString("X8") + ").");
            var sid = new SecurityIdentifier(appContainerSid);

            string? accessError = EnsureAccess(sid, launch);
            if (accessError != null) return Failed(accessError);
            EnsureDesktopAccess();

            // The output file is opened HERE (the parent) and handed over as the child's stdout/stderr, so the sandbox identity
            // never needs - and never gets - write access to anything but the run folder.
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var inheritable = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
            outHandle = CreateFileW(outputPath, 0x40000000 /*GENERIC_WRITE*/ | 0x80000000 /*GENERIC_READ*/, 3, ref inheritable, 2 /*CREATE_ALWAYS*/, 0x80, IntPtr.Zero);
            nulHandle = CreateFileW("NUL", 0x80000000, 3, ref inheritable, 3 /*OPEN_EXISTING*/, 0x80, IntPtr.Zero);
            if (outHandle == new IntPtr(-1) || nulHandle == new IntPtr(-1))
                return Failed("The run output could not be opened.");

            job = CreateJob(launch.Limits);
            if (job == IntPtr.Zero) return Failed("The Job Object limits could not be applied.");

            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size))
                return Failed("Process attributes could not be initialised.");

            capabilitiesPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
            Marshal.StructureToPtr(new SECURITY_CAPABILITIES { AppContainerSid = appContainerSid, Capabilities = IntPtr.Zero, CapabilityCount = 0 },
                capabilitiesPtr, false);
            if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20009), capabilitiesPtr,
                    new IntPtr(Marshal.SizeOf<SECURITY_CAPABILITIES>()), IntPtr.Zero, IntPtr.Zero))
                return Failed("The sandbox identity could not be applied to the process.");

            handleList = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(handleList, 0, nulHandle);
            Marshal.WriteIntPtr(handleList, IntPtr.Size, outHandle);
            if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), handleList, new IntPtr(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero))
                return Failed("The inherited handle list could not be restricted.");

            var startup = new STARTUPINFOEX();
            startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            startup.StartupInfo.dwFlags = 0x100 | 0x1; // USESTDHANDLES | USESHOWWINDOW
            startup.StartupInfo.wShowWindow = 0;
            startup.StartupInfo.hStdInput = nulHandle;
            startup.StartupInfo.hStdOutput = outHandle;
            startup.StartupInfo.hStdError = outHandle;
            startup.lpAttributeList = attributes;

            environment = BuildEnvironment(launch.Environment);
            var commandLine = new StringBuilder(Quote(launch.Executable));
            foreach (string argument in launch.Arguments) commandLine.Append(' ').Append(Quote(argument));

            const uint ExtendedStartupInfoPresent = 0x80000, CreateUnicodeEnvironment = 0x400, CreateSuspended = 0x4, CreateNoWindow = 0x08000000;
            // The one place the interpreter is started (listed in the Gaming hard-off launch inventory with this method's gate).
            bool CreateProcessW(string app, StringBuilder line, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFOEX info, out PROCESS_INFORMATION created) =>
                NativeCreateProcess(app, line, IntPtr.Zero, IntPtr.Zero, inherit, flags, env, cwd, ref info, out created);
            if (!CreateProcessW(launch.Executable, commandLine, true,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended | CreateNoWindow,
                    environment, launch.WorkDirectory, ref startup, out process))
                return Failed("The interpreter could not be started inside the sandbox: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);

            if (!AssignProcessToJobObject(job, process.hProcess))
                return Failed("The interpreter could not be attached to its Job Object: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if (ResumeThread(process.hThread) == uint.MaxValue)
                return Failed("The interpreter could not be resumed.");
            resumed = true;

            // Wait: exit, wall-clock timeout, output/disk overflow, cancel (caller or Gaming).
            DateTime deadline = DateTime.UtcNow.AddSeconds(launch.Limits.TimeoutSeconds);
            DateTime nextDiskCheck = DateTime.UtcNow;
            bool timedOut = false, limitHit = false;
            string? limitReason = null;
            while (true)
            {
                if (linked.IsCancellationRequested)
                {
                    TerminateJobObject(job, 1223);
                    WaitForSingleObject(process.hProcess, 3000);
                    gamingScope?.Check(gamingTicket);
                    ct.ThrowIfCancellationRequested();
                    if (gamingScope != null) throw gamingScope.Refusal();
                    throw new OperationCanceledException(linked.Token);
                }
                if (WaitForSingleObject(process.hProcess, 20) == 0) break;
                if (DateTime.UtcNow >= deadline) { timedOut = true; break; }
                if (SafeLength(outputPath) > launch.Limits.MaxOutputBytes)
                {
                    limitHit = true; limitReason = "the output passed " + (launch.Limits.MaxOutputBytes >> 10) + " KiB"; break;
                }
                if (DateTime.UtcNow >= nextDiskCheck)
                {
                    nextDiskCheck = DateTime.UtcNow.AddMilliseconds(150);
                    if (FolderBytes(launch.WorkDirectory, launch.Limits.MaxWorkFolderBytes) > launch.Limits.MaxWorkFolderBytes)
                    {
                        limitHit = true; limitReason = "the files it wrote passed " + (launch.Limits.MaxWorkFolderBytes >> 20) + " MiB"; break;
                    }
                }
                await Task.Yield();
            }

            int? exitCode = null;
            if (timedOut || limitHit)
            {
                TerminateJobObject(job, 1460);
                WaitForSingleObject(process.hProcess, 5000);
            }
            else if (GetExitCodeProcess(process.hProcess, out uint code)) exitCode = unchecked((int)code);
            // Anything the program left behind (it cannot create a second process, but be sure) dies with the job.
            TerminateJobObject(job, 0);
            CloseHandle(outHandle); outHandle = IntPtr.Zero;

            (string text, bool truncated) = ReadOutput(outputPath, launch.Limits.MaxOutputBytes);
            return new ChatCodeProcessResult(exitCode, text, truncated || limitHit, timedOut, limitHit, limitReason, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnavailableDuringGamingException) { throw; }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return Failed(ex.Message);
        }
        finally
        {
            if (!resumed && process.hProcess != IntPtr.Zero) TerminateProcess(process.hProcess, 1);
            if (job != IntPtr.Zero) { TerminateJobObject(job, 0); CloseHandle(job); }
            if (process.hThread != IntPtr.Zero) CloseHandle(process.hThread);
            if (process.hProcess != IntPtr.Zero) CloseHandle(process.hProcess);
            if (outHandle != IntPtr.Zero && outHandle != new IntPtr(-1)) CloseHandle(outHandle);
            if (nulHandle != IntPtr.Zero && nulHandle != new IntPtr(-1)) CloseHandle(nulHandle);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (capabilitiesPtr != IntPtr.Zero) Marshal.FreeHGlobal(capabilitiesPtr);
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (appContainerSid != IntPtr.Zero) FreeSid(appContainerSid);
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static ChatCodeProcessResult Failed(string reason) => new(null, string.Empty, false, false, false, null, reason);

    // ------------------------------------------------------------------ access grants

    /// <summary>Gives the sandbox identity exactly: read on the interpreter folder, modify on the private run root, traverse on their parents.</summary>
    private static string? EnsureAccess(SecurityIdentifier sid, ChatCodeLaunch launch)
    {
        try
        {
            lock (AccessGate)
            {
                Directory.CreateDirectory(launch.WritableRoot);
                foreach (string read in launch.ReadOnlyRoots) GrantTree(read, sid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
                GrantTree(launch.WritableRoot, sid, FileSystemRights.Modify | FileSystemRights.Synchronize);
                foreach (string folder in launch.ReadOnlyRoots.Append(launch.WritableRoot))
                    for (DirectoryInfo? parent = Directory.GetParent(folder); parent?.Parent != null; parent = parent.Parent)
                        GrantTraverse(parent.FullName, sid);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException or SystemException)
        {
            return "The sandbox identity could not be granted access to its own folders: " + ex.Message;
        }
    }

    private static void GrantTree(string folder, SecurityIdentifier sid, FileSystemRights rights)
    {
        string key = folder.TrimEnd('\\') + "|" + rights;
        if (GrantedFolders.Contains(key)) return;
        var info = new DirectoryInfo(folder);
        DirectorySecurity security = info.GetAccessControl(AccessControlSections.Access);
        bool present = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule =>
            rule.IdentityReference == sid && rule.AccessControlType == AccessControlType.Allow
            && (rule.FileSystemRights & rights) == rights && rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit));
        if (!present)
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        GrantedFolders.Add(key);
    }

    private static void GrantTraverse(string folder, SecurityIdentifier sid)
    {
        string key = folder.TrimEnd('\\') + "|traverse";
        if (GrantedFolders.Contains(key)) return;
        try
        {
            var security = new DirectorySecurity(folder, AccessControlSections.Access);
            bool present = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule =>
                rule.IdentityReference == sid && rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute);
            if (!present)
            {
                // Non-inheritable, directory-only, and SetFileSecurity does not re-propagate to children: traverse only.
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                SetFileSecurity(folder, 4 /*DACL*/, security.GetSecurityDescriptorBinaryForm());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        GrantedFolders.Add(key);
    }

    /// <summary>Interpreters that load user32 need the window station and desktop to accept AppContainers (ALL APPLICATION PACKAGES).</summary>
    private static void EnsureDesktopAccess()
    {
        if (Interlocked.CompareExchange(ref _desktopAccessGranted, 1, 0) != 0) return;
        try
        {
            GrantUserObject(GetProcessWindowStation());
            GrantUserObject(GetThreadDesktop(GetCurrentThreadId()));
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException) { }
    }

    private static void GrantUserObject(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        int info = 4;
        GetUserObjectSecurity(handle, ref info, null, 0, out int needed);
        if (needed <= 0) return;
        byte[] descriptor = new byte[needed];
        if (!GetUserObjectSecurity(handle, ref info, descriptor, needed, out _)) return;
        var raw = new RawSecurityDescriptor(descriptor, 0);
        if (raw.DiscretionaryAcl == null) return;
        var allApplicationPackages = new SecurityIdentifier("S-1-15-2-1");
        foreach (GenericAce ace in raw.DiscretionaryAcl)
            if (ace is CommonAce common && common.SecurityIdentifier == allApplicationPackages && (common.AccessMask & 0x10000000) != 0) return;
        raw.DiscretionaryAcl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, unchecked((int)0x10000000), allApplicationPackages, false, null));
        byte[] binary = new byte[raw.BinaryLength];
        raw.GetBinaryForm(binary, 0);
        SetUserObjectSecurity(handle, ref info, binary);
    }

    // ------------------------------------------------------------------ helpers

    private static IntPtr CreateJob(ChatCodeLimits limits)
    {
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        extended.BasicLimitInformation.LimitFlags = 0x2000 /*KILL_ON_JOB_CLOSE*/ | 0x8 /*ACTIVE_PROCESS*/ | 0x100 /*PROCESS_MEMORY*/
            | 0x200 /*JOB_MEMORY*/ | 0x2 /*PROCESS_TIME*/ | 0x400 /*DIE_ON_UNHANDLED_EXCEPTION*/;
        extended.BasicLimitInformation.ActiveProcessLimit = 1;
        extended.BasicLimitInformation.PerProcessUserTimeLimit = limits.CpuSeconds * 10_000_000L;
        extended.ProcessMemoryLimit = (UIntPtr)(ulong)limits.MemoryBytes;
        extended.JobMemoryLimit = (UIntPtr)(ulong)limits.MemoryBytes;
        if (!SetJob(job, 9, extended)) { CloseHandle(job); return IntPtr.Zero; }

        // No clipboard, desktop switching, display/system-parameter changes, global atoms, or USER handles outside the job.
        var ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS { UIRestrictionsClass = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x40 | 0x80 };
        SetJob(job, 4, ui);
        return job;
    }

    private static bool SetJob<T>(IntPtr job, uint infoClass, T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, buffer, false);
            return SetInformationJobObject(job, infoClass, buffer, (uint)size);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IntPtr BuildEnvironment(IReadOnlyDictionary<string, string> variables)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows, ["windir"] = windows, ["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd('\\'),
            ["PATH"] = Path.Combine(windows, "System32"), ["PATHEXT"] = ".EXE;.CMD"
        };
        // Neutral system variables only (never user secrets, never the user's PATH or profile folders).
        foreach (string name in new[] { "ComSpec", "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "ProgramData", "ALLUSERSPROFILE",
                     "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "OS", "PUBLIC" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } inherited) merged[name] = inherited;
        foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            string key = item.Key?.ToString() ?? string.Empty;
            if (key.Length == 0 || merged.ContainsKey(key) || key is "PATH" or "Path" || IsSecretName(key)) continue;
            merged[key] = item.Value?.ToString() ?? string.Empty;
        }
        foreach (KeyValuePair<string, string> pair in variables) merged[pair.Key] = pair.Value;
        var block = new StringBuilder();
        foreach (KeyValuePair<string, string> pair in merged) block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        block.Append('\0');
        byte[] bytes = Encoding.Unicode.GetBytes(block.ToString());
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    private static bool IsSecretName(string name) =>
        new[] { "KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "AUTH", "COOKIE", "SESSION" }
            .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static string Quote(string argument) =>
        argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch (IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; }
    }

    private static long FolderBytes(string folder, long stopAbove)
    {
        long total = 0;
        try
        {
            foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                total += file.Length;
                if (total > stopAbove) return total;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    private static (string Text, bool Truncated) ReadOutput(string path, int maximumBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int count = (int)Math.Min(stream.Length, maximumBytes);
            byte[] buffer = new byte[count];
            int read = 0;
            while (read < count) { int n = stream.Read(buffer, read, count - read); if (n <= 0) break; read += n; }
            string text = new UTF8Encoding(false, false).GetString(buffer, 0, read);
            return (text, stream.Length > maximumBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (string.Empty, false); }
    }

    // ------------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_CAPABILITIES { public IntPtr AppContainerSid, Capabilities; public int CapabilityCount, Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_UI_RESTRICTIONS { public uint UIRestrictionsClass; }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateAppContainerProfile(string name, string displayName, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetFileSecurityW")]
    private static extern bool SetFileSecurity(string fileName, uint information, byte[] descriptor);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    private static extern bool NativeCreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, uint infoClass, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(int threadId);
    [DllImport("kernel32.dll")] private static extern int GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectSecurity(IntPtr handle, ref int requested, byte[]? descriptor, int length, out int needed);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetUserObjectSecurity(IntPtr handle, ref int requested, byte[] descriptor);
}
