// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using KCDMP_launcher.Models;

namespace KcdMp.Client.Tests;

public class LauncherLifecycleAndVerboseLogTests
{
    private static int FreeLoopbackPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public void ClientConfig_verbose_logging_defaults_false_and_parses_cli_flags()
    {
        var cfg = new ClientConfig();
        Assert.False(cfg.VerboseLogging);

        cfg.ApplyCommandLine(["--host", "127.0.0.1", "--verbose"]);
        Assert.True(cfg.VerboseLogging);

        cfg.ApplyCommandLine(["--host", "127.0.0.1", "--no-verbose"]);
        Assert.False(cfg.VerboseLogging);
    }

    [Fact]
    public void LauncherShutdownRules_scopes_install_companions_and_normalizes_modding_tools_trace_server_paths()
    {
        const string installDir = @"C:\Games\KCDMP";
        Assert.True(LauncherShutdownRules.IsInstallCompanion("KcdMpClient", @"C:\Games\KCDMP\KcdMpClient.exe", installDir));
        Assert.True(LauncherShutdownRules.IsInstallCompanion("KcdMpServer", @"c:\games\kcdmp\KcdMpServer.exe", installDir));
        Assert.True(LauncherShutdownRules.IsInstallCompanion("KcdMpMasterServer", @"C:\Games\KCDMP\MasterServer\KcdMpMasterServer.exe", installDir));

        // Sibling folders and unrelated process names never match
        Assert.False(LauncherShutdownRules.IsInstallCompanion("KcdMpClient", @"C:\Games\KCDMP_Other\KcdMpClient.exe", installDir));
        Assert.False(LauncherShutdownRules.IsInstallCompanion("KingdomCome", @"C:\Games\KCDMP\KingdomCome.exe", installDir));

        // Relative ..\..\ path as spawned by KCD2Mod's trace_server_launcher.exe
        string tempRoot = Path.Combine(Path.GetTempPath(), "kcd2mod-trace-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string binDir = Path.Combine(tempRoot, "Bin", "Win64ReleaseSteamLTO_DLL");
            string traceDir = Path.Combine(tempRoot, "Tools", "TraceServer");
            Directory.CreateDirectory(binDir);
            Directory.CreateDirectory(traceDir);
            File.WriteAllText(Path.Combine(tempRoot, "steam_appid.txt"), "2429020");

            string gameExe = Path.Combine(binDir, "KingdomCome.exe");
            string relTraceExe = Path.Combine(binDir, @"..\..\Tools\TraceServer\trace_server.run.exe");

            Assert.True(LauncherShutdownRules.IsModdingToolsTraceServer("trace_server.run", relTraceExe, gameExe));
            Assert.True(LauncherShutdownRules.IsModdingToolsTraceServer("trace_server.run", relTraceExe, configuredGamePath: null));
            Assert.True(LauncherShutdownRules.IsModdingToolsTraceServer("trace_server_launcher", Path.Combine(traceDir, "trace_server_launcher.exe"), gameExe));

            // Unrelated executables or trace_server under a different root are rejected
            Assert.False(LauncherShutdownRules.IsModdingToolsTraceServer("trace_server.run", @"C:\DevTools\trace_server.run.exe", gameExe));
            Assert.False(LauncherShutdownRules.IsModdingToolsTraceServer("trace_server.run", @"C:\DevTools\trace_server.run.exe", configuredGamePath: null));
            Assert.False(LauncherShutdownRules.IsModdingToolsTraceServer("trace_server.run", @"C:\OtherKCD2Mod\Tools\TraceServer\trace_server.run.exe", gameExe));
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LauncherLifecycle_signals_agent_ipc_shutdown_stops_tracked_children_and_runs_idempotently()
    {
        int port = FreeLoopbackPort();
        int shutdownSignals = 0;
        var ipc = new VersionIpcServer(
            getPeers: static () => [],
            port: port,
            onShutdown: () => Interlocked.Increment(ref shutdownSignals));
        ipc.Start();

        string isolatedDir = Path.Combine(Path.GetTempPath(), "kcdmp-lifecycle-iso-" + Guid.NewGuid().ToString("N"));
        string fakeGameDir = Path.Combine(isolatedDir, "FakeGame", "Tools", "TraceServer");
        Directory.CreateDirectory(fakeGameDir);
        File.WriteAllText(Path.Combine(isolatedDir, "FakeGame", "steam_appid.txt"), "2429020");

        var logs = new List<string>();
        int cancelCalls = 0;
        Process? dummyRelay = null;
        try
        {
            dummyRelay = Process.Start(new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "sh",
                Arguments = OperatingSystem.IsWindows() ? "-NoProfile -Command \"Start-Sleep -Seconds 30\"" : "-c \"sleep 30\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(dummyRelay);

            using var lifecycle = new LauncherLifecycle(isolatedDir, logs.Add);
            lifecycle.Configure(Path.Combine(isolatedDir, "FakeGame", "Bin", "Win64", "KingdomCome.exe"), port);
            lifecycle.RegisterCancelCallback(() => Interlocked.Increment(ref cancelCalls));
            lifecycle.TrackRelay(dummyRelay);

            lifecycle.ShutdownAll("window-close");
            lifecycle.ShutdownAll("app-run-end");
            lifecycle.ShutdownAll("main-finally");

            Assert.Equal(1, cancelCalls);
            Assert.Equal(1, Volatile.Read(ref shutdownSignals));
            Assert.True(dummyRelay!.WaitForExit(2000));
            Assert.Single(logs, l => l.StartsWith("MP-SHUTDOWN starting", StringComparison.Ordinal));
            Assert.Single(logs, l => l.StartsWith("MP-SHUTDOWN complete", StringComparison.Ordinal));
        }
        finally
        {
            ipc.Stop();
            try { if (dummyRelay is { HasExited: false }) dummyRelay.Kill(entireProcessTree: true); } catch { }
            dummyRelay?.Dispose();
            try { if (Directory.Exists(isolatedDir)) Directory.Delete(isolatedDir, recursive: true); } catch { }
        }
    }
}
