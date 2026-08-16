using System.IO;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ModularFramework.HostLib;

namespace AppHost2;

public sealed partial class MainWindow : Window
{
    private ModuleSupervisor? _sup;
    private readonly DispatcherQueue _dq = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherTimer _statsTimer;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        Title = "Modular Framework — AppHost";

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();

        Activated += (_, _) => EnsureLoaded();
    }

    private bool _loaded;
    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var sup = EnsureSupervisor();
            foreach (var id in sup.Modules.Keys.OrderBy(k => k))
            {
                NavView.MenuItems.Add(new NavigationViewItem
                {
                    Content = id,
                    Tag = "module:" + id,
                    Icon = new FontIcon { Glyph = "\u25CF", FontSize = 10 },
                });
            }
            NavView.SelectedItem = NavView.MenuItems[0];
            ContentFrame.Navigate(typeof(Pages.HomePage));
        }
        catch (Exception ex)
        {
            StatsText.Text = ex.Message;
        }
    }

    private ModuleSupervisor EnsureSupervisor()
    {
        if (_sup != null) return _sup;
        var modulesRoot = ModulesRootBox.Text.Trim();
        var logsRoot = Path.Combine(Path.GetTempPath(), "mf-apphost2-logs");
        var moduleHostExe = Path.Combine(AppContext.BaseDirectory, "modulehost", "ModuleHost.exe");
        if (!File.Exists(moduleHostExe))
            throw new InvalidOperationException($"Thiếu ModuleHost.exe: {moduleHostExe} — build lại HostLib.");
        _sup = new ModuleSupervisor(moduleHostExe, modulesRoot, logsRoot);
        _sup.StateChanged += inst => _dq.TryEnqueue(() =>
        {
            if (ContentFrame.Content is Pages.ModulePage mp && mp.ModuleId == inst.Manifest.Id)
                mp.RefreshState();
        });
        return _sup;
    }

    private void UpdateStats()
    {
        if (_sup == null) return;
        var all = _sup.Modules.Values.ToList();
        int running = all.Count(m => m.State == ModuleRunState.Running);
        int stopped = all.Count(m => m.State == ModuleRunState.Stopped);
        int disabled = all.Count(m => m.State == ModuleRunState.Disabled);
        StatsText.Text = $"● {all.Count} module · ● {running} chạy · ● {stopped} tắt · ● {disabled} disabled";
    }

    private void ScanBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _sup?.Dispose();
            _sup = null;
            // giữ 5 item cố định: [0] DPI 2 lớp, [1] separator, [2] header MODULES,
            // [3] separator, [4] Isolation demo — xóa module items từ cuối
            while (NavView.MenuItems.Count > 5)
                NavView.MenuItems.RemoveAt(NavView.MenuItems.Count - 1);
            _loaded = false; // cho phép EnsureLoaded scan + thêm lại 14 module
            EnsureLoaded();
        }
        catch (Exception ex) { StatsText.Text = ex.Message; }
    }

    private async void StartAllBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sup = EnsureSupervisor();
            foreach (var id in sup.Modules.Keys.ToList())
                await sup.StartAsync(id);
        }
        catch (Exception ex) { StatsText.Text = ex.Message; }
    }

    private async void StopAllBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sup == null) return;
            foreach (var id in _sup.Modules.Keys.ToList())
                await _sup.StopAsync(id);
        }
        catch (Exception ex) { StatsText.Text = ex.Message; }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            Navigate(tag);
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is string tag)
            Navigate(tag);
    }

    private void Navigate(string tag)
    {
        if (tag == "home")
            ContentFrame.Navigate(typeof(Pages.HomePage), _sup);
        else if (tag == "demo")
            ContentFrame.Navigate(typeof(Pages.DemoPage), _sup);
        else if (tag.StartsWith("module:"))
        {
            var id = tag["module:".Length..];
            var pageType = id switch
            {
                "tweaks" => typeof(Pages.TweaksView),
                "system-cleanup" => typeof(Pages.CleanupView),
                "game-boost" => typeof(Pages.GameBoostView),
                "windows-activation" => typeof(Pages.ActivationView),
                "blockcheck" => typeof(Pages.BlockcheckView),
                "components-remover" => typeof(Pages.ComponentsView),
                "startup-manager" => typeof(Pages.StartupView),
                "appx-manager" => typeof(Pages.AppxView),
                _ => typeof(Pages.ModulePage),
            };
            ContentFrame.Navigate(pageType, new object[] { _sup, id });
        }
    }

    public ModuleSupervisor? Supervisor => _sup;
}
