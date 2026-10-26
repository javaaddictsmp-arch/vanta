namespace TuneUp.Services;

/// <summary>
/// Simple composition root: one place that creates every service and wires them together.
/// (A full DI container would be overkill for an app this size.)
/// </summary>
public static class AppServices
{
    public static SettingsService Settings { get; private set; } = null!;
    public static ChangeLogService ChangeLog { get; private set; } = null!;
    public static TweakCatalogService Catalog { get; private set; } = null!;
    public static TweakEngine Engine { get; private set; } = null!;
    public static HardwareMonitorService Hardware { get; private set; } = null!;
    public static MetricsHub Hub { get; private set; } = null!;
    public static CsvLogService Csv { get; private set; } = null!;
    public static SystemInfoService SystemInfo { get; private set; } = null!;
    public static HealthScoreService Health { get; private set; } = null!;

    // Phase 2: FPS Boost Center
    public static GameDetectionService Games { get; private set; } = null!;
    public static ProfileService Profiles { get; private set; } = null!;
    public static ProcessRuleService Rules { get; private set; } = null!;
    public static PowerPlanService Power { get; private set; } = null!;
    public static TimerResolutionService Timer { get; private set; } = null!;
    public static BoostEngine Boost { get; private set; } = null!;
    public static GameWatcherService Watcher { get; private set; } = null!;
    public static FrametimeCaptureService Frametimes { get; private set; } = null!;
    public static BenchmarkService Benchmarks { get; private set; } = null!;
    public static LatencyService Latency { get; private set; } = null!;
    public static GpuDisplayService Gpu { get; private set; } = null!;
    public static GameConfigService GameConfigs { get; private set; } = null!;

    // Phase 3: System Scan
    public static ScanService Scan { get; private set; } = null!;

    // Phase 4
    public static CleanerService Cleaner { get; private set; } = null!;
    public static RamService Ram { get; private set; } = null!;
    public static GameConnectionService GameConnection { get; private set; } = null!;
    public static SessionRecorder Sessions { get; private set; } = null!;

    public static void Init()
    {
        Settings = new SettingsService();
        Settings.Load();

        ChangeLog = new ChangeLogService();
        ChangeLog.Load();

        Catalog = new TweakCatalogService();
        Catalog.Load();

        Engine = new TweakEngine(ChangeLog, new BackupService(), new RestorePointService(), Settings);

        Hardware = new HardwareMonitorService();
        Hub = new MetricsHub(Hardware, Settings);
        Csv = new CsvLogService();

        SystemInfo = new SystemInfoService();
        Health = new HealthScoreService();

        Games = new GameDetectionService();
        Games.LoadSaved();
        Profiles = new ProfileService();
        Profiles.Load();
        Rules = new ProcessRuleService();
        Rules.Load();
        Power = new PowerPlanService();
        Timer = new TimerResolutionService();
        Boost = new BoostEngine(Profiles, Power, Timer, Settings);
        Watcher = new GameWatcherService(Games, Boost, Profiles, Rules, Settings);
        Frametimes = new FrametimeCaptureService();
        Benchmarks = new BenchmarkService();
        Latency = new LatencyService();
        Gpu = new GpuDisplayService();
        GameConfigs = new GameConfigService();
        GameConfigs.Load();
        Scan = new ScanService();
        Cleaner = new CleanerService();
        Ram = new RamService(Settings);
        GameConnection = new GameConnectionService();
        Watcher.RunningChanged += (_, _) => FollowGameServer();
        Sessions = new SessionRecorder(Watcher, Hub, Frametimes, Boost, GameConnection, Settings);
    }

    /// <summary>Points the live game-server ping at whatever game is running (or stops it).</summary>
    public static void FollowGameServer()
    {
        var running = Watcher.Running.FirstOrDefault();
        if (running != null && Settings.Current.FollowGameServer) GameConnection.Follow(running.Game.Name, running.Pids);
        else if (GameConnection.IsRunning) GameConnection.Stop();
    }

    public static void Shutdown()
    {
        // End any boost first so the PC is never left "boosted" after Vanta closes.
        try { Boost?.ShutdownBlocking(); } catch { /* ignore */ }
        try { Watcher?.Stop(); } catch { /* ignore */ }
        try { Sessions?.Dispose(); } catch { /* ignore */ }
        try { GameConnection?.Dispose(); } catch { /* ignore */ }
        try { Ram?.Dispose(); } catch { /* ignore */ }
        try { Timer?.Release(); } catch { /* ignore */ }
        try { Frametimes?.Dispose(); } catch { /* ignore */ }
        try { Csv?.Dispose(); } catch { /* ignore */ }
        try { Hub?.Dispose(); } catch { /* ignore */ }
    }
}
