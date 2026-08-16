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
    private bool _logPaused;

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) =>
        {
            ModuleGrid.Items.Refresh();
            UpdateStats();
        };
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

    private void UpdateStats()
    {
        if (_sup == null) return;
        var all = _sup.Modules.Values.ToList();
        StatTotal.Text = all.Count.ToString();
        StatRunning.Text = all.Count(m => m.State == ModuleRunState.Running).ToString();
        StatStopped.Text = all.Count(m => m.State == ModuleRunState.Stopped).ToString();
        StatDisabled.Text = all.Count(m => m.State == ModuleRunState.Disabled).ToString();
    }

    private async void ModuleStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (((FrameworkElement)sender).DataContext is not ModuleInstance inst) return;
            var sup = EnsureSupervisor();
            await sup.StartAsync(inst.Manifest.Id);
            StatusText.Text = $"▶ {inst.Manifest.Id}: {sup.Modules[inst.Manifest.Id].State}";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Lỗi start"); }
    }

    private async void ModuleStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (((FrameworkElement)sender).DataContext is not ModuleInstance inst) return;
            var sup = EnsureSupervisor();
            await sup.StopAsync(inst.Manifest.Id);
            StatusText.Text = $"■ {inst.Manifest.Id}: stopped";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Lỗi stop"); }
    }

    private void PauseLog_Click(object sender, RoutedEventArgs e)
    {
        _logPaused = !_logPaused;
        PauseLogBtn.Content = _logPaused ? "▶ Resume" : "⏸ Pause";
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        _lastLogTail = "";
    }

    private void ModuleGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ModuleGrid.SelectedItem is ModuleInstance inst)
        {
            _selectedId = inst.Manifest.Id;
            LogTitle.Text = $"Log — {inst.Manifest.Id}";
            LogPath.Text = inst.LogFile;
            RefreshLog();
            UpdateOps();
        }
    }

    private void RefreshLog()
    {
        if (_logPaused || _selectedId == null || _sup == null || !_sup.Modules.TryGetValue(_selectedId, out var inst)) return;
        try
        {
            if (!File.Exists(inst.LogFile)) { LogBox.Text = "(chưa có log)"; return; }
            var tail = string.Join("\n", File.ReadLines(inst.LogFile).TakeLast(500));
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

    // ── Module ops explorer ─────────────────────────────────────────
    private static readonly Dictionary<string, Dictionary<string, string>> ModuleOps = new()
    {
        ["tweaks"] = new()
        {
            ["list"] = "Danh sách tweaks (9 network + 3 system)",
            ["apply"] = "Áp tweak — {\"index\": 0}",
            ["applyGroup"] = "Áp nhóm — {\"group\": \"network\" | \"system\"}",
            ["status"] = "Trạng thái (applied?) — {\"index\": 0}",
            ["rollback"] = "Khôi phục giá trị cũ — {\"index\": 0}",
        },
        ["startup-manager"] = new()
        {
            ["list"] = "Liệt kê startup (Run keys + folders)",
            ["set"] = "Bật/tắt — {\"index\": 0, \"enabled\": false}",
        },
        ["appx-manager"] = new()
        {
            ["list"] = "Trạng thái 17 nhóm UWP packages",
            ["remove"] = "Gỡ package (cần admin) — {\"pattern\": \"Microsoft.Clipchamp*\"}",
            ["clearCache"] = "Reset-AppxPackage — {\"pattern\": \"...\"}",
        },
        ["system-cleanup"] = new()
        {
            ["scan"] = "Dung lượng từng target (temp/prefetch/bin)",
            ["clean"] = "Dọn file tạm (trả MB đã giải phóng)",
            ["emptyBin"] = "Dọn thùng rác",
        },
        ["blockcheck"] = new()
        {
            ["run"] = "Quét DPI strategy — {\"domain\": \"tiktok.com\", \"ipv4\": true, \"ipv6\": false}",
            ["poll"] = "Trạng thái quét (running? strategies?)",
            ["cancel"] = "Hủy quét đang chạy",
        },
        ["zapret-engine"] = new()
        {
            ["start"] = "Bật winws2 (dùng enginePath trong config)",
            ["stop"] = "Tắt engine",
            ["status"] = "Trạng thái engine + dòng log cuối",
        },
        ["proxy-client"] = new()
        {
            ["buildConfig"] = "Sinh config sing-box — {\"type\":\"vless-reality\",\"server\":\"1.2.3.4\",\"port\":443,\"uuid\":\"...\",\"sni\":\"...\",\"publicKey\":\"...\",\"shortId\":\"...\"}",
            ["check"] = "Validate config bằng sing-box — {\"config\": \"<json string>\"}",
            ["start"] = "Start sing-box — {\"config\": \"<json string>\"}",
            ["stop"] = "Stop sing-box",
            ["status"] = "Trạng thái proxy",
        },
        ["profiles"] = new()
        {
            ["list"] = "Danh sách profile",
            ["apply"] = "Áp profile theo WiFi/domain hiện tại",
        },
        ["game-boost"] = new()
        {
            ["boost"] = "Stop services (+kill explorer max) — {\"mode\": \"normal\" | \"max\"}",
            ["restore"] = "Khôi phục services + explorer",
            ["status"] = "Trạng thái boost hiện tại",
        },
        ["components-remover"] = new()
        {
            ["list"] = "5 scripts (Edge/OneDrive/Defender/...)",
            ["run"] = "Chạy script (cần admin) — {\"id\": \"RemovePCHealthCheck\"}",
        },
        ["android-tools"] = new()
        {
            ["devices"] = "Danh sách thiết bị ADB",
            ["shell"] = "Lệnh shell — {\"cmd\": \"getprop ro.product.model\"}",
            ["killServer"] = "adb kill-server",
        },
        ["windows-activation"] = new()
        {
            ["status"] = "Trạng thái kích hoạt (WMI)",
            ["edition"] = "Thông tin edition/build",
            ["listMethods"] = "4 phương thức MAS (hwid/ohook/kms/tsforge)",
            ["activate"] = "Chạy MAS script (cần admin) — {\"method\": \"hwid\"}",
            ["traces"] = "Dò dấu vết activation (KMS task/folder/sppc.dll)",
            ["cleanKms"] = "Dọn dấu vết KMS — {\"dryRun\": true}",
        },
        ["hello"] = new()
        {
            ["echo"] = "Echo lại text — {\"text\": \"xin chao\"}",
        },
        ["crashy"] = new()
        {
            ["boom"] = "Ném exception (demo cách ly)",
            ["exit"] = "Environment.Exit (demo)",
            ["hang"] = "Treo 30s (demo)",
        },
    };

    private void UpdateOps()
    {
        if (_selectedId == null) { OpsList.ItemsSource = null; return; }
        if (ModuleOps.TryGetValue(_selectedId, out var ops))
        {
            OpsTitle.Text = $"Ops — {_selectedId}";
            OpsList.ItemsSource = ops.Keys.ToList();
        }
        else
        {
            OpsTitle.Text = $"Ops — {_selectedId} (chưa có mô tả)";
            OpsList.ItemsSource = null;
        }
        OpsHint.Text = "Chọn một op để điền tên, nhập args JSON rồi bấm Chạy.";
    }

    private void OpsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (OpsList.SelectedItem is string op && _selectedId != null && ModuleOps.TryGetValue(_selectedId, out var ops))
        {
            OpBox.Text = op;
            OpsDesc.Text = ops[op];
        }
    }

    private async void RunOp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_selectedId == null) { ResultBox.Text = "Chọn một module ở bảng trái trước."; return; }
            string op = OpBox.Text.Trim();
            if (op.Length == 0) { ResultBox.Text = "Nhập tên op (hoặc chọn từ danh sách)."; return; }
            var sup = EnsureSupervisor();
            if (!sup.Modules.ContainsKey(_selectedId)) { ResultBox.Text = $"Module '{_selectedId}' không tồn tại."; return; }
            if (sup.Modules[_selectedId].State != ModuleRunState.Running)
            {
                StatusText.Text = $"▶ start {_selectedId}...";
                await sup.StartAsync(_selectedId);
            }
            JsonElement args = JsonSerializer.SerializeToElement(new { });
            string argsText = ArgsBox.Text.Trim();
            if (argsText.Length > 0)
            {
                using var doc = JsonDocument.Parse(argsText);
                args = doc.RootElement.Clone();
            }
            var result = await sup.CallAsync($"{_selectedId}.{op}", args);
            string pretty = JsonSerializer.Serialize(JsonDocument.Parse(result.GetRawText()).RootElement,
                new JsonSerializerOptions { WriteIndented = true });
            ResultBox.Text = pretty;
            StatusText.Text = $"✓ {_selectedId}.{op} — xem kết quả bên phải";
        }
        catch (Exception ex)
        {
            ResultBox.Text = $"Lỗi: {ex.GetType().Name} — {ex.Message}";
        }
    }

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
