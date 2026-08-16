using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using ModularFramework.HostLib;

namespace AppHost2.Pages;

public sealed partial class HomePage : Page
{
    private ModuleSupervisor? _sup;

    private static string NodeFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mf-profiles", "node.json");

    public HomePage()
    {
        InitializeComponent();
        LoadNode();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _sup = e.Parameter as ModuleSupervisor;
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

    private void SaveNodeBtn_Click(object sender, RoutedEventArgs e)
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
            HomeStatus.Text = "✅ Node đã lưu";
        }
        catch (Exception ex) { HomeStatus.Text = $"Lỗi lưu node: {ex.Message}"; }
    }

    private async void TwoLayerOnBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sup == null) { HomeStatus.Text = "Supervisor chưa sẵn sàng"; return; }
            if (!_sup.Modules.ContainsKey("zapret-engine") || !_sup.Modules.ContainsKey("proxy-client"))
            {
                HomeStatus.Text = "Cần module zapret-engine + proxy-client trong modules root";
                return;
            }
            if (_sup.Modules["zapret-engine"].State != ModuleRunState.Running)
                await _sup.StartAsync("zapret-engine");
            var eng = await _sup.CallAsync("zapret-engine.start");
            HomeStatus.Text = eng.GetProperty("ok").GetBoolean()
                ? "🧪 Lớp 1 engine: OK — đang bật sing-box..."
                : $"🧪 Lớp 1 lỗi: {eng.GetProperty("error").GetString()}";

            if (_sup.Modules["proxy-client"].State != ModuleRunState.Running)
                await _sup.StartAsync("proxy-client");
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
            else args["password"] = NodeSecretBox.Text.Trim();

            var cfg = await _sup.CallAsync("proxy-client.buildConfig", JsonSerializer.SerializeToElement(args));
            if (!cfg.GetProperty("ok").GetBoolean())
            {
                HomeStatus.Text = $"🛡️ buildConfig lỗi: {cfg.GetProperty("error").GetString()}";
                return;
            }
            var prx = await _sup.CallAsync("proxy-client.start",
                JsonSerializer.SerializeToElement(new { config = cfg.GetProperty("args").GetString() }));
            HomeStatus.Text = prx.GetProperty("ok").GetBoolean()
                ? "🚀 2 lớp ĐÃ BẬT (engine + sing-box) — chạy elevated để TUN hoạt động"
                : $"🛡️ sing-box lỗi: {prx.GetProperty("error").GetString()}";
        }
        catch (Exception ex) { HomeStatus.Text = $"Lỗi 2 lớp: {ex.Message}"; }
    }

    private async void TwoLayerOffBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sup == null) return;
            if (_sup.Modules.ContainsKey("proxy-client")) await _sup.CallAsync("proxy-client.stop");
            if (_sup.Modules.ContainsKey("zapret-engine")) await _sup.CallAsync("zapret-engine.stop");
            HomeStatus.Text = "⏹ Đã tắt 2 lớp";
        }
        catch (Exception ex) { HomeStatus.Text = $"Lỗi tắt: {ex.Message}"; }
    }
}
