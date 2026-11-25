using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// The current user's sign-in task through the Windows Task Scheduler service (the "Schedule.Service" automation object, late bound:
    /// no extra package). Only the task named <see cref="LogonRegistration.TaskName"/> in the root folder is read, created or removed.
    /// The calls are handled by the Task Scheduler service itself, so they are never redirected into another app's private registry or file
    /// view, which is what made a Run value invisible to Windows on a PC where PaRuX had been set up from inside such an app.
    /// </summary>
    public sealed class ScheduledTaskLogonStore : ILogonTaskStore
    {
        private const int CreateOrUpdate = 6;            // TASK_CREATE_OR_UPDATE
        private const int LogonInteractiveToken = 3;     // TASK_LOGON_INTERACTIVE_TOKEN
        private const int HasNotRun = 0x00041303;        // SCHED_S_TASK_HAS_NOT_RUN
        private const int AccessDeniedHr = unchecked((int)0x80070005);
        private const int NotFoundHr = unchecked((int)0x80070002);

        private readonly bool _log;

        public ScheduledTaskLogonStore(bool log = true) => _log = log;

        public LogonTaskInfo? Get(string name)
        {
            object? svc = null, folder = null, task = null;
            try
            {
                svc = Connect();
                folder = Call(svc, "GetFolder", "\\");
                try { task = Call(folder!, "GetTask", name); }
                catch (Exception ex) when (HResultOf(ex) == NotFoundHr) { return null; }
                if (task is null) return null;
                string xml = Prop(task, "Xml") as string ?? string.Empty;
                bool enabled = Prop(task, "Enabled") is bool b && b;
                DateTimeOffset? lastRun = Prop(task, "LastRunTime") is DateTime t && t.Year >= 2000
                    ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Local)).ToUniversalTime() : null;
                int? result = Prop(task, "LastTaskResult") is int r && r != HasNotRun ? r : null;
                (string? command, string? args) = ReadExec(xml);
                return new LogonTaskInfo(command, args, enabled, lastRun, result, xml);
            }
            catch (Exception ex)
            {
                if (_log) Log.Write("Sign-in task: read failed (" + Describe(ex) + ").");
                return null;
            }
            finally { Release(task); Release(folder); Release(svc); }
        }

        public LogonTaskCallResult Register(string name, string xml)
        {
            object? svc = null, folder = null, task = null;
            try
            {
                svc = Connect();
                folder = Call(svc, "GetFolder", "\\");
                task = Call(folder!, "RegisterTask", name, xml, CreateOrUpdate, null, null, LogonInteractiveToken, null);
                if (_log) Log.Write("Sign-in task registered for the current user.");
                return LogonTaskCallResult.Success;
            }
            catch (Exception ex)
            {
                if (_log) Log.Write("Sign-in task: register failed (" + Describe(ex) + ").");
                return LogonTaskCallResult.Failed(Describe(ex), HResultOf(ex) == AccessDeniedHr || Inner(ex) is UnauthorizedAccessException);
            }
            finally { Release(task); Release(folder); Release(svc); }
        }

        public LogonTaskCallResult Delete(string name)
        {
            object? svc = null, folder = null;
            try
            {
                svc = Connect();
                folder = Call(svc, "GetFolder", "\\");
                try { Call(folder!, "DeleteTask", name, 0); }
                catch (Exception ex) when (HResultOf(ex) == NotFoundHr) { }
                if (_log) Log.Write("Sign-in task removed.");
                return LogonTaskCallResult.Success;
            }
            catch (Exception ex)
            {
                if (_log) Log.Write("Sign-in task: remove failed (" + Describe(ex) + ").");
                return LogonTaskCallResult.Failed(Describe(ex), HResultOf(ex) == AccessDeniedHr || Inner(ex) is UnauthorizedAccessException);
            }
            finally { Release(folder); Release(svc); }
        }

        internal static (string? Command, string? Arguments) ReadExec(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return (null, null);
            try
            {
                XElement? exec = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Exec");
                return (exec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value,
                        exec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value);
            }
            catch (System.Xml.XmlException) { return (null, null); }
        }

        private static object Connect()
        {
            Type type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            object svc = Activator.CreateInstance(type)!;
            Call(svc, "Connect");
            return svc;
        }

        private static object? Call(object target, string method, params object?[] args) =>
            target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);

        private static object? Prop(object target, string name) =>
            target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

        private static Exception Inner(Exception ex) => ex is TargetInvocationException { InnerException: { } i } ? i : ex;
        private static int HResultOf(Exception ex) => Inner(ex).HResult;
        private static string Describe(Exception ex) { Exception i = Inner(ex); return i.GetType().Name + " 0x" + i.HResult.ToString("X8"); }

        private static void Release(object? com)
        {
            try { if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com); }
            catch (ArgumentException) { }
        }
    }
}
