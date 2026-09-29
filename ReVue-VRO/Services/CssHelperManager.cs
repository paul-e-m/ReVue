using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using ReVueVRO.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReVueVRO.Services;

public sealed class CssHelperManager : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int GracefulShutdownTimeoutMsec = 1500;
    private const int ForcedShutdownTimeoutMsec = 1000;

    private static readonly IReadOnlyDictionary<string, string> HelperExeNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Legacy"] = "GetSessionInfo_LegacyCSS.exe",
            ["Online CSS"] = "GetSessionInfo_OnlineCSS.exe",
            ["Offline CSS"] = "GetSessionInfo_OfflineCSS.exe"
        };

    private readonly object syncGate = new();
    private readonly ILogger<CssHelperManager> logger;
    private readonly SafeJobHandle? helperJob;
    private Process? activeProcess;
    private int? activeProcessId;
    private string? activeExecutablePath;
    private bool disposed;

    public CssHelperManager(ILogger<CssHelperManager> logger)
    {
        this.logger = logger;
        helperJob = CreateHelperJob();
    }

    public void Synchronize(AppConfig config, bool recoverStaleProcesses = false)
    {
        lock (syncGate)
        {
            if (disposed) return;

            var targetExecutablePath = GetHelperExecutablePath(config);

            if (recoverStaleProcesses)
                RecoverStartupProcesses(targetExecutablePath);

            if (IsTrackedHelperRunning(targetExecutablePath))
                return;

            StopTrackedHelper("the CSS integration changed");

            if (targetExecutablePath != null)
                StartHelper(targetExecutablePath);
        }
    }

    public void Dispose()
    {
        lock (syncGate)
        {
            if (disposed) return;
            disposed = true;

            StopTrackedHelper("ReVue-VRO is shutting down");
            helperJob?.Dispose();
        }
    }

    private SafeJobHandle? CreateHelperJob()
    {
        var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            logger.LogError(error, "Could not create the CSS helper Job Object. Crash cleanup will be unavailable.");
            job.Dispose();
            return null;
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (NativeMethods.SetInformationJobObject(job, JobObjectInfoType.ExtendedLimitInformation, buffer, (uint)size))
                return job;

            var error = new Win32Exception(Marshal.GetLastWin32Error());
            logger.LogError(error, "Could not configure the CSS helper Job Object. Crash cleanup will be unavailable.");
            job.Dispose();
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private string? GetHelperExecutablePath(AppConfig config)
    {
        var cssLink = config.CSSLink?.Trim() ?? string.Empty;
        if (!HelperExeNames.TryGetValue(cssLink, out var exeName))
            return null;

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, exeName));
    }

    private void RecoverStartupProcesses(string? targetExecutablePath)
    {
        foreach (var exeName in HelperExeNames.Values)
        {
            var expectedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, exeName));
            var processName = Path.GetFileNameWithoutExtension(exeName);

            Process[] candidates;
            try
            {
                candidates = Process.GetProcessesByName(processName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not scan for a stale CSS helper process named {ProcessName}.", processName);
                continue;
            }

            foreach (var candidate in candidates)
            {
                var adopted = false;
                try
                {
                    if (candidate.HasExited) continue;

                    var candidatePath = TryGetExecutablePath(candidate);
                    if (candidatePath == null)
                    {
                        logger.LogWarning(
                            "Ignored startup process {ProcessName} ({ProcessId}) because its executable path could not be verified.",
                            candidate.ProcessName,
                            candidate.Id);
                        continue;
                    }

                    if (!PathsEqual(candidatePath, expectedPath))
                    {
                        logger.LogInformation(
                            "Ignored startup process {ProcessName} ({ProcessId}) because it is running from {ExecutablePath}.",
                            candidate.ProcessName,
                            candidate.Id,
                            candidatePath);
                        continue;
                    }

                    if (activeProcess == null && PathsEqual(candidatePath, targetExecutablePath))
                    {
                        AssignToHelperJob(candidate);
                        activeProcess = candidate;
                        activeProcessId = candidate.Id;
                        activeExecutablePath = candidatePath;
                        adopted = true;
                        logger.LogInformation(
                            "Adopted existing CSS helper {ExecutablePath} ({ProcessId}) during startup recovery.",
                            candidatePath,
                            candidate.Id);
                        continue;
                    }

                    StopProcess(candidate, candidatePath, candidate.Id, "startup recovery");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not recover CSS helper process {ProcessId}.", TryGetProcessId(candidate));
                }
                finally
                {
                    if (!adopted)
                        candidate.Dispose();
                }
            }
        }
    }

    private bool IsTrackedHelperRunning(string? targetExecutablePath)
    {
        if (activeProcess == null || activeExecutablePath == null)
            return false;

        try
        {
            if (activeProcessId != activeProcess.Id || activeProcess.HasExited)
            {
                ClearTrackedHelper();
                return false;
            }
        }
        catch
        {
            ClearTrackedHelper();
            return false;
        }

        return targetExecutablePath != null && PathsEqual(activeExecutablePath, targetExecutablePath);
    }

    private void StartHelper(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            logger.LogWarning("CSS helper executable was not found at {ExecutablePath}.", executablePath);
            return;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            if (!process.Start())
            {
                logger.LogError("CSS helper {ExecutablePath} did not start.", executablePath);
                process.Dispose();
                return;
            }

            AssignToHelperJob(process);
            activeProcess = process;
            activeProcessId = process.Id;
            activeExecutablePath = executablePath;
            logger.LogInformation("Started CSS helper {ExecutablePath} ({ProcessId}).", executablePath, process.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not start CSS helper {ExecutablePath}.", executablePath);
            TryForceStop(process);
            process.Dispose();
        }
    }

    private void AssignToHelperJob(Process process)
    {
        if (helperJob == null) return;

        try
        {
            if (NativeMethods.AssignProcessToJobObject(helperJob, process.Handle))
                return;

            var error = new Win32Exception(Marshal.GetLastWin32Error());
            logger.LogWarning(
                error,
                "Could not assign CSS helper process {ProcessId} to the Job Object. Crash cleanup will be unavailable for this process.",
                process.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not assign CSS helper process {ProcessId} to the Job Object. Crash cleanup will be unavailable for this process.",
                TryGetProcessId(process));
        }
    }

    private void StopTrackedHelper(string reason)
    {
        if (activeProcess == null)
        {
            activeProcessId = null;
            activeExecutablePath = null;
            return;
        }

        var process = activeProcess;
        var processId = activeProcessId;
        var executablePath = activeExecutablePath ?? "the tracked CSS helper";
        activeProcess = null;
        activeProcessId = null;
        activeExecutablePath = null;

        try
        {
            StopProcess(process, executablePath, processId ?? TryGetProcessId(process), reason);
        }
        finally
        {
            process.Dispose();
        }
    }

    private void StopProcess(Process process, string executablePath, int processId, string reason)
    {
        try
        {
            if (process.HasExited) return;

            var closeRequested = process.CloseMainWindow();
            if (closeRequested && process.WaitForExit(GracefulShutdownTimeoutMsec))
            {
                logger.LogInformation(
                    "CSS helper {ExecutablePath} ({ProcessId}) exited gracefully because {Reason}.",
                    executablePath,
                    processId,
                    reason);
                return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(ForcedShutdownTimeoutMsec);
            logger.LogInformation(
                "Stopped CSS helper {ExecutablePath} ({ProcessId}) because {Reason}.",
                executablePath,
                processId,
                reason);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not stop CSS helper {ExecutablePath} ({ProcessId}) because {Reason}.",
                executablePath,
                processId,
                reason);
        }
    }

    private void TryForceStop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private void ClearTrackedHelper()
    {
        activeProcess?.Dispose();
        activeProcess = null;
        activeProcessId = null;
        activeExecutablePath = null;
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName is { Length: > 0 } path
                ? Path.GetFullPath(path)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static int TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return -1;
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        return left != null && right != null &&
            string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.CloseHandle(handle);
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(
            SafeJobHandle job,
            JobObjectInfoType infoType,
            IntPtr jobObjectInfo,
            uint jobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
