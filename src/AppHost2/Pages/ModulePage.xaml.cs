using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using ModularFramework.HostLib;

namespace AppHost2.Pages;

public sealed partial class ModulePage : Page
{
    private ModuleSupervisor? _sup;
    private readonly DispatcherTimer _logTimer;
    private readonly DispatcherTimer _autoTimer;
    private string _lastLogTail = "";
    private string _selectedOp = "";
    private string _lastArgs = "";
    private readonly DispatcherQueue _dq = DispatcherQueue.GetForCurrentThread();

    public string ModuleId { get; private set; } = "";

    public ModulePage()
    {
        InitializeComponent();
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _logTimer.Tick += (_, _) => RefreshLog();
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autoTimer.Tick += async (_, _) => await RunOpCoreAsync(refreshOnly: true);
    }

    // ── Ops map (14 module) ─────────────────────────────────────────
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

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is object[] { Length: 2 } p && p[0] is ModuleSupervisor sup && p[1] is string id)
        {
            _sup = sup;
            ModuleId = id;
            ModTitle.Text = sup.Modules[id].Manifest.DisplayName;
            if (ModuleOps.TryGetValue(id, out var ops))
            {
                OpsList.ItemsSource = ops.ToList();
                if (ops.Count > 0) OpsList.SelectedIndex = 0;
            }
            RefreshState();
            _logTimer.Start();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logTimer.Stop();
        _autoTimer.Stop();
    }

    public void RefreshState()
    {
        if (_sup == null || !_sup.Modules.TryGetValue(ModuleId, out var inst)) return;
        ModState.Text = inst.State.ToString();
        ModPid.Text = inst.Process?.Id is > 0 ? $"pid {inst.Process.Id}" : "";
        ModStateBadge.Background = inst.State switch
        {
            ModuleRunState.Running => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SeaGreen),
            ModuleRunState.Disabled => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed),
            _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DimGray),
        };
        ModState.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
    }

    private async void ModStartBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sup == null) return;
            await _sup.StartAsync(ModuleId);
            RefreshState();
        }
        catch (Exception ex) { ResultBox.Text = $"Lỗi start: {ex.Message}"; }
    }

    private async void ModStopBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sup == null) return;
            await _sup.StopAsync(ModuleId);
            RefreshState();
        }
        catch (Exception ex) { ResultBox.Text = $"Lỗi stop: {ex.Message}"; }
    }

    private void OpsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpsList.SelectedItem is KeyValuePair<string, string> kv)
        {
            _selectedOp = kv.Key;
            OpDescText.Text = kv.Value;
            ResultList.ItemsSource = null;
            ResultList.Visibility = Visibility.Collapsed;
            ResultJsonPanel.Visibility = Visibility.Visible;
            ResultBox.Text = "";
            // op dạng live-list (devices...) → tự bật auto-refresh
            AutoRefreshChk.IsChecked = kv.Key == "devices";
        }
    }

    private void RefreshOpBtn_Click(object sender, RoutedEventArgs e) => _ = RunOpCoreAsync(refreshOnly: true);

    private void AutoRefreshChk_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshChk.IsChecked == true) _autoTimer.Start();
        else _autoTimer.Stop();
    }

    private async void RunOpBtn_Click(object sender, RoutedEventArgs e) => await RunOpCoreAsync(refreshOnly: false);

    private async Task RunOpCoreAsync(bool refreshOnly)
    {
        try
        {
            if (_sup == null) return;
            string op = _selectedOp;
            if (op.Length == 0 && !refreshOnly) { ResultBox.Text = "Chọn một op ở cột trái."; return; }
            if (_sup.Modules[ModuleId].State != ModuleRunState.Running)
                await _sup.StartAsync(ModuleId);
            JsonElement args = JsonSerializer.SerializeToElement(new { });
            if (!refreshOnly)
            {
                _lastArgs = ArgsBox.Text.Trim();
                if (_lastArgs.Length > 0)
                {
                    using var doc = JsonDocument.Parse(_lastArgs);
                    args = doc.RootElement.Clone();
                }
            }
            else if (_lastArgs.Length > 0)
            {
                using var doc = JsonDocument.Parse(_lastArgs);
                args = doc.RootElement.Clone();
            }
            var result = await _sup.CallAsync($"{ModuleId}.{op}", args);
            RefreshState();
            ShowResult(result);
        }
        catch (Exception ex)
        {
            ResultJsonPanel.Visibility = Visibility.Visible;
            ResultList.Visibility = Visibility.Collapsed;
            ResultBox.Text = $"Lỗi: {ex.GetType().Name} — {ex.Message}";
        }
    }

    /// <summary>Hiển thị kết quả: array → ListView (đẹp, cập nhật được), khác → JSON pretty.</summary>
    private void ShowResult(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Array)
        {
            var items = new List<string>();
            foreach (var e in result.EnumerateArray())
                items.Add(ItemDisplay(e));
            ResultList.ItemsSource = items;
            ResultList.Visibility = Visibility.Visible;
            ResultJsonPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            ResultJsonPanel.Visibility = Visibility.Visible;
            ResultList.Visibility = Visibility.Collapsed;
            ResultBox.Text = JsonSerializer.Serialize(JsonDocument.Parse(result.GetRawText()).RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private static string ItemDisplay(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var parts = new List<string>();
            foreach (var key in new[] { "name", "displayName", "valueName", "packageFullName", "id", "device", "description", "status", "state", "path", "url" })
            {
                if (e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined)
                {
                    var s = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString();
                    if (!string.IsNullOrEmpty(s)) parts.Add(s);
                }
            }
            return parts.Count > 0 ? string.Join("  ·  ", parts) : e.GetRawText();
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString()! : e.ToString();
    }

    private void RefreshLog()
    {
        if (_sup == null || !_sup.Modules.TryGetValue(ModuleId, out var inst)) return;
        try
        {
            if (!File.Exists(inst.LogFile)) { LogBox.Text = "(chưa có log)"; return; }
            var tail = string.Join("\n", File.ReadLines(inst.LogFile).TakeLast(500));
            if (tail != _lastLogTail)
            {
                _lastLogTail = tail;
                LogBox.Text = tail;
                LogBox.SelectionStart = tail.Length;
                LogBox.SelectionLength = 0;
            }
        }
        catch { }
    }
}
