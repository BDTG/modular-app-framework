using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppHost2.Pages;

public sealed partial class ComponentsView : ModuleViewBase
{
    public class ListItem
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
    }

    private readonly ObservableCollection<ListItem> _items = new();

    public ComponentsView()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _items;
    }

    protected override async Task OnInitAsync()
    {
        LoadRing.IsActive = true;
        try
        {
            var r = await CallOp("list");
            _items.Clear();
            if (r.ValueKind == JsonValueKind.Array)
                foreach (var item in r.EnumerateArray())
                    _items.Add(new ListItem
                    {
                        Key = item.TryGetProperty("target", out var k) ? k.GetString() ?? "" : item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : item.TryGetProperty("index", out var idx) ? idx.GetInt32().ToString() : "",
                        DisplayName = item.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : (item.TryGetProperty("target", out var t) ? t.GetString() ?? (item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "") : ""),
                        Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    });
        }
        catch (Exception ex) { StatusText.Text = $"Lỗi: {ex.Message}"; }
        LoadRing.IsActive = false;
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
        {
            StatusText.Text = $"Đang xử lý {key}...";
            try
            {
                var args = JsonSerializer.SerializeToElement(int.TryParse(key, out var i) ? (object)new { index = i } : new { id = key });
                var r = await CallOp("run", args);
                StatusText.Text = $"✅ {key}: {r.GetRawText()[..Math.Min(80, r.GetRawText().Length)]}";
            }
            catch (Exception ex) { StatusText.Text = $"Lỗi: {ex.Message}"; }
        }
    }
}