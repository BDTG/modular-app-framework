using System.Reflection;
using System.Text.Json;
using ModularFramework;

// ModuleHost.exe --module <dir> --pipe <pipeName> --logs <logsDir>
// Tiến trình generic: load 1 module (manifest + entry dll), serve JSON-RPC qua named pipe.

var cliArgs = ParseArgs(Environment.GetCommandLineArgs());
string moduleDir = cliArgs["--module"];
string pipeName = cliArgs["--pipe"];
string logsDir = cliArgs.GetValueOrDefault("--logs", Path.Combine(Path.GetTempPath(), "mf-logs"));

try
{
    var manifest = ModuleManifest.Load(moduleDir);
    Directory.CreateDirectory(logsDir);
    var logFile = Path.Combine(logsDir, $"{manifest.Id}.log");
    var log = new FileLog(logFile);

    log.Info($"ModuleHost up: {manifest.Id} v{manifest.Version} entry={manifest.Entry} pid={Environment.ProcessId}");

    // Load entry assembly + tìm IModule
    var asm = Assembly.LoadFrom(Path.Combine(moduleDir, manifest.Entry));
    var allTypes = asm.GetTypes();
    Console.Error.WriteLine($"[debug] types in {manifest.Entry}: {string.Join(", ", allTypes.Select(t => t.FullName + "→" + string.Join("|", t.GetInterfaces().Select(i => i.FullName ?? "?"))))}");
    var moduleType = allTypes.FirstOrDefault(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract)
        ?? throw new InvalidDataException($"Không tìm thấy IModule trong {manifest.Entry}");
    var module = (IModule)Activator.CreateInstance(moduleType)!;

    var ctx = new ModuleContext(manifest, logFile, log);
    var moduleOps = module as IModuleOps;

    // Chờ host kết nối (timeout 30s)
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var channel = await JsonRpcChannel.WaitForHostAsync(pipeName, cts.Token);
    log.Info("Host connected.");

    var state = ModuleStatus.Stopped;
    var startCts = new CancellationTokenSource();

    channel.OnRequest = async (method, prms, ct) =>
    {
        switch (method)
        {
            case "ping":
                return Json("pong", true, "id", manifest.Id, "version", manifest.Version, "pid", Environment.ProcessId, "state", state.ToString());
            case "start":
                if (state == ModuleStatus.Running) return Json("ok", true, "state", "already_running");
                state = ModuleStatus.Starting;
                startCts = new CancellationTokenSource();
                await module.StartAsync(ctx, startCts.Token);
                state = ModuleStatus.Running;
                log.Info("Module started.");
                return Json("ok", true);
            case "stop":
                state = ModuleStatus.Stopping;
                await module.StopAsync(startCts.Token);
                state = ModuleStatus.Stopped;
                log.Info("Module stopped.");
                return Json("ok", true);
            case "status":
                return Json("state", state.ToString(), "pid", Environment.ProcessId, "version", manifest.Version);
            default:
                if (moduleOps != null)
                {
                    // Op của module phải có prefix "<moduleId>." để không đụng core ops (ping/start/stop/status)
                    var prefix = manifest.Id + ".";
                    if (method.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return await moduleOps.HandleOpAsync(method[prefix.Length..], prms, ct);
                }
                throw new JsonRpcException($"unknown op: {method}");
        }
    };

    await channel.ReadLoopAsync(CancellationToken.None);
    log.Info("Pipe closed — exiting.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Timed out waiting for host connection");
    return 3;
}
catch (Exception ex)
{
    // Ghi fatal ra file (stderr của ModuleHost thường bị host spawn không redirect → mất)
    try
    {
        Directory.CreateDirectory(logsDir);
        File.AppendAllText(Path.Combine(logsDir, "modulehost-fatal.log"),
            $"{DateTime.Now:O} fatal: {ex}\n");
    }
    catch { }
    Console.Error.WriteLine($"ModuleHost fatal: {ex}");
    return 2;
}

static Dictionary<string, string> ParseArgs(string[] argv)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 1; i < argv.Length; i += 2)
        if (argv[i].StartsWith("--") && i + 1 < argv.Length)
            d[argv[i]] = argv[i + 1];
    return d;
}

static JsonElement Json(params object[] kv)
{
    using var ms = new MemoryStream();
    using (var w = new Utf8JsonWriter(ms))
    {
        w.WriteStartObject();
        for (int i = 0; i < kv.Length; i += 2)
        {
            var key = (string)kv[i];
            var val = kv[i + 1];
            switch (val)
            {
                case string s: w.WriteString(key, s); break;
                case bool b: w.WriteBoolean(key, b); break;
                case int n: w.WriteNumber(key, n); break;
                case long n: w.WriteNumber(key, n); break;
                default: w.WriteString(key, val.ToString()); break;
            }
        }
        w.WriteEndObject();
    }
    return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
}

sealed class ModuleContext : IModuleContext
{
    public IModuleLog Log { get; }
    public IReadOnlyDictionary<string, JsonElement> Config { get; }
    public string ModuleDirectory { get; }
    private readonly string _logFile;

    public ModuleContext(ModuleManifest manifest, string logFile, FileLog log)
    {
        ModuleDirectory = manifest.Directory;
        Config = manifest.Config;
        _logFile = logFile;
        Log = new ContextLog(log, this);
    }

    public async Task<JsonElement> CallHostAsync(string op, JsonElement args, CancellationToken ct)
    {
        // Ở scaffold ModuleHost không giữ reference tới channel ở đây — op "log" ghi thẳng file.
        if (op == "log")
        {
            var line = args.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            File.AppendAllText(_logFile, JsonSerializer.Serialize(new { ts = DateTime.Now, op = "hostlog", message = line }) + "\n");
            return JsonSerializer.SerializeToElement(new { ok = true });
        }
        throw new JsonRpcException($"host op not supported in scaffold: {op}");
    }
}

sealed class ContextLog : IModuleLog
{
    private readonly FileLog _file;
    private readonly ModuleContext _ctx;
    public ContextLog(FileLog file, ModuleContext ctx) { _file = file; _ctx = ctx; }
    public void Info(string m) => _file.Write("info", m);
    public void Warn(string m) => _file.Write("warn", m);
    public void Error(string m) => _file.Write("error", m);
}

sealed class FileLog
{
    private readonly string _path;
    private readonly object _lock = new();
    public FileLog(string path) => _path = path;
    public void Write(string level, string message)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_path,
                    JsonSerializer.Serialize(new { ts = DateTime.Now.ToString("O"), level, msg = message, pid = Environment.ProcessId }) + "\n");
            }
            catch { }
        }
    }
    public void Info(string m) => Write("info", m);
}
