using System.Diagnostics;
using System.Text.Json;
using ModularFramework.HostLib;

// Probe v2: dùng ĐÚNG ModuleSupervisor — bisect xem lỗi nằm ở supervisor hay transport.

string modulesRoot = Path.GetFullPath("../../../../modules", AppContext.BaseDirectory);
string logsRoot = Path.Combine(Path.GetTempPath(), "mf-probe2-logs");
string moduleHostExe = Path.Combine(AppContext.BaseDirectory, "modulehost", "ModuleHost.exe");
Console.WriteLine($"modules={modulesRoot}");

using var sup = new ModuleSupervisor(moduleHostExe, modulesRoot, logsRoot);
sup.StateChanged += inst => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {inst.Manifest.Id}: {inst.State}" + (inst.LastError is null ? "" : $" ({inst.LastError})"));

Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] starting hello");
await sup.StartAsync("hello");
await Task.Delay(1000);

Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ping storm hello (10x, 500ms apart)...");
for (int i = 0; i < 10; i++)
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var r = await sup.CallAsync("hello", "ping", null, cts.Token);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ping {i}: OK {r}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ping {i}: FAIL {ex.GetType().Name}: {ex.Message}");
    }
    await Task.Delay(500);
}

Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] echo...");
try
{
    var e = await sup.CallAsync("hello.echo", JsonSerializer.SerializeToElement(new { text = "x" }));
    Console.WriteLine($"echo OK: {e}");
}
catch (Exception ex)
{
    Console.WriteLine($"echo FAIL: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] starting crashy + ping storm...");
await sup.StartAsync("crashy");
for (int i = 0; i < 8; i++)
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var r = await sup.CallAsync("hello", "ping", null, cts.Token);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] hello ping {i}: OK");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] hello ping {i}: FAIL {ex.GetType().Name}");
    }
    await Task.Delay(500);
}

Console.WriteLine("DONE");
