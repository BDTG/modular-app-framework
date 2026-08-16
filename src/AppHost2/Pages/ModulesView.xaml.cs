using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using ModularFramework.HostLib;
using Windows.Storage.Pickers;

namespace AppHost2.Pages;

public sealed partial class ModulesView : Page
{
    public class ModuleCard
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Icon { get; set; } = "\uE7B8";
        public bool Enabled { get; set; } = true;
        public string StateText { get; set; } = "";
        public SolidColorBrush StateBrush { get; set; } = new(Microsoft.UI.Colors.DimGray);
        public string PidText { get; set; } = "";
        public string RestartText { get; set; } = "";
        public string Root { get; set; } = "";
    }

    private ModuleSupervisor? _sup;
    private MainWindow? _mw;
    private string _modulesRoot = "";
    private readonly ObservableCollection<ModuleCard> _cards = new();
    private readonly DispatcherTimer _stateTimer;
    private bool _busy;

    public ModulesView()
    {
        InitializeComponent();
        ModulesList.ItemsSource = _cards;
        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _stateTimer.Tick += (_, _) => RefreshStates();
        _stateTimer.Start();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is object[] { Length: 3 } p && p[0] is ModuleSupervisor sup && p[1] is string root)
        {
            _sup = sup;
            _modulesRoot = root;
            _mw = p[2] as MainWindow;
            RefreshList();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _stateTimer.Stop();
    }

    private void RefreshList()
    {
        _cards.Clear();
        if (_sup == null) return;
        foreach (var inst in _sup.Modules.Values.OrderBy(m => m.Manifest.Id))
        {
            var disabled = inst.State == ModuleRunState.Disabled;
            _cards.Add(new ModuleCard
            {
                Id = inst.Manifest.Id,
                Name = inst.Manifest.DisplayName,
                Version = inst.Manifest.Version,
                Icon = inst.Manifest.RequiresElevation ? "\uE7B8" : "\uE7C4",
                Enabled = !disabled,
                StateText = inst.State.ToString(),
                StateBrush = inst.State switch
                {
                    ModuleRunState.Running => new SolidColorBrush(Microsoft.UI.Colors.SeaGreen),
                    ModuleRunState.Disabled => new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
                    _ => new SolidColorBrush(Microsoft.UI.Colors.DimGray),
                },
                PidText = inst.Process?.Id is > 0 ? $"pid {inst.Process.Id}" : "",
                RestartText = inst.RestartCount > 0 ? $"restart ×{inst.RestartCount}" : "",
                Root = inst.ModuleRoot,
            });
        }
    }

    private void RefreshStates()
    {
        if (_sup == null) return;
        foreach (var card in _cards)
        {
            if (!_sup.Modules.TryGetValue(card.Id, out var inst)) continue;
            card.StateText = inst.State.ToString();
            card.StateBrush = inst.State switch
            {
                ModuleRunState.Running => new SolidColorBrush(Microsoft.UI.Colors.SeaGreen),
                ModuleRunState.Disabled => new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
                _ => new SolidColorBrush(Microsoft.UI.Colors.DimGray),
            };
            card.PidText = inst.Process?.Id is > 0 ? $"pid {inst.Process.Id}" : "";
        }
    }

    // ── Install từ file zip (KernelSU-style) ────────────────────────
    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".zip");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowHandle);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            WorkRing.IsActive = true;
            StatusText.Text = $"Đang cài {file.Name}...";
            var tmp = Path.Combine(Path.GetTempPath(), "mf-install-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tmp);
            try
            {
                ZipFile.ExtractToDirectory(file.Path, tmp);
                var manifestPath = Path.Combine(tmp, "module.json");
                if (!File.Exists(manifestPath))
                {
                    // có thể zip bọc 1 thư mục con — tìm module.json 1 cấp
                    var sub = Directory.GetDirectories(tmp).FirstOrDefault(d => File.Exists(Path.Combine(d, "module.json")));
                    if (sub != null) { manifestPath = Path.Combine(sub, "module.json"); tmp = sub; }
                }
                if (!File.Exists(manifestPath)) { StatusText.Text = "⚠ Zip không chứa module.json — không phải module package"; return; }

                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var id = doc.RootElement.TryGetProperty("id", out var i) ? i.GetString() : null;
                var entry = doc.RootElement.TryGetProperty("entry", out var en) ? en.GetString() : null;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(entry)) { StatusText.Text = "⚠ module.json thiếu id/entry"; return; }
                if (!File.Exists(Path.Combine(tmp, entry))) { StatusText.Text = $"⚠ Thiếu entry dll: {entry}"; return; }

                var dest = Path.Combine(_modulesRoot, id);
                if (Directory.Exists(dest))
                {
                    StatusText.Text = $"⚠ Module '{id}' đã tồn tại — gỡ cũ trước khi cài";
                    return;
                }
                Directory.CreateDirectory(_modulesRoot);
                // copy toàn bộ (kể cả bundle/) trừ file tạm
                foreach (var f in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(tmp, f);
                    var target = Path.Combine(dest, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(f, target);
                }
                _sup?.Rescan();
                RefreshList();
                StatusText.Text = $"✅ Đã cài module '{id}' — bấm ▶ Start để chạy";
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(tmp) is { } p ? p : tmp, recursive: true); } catch { }
            }
        }
        catch (Exception ex) { StatusText.Text = $"Lỗi cài: {ex.Message}"; }
        WorkRing.IsActive = false;
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _sup?.Rescan();
            RefreshList();
            StatusText.Text = "⟳ Đã quét lại";
        }
        catch (Exception ex) { StatusText.Text = $"Lỗi: {ex.Message}"; }
    }

    private void Card_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ModuleCard card)
            _mw?.NavigateToModule(card.Id);
    }

    private async void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not ToggleSwitch ts || ts.Tag is not string id) return;
        _busy = true;
        try
        {
            if (_sup == null || !_sup.Modules.TryGetValue(id, out var inst)) return;
            var flag = Path.Combine(inst.ModuleRoot, "disabled.flag");
            if (ts.IsOn && File.Exists(flag)) File.Delete(flag);
            if (!ts.IsOn && !File.Exists(flag))
            {
                if (inst.State == ModuleRunState.Running) await _sup.StopAsync(id);
                File.WriteAllText(flag, "disabled by Module Manager");
            }
            _sup.Rescan();
            RefreshList();
            StatusText.Text = ts.IsOn ? $"✅ Đã bật '{id}'" : $"⏸ Đã tắt '{id}'";
        }
        catch (Exception ex) { StatusText.Text = $"Lỗi: {ex.Message}"; }
        _busy = false;
    }

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            try { await _sup?.StartAsync(id)!; StatusText.Text = $"▶ {id} đang chạy"; }
            catch (Exception ex) { StatusText.Text = $"Lỗi: {ex.Message}"; }
            RefreshList();
        }
    }

    private async void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            try { await _sup?.StopAsync(id)!; StatusText.Text = $"■ {id} đã dừng"; }
            catch (Exception ex) { StatusText.Text = $"Lỗi: {ex.Message}"; }
            RefreshList();
        }
    }

    private void OpenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id && _sup != null && _sup.Modules.TryGetValue(id, out var inst))
        {
            try { Process.Start("explorer.exe", inst.ModuleRoot); }
            catch { }
        }
    }

    private async void RemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id && _sup != null && _sup.Modules.TryGetValue(id, out var inst))
        {
            var dialog = new ContentDialog
            {
                Title = $"Gỡ module '{id}'?",
                Content = "Thư mục module sẽ bị xóa khỏi modules root. Hành động này không thể hoàn tác.",
                PrimaryButtonText = "Gỡ",
                CloseButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                if (inst.State == ModuleRunState.Running)
                    await _sup.StopAsync(id);
                Directory.Delete(inst.ModuleRoot, recursive: true);
                _sup.Rescan();
                RefreshList();
                StatusText.Text = $"🗑 Đã gỡ '{id}'";
            }
            catch (Exception ex) { StatusText.Text = $"Lỗi gỡ: {ex.Message}"; }
        }
    }
}
