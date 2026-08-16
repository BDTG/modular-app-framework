using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppHost2.Pages;

public sealed partial class CleanupView : ModuleViewBase
{
    public class TargetItem
    {
        public string TargetName { get; set; } = "";
        public string Path { get; set; } = "";
        public string Icon { get; set; } = "\u0044";
        public long SizeBytes { get; set; }
        public string SizeText => SizeBytes switch
        {
            < 0 => "—",
            _ when SizeBytes < 1_048_576 => $"{SizeBytes / 1024.0:F0} KB",
            _ when SizeBytes < 1_073_741_824 => $"{SizeBytes / 1_048_576.0:F1} MB",
            _ => $"{SizeBytes / 1_073_741_824.0:F2} GB",
        };
    }

    private readonly ObservableCollection<TargetItem> _items = new();

    public CleanupView()
    {
        InitializeComponent();
        TargetsList.ItemsSource = _items;
    }

    protected override async Task OnInitAsync() => await ScanAsync();

    private async Task ScanAsync()
    {
        ScanRing.IsActive = true;
        StatusText.Text = "Đang quét...";
        try
        {
            var result = await CallOp("scan");
            _items.Clear();
            if (result.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in result.EnumerateArray())
                {
                    _items.Add(new TargetItem
                    {
                        TargetName = item.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "",
                        Path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                        SizeBytes = item.TryGetProperty("sizeMb", out var s) ? (long)(s.GetDouble() * 1_048_576) : 0,
                        Icon = (item.TryGetProperty("target", out var tn) ? tn.GetString() : "") switch
                        {
                            "Temp" or "temp" => "\uE74D",
                            "Windows Temp" or "winTemp" => "\uE9F5",
                            "Prefetch" or "prefetch" => "\uE8B7",
                            "Recycle Bin" or "bin" => "\uE74D",
                            _ => "\uE8B7",
                        },
                    });
                }
            }
            var total = _items.Sum(i => i.SizeBytes);
            StatusText.Text = $"Tổng: {(total >= 1_073_741_824 ? $"{total / 1_073_741_824.0:F2} GB" : $"{total / 1_048_576.0:F0} MB")} — bấm Dọn từng loại hoặc Dọn tất cả.";
        }
        catch (Exception ex) { StatusText.Text = $"Lỗi quét: {ex.Message}"; }
        ScanRing.IsActive = false;
    }

    private async void ScanBtn_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void CleanOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string target)
        {
            StatusText.Text = $"Đang dọn {target}...";
            try
            {
                var args = JsonSerializer.SerializeToElement(new { target });
                var r = await CallOp("clean", args);
                var freed = r.TryGetProperty("freedMb", out var f) ? f.GetDouble() : 0;
                StatusText.Text = $"✅ {target}: đã giải phóng {freed:F1} MB";
                await ScanAsync();
            }
            catch (Exception ex) { StatusText.Text = $"Lỗi: {ex.Message}"; }
        }
    }

    private async void CleanAllBtn_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Đang dọn tất cả...";
        double totalFreed = 0;
        foreach (var item in _items.ToList())
        {
            try
            {
                var args = JsonSerializer.SerializeToElement(new { target = item.TargetName });
                var r = await CallOp("clean", args);
                if (r.TryGetProperty("freedMb", out var f)) totalFreed += f.GetDouble();
            }
            catch { }
        }
        StatusText.Text = $"🧹 Đã dọn xong — giải phóng tổng cộng {totalFreed:F1} MB";
        await ScanAsync();
    }

    private async void EmptyBinBtn_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Đang dọn thùng rác...";
        try
        {
            await CallOp("emptyBin");
            StatusText.Text = "🗑 Thùng rác đã dọn";
            await ScanAsync();
        }
        catch (Exception ex) { StatusText.Text = $"Lỗi: {ex.Message}"; }
    }

    protected override void OnStateTick()
    {
        if (Sup == null || !Sup.Modules.TryGetValue(ModuleId, out var inst)) return;
        StatusText.Text = $"System Cleanup — {inst.State}";
    }
}