using System.Diagnostics;
using System.Text.Json;
using ModularFramework;

namespace ProfilesModule;

/// <summary>
/// Module quản lý DomainProfile (domain + mạng + chiến lược) — port từ ProfileManager
/// của wrapper, sửa: lưu %LOCALAPPDATA% (không BaseDirectory), atomic write + lock,
/// GetCurrentNetworkName giữ nguyên (netsh) — lưu ý: winws2 đã có --ssid-filter native,
/// module này sẽ được thay dần bằng sinh multi-instance config (xem docs).
/// Ops: list, all, save, delete, network.
/// </summary>
public sealed class ProfilesModule : IModule, IModuleOps
{
    private IModuleContext? _ctx;
    private string _dataDir = "";
    private string DataFile => Path.Combine(_dataDir, "profiles.json");
    private readonly object _lock = new();

    public string Id => "profiles";

    public Task<ModuleStatus> StartAsync(IModuleContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        var configured = ctx.Config.TryGetValue("dataDir", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() : "";
        if (string.IsNullOrWhiteSpace(configured))
            configured = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModularFramework");
        _dataDir = Environment.ExpandEnvironmentVariables(configured);
        Directory.CreateDirectory(_dataDir);
        ctx.Log.Info($"Profiles ready. dataFile={DataFile}");
        return Task.FromResult(ModuleStatus.Running);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<JsonElement> HandleOpAsync(string op, JsonElement args, CancellationToken ct) => op switch
    {
        "list" => Task.FromResult(List(args)),
        "all" => Task.FromResult(ListAll()),
        "save" => Task.FromResult(Save(args)),
        "delete" => Task.FromResult(Delete(args)),
        "network" => Task.FromResult(JsonSerializer.SerializeToElement(new { name = GetCurrentNetworkName() })),
        _ => throw new JsonRpcException($"profiles: unknown op {op}"),
    };

    private List<Profile> Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(DataFile)) return new List<Profile>();
                return JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(DataFile)) ?? new List<Profile>();
            }
            catch (Exception ex)
            {
                _ctx?.Log.Warn($"load failed: {ex.Message}");
                return new List<Profile>();
            }
        }
    }

    private void SaveAll(List<Profile> profiles)
    {
        lock (_lock)
        {
            // atomic write: temp + move
            string tmp = DataFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, DataFile, overwrite: true);
        }
    }

    private JsonElement List(JsonElement args)
    {
        string network = args.TryGetProperty("network", out var n) ? n.GetString() ?? "" : "";
        var items = Load()
            .Where(p => p.NetworkName.Equals(network, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Domain)
            .ToList();
        return JsonSerializer.SerializeToElement(items);
    }

    private JsonElement ListAll()
    {
        return JsonSerializer.SerializeToElement(Load().OrderBy(p => p.NetworkName).ThenBy(p => p.Domain));
    }

    private JsonElement Save(JsonElement args)
    {
        try
        {
            var profile = new Profile
            {
                Domain = args.TryGetProperty("domain", out var dom) ? dom.GetString() ?? "" : "",
                NetworkName = args.TryGetProperty("networkName", out var net) ? net.GetString() ?? "" : "",
                Strategy = args.TryGetProperty("strategy", out var st) ? st.GetString() ?? "" : "",
                RawArgs = args.TryGetProperty("rawArgs", out var ra) ? ra.GetString() ?? "" : "",
                ScannedAt = args.TryGetProperty("scannedAt", out var ts) && ts.TryGetDateTime(out var dt) ? dt : DateTime.Now,
            };
            var all = Load();
            all.RemoveAll(p => p.Domain.Equals(profile.Domain, StringComparison.OrdinalIgnoreCase)
                            && p.NetworkName.Equals(profile.NetworkName, StringComparison.OrdinalIgnoreCase));
            all.Add(profile);
            SaveAll(all);
            _ctx?.Log.Info($"Saved profile: {profile.Domain} @ {profile.NetworkName}");
            return JsonSerializer.SerializeToElement(new { ok = true });
        }
        catch (Exception ex)
        {
            _ctx?.Log.Error($"save failed: {ex.Message}");
            return JsonSerializer.SerializeToElement(new { ok = false, error = ex.Message });
        }
    }

    private JsonElement Delete(JsonElement args)
    {
        string domain = args.TryGetProperty("domain", out var dom) ? dom.GetString() ?? "" : "";
        string network = args.TryGetProperty("networkName", out var net) ? net.GetString() ?? "" : "";
        var all = Load();
        all.RemoveAll(p => p.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)
                        && p.NetworkName.Equals(network, StringComparison.OrdinalIgnoreCase));
        SaveAll(all);
        _ctx?.Log.Info($"Deleted profile: {domain} @ {network}");
        return JsonSerializer.SerializeToElement(new { ok = true });
    }

    /// <summary>SSID WiFi hiện tại qua netsh (giữ nguyên từ wrapper).</summary>
    public static string GetCurrentNetworkName()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "wlan show interfaces",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                }
            };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            foreach (var line in output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("SSID") && !trimmed.StartsWith("BSSID"))
                {
                    int colon = trimmed.IndexOf(':');
                    if (colon >= 0)
                    {
                        string ssid = trimmed[(colon + 1)..].Trim();
                        if (!string.IsNullOrEmpty(ssid)) return ssid;
                    }
                }
            }
        }
        catch { }
        return "Ethernet";
    }

    public sealed class Profile
    {
        public string Domain { get; set; } = "";
        public string NetworkName { get; set; } = "";
        public string Strategy { get; set; } = "";
        public string RawArgs { get; set; } = "";
        public DateTime ScannedAt { get; set; } = DateTime.Now;
    }
}
