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
    private string _modulesRoot = "C:\\Users\\BDTG\\Projects\\mf-all";
    private readonly DispatcherQueue _dq = DispatcherQueue.GetForCurrentThread();

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        Title = "Modular Framework — AppHost";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Activated += (_, _) => EnsureLoaded();
    }

    private bool _loaded;
    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var settings = AppSettings.Load();
            if (!string.IsNullOrEmpty(settings.ModulesRoot)) _modulesRoot = settings.ModulesRoot;
            if (Content is FrameworkElement fe)
                fe.RequestedTheme = settings.DarkTheme ? ElementTheme.Dark : ElementTheme.Light;
            var sup = EnsureSupervisor();
            NavView.SelectedItem = NavView.MenuItems[1]; // Module Manager
            ContentFrame.Navigate(typeof(Pages.ModulesView), new object[] { _sup, _modulesRoot, this });
            if (settings.AutoStartAll) _ = StartAllAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AppHost2: {ex}");
        }
    }

    private ModuleSupervisor EnsureSupervisor()
    {
        if (_sup != null) return _sup;
        var modulesRoot = _modulesRoot;
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

    private async Task StartAllAsync()
    {
        try
        {
            var sup = EnsureSupervisor();
            foreach (var id in sup.Modules.Keys.ToList())
                await sup.StartAsync(id);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"AppHost2: {ex}"); }
    }

    private async void StopAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sup == null) return;
            foreach (var id in _sup.Modules.Keys.ToList())
                await _sup.StopAsync(id);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"AppHost2: {ex}"); }
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
        if (tag == "manager")
            ContentFrame.Navigate(typeof(Pages.ModulesView), new object[] { _sup, _modulesRoot, this });
        else if (tag == "settings")
            ContentFrame.Navigate(typeof(Pages.SettingsView), this);
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

    public void SetModulesRoot(string root) => _modulesRoot = root;

    /// <summary>Mở trang view chuyên biệt của module (từ Module Manager).</summary>
    public void NavigateToModule(string id) => Navigate("module:" + id);

    public void RescanModules()
    {
        try
        {
            _sup?.Rescan();
        }
        catch { }
    }
}
