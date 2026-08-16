using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ModularFramework.HostLib;

namespace AppHost;

public partial class MainWindow : Window
{
    private ModuleSupervisor? _sup;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _logTimer;
    private string? _selectedId;
    private string _lastLogTail = "";

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => ModuleGrid.Items.Refresh();
        _refreshTimer.Start();

        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _logTimer.Tick += (_, _) => RefreshLog();
        _logTimer.Start();

        Loaded += (_, _) =>
        {
            try
            {
                var sup = EnsureSupervisor();
                ModuleGrid.ItemsSource = sup.Modules.Values.ToList();
                StatusText.Text = $"Đã scan {sup.Modules.Count} module — bấm '▶ Start tất cả' để chạy";
                LoadNode();
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        };
    }

    private ModuleSupervisor EnsureSupervisor()
    {
        if (_sup != null) return _sup;
        var modulesRoot = ModulesRootBox.Text.Trim();
        var logsRoot = Path.Combine(Path.GetTempPath(), "mf-apphost-logs");
        var moduleHostExe = Path.Combine(AppContext.BaseDirectory, "modulehost", "ModuleHost.exe");
        if (!File.Exists(moduleHostExe))
            throw new InvalidOperationException($"Thiếu ModuleHost.exe: {moduleHostExe} — build lại HostLib.");
        _sup = new ModuleSupervisor(moduleHostExe, modulesRoot, logsRoot);
        _sup.StateChanged += inst =>
            Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = $"{inst.Manifest.Id}: {inst.State}" + (inst.LastError is null ? "" : $" — {inst.LastError}");
                if (_selectedId == inst.Manifest.Id) RefreshLog();
            });
        ModuleGrid.ItemsSource = _sup.Modules.Values.ToList();
        return _sup;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _sup?.Dispose();
        _sup = null;
        EnsureSupervisor();
        ModuleGrid.ItemsSource = _sup.Modules.Values.ToList();
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sup = EnsureSupervisor();
            foreach (var id in sup.Modules.Keys.ToList())
                await sup.StartAsync(id);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Lỗi"); }
    }

    private async void StopAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sup == null) return;
            foreach (var id in _sup.Modules.Keys.ToList())
                await _sup.StopAsync(id);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Lỗi"); }
    }

    private void ModuleGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ModuleGrid.SelectedItem is ModuleInstance inst)
        {
            _selectedId = inst.Manifest.Id;
            LogTitle.Text = $"Log: {inst.Manifest.Id} ({inst.LogFile})";
            RefreshLog();
        }
    }

    private void RefreshLog()
    {
        if (_selectedId == null || _sup == null || !_sup.Modules.TryGetValue(_selectedId, out var inst)) return;
        try
        {
            if (!File.Exists(inst.LogFile)) { LogBox.Text = "(chưa có log)"; return; }
            var tail = string.Join("\n", File.ReadLines(inst.LogFile).TakeLast(200));
            if (tail != _lastLogTail)
            {
                _lastLogTail = tail;
                LogBox.Text = tail;
                LogBox.ScrollToEnd();
            }
        }
        catch { }
    }

    private async void Boom_Click(object sender, RoutedEventArgs e) => await DemoOp("boom");
    private async void Exit_Click(object sender, RoutedEventArgs e) => await DemoOp("exit");
    private async void Hang_Click(object sender, RoutedEventArgs e) => await DemoOp("hang");

    // ── 🛡️ Chống DPI 2 lớp ─────────────────────────────────────────
    private static string NodeFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mf-profiles", "node.json");

    private void LoadNode()
    {
        try
        {
            if (!File.Exists(NodeFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(NodeFile));
            var r = doc.RootElement;
            NodeTypeBox.SelectedIndex = r.TryGetProperty("type", out var t) && t.GetString() == "hysteria2" ? 1 : 0;
            if (r.TryGetProperty("server", out var s)) NodeServerBox.Text = s.GetString() ?? "";
            if (r.TryGetProperty("port", out var p)) NodePortBox.Text = p.GetInt32().ToString();
            if (r.TryGetProperty("secret", out var sec)) NodeSecretBox.Text = sec.GetString() ?? "";
            if (r.TryGetProperty("sni", out var sni)) NodeSniBox.Text = sni.GetString() ?? "";
            if (r.TryGetProperty("publicKey", out var pk)) NodeKeyBox.Text = pk.GetString() ?? "";
            if (r.TryGetProperty("shortId", out var sid)) NodeShortIdBox.Text = sid.GetString() ?? "";
        }
        catch { }
    }

    private void SaveNode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NodeFile)!);
            var node = new
            {
                type = NodeTypeBox.SelectedIndex == 1 ? "hysteria2" : "vless-reality",
                server = NodeServerBox.Text.Trim(),
                port = int.TryParse(NodePortBox.Text.Trim(), out var pt) ? pt : 443,
                secret = NodeSecretBox.Text.Trim(),
                sni = NodeSniBox.Text.Trim(),
                publicKey = NodeKeyBox.Text.Trim(),
                shortId = NodeShortIdBox.Text.Trim(),
            };
            File.WriteAllText(NodeFile, JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true }));
            StatusText.Text = "✅ Node đã lưu";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Lỗi lưu node"); }
    }

    private async void TwoLayerOn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sup = EnsureSupervisor();
            if (!sup.Modules.ContainsKey("zapret-engine") || !sup.Modules.ContainsKey("proxy-client"))
            {
                MessageBox.Show("Cần module zapret-engine + proxy-client trong modules root", "Lỗi");
                return;
            }
            // Lớp 1: zapret-engine (enginePath từ config module — không cần args)
            if (sup.Modules["zapret-engine"].State != ModuleRunState.Running)
                await sup.StartAsync("zapret-engine");
            var eng = await sup.CallAsync("zapret-engine.start");
            StatusText.Text = eng.GetProperty("ok").GetBoolean()
                ? "🧪 Lớp 1 engine: OK — đang bật sing-box..."
                : $"🧪 Lớp 1 lỗi: {eng.GetProperty("error").GetString()}";

            // Lớp 2: proxy-client với node hiện tại (start module TRƯỚC khi gọi op)
            if (sup.Modules["proxy-client"].State != ModuleRunState.Running)
                await sup.StartAsync("proxy-client");
            string type = NodeTypeBox.SelectedIndex == 1 ? "hysteria2" : "vless-reality";
            var args = new Dictionary<string, object>
            {
                ["type"] = type,
                ["server"] = NodeServerBox.Text.Trim(),
                ["port"] = int.TryParse(NodePortBox.Text.Trim(), out var pt) ? pt : 443,
                ["sni"] = NodeSniBox.Text.Trim(),
            };
            if (type == "vless-reality")
            {
                args["uuid"] = NodeSecretBox.Text.Trim();
                args["publicKey"] = NodeKeyBox.Text.Trim();
                args["shortId"] = NodeShortIdBox.Text.Trim();
            }
            else
            {
                args["password"] = NodeSecretBox.Text.Trim();
            }

            var cfg = await sup.CallAsync("proxy-client.buildConfig", JsonSerializer.SerializeToElement(args));
            if (!cfg.GetProperty("ok").GetBoolean())
            {
                StatusText.Text = $"🛡️ buildConfig lỗi: {cfg.GetProperty("error").GetString()}";
                return;
            }
            var prx = await sup.CallAsync("proxy-client.start",
                JsonSerializer.SerializeToElement(new { config = cfg.GetProperty("args").GetString() }));
            StatusText.Text = prx.GetProperty("ok").GetBoolean()
                ? "🚀 2 lớp ĐÃ BẬT (engine + sing-box) — xem log module để theo dõi"
                : $"🛡️ sing-box lỗi: {prx.GetProperty("error").GetString()}";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Lỗi 2 lớp"); }
    }

    private async void TwoLayerOff_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sup = EnsureSupervisor();
            if (sup.Modules.ContainsKey("proxy-client")) await sup.CallAsync("proxy-client.stop");
            if (sup.Modules.ContainsKey("zapret-engine")) await sup.CallAsync("zapret-engine.stop");
            StatusText.Text = "⏹ Đã tắt 2 lớp";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Lỗi tắt"); }
    }

    private async Task DemoOp(string op)
    {
        try
        {
            var sup = EnsureSupervisor();
            var result = await sup.CallAsync("crashy." + op, JsonSerializer.SerializeToElement(new { }));
            StatusText.Text = $"crashy {op} → {result}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"crashy {op} → {ex.GetType().Name} (module đã chết, supervisor đang restart...)";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _sup?.Dispose();
        base.OnClosed(e);
    }
}
