using System.Text.Json;
using ModularFramework;

namespace HelloModule;

public sealed class HelloModule : IModule, IModuleOps
{
    private IModuleContext? _ctx;
    private CancellationTokenSource? _tickCts;
    private Task? _tickTask;

    public string Id => "hello";

    public Task<ModuleStatus> StartAsync(IModuleContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        var greeting = ctx.Config.TryGetValue("greeting", out var g) ? g.GetString() : "hello";
        ctx.Log.Info($"Started. greeting={greeting}");

        // Ví dụ dùng CallHostAsync + vòng tick chứng minh module sống độc lập
        _tickCts = new CancellationTokenSource();
        _tickTask = Task.Run(async () =>
        {
            int n = 0;
            while (!_tickCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, _tickCts.Token).ContinueWith(_ => { });
                    n++;
                    ctx.Log.Info($"tick #{n} — host op log: {await ctx.CallHostAsync("log", JsonSerializer.SerializeToElement(new { message = $"hello tick {n}" }), CancellationToken.None)}");
                }
                catch (Exception ex)
                {
                    ctx.Log.Warn($"tick error: {ex.Message}");
                }
            }
        });
        return Task.FromResult(ModuleStatus.Running);
    }

    public Task StopAsync(CancellationToken ct)
    {
        _tickCts?.Cancel();
        _ctx?.Log.Info("Stopped.");
        return Task.CompletedTask;
    }

    public async Task<JsonElement> HandleOpAsync(string op, JsonElement args, CancellationToken ct)
    {
        switch (op)
        {
            case "echo":
                return JsonSerializer.SerializeToElement(new
                {
                    echoed = args.TryGetProperty("text", out var t) ? t.GetString() : "",
                    pid = Environment.ProcessId,
                });
            default:
                throw new JsonRpcException($"hello: unknown op {op}");
        }
    }
}
