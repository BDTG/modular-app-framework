using System.Diagnostics;
using System.Text.Json;
using ModularFramework;

namespace BlockcheckModule;

/// <summary>
/// Module chạy blockcheck2.sh (zapret2) qua cygwin win-bundle — port từ
/// RunBlockcheckButton_Click của wrapper. Không UAC bên trong (module requiresElevation).
/// Ops: run {domain, ipv4, ipv6} → chạy nền; poll → trạng thái + strategies; cancel.
/// </summary>
public sealed class BlockcheckModule : IModule, IModuleOps
{
    private IModuleContext? _ctx;
    private Process? _cmdProc;
    private string _logFile = "";
    private bool _running;
    private bool _done;
    private string _lastError = "";
    private List<Strategy> _strategies = new();
    private readonly object _lock = new();

    public sealed record Strategy(string Type, string Args, string Label);

    public string Id => "blockcheck";

    public Task<ModuleStatus> StartAsync(IModuleContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        ctx.Log.Info("Blockcheck ready. Ops: run, poll, cancel.");
        return Task.FromResult(ModuleStatus.Running);
    }

    public Task StopAsync(CancellationToken ct) => CancelAsync();

    public Task<JsonElement> HandleOpAsync(string op, JsonElement args, CancellationToken ct) => op switch
    {
        "run" => RunAsync(args),
        "poll" => Task.FromResult(Poll()),
        "cancel" => CancelAsync(),
        _ => throw new JsonRpcException($"blockcheck: unknown op {op}"),
    };

    private string FindBundle()
    {
        var configured = _ctx?.Config.TryGetValue("bundlePath", out var b) == true && b.ValueKind == JsonValueKind.String
            ? b.GetString() : "";
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;

        string[] candidates =
        [
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zapret-win-bundle"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "zapret-win-bundle"),
            Path.Combine(Environment.CurrentDirectory, "zapret-win-bundle"),
        ];
        foreach (var path in candidates)
            if (Directory.Exists(path)) return Path.GetFullPath(path);
        return "";
    }

    private static string RunCygpath(string cygpathExe, string windowsPath)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cygpathExe,
                Arguments = $"-C OEM -a -u \"{windowsPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            }
        };
        proc.Start();
        string result = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        return result;
    }

    private async Task<JsonElement> RunAsync(JsonElement args)
    {
        lock (_lock)
        {
            if (_running) return Json(ok: false, error: "blockcheck đang chạy — chờ hoặc cancel trước");
            _running = true; _done = false; _lastError = ""; _strategies = new();
        }

        string bundle = FindBundle();
        if (bundle.Length == 0)
        {
            lock (_lock) { _running = false; _done = true; _lastError = "Không tìm thấy zapret-win-bundle. Chạy src/scripts/download_zapret_binaries.ps1 trước."; }
            _ctx?.Log.Error(_lastError);
            return Json(ok: false, error: _lastError);
        }

        string bashPath = Path.Combine(bundle, "cygwin", "bin", "bash.exe");
        string cygpathExe = Path.Combine(bundle, "cygwin", "bin", "cygpath.exe");
        // zapret2: blockcheck2.sh nằm ở blockcheck/zapret2/ (win-bundle mới)
        string blockcheckSh = Path.Combine(bundle, "blockcheck", "zapret2", "blockcheck2.sh");
        if (!File.Exists(blockcheckSh))
            blockcheckSh = Path.Combine(bundle, "blockcheck", "zapret", "blockcheck.sh"); // fallback v1
        if (!File.Exists(bashPath) || !File.Exists(blockcheckSh))
        {
            lock (_lock) { _running = false; _done = true; _lastError = $"Thiếu bash.exe hoặc blockcheck2.sh trong bundle: {bundle}"; }
            _ctx?.Log.Error(_lastError);
            return Json(ok: false, error: _lastError);
        }

        try
        {
            string cygScript = RunCygpath(cygpathExe, blockcheckSh);
            string zapretBase = cygScript.Replace("/blockcheck2.sh", "").Replace("/blockcheck.sh", "");

            string nativeWinws = Path.Combine(bundle, "zapret-winws", "winws2.exe");
            if (!File.Exists(nativeWinws)) nativeWinws = Path.Combine(bundle, "zapret-winws", "winws.exe");
            string cygWinws = RunCygpath(cygpathExe, nativeWinws);

            string domain = args.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "";
            bool useIpv4 = !args.TryGetProperty("ipv4", out var v4) || v4.GetBoolean();
            bool useIpv6 = args.TryGetProperty("ipv6", out var v6) && v6.GetBoolean();

            string cygwinBin = Path.Combine(bundle, "cygwin", "bin");
            var env = new List<string>
            {
                $"set \"PATH={cygwinBin};%PATH%\"",
                "set \"BATCH=1\"",
                "set \"CURL_CMD=1\"",
                "set \"SKIP_DNSCHECK=1\"",
                $"set \"WINWS={cygWinws}\"",
                $"set \"MDIG={zapretBase}/mdig/mdig.exe\"",
                $"set \"TPWS={zapretBase}/tpws/tpws.exe\"",
            };
            if (!string.IsNullOrEmpty(domain)) env.Add($"set \"DOMAINS={domain}\"");
            if (useIpv4 && !useIpv6) env.Add("set \"IPV=4\"");
            else if (!useIpv4 && useIpv6) env.Add("set \"IPV=6\"");

            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            foreach (var old in new DirectoryInfo(logDir).GetFiles("blockcheck_*.txt").OrderByDescending(f => f.CreationTime).Skip(4))
            { try { old.Delete(); } catch { } }

            string logFile = Path.Combine(logDir, $"blockcheck_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            string sentinel = logFile + ".running";
            File.WriteAllText(sentinel, "running");

            string batchFile = Path.Combine(Path.GetTempPath(), $"zapret_blockcheck_{Guid.NewGuid():N}.cmd");
            string batch = "@echo off\r\n" +
                string.Join("\r\n", env) + "\r\n" +
                $"\"{bashPath}\" -l -c \"'{cygScript}' 2>&1 | tee '{logFile.Replace("\\", "/")}'\" \r\n" +
                $"del \"{sentinel}\" 2>nul\r\n" +
                "exit /b %errorlevel%\r\n";
            File.WriteAllText(batchFile, batch);

            // Không UAC bên trong module: module khai requiresElevation → host sẽ spawn elevated (TODO Giai đoạn sau)
            _cmdProc = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/c \"{batchFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            lock (_lock) { _logFile = logFile; }
            _ctx?.Log.Info($"Blockcheck started: domain={domain} log={logFile}");

            _ = Task.Run(async () =>
            {
                // watcher: sentinel biến mất hoặc process chết → parse SUMMARY
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromMinutes(30))
                {
                    await Task.Delay(2000);
                    bool sentinelGone = !File.Exists(sentinel);
                    bool procGone = _cmdProc == null || _cmdProc.HasExited;
                    if (sentinelGone || procGone)
                    {
                        try { if (File.Exists(sentinel)) File.Delete(sentinel); } catch { }
                        ParseStrategies(logFile);
                        lock (_lock) { _running = false; _done = true; }
                        _ctx?.Log.Info($"Blockcheck done: {_strategies.Count} strategy(ies)");
                        return;
                    }
                }
                lock (_lock) { _running = false; _done = true; _lastError = "blockcheck timeout 30 phút"; }
            });

            return Json(ok: true, error: "", logFile: logFile);
        }
        catch (Exception ex)
        {
            lock (_lock) { _running = false; _done = true; _lastError = ex.Message; }
            _ctx?.Log.Error($"Blockcheck failed: {ex.Message}");
            return Json(ok: false, error: ex.Message);
        }
    }

    private void ParseStrategies(string logFilePath)
    {
        var found = new List<Strategy>();
        try
        {
            if (!File.Exists(logFilePath)) return;
            string content = File.ReadAllText(logFilePath);
            int summaryIdx = content.IndexOf("* SUMMARY");
            if (summaryIdx < 0) summaryIdx = 0; // blockcheck2 có thể đổi format — parse toàn file nếu thiếu SUMMARY
            foreach (var rawLine in content[summaryIdx..].Split('\n'))
            {
                string line = rawLine.Trim().TrimEnd('\r');
                // chấp nhận cả "winws" (v1) lẫn "winws2" (v2)
                int marker = line.IndexOf(": winws");
                if (marker < 0) continue;
                if (line.Contains("working without bypass") || line.Contains("test aborted")) continue;

                string args = line[(marker + 8)..].Trim();
                string testType = line[..marker].Trim();
                string label = testType.Contains("http3") ? $"[HTTP3/QUIC] {args}"
                    : testType.Contains("https") ? $"[HTTPS] {args}"
                    : $"[HTTP] {args}";
                found.Add(new Strategy(testType, args, label));
            }
        }
        catch (Exception ex) { _ctx?.Log.Warn($"parse failed: {ex.Message}"); }
        lock (_lock) _strategies = found;
    }

    private JsonElement Poll()
    {
        lock (_lock)
        {
            return JsonSerializer.SerializeToElement(new
            {
                running = _running,
                done = _done,
                error = _lastError,
                logFile = _logFile,
                strategies = _strategies.Select(s => new { type = s.Type, args = s.Args, label = s.Label }),
            });
        }
    }

    private async Task<JsonElement> CancelAsync()
    {
        Process? proc;
        lock (_lock) { proc = _cmdProc; _cmdProc = null; }
        if (proc != null)
        {
            try
            {
                bool exited; try { exited = proc.HasExited; } catch { exited = true; }
                if (!exited)
                {
                    var kill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        ArgumentList = { "/F", "/T", "/PID", proc.Id.ToString() },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    if (kill != null) await kill.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch { }
        }
        lock (_lock) { _running = false; _done = true; }
        _ctx?.Log.Info("Blockcheck cancelled.");
        return Json(ok: true, error: "");
    }

    private static JsonElement Json(bool ok, string error, string logFile = "")
    {
        return JsonSerializer.SerializeToElement(new { ok, error, logFile });
    }
}
