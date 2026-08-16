using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppHost2.Pages;

public sealed partial class GameBoostView : ModuleViewBase
{
    public GameBoostView() { InitializeComponent(); }

    protected override async Task OnInitAsync() => await RefreshStatus();

    private async Task RefreshStatus()
    {
        try
        {
            var r = await CallOp("status");
            if (r.TryGetProperty("boosted", out var b) && b.GetBoolean())
            {
                BoostState.Text = "🟢 Đang boost";
                BoostState.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SeaGreen);
                var stopped = r.TryGetProperty("stoppedServices", out var s) ? s.GetString() : "";
                StoppedServices.Text = stopped ?? "";
                ExplorerState.Text = (r.TryGetProperty("explorerKilled", out var e) && e.GetBoolean()) ? "đã tắt" : "đang chạy";
            }
            else
            {
                BoostState.Text = "⚪ Chưa boost";
                BoostState.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DimGray);
                StoppedServices.Text = "";
                ExplorerState.Text = "đang chạy";
            }
        }
        catch { }
    }

    private async void BoostBtn_Click(object sender, RoutedEventArgs e)
    {
        LoadRing.IsActive = true;
        try
        {
            var mode = MaxModeChk.IsChecked == true ? "max" : "normal";
            var r = await CallOp("boost", JsonSerializer.SerializeToElement(new { mode }));
            var stopped = r.TryGetProperty("stopped", out var s) ? s.GetString() : "";
            LogBox.Text = $"[{DateTime.Now:HH:mm:ss}] Boost ({mode}): {r}\n" + LogBox.Text;
            await RefreshStatus();
        }
        catch (Exception ex) { LogBox.Text = $"Lỗi: {ex.Message}\n" + LogBox.Text; }
        LoadRing.IsActive = false;
    }

    private async void RestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        LoadRing.IsActive = true;
        try
        {
            var r = await CallOp("restore");
            LogBox.Text = $"[{DateTime.Now:HH:mm:ss}] Restore: {r}\n" + LogBox.Text;
            await RefreshStatus();
        }
        catch (Exception ex) { LogBox.Text = $"Lỗi: {ex.Message}\n" + LogBox.Text; }
        LoadRing.IsActive = false;
    }

    protected override void OnStateTick() => _ = RefreshStatus();
}