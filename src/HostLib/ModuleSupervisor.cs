using System.Diagnostics;
using System.Text.Json;
using ModularFramework;

namespace ModularFramework.HostLib;

/// <summary>Trạng thái 1 module nhìn từ host.</summary>
public enum ModuleRunState
{
    Disabled,     // hỏng quá N lần, không tự restart nữa
    Stopped,
    Starting,
    Running,
    Restarting,
    Stopping,
    Dead,         // crash, đang chờ backoff
}

public sealed class ModuleInstance : IDisposable
{
    public required ModuleManifest Manifest { get; init; }
    public required string ModuleRoot { get; init; }
    public ModuleRunState State { get; internal set; } = ModuleRunState.Stopped;
    public int RestartCount { get; internal set; }
    public int ExitCode { get; internal set; }
    public string? LastError { get; internal set; }
    public DateTime? LastExitAt { get; internal set; }
    public string LogFile { get; internal set; } = "";
    public Process? Process { get; internal set; }
    public JsonRpcChannel? Channel { get; internal set; }
    public string PipeName { get; internal set; } = "";

    public void Dispose()
    {
        Channel?.Dispose();
        Process?.Dispose();
    }
}

/// <summary>
/// Supervisor: spawn ModuleHost.exe cho mỗi module, ping heartbeat, restart backoff,
/// crash bundle, disable sau N lần fail liên tiếp. Module chết KHÔNG ảnh hưởng module khác.
/// </summary>
public sealed class ModuleSupervisor : IDisposable
{
    private readonly string _moduleHostExe;
    private readonly string _modulesRoot;
    private readonly string _logsRoot;
    private readonly string _crashesRoot;
    private readonly Dictionary<string, ModuleInstance> _modules = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _watchTask;
    public event Action<ModuleInstance>? StateChanged;

    public ModuleSupervisor(string moduleHostExe, string modulesRoot, string logsRoot)
    {
        _moduleHostExe = moduleHostExe;
        _modulesRoot = modulesRoot;
        _logsRoot = logsRoot;
        _crashesRoot = Path.Combine(logsRoot, "crashes");
        Directory.CreateDirectory(_logsRoot);
        Directory.CreateDirectory(_crashesRoot);
        ScanModules();
        _watchTask = Task.Run(WatchLoopAsync);
    }

    public IReadOnlyDictionary<string, ModuleInstance> Modules => _modules;

    private void ScanModules()
    {
        foreach (var dir in Directory.GetDirectories(_modulesRoot))
        {
            var manifestPath = Path.Combine(dir, "module.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = ModuleManifest.Load(dir);
                if (string.IsNullOrEmpty(manifest.Entry) ||
                    !File.Exists(Path.Combine(dir, manifest.Entry)))
                {
                    continue; // chưa build — bỏ qua
                }
                var inst = new ModuleInstance
                {
                    Manifest = manifest,
                    ModuleRoot = dir,
                    LogFile = Path.Combine(_logsRoot, $"{manifest.Id}.log"),
                };
                _modules[manifest.Id] = inst;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Scan {dir}: {ex.Message}");
            }
        }
    }

    public async Task StartAsync(string moduleId, CancellationToken ct = default)
    {
        if (!_modules.TryGetValue(moduleId, out var inst))
            throw new KeyNotFoundException(moduleId);
        inst.RestartCount = 0;
        await SpawnAndConnectAsync(inst, ct);
        // BUG FIX: luôn gọi "start" (module.StartAsync init ctx/config) — AutoStart chỉ
        // quyết định start tự động khi scan, không quyết định ngữ nghĩa StartAsync(moduleId).
        await CallAsync(inst, "start", ct);
        StateChanged?.Invoke(inst);
    }

    public async Task StopAsync(string moduleId, CancellationToken ct = default)
    {
        if (!_modules.TryGetValue(moduleId, out var inst)) return;
        SetState(inst, ModuleRunState.Stopping);
        try { await CallAsync(inst, "stop", ct); } catch { }
        KillTree(inst);
        SetState(inst, ModuleRunState.Stopped);
        StateChanged?.Invoke(inst);
    }

    public async Task<JsonElement> CallAsync(string moduleId, string method, JsonElement? args = null, CancellationToken ct = default)
    {
        var inst = _modules[moduleId];
        return await CallAsync(inst, method, ct, args);
    }

    /// <summary>Gọi op dạng "<moduleId>.<op>" — 1 tham số gọn, đúng convention wire.</summary>
    public async Task<JsonElement> CallAsync(string prefixedMethod, JsonElement? args = null, CancellationToken ct = default)
    {
        int dot = prefixedMethod.IndexOf('.');
        if (dot <= 0) throw new ArgumentException("prefixedMethod phải dạng '<moduleId>.<op>'", nameof(prefixedMethod));
        var inst = _modules[prefixedMethod[..dot]];
        return await CallAsync(inst, prefixedMethod, ct, args); // gửi NGUYÊN cả prefix lên wire
    }

    private static async Task<JsonElement> CallAsync(ModuleInstance inst, string method, CancellationToken ct, JsonElement? args = null)
    {
        if (inst.Channel == null) throw new InvalidOperationException("module not connected");
        return await inst.Channel.CallAsync(method, args, ct);
    }

    public async Task<bool> PingAsync(string moduleId, CancellationToken ct = default)
    {
        try
        {
            var r = await CallAsync(moduleId, "ping", ct: ct);
            return r.TryGetProperty("pong", out var p) && p.GetBoolean();
        }
        catch { return false; }
    }

    private async Task SpawnAndConnectAsync(ModuleInstance inst, CancellationToken ct)
    {
        inst.PipeName = $"mf-{inst.Manifest.Id}-{Guid.NewGuid():N}";
        var psi = new ProcessStartInfo
        {
            FileName = _moduleHostExe,
            ArgumentList = { "--module", inst.ModuleRoot, "--pipe", inst.PipeName, "--logs", _logsRoot },
            WorkingDirectory = Path.GetDirectoryName(_moduleHostExe) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // TODO: RequiresElevation → UseShellExecute=true + Verb=runas (UAC 1 lần cho cả app là hướng tốt hơn)
        inst.Process = Process.Start(psi) ?? throw new InvalidOperationException("spawn failed");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
        inst.Channel = await JsonRpcChannel.ConnectToModuleAsync(inst.PipeName, timeoutCts.Token);
        // BẮT BUỘC: host-side phải có vòng đọc pipe để nhận response từ module
        _ = Task.Run(async () =>
        {
            try { await inst.Channel.ReadLoopAsync(CancellationToken.None); }
            catch { /* pipe đóng = module chết, watch loop sẽ xử lý */ }
        });
        SetState(inst, ModuleRunState.Running);
    }

    private async Task WatchLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await Task.Delay(1000, _cts.Token).ContinueWith(_ => { });
            foreach (var inst in _modules.Values.ToList())
            {
                if (inst.Process == null) continue;
                bool exited;
                try { exited = inst.Process.HasExited; } catch { exited = true; }

                if (exited && inst.State is ModuleRunState.Running or ModuleRunState.Starting or ModuleRunState.Restarting)
                {
                    inst.ExitCode = inst.Process.ExitCode;
                    inst.LastExitAt = DateTime.Now;
                    WriteCrashBundle(inst);
                    await ScheduleRestartAsync(inst);
                }
                else if (!exited && inst.State == ModuleRunState.Running && inst.Channel != null)
                {
                    // heartbeat: ping fail → coi như chết
                    bool alive;
                    string pingDetail = "";
                    try
                    {
                        using var pingCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        var pingRes = await CallAsync(inst, "ping", pingCts.Token);
                        alive = pingRes.TryGetProperty("pong", out var p) && p.GetBoolean();
                        pingDetail = alive ? "pong=true" : $"NO-PONG raw={pingRes}";
                    }
                    catch (Exception ex) { alive = false; pingDetail = $"{ex.GetType().Name}: {ex.Message}"; }
                    if (!alive)
                    {
                        Console.WriteLine($"[WATCH] {DateTime.Now:HH:mm:ss.fff} {inst.Manifest.Id} heartbeat FAIL: {pingDetail}");
                        inst.LastError = "heartbeat ping failed";
                        WriteCrashBundle(inst);
                        KillTree(inst);
                        await ScheduleRestartAsync(inst);
                    }
                }
            }
        }
    }

    private async Task ScheduleRestartAsync(ModuleInstance inst)
    {
        inst.RestartCount++;
        if (inst.RestartCount > inst.Manifest.Health.MaxFailuresBeforeDisable)
        {
            SetState(inst, ModuleRunState.Disabled);
            inst.LastError = $"disabled after {inst.RestartCount} consecutive failures";
            StateChanged?.Invoke(inst);
            return;
        }
        SetState(inst, ModuleRunState.Restarting);
        StateChanged?.Invoke(inst);
        int backoff = inst.Manifest.Health.RestartBackoffSec[
            Math.Min(inst.RestartCount - 1, inst.Manifest.Health.RestartBackoffSec.Length - 1)];
        await Task.Delay(TimeSpan.FromSeconds(backoff), _cts.Token).ContinueWith(_ => { });
        try
        {
            inst.Channel?.Dispose();
            inst.Channel = null;
            await SpawnAndConnectAsync(inst, CancellationToken.None);
            // BUG FIX: module đã được start trước khi crash → restart phải start lại
            // (không phụ thuộc AutoStart — nếu không module sẽ không init ctx/config).
            await CallAsync(inst, "start", CancellationToken.None);
        }
        catch (Exception ex)
        {
            inst.LastError = $"restart failed: {ex.Message}";
            await ScheduleRestartAsync(inst);
        }
        StateChanged?.Invoke(inst);
    }

    private void WriteCrashBundle(ModuleInstance inst)
    {
        try
        {
            var crashDir = Path.Combine(_crashesRoot, inst.Manifest.Id);
            Directory.CreateDirectory(crashDir);
            var tail = Tail(inst.LogFile, 50);
            var bundle = new
            {
                module = inst.Manifest.Id,
                version = inst.Manifest.Version,
                exitCode = inst.ExitCode,
                exitAt = inst.LastExitAt,
                error = inst.LastError,
                restartCount = inst.RestartCount,
                lastLogLines = tail,
            };
            File.WriteAllText(Path.Combine(crashDir, $"crash-{DateTime.Now:yyyyMMdd_HHmmss}.json"),
                JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string[] Tail(string file, int n)
    {
        try
        {
            if (!File.Exists(file)) return [];
            return File.ReadLines(file).TakeLast(n).ToArray();
        }
        catch { return []; }
    }

    private static void KillTree(ModuleInstance inst)
    {
        try
        {
            if (inst.Process == null) return;
            bool exited;
            try { exited = inst.Process.HasExited; } catch { exited = true; }
            if (!exited)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    ArgumentList = { "/F", "/T", "/PID", inst.Process.Id.ToString() },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.WaitForExit(5000);
            }
        }
        catch { }
        inst.Channel?.Dispose();
        inst.Channel = null;
        inst.Process?.Dispose();
        inst.Process = null;
    }

    private void SetState(ModuleInstance inst, ModuleRunState state)
    {
        inst.State = state;
        StateChanged?.Invoke(inst);
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var inst in _modules.Values)
        {
            try { KillTree(inst); } catch { }
            inst.Dispose();
        }
        try { _watchTask.Wait(2000); } catch { }
    }
}
