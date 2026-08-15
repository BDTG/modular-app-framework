using System.Text.Json;

namespace ModularFramework;

/// <summary>Hợp đồng module — thứ DUY NHẤT module phải implement.</summary>
public interface IModule
{
    string Id { get; }
    Task<ModuleStatus> StartAsync(IModuleContext ctx, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

/// <summary>3 thứ duy nhất module được phép dùng: log, gọi host, config.</summary>
public interface IModuleContext
{
    IModuleLog Log { get; }
    Task<JsonElement> CallHostAsync(string op, JsonElement args, CancellationToken ct);
    IReadOnlyDictionary<string, JsonElement> Config { get; }
    string ModuleDirectory { get; }
}

public interface IModuleLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

public enum ModuleStatus
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted,
}

/// <summary>Ops tùy biến module expose cho host (boom/exit/echo...).</summary>
public interface IModuleOps
{
    Task<JsonElement> HandleOpAsync(string op, JsonElement args, CancellationToken ct);
}
