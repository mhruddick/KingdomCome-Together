// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;

namespace KCDMP_launcher.Models
{
    /// <summary>
    /// Pure path and process-classification rules for launcher companion shutdown
    /// (compiled into KcdMp.Client.Tests without UI dependencies).
    /// </summary>
    public static class LauncherShutdownRules
    {
        private static readonly HashSet<string> CompanionProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "KcdMpClient",
            "KcdMpServer",
            "KcdMpMasterServer",
        };

        private static readonly HashSet<string> TraceServerProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "trace_server.run",
            "trace_server",
            "trace_server_launcher",
        };

        public static IReadOnlyCollection<string> CompanionNames => CompanionProcessNames;
        public static IReadOnlyCollection<string> TraceServerNames => TraceServerProcessNames;

        /// <summary>True when <paramref name="candidatePath"/> resolves inside <paramref name="rootDirectory"/>.</summary>
        public static bool IsUnderDirectory(string? candidatePath, string? rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootDirectory))
                return false;
            try
            {
                string fullCandidate = Path.GetFullPath(candidatePath);
                string fullRoot = Path.GetFullPath(rootDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves the Modding Tools install root (&lt;KCD2Mod&gt;) from a configured KingdomCome.exe path,
        /// walking up past Bin\&lt;Flavor&gt;\KingdomCome.exe to find Tools\TraceServer or steam_appid.txt.
        /// </summary>
        public static string? ResolveModdingToolsRoot(string? configuredGamePath)
        {
            if (string.IsNullOrWhiteSpace(configuredGamePath)) return null;
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(configuredGamePath));
                for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
                {
                    if (Directory.Exists(Path.Combine(dir, "Tools", "TraceServer"))
                        || File.Exists(Path.Combine(dir, "steam_appid.txt")))
                    {
                        return dir;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
            return null;
        }

        /// <summary>
        /// True when a process is Warhorse's KCD2 Modding Tools trace_server (Tools\TraceServer\trace_server*.exe),
        /// normalizing relative segments like Bin\Win64ReleaseSteamLTO_DLL\..\..\Tools\TraceServer\trace_server.run.exe.
        /// Never matches unrelated executables outside a Tools\TraceServer folder.
        /// </summary>
        public static bool IsModdingToolsTraceServer(string? processName, string? executablePath, string? configuredGamePath)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;
            string cleanName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;
            if (!TraceServerProcessNames.Contains(cleanName)) return false;
            if (string.IsNullOrWhiteSpace(executablePath)) return false;

            string fullExe;
            try { fullExe = Path.GetFullPath(executablePath); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

            if (ResolveModdingToolsRoot(configuredGamePath) is { } mtRoot)
                return IsUnderDirectory(fullExe, Path.Combine(mtRoot, "Tools", "TraceServer"));

            string? parent = Path.GetDirectoryName(fullExe)?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string suffix = Path.DirectorySeparatorChar + Path.Combine("Tools", "TraceServer");
            return parent is not null && parent.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when a process is one of our install folder's companion executables (Client, Server, MasterServer).</summary>
        public static bool IsInstallCompanion(string? processName, string? executablePath, string? installDirectory)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;
            string cleanName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;
            if (!CompanionProcessNames.Contains(cleanName)) return false;
            return IsUnderDirectory(executablePath, installDirectory);
        }
    }

    /// <summary>
    /// Coordinates clean + guaranteed shutdown of every companion process Together starts
    /// (KcdMpClient, KcdMpServer, KcdMpMasterServer, and KCD2Mod's trace_server):
    /// <list type="number">
    /// <item>Win32 Job Object (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>) reaps KcdMp* children even if the launcher crashes.</item>
    /// <item>Window close (<c>X</c> / Alt+F4) and in-app <c>EXIT</c> both run <see cref="ShutdownAll"/> idempotently.</item>
    /// <item>Sends loopback <c>POST /shutdown</c> to KcdMpClient first so it releases save locks and disconnects cleanly,
    ///   posts <c>WM_QUIT</c> to <c>trace_server</c> so its tray icon unregisters cleanly, and force-kills any stragglers.</item>
    /// </list>
    /// </summary>
    public sealed class LauncherLifecycle : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _installDir;
        private readonly Action<string>? _log;
        private readonly List<Action> _cancelCallbacks = new();
        private IntPtr _jobHandle;
        private Process? _agent;
        private Process? _relay;
        private Process? _master;
        private string? _gamePath;
        private int _versionIpcPort = 5902;
        private int _shuttingDown;
        private bool _disposed;

        public LauncherLifecycle(string? installDirectory = null, Action<string>? log = null)
        {
            _installDir = string.IsNullOrWhiteSpace(installDirectory) ? AppContext.BaseDirectory : installDirectory;
            _log = log;
            if (OperatingSystem.IsWindows())
                _jobHandle = TryCreateKillOnCloseJob();
        }

        public void Configure(string? gamePath, int versionIpcPort)
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(gamePath)) _gamePath = gamePath;
                if (versionIpcPort is > 0 and <= 65535) _versionIpcPort = versionIpcPort;
            }
        }

        public void RegisterCancelCallback(Action cancel)
        {
            lock (_gate) _cancelCallbacks.Add(cancel);
        }

        public void TrackAgent(Process? process, int versionIpcPort)
        {
            lock (_gate)
            {
                _agent = process;
                if (versionIpcPort is > 0 and <= 65535) _versionIpcPort = versionIpcPort;
            }
            AssignToJob(process, "KcdMpClient");
        }

        public void TrackRelay(Process? process)
        {
            lock (_gate) _relay = process;
            AssignToJob(process, "KcdMpServer");
        }

        public void TrackMasterServer(Process? process)
        {
            lock (_gate) _master = process;
            AssignToJob(process, "KcdMpMasterServer");
        }

        /// <summary>Stops Modding Tools' trace_server when KingdomCome.exe is no longer running.</summary>
        public void StopTraceServerIfGameExited(string? gamePathOverride = null)
        {
            try
            {
                if (Process.GetProcessesByName("KingdomCome").Length > 0) return;
            }
            catch { }

            string? gp;
            lock (_gate) gp = gamePathOverride ?? _gamePath;
            StopTraceServers(gp);
        }

        /// <summary>Idempotent shutdown invoked from ConfirmExit, Photino WindowClosing, and Main finally.</summary>
        public void ShutdownAll(string reason)
        {
            if (Interlocked.Exchange(ref _shuttingDown, 1) != 0) return;

            Process? agent, relay, master;
            string? gamePath;
            int ipcPort;
            Action[] callbacks;
            lock (_gate)
            {
                agent = _agent;
                relay = _relay;
                master = _master;
                gamePath = _gamePath;
                ipcPort = _versionIpcPort;
                callbacks = _cancelCallbacks.ToArray();
            }

            Log($"MP-SHUTDOWN starting ({reason})");

            foreach (var cb in callbacks)
            {
                try { cb(); } catch { }
            }

            // 1. Signal KcdMpClient over loopback IPC so GameBridge unwinds finally blocks & releases save locks cleanly
            bool requestedGracefulAgent = TryRequestAgentShutdown(ipcPort);
            if (requestedGracefulAgent && agent is not null)
            {
                try { agent.WaitForExit(700); } catch { }
            }

            // 2. Stop tracked processes + sweep any install-directory KcdMpClient / KcdMpServer / KcdMpMasterServer
            StopTrackedProcess(agent, "KcdMpClient", gracefulMs: requestedGracefulAgent ? 150 : 0);
            StopTrackedProcess(relay, "KcdMpServer", gracefulMs: 200);
            StopTrackedProcess(master, "KcdMpMasterServer", gracefulMs: 200);
            SweepInstallCompanions();

            // 3. Stop KCD2 Modding Tools' trace_server (window + system tray icon)
            StopTraceServers(gamePath);

            Log($"MP-SHUTDOWN complete ({reason})");
        }

        private void Log(string message)
        {
            try { _log?.Invoke(message); } catch { }
        }

        private static bool TryRequestAgentShutdown(int port)
        {
            if (port is <= 0 or > 65535) return false;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
                using var resp = http.PostAsync($"http://localhost:{port}/shutdown", content: null).GetAwaiter().GetResult();
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private void StopTrackedProcess(Process? proc, string label, int gracefulMs)
        {
            if (proc is null) return;
            try
            {
                if (proc.HasExited) return;
                int pid = proc.Id;
                if (gracefulMs > 0)
                {
                    PostQuitToProcessThreads(proc);
                    try { proc.CloseMainWindow(); } catch { }
                    if (proc.WaitForExit(gracefulMs))
                    {
                        Log($"MP-SHUTDOWN {label} (pid {pid}) exited cleanly");
                        return;
                    }
                }
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(500);
                Log($"MP-SHUTDOWN {label} (pid {pid}) terminated");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
            }
        }

        private void SweepInstallCompanions()
        {
            int selfPid = Environment.ProcessId;
            foreach (string name in LauncherShutdownRules.CompanionNames)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (var p in procs)
                {
                    using (p)
                    {
                        try
                        {
                            if (p.Id == selfPid || p.HasExited) continue;
                            string? exePath = TryGetExecutablePath(p);
                            if (!LauncherShutdownRules.IsInstallCompanion(p.ProcessName, exePath, _installDir))
                                continue;

                            int pid = p.Id;
                            PostQuitToProcessThreads(p);
                            if (!p.WaitForExit(150))
                            {
                                p.Kill(entireProcessTree: true);
                                p.WaitForExit(400);
                            }
                            Log($"MP-SHUTDOWN swept {name} (pid {pid})");
                        }
                        catch { }
                    }
                }
            }
        }

        private void StopTraceServers(string? configuredGamePath)
        {
            bool killedAny = false;
            foreach (string name in LauncherShutdownRules.TraceServerNames)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (var p in procs)
                {
                    using (p)
                    {
                        try
                        {
                            if (p.HasExited) continue;
                            string? exePath = TryGetExecutablePath(p);
                            if (!LauncherShutdownRules.IsModdingToolsTraceServer(p.ProcessName, exePath, configuredGamePath))
                                continue;

                            int pid = p.Id;
                            // Post WM_QUIT directly to UI threads so Qt's event loop exits and deletes its system tray icon
                            // (WM_CLOSE on trace_server minimizes to the tray instead of exiting).
                            PostQuitToProcessThreads(p);
                            if (p.WaitForExit(350))
                            {
                                Log($"MP-SHUTDOWN trace_server (pid {pid}) exited cleanly");
                            }
                            else
                            {
                                p.Kill(entireProcessTree: true);
                                p.WaitForExit(400);
                                killedAny = true;
                                Log($"MP-SHUTDOWN trace_server (pid {pid}) terminated");
                            }
                        }
                        catch { }
                    }
                }
            }

            if (killedAny && OperatingSystem.IsWindows())
                RefreshTrayIconsBestEffort();
        }

        private static string? TryGetExecutablePath(Process p)
        {
            try { return p.MainModule?.FileName; }
            catch { return null; }
        }

        private static void PostQuitToProcessThreads(Process p)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                foreach (ProcessThread t in p.Threads)
                {
                    using (t)
                    {
                        try { PostThreadMessageW((uint)t.Id, WmQuit, IntPtr.Zero, IntPtr.Zero); }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void AssignToJob(Process? p, string label)
        {
            if (!OperatingSystem.IsWindows() || _jobHandle == IntPtr.Zero || p is null) return;
            try
            {
                if (!p.HasExited)
                    AssignProcessToJobObject(_jobHandle, p.Handle);
            }
            catch (Exception ex)
            {
                Log($"MP-SHUTDOWN could not attach {label} to job object ({ex.GetType().Name})");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ShutdownAll("dispose");
            if (_jobHandle != IntPtr.Zero)
            {
                CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
        }

        // ---- Win32 Job Object & Tray Icon Sweep ----

        private const uint WmQuit = 0x0012;
        private const uint WmMouseMove = 0x0200;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitBreakawayOk = 0x00000800;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static IntPtr TryCreateKillOnCloseJob()
        {
            try
            {
                IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitBreakawayOk;
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
                return job;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static void RefreshTrayIconsBestEffort()
        {
            try
            {
                IntPtr tray = FindWindowW("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero)
                {
                    IntPtr notify = FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                    if (notify != IntPtr.Zero)
                    {
                        SweepToolbar(FindWindowExW(notify, IntPtr.Zero, "ToolbarWindow32", null));
                        IntPtr pager = FindWindowExW(notify, IntPtr.Zero, "SysPager", null);
                        if (pager != IntPtr.Zero)
                            SweepToolbar(FindWindowExW(pager, IntPtr.Zero, "ToolbarWindow32", null));
                    }
                }
                IntPtr overflow = FindWindowW("NotifyIconOverflowWindow", null);
                if (overflow != IntPtr.Zero)
                    SweepToolbar(FindWindowExW(overflow, IntPtr.Zero, "ToolbarWindow32", null));
            }
            catch { }
        }

        private static void SweepToolbar(IntPtr toolbar)
        {
            if (toolbar == IntPtr.Zero || !GetClientRect(toolbar, out var rc)) return;
            for (int y = 4; y < rc.Bottom; y += 8)
            {
                for (int x = 4; x < rc.Right; x += 8)
                {
                    IntPtr lParam = (IntPtr)((y << 16) | (x & 0xFFFF));
                    PostMessageW(toolbar, WmMouseMove, IntPtr.Zero, lParam);
                }
            }
        }
    }
}
