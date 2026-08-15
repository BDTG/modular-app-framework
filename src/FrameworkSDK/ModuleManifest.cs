using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModularFramework;

/// <summary>Manifest module.json — 1 module = 1 thư mục, manifest là nguồn sự thật.</summary>
public sealed class ModuleManifest
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Entry { get; set; } = "";          // tên dll entry (vd "HelloModule.dll")
    public string DisplayName { get; set; } = "";
    public bool RequiresElevation { get; set; }      // host spawn với runas nếu true
    public bool AutoStart { get; set; }
    public HealthConfig Health { get; set; } = new();
    public Dictionary<string, JsonElement> Config { get; set; } = new();

    public static ModuleManifest Load(string moduleDir)
    {
        var path = Path.Combine(moduleDir, "module.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Thiếu {path}");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var m = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(path), options)
                ?? throw new InvalidDataException($"module.json rỗng/hỏng: {path}");
        m.Directory = Path.GetFullPath(moduleDir);
        return m;
    }

    [JsonIgnore] public string Directory { get; private set; } = "";
}

public sealed class HealthConfig
{
    public int PingTimeoutSec { get; set; } = 5;
    public int[] RestartBackoffSec { get; set; } = [2, 5, 15, 60];
    public int MaxFailuresBeforeDisable { get; set; } = 3;
}
