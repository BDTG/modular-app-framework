using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppHost2.Pages;

public sealed partial class TweaksView : ModuleViewBase
{
    public class TweakItem
    {
        public int Index { get; set; }
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public string GroupLabel { get; set; } = "";
        public bool Applied { get; set; }
    }

    private readonly ObservableCollection<TweakItem> _items = new();
    private bool _loading;
    private readonly DispatcherQueue _dq = DispatcherQueue.GetForCurrentThread();

    public TweaksView()
    {
        InitializeComponent();
        TweaksList.ItemsSource = _items;
    }

    protected override async Task OnInitAsync() => await LoadTweaksAsync();

    private async Task LoadTweaksAsync()
    {
        _loading = true;
        LoadRing.IsActive = true;
        StatusText.Text = "";
        try
        {
            var result = await CallOp("list");
            _items.Clear();
            if (result.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in result.EnumerateArray())
                {
                    var idx = item.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                    var name = item.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : "";
                    var desc = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    var group = item.TryGetProperty("group", out var g) ? (g.GetString() == "network" ? "🌐" : "⚙") : "";
                    var applied = item.TryGetProperty("applied", out var a) && a.GetBoolean();
                    _items.Add(new TweakItem { Index = idx, DisplayName = name, Description = desc, GroupLabel = group, Applied = applied });
                }
            }
        }
        catch (Exception ex) { StatusText.Text = $"Lỗi tải: {ex.Message}"; }
        _loading = false;
        LoadRing.IsActive = false;
    }

    private async void ItemToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch ts || ts.Tag is not int idx) return;
        var args = JsonSerializer.SerializeToElement(new { index = idx });
        try
        {
            if (ts.IsOn)
            {
                var r = await CallOp("apply", args);
                StatusText.Text = r.TryGetProperty("ok", out var ok) && ok.GetBoolean()
                    ? $"✅ Đã áp dụng tweak #{idx}" : $"⚠ Không áp dụng được: {r}";
            }
            else
            {
                var r = await CallOp("rollback", args);
                StatusText.Text = r.TryGetProperty("ok", out var ok) && ok.GetBoolean()
                    ? $"↩ Đã khôi phục tweak #{idx}" : $"⚠ Không rollback được: {r}";
            }
        }
        catch (Exception ex) { StatusText.Text = $"Lỗi: {ex.Message}"; }
    }

    private async void ReloadBtn_Click(object sender, RoutedEventArgs e) => await LoadTweaksAsync();

    private async void RollbackAllBtn_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Đang khôi phục tất cả...";
        foreach (var item in _items)
        {
            try
            {
                var args = JsonSerializer.SerializeToElement(new { index = item.Index });
                await CallOp("rollback", args);
                item.Applied = false;
            }
            catch { }
        }
        StatusText.Text = "↩ Đã khôi phục tất cả tweaks";
    }

    protected override void OnStateTick()
    {
        if (Sup == null || !Sup.Modules.TryGetValue(ModuleId, out var inst)) return;
        DescText.Text = $"Tinh chỉnh registry (network + system) — {inst.State}" + (inst.Process?.Id is > 0 ? $" · pid {inst.Process.Id}" : "");
    }
}