using System.Diagnostics;
using System.Text.Json;
using ModularFramework;

namespace ZapretEngineModule;

/// <summary>
/// Module quản lý tiến trình winws2 (zapret2). Port từ ZapretRunner + ZapretConfig (v1→v2).
/// Ops: start, stop, status, buildArgs.
/// </summary>
public sealed class ZapretEngineModule : IModule, IModuleOps
{
    private IModuleContext? _ctx;
    private Process? _proc;
    private string _lastLogLine = "";
    private string? _enginePath;
    private string LastError = "";
    private readonly object _lock = new();

    public string Id => "zapret-engine";

    public Task<ModuleStatus> StartAsync(IModuleContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        _enginePath = ctx.Config.TryGetValue("enginePath", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : "";
        ctx.Log.Info($"ZapretEngine ready. enginePath={_enginePath}");
        return Task.FromResult(ModuleStatus.Running);
    }

    public Task StopAsync(CancellationToken ct) => KillTreeAsync();

    public async Task<JsonElement> HandleOpAsync(string op, JsonElement args, CancellationToken ct)
    {
        switch (op)
        {
            case "start":
            {
                var exe = args.TryGetProperty("enginePath", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() : _enginePath ?? "";
                var cmd = args.TryGetProperty("args", out var a) ? a.GetString() ?? "" : "";
                var wd = args.TryGetProperty("workingDir", out var w) ? w.GetString() : null;
                bool ok = await StartEngineAsync(exe, cmd, wd);
                return JsonSerializer.SerializeToElement(new { ok, error = ok ? "" : LastError, pid = CurrentPid() });
            }
            case "stop":
                await KillTreeAsync();
                return JsonSerializer.SerializeToElement(new { ok = true });
            case "status":
                return JsonSerializer.SerializeToElement(new
                {
                    running = IsRunning(),
                    pid = CurrentPid(),
                    exitCode = ExitCode(),
                    enginePath = _enginePath,
                    lastLogLine = _lastLogLine,
                });
            case "buildArgs":
                // Mapping flag zapret v1 → v2 (xem docs/phan-tich-wrapper.md mục 4)
                return JsonSerializer.SerializeToElement(new { args = BuildWinws2Args(args) });
            default:
                throw new JsonRpcException($"zapret-engine: unknown op {op}");
        }
    }

    private bool IsRunning()
    {
        lock (_lock)
        {
            try { return _proc != null && !_proc.HasExited; } catch { return false; }
        }
    }

    private int CurrentPid()
    {
        lock (_lock)
        {
            try { return _proc?.Id ?? 0; } catch { return 0; }
        }
    }

    private int? ExitCode()
    {
        lock (_lock)
        {
            try { return _proc is { HasExited: true } ? _proc.ExitCode : null; } catch { return null; }
        }
    }

    private async Task<bool> StartEngineAsync(string exe, string cmdArgs, string? workingDir)
    {
        await KillTreeAsync();
        _enginePath = exe;

        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            LastError = $"Không tìm thấy engine: {exe}";
            _ctx?.Log.Error(LastError);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(exe),
                Arguments = cmdArgs,
                WorkingDirectory = workingDir ?? Path.GetDirectoryName(Path.GetFullPath(exe)) ?? "",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            // .cmd/.bat (fake engine khi test) → chạy qua cmd.exe
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                psi.Arguments = $"/c \"\"{Path.GetFullPath(exe)}\" {cmdArgs}\"";
                psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? "";
            }

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                _lastLogLine = e.Data;
                _ctx?.Log.Info($"out: {e.Data}");
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                _lastLogLine = e.Data;
                _ctx?.Log.Warn($"err: {e.Data}");
            };

            if (!proc.Start())
            {
                LastError = "Process.Start trả về false";
                return false;
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            lock (_lock) _proc = proc;
            _ctx?.Log.Info($"Engine started: {exe} pid={proc.Id}");
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            _ctx?.Log.Error(LastError);
            return false;
        }
    }

    private async Task KillTreeAsync()
    {
        Process? proc;
        lock (_lock) { proc = _proc; _proc = null; }
        if (proc == null) return;

        bool exited;
        try { exited = proc.HasExited; } catch { exited = true; }
        if (!exited)
        {
            try
            {
                // taskkill /T giết cả cây con (winws2 + children)
                var kill = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    ArgumentList = { "/F", "/T", "/PID", proc.Id.ToString() },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (kill != null) await kill.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) { _ctx?.Log.Warn($"kill tree failed: {ex.Message}"); }
        }
        try { proc.Dispose(); } catch { }
        _ctx?.Log.Info("Engine stopped.");
    }

    /// <summary>Sinh args winws2 từ config kiểu cũ (ZapretConfig của wrapper) — mapping v1→v2.</summary>
    internal static string BuildWinws2Args(JsonElement c)
    {
        var parts = new List<string>();

        bool v4 = !c.TryGetProperty("filterIpv4", out var f4) || f4.GetBoolean();
        bool v6 = c.TryGetProperty("filterIpv6", out var f6) && f6.GetBoolean();
        var l3 = new List<string>();
        if (v4) l3.Add("ipv4");
        if (v6) l3.Add("ipv6");
        if (l3.Count > 0) parts.Add($"--wf-l3={string.Join(",", l3)}");

        // v1: --wf-udp=... / --wf-tcp=...  →  v2: directional --wf-udp-out / --wf-tcp-out
        var udp = c.TryGetProperty("udpPorts", out var u) ? u.GetString() : "443,50000-65535";
        if (!string.IsNullOrWhiteSpace(udp)) parts.Add($"--wf-udp-out={udp}");

        var tcp = c.TryGetProperty("tcpPorts", out var t) ? t.GetString() : "80,443";
        if (!string.IsNullOrWhiteSpace(tcp)) parts.Add($"--wf-tcp-out={tcp}");

        // v1: --dpi-desync=fake  →  v2: Lua instances (win-bundle ship lua/ cạnh winws2)
        bool fakeUdp = !c.TryGetProperty("fakeUdp", out var f) || f.GetBoolean();
        if (fakeUdp)
        {
            parts.Add("--lua-init=@zapret-lib.lua");
            parts.Add("--lua-init=@zapret-obfs.lua");
            parts.Add("--lua-desync=fake:blob=fake_default_tls:badsum:strategy=1");
        }

        bool hostlistAuto = !c.TryGetProperty("hostlistAuto", out var h) || h.GetBoolean();
        if (hostlistAuto) parts.Add("--hostlist-auto=hostlist.txt");

        return string.Join(" ", parts);
    }
}
