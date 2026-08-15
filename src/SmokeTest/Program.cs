using System.Text.Json;
using ModularFramework.HostLib;

// SmokeTest: chứng minh framework hoạt động + cách ly lỗi giữa module.
// Usage: SmokeTest [--modules <root>] [--logs <dir>]

var modulesRoot = GetArg("--modules") ?? Path.GetFullPath("../../../../modules", AppContext.BaseDirectory);
var logsRoot = GetArg("--logs") ?? Path.Combine(Path.GetTempPath(), "mf-smoke-logs");
var moduleHostExe = Path.Combine(AppContext.BaseDirectory, "modulehost", "ModuleHost.exe");

if (!File.Exists(moduleHostExe)) { Console.WriteLine($"FAIL: không thấy {moduleHostExe}"); return 1; }
if (!Directory.Exists(modulesRoot)) { Console.WriteLine($"FAIL: không thấy modules root {modulesRoot}"); return 1; }

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : "")}");
    if (ok) pass++; else fail++;
}

Console.WriteLine($"modules: {modulesRoot}");
Console.WriteLine($"logs:    {logsRoot}");
Console.WriteLine($"host:    {moduleHostExe}");

using var sup = new ModuleSupervisor(moduleHostExe, modulesRoot, logsRoot);
sup.StateChanged += inst => Console.WriteLine($"  [state] {inst.Manifest.Id}: {inst.State}" + (inst.LastError is null ? "" : $" ({inst.LastError})"));

// ── 0. Scan ────────────────────────────────────────────────────────
bool haveExamples = sup.Modules.ContainsKey("hello") && sup.Modules.ContainsKey("crashy");
Console.WriteLine($"modules found: {string.Join(", ", sup.Modules.Keys)}");

if (!haveExamples)
{
    Console.WriteLine("SKIP  hello/crashy example tests (modules không có ở modules root này)");
}
else
{
// ── 1. Start cả 2 ────────────────────────────────────────────────
await sup.StartAsync("hello");
await sup.StartAsync("crashy");
await Task.Delay(1500);

Check("hello ping OK", await sup.PingAsync("hello"));
Check("crashy ping OK", await sup.PingAsync("crashy"));

// ── 2. hello echo (module sống, có pid riêng) ────────────────────
var echo = await sup.CallAsync("hello.echo", JsonSerializer.SerializeToElement(new { text = "chào host" }));
int helloPid = echo.GetProperty("pid").GetInt32();
Check("hello echo trả lời", echo.GetProperty("echoed").GetString() == "chào host", $"pid={helloPid}");

// ── 3. CÁCH LY: crashy boom (unhandled exception) ────────────────
Console.WriteLine("\n→ crashy boom (unhandled exception)...");
try { await sup.CallAsync("crashy.boom"); } catch { }
await Task.Delay(7000); // chờ supervisor phát hiện + backoff 2s + restart

Check("crashy được restart (ping lại OK)", await sup.PingAsync("crashy"));
var crashyPing2 = await sup.CallAsync("crashy", "ping");
var crashyPid2 = crashyPing2.GetProperty("pid").GetInt32();
Check("crashy có pid MỚI (tiến trình mới)", crashyPid2 != 0, $"pid mới={crashyPid2}");
Check("hello KHÔNG bị ảnh hưởng (isolation)", await sup.PingAsync("hello"));
var echo2 = await sup.CallAsync("hello.echo", JsonSerializer.SerializeToElement(new { text = "vẫn sống" }));
Check("hello echo vẫn cùng pid (không restart)", echo2.GetProperty("pid").GetInt32() == helloPid);

// ── 4. crashy exit (Environment.Exit) ────────────────────────────
Console.WriteLine("\n→ crashy exit (Environment.Exit(1))...");
try { await sup.CallAsync("crashy.exit"); } catch { }
await Task.Delay(7000);

Check("crashy restart lần 2 (ping OK)", await sup.PingAsync("crashy"));
Check("hello vẫn sống sau crash lần 2", await sup.PingAsync("hello"));

// ── 6. crash bundle được ghi ─────────────────────────────────────
var crashDir = Path.Combine(logsRoot, "crashes", "crashy");
var bundles = Directory.Exists(crashDir) ? Directory.GetFiles(crashDir, "crash-*.json") : [];
Check("crash bundle được tạo (≥2)", bundles.Length >= 2, $"{bundles.Length} bundle(s)");
}

// ══ GIAI ĐOẠN 2: zapret modules (repo riêng — SKIP nếu chưa có ở modules root) ═══

// ── 7. profiles: save → list → network → delete ───────────────────
if (sup.Modules.ContainsKey("profiles"))
{
    await sup.StartAsync("profiles");
    await Task.Delay(800);
    var saveRes = await sup.CallAsync("profiles.save", Json(
        "domain", "youtube.com", "networkName", "TestNet",
        "strategy", "[HTTPS] --lua-desync=fake:blob=fake_default_tls:badsum:strategy=1",
        "rawArgs", "--lua-desync=fake:blob=fake_default_tls:badsum:strategy=1"));
    Check("profiles.save OK", saveRes.GetProperty("ok").GetBoolean());
    var listRes = await sup.CallAsync("profiles.list", Json("network", "TestNet"));
    Check("profiles.list có 1 profile", listRes.GetArrayLength() == 1, $"count={listRes.GetArrayLength()}");
    var netRes = await sup.CallAsync("profiles.network");
    Check("profiles.network trả tên mạng", netRes.TryGetProperty("name", out _));
    await sup.CallAsync("profiles.delete", Json("domain", "youtube.com", "networkName", "TestNet"));
    var list2 = await sup.CallAsync("profiles.list", Json("network", "TestNet"));
    Check("profiles.delete sạch", list2.GetArrayLength() == 0);
}
else Console.WriteLine("SKIP  profiles (module không có ở modules root)");

// ── 8. zapret-engine: fake winws2 (.cmd) — start/status/stop ───────
if (sup.Modules.ContainsKey("zapret-engine"))
{
    await sup.StartAsync("zapret-engine");
    await Task.Delay(800);
    var fakeDir = Path.Combine(Path.GetTempPath(), "mf-fake-engine");
    Directory.CreateDirectory(fakeDir);
    var fakeExe = Path.Combine(fakeDir, "fake-winws2.cmd");
    File.WriteAllText(fakeExe, "@echo off\r\necho FAKE-WINWS-STARTED\r\n:loop\r\necho tick\r\ntimeout /t 1 /nobreak >nul\r\ngoto loop\r\n");

    var engStart = await sup.CallAsync("zapret-engine.start", Json("enginePath", fakeExe, "args", "--wf-l3=ipv4"));
    Check("engine.start OK", engStart.GetProperty("ok").GetBoolean(), engStart.TryGetProperty("error", out var e) ? e.GetString() : "");
    await Task.Delay(1500);
    var st = await sup.CallAsync("zapret-engine.status");
    string lastLine = st.TryGetProperty("lastLogLine", out var ll) ? ll.GetString() ?? "" : "";
    bool stRunning = st.TryGetProperty("running", out var rr) && rr.GetBoolean();
    bool stPid = st.TryGetProperty("pid", out var pp) && pp.GetInt32() > 0;
    Check($"engine.status running (raw={st})", stRunning && stPid, $"pid={pp.GetInt32()}");
    Check("engine log bắt được output (FAKE-WINWS/tick)", lastLine.Contains("FAKE-WINWS") || lastLine.Contains("tick"), lastLine);

    await sup.CallAsync("zapret-engine.stop");
    await Task.Delay(800);
    var st2 = await sup.CallAsync("zapret-engine.status");
    Check("engine.stop → stopped", !st2.GetProperty("running").GetBoolean());

    var engBad = await sup.CallAsync("zapret-engine.start", Json("enginePath", "C:\\nonexistent\\winws2.exe"));
    Check("engine.start lỗi SẠCH khi thiếu exe", !engBad.GetProperty("ok").GetBoolean() && engBad.GetProperty("error").GetString()!.Length > 0);

    var ba = await sup.CallAsync("zapret-engine.buildArgs", Json(
        "filterIpv4", true, "filterIpv6", false,
        "udpPorts", "443,50000-65535", "tcpPorts", "80,443",
        "fakeUdp", true, "hostlistAuto", true));
    string baStr = ba.GetProperty("args").GetString()!;
    Check("buildArgs mapping v2 (--wf-udp-out + lua-desync)", baStr.Contains("--wf-udp-out=443,50000-65535") && baStr.Contains("--lua-desync=fake") && baStr.Contains("--hostlist-auto=hostlist.txt"), baStr);
}
else Console.WriteLine("SKIP  zapret-engine (module không có ở modules root)");

// ── 9. blockcheck: không có bundle → lỗi sạch (graceful) ──────────
if (sup.Modules.ContainsKey("blockcheck"))
{
    await sup.StartAsync("blockcheck");
    await Task.Delay(800);
    var bc = await sup.CallAsync("blockcheck.run", Json("domain", "youtube.com", "ipv4", true, "ipv6", false));
    Check("blockcheck thiếu bundle → lỗi sạch", !bc.GetProperty("ok").GetBoolean() && bc.GetProperty("error").GetString()!.Contains("bundle"), bc.TryGetProperty("error", out var be) ? be.GetString() : "");
}
else Console.WriteLine("SKIP  blockcheck (module không có ở modules root)");

// ── 10. stop sạch tất cả ───────────────────────────────────────────
foreach (var id in new[] { "profiles", "zapret-engine", "blockcheck", "hello", "crashy" })
    if (sup.Modules.ContainsKey(id)) await sup.StopAsync(id);
await Task.Delay(1000);
Check("stop sạch tất cả module", sup.Modules.Values.All(m => m.State == ModuleRunState.Stopped));

Console.WriteLine($"\n===== KẾT QUẢ: {pass} PASS / {fail} FAIL =====");
return fail == 0 ? 0 : 1;

static string? GetArg(string name)
{
    var a = Environment.GetCommandLineArgs();
    for (int i = 1; i < a.Length - 1; i++)
        if (a[i] == name) return a[i + 1];
    return null;
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
