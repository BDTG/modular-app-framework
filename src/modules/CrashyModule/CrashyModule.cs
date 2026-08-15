using System.Text.Json;
using ModularFramework;

namespace CrashyModule;

/// <summary>
/// Module DEMO chứng minh cách ly lỗi: boom/exit/hang giết CHÍNH NÓ,
/// các module khác (hello) không hề hấn — supervisor restart với backoff,
/// disable sau 3 lần fail liên tiếp.
/// </summary>
public sealed class CrashyModule : IModule, IModuleOps
{
    private IModuleContext? _ctx;

    public string Id => "crashy";

    public Task<ModuleStatus> StartAsync(IModuleContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        ctx.Log.Info("Crashy started. Ops: boom (throw), exit (Environment.Exit), hang (sleep 30s).");
        return Task.FromResult(ModuleStatus.Running);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<JsonElement> HandleOpAsync(string op, JsonElement args, CancellationToken ct)
    {
        switch (op)
        {
            case "boom":
                // FailFast = crash process THẬT (không catch được) — chứng minh cách ly
                _ctx?.Log.Error("BOOM — Environment.FailFast (crash process thật)...");
                Environment.FailFast("BOOM: fail-fast demo");
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
            case "exit":
                _ctx?.Log.Error("EXIT — Environment.Exit(1) demo");
                Environment.Exit(1);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
            case "hang":
                _ctx?.Log.Error("HANG — sleeping 30s (host sẽ ping-fail và giết)");
                Thread.Sleep(30_000);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
            default:
                throw new JsonRpcException($"crashy: unknown op {op}");
        }
    }
}
