using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace AppHost2.Pages;

public sealed partial class SettingsView : Page
{
    private MainWindow? _mw;
    private AppSettings _settings = new();

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _mw = e.Parameter as MainWindow;
        _settings = AppSettings.Load();
        RootBox.Text = _settings.ModulesRoot;
        AutoStartAllSwitch.IsOn = _settings.AutoStartAll;
        DarkThemeSwitch.IsOn = _settings.DarkTheme;
        LogsPath.Text = Path.Combine(Path.GetTempPath(), "mf-apphost2-logs");
        InfoText.Text = _mw?.Supervisor != null
            ? $"{_mw.Supervisor.Modules.Count} modules · ModuleHost: {Path.Combine(AppContext.BaseDirectory, "modulehost", "ModuleHost.exe")}"
            : "";
    }

    private void ApplyRootBtn_Click(object sender, RoutedEventArgs e)
    {
        var root = RootBox.Text.Trim();
        if (!Directory.Exists(root)) { InfoText.Text = "⚠ Thư mục không tồn tại"; return; }
        _settings.ModulesRoot = root;
        _settings.Save();
        if (_mw != null)
        {
            _mw.SetModulesRoot(root);
            _mw.RescanModules();
        }
        InfoText.Text = "✅ Đã lưu + quét lại";
    }

    private void AutoStartAll_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.AutoStartAll = AutoStartAllSwitch.IsOn;
        _settings.Save();
    }

    private void DarkTheme_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.DarkTheme = DarkThemeSwitch.IsOn;
        _settings.Save();
        if (_mw?.Content is FrameworkElement fe)
            fe.RequestedTheme = DarkThemeSwitch.IsOn ? ElementTheme.Dark : ElementTheme.Light;
    }

    private void OpenLogsBtn_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", Path.Combine(Path.GetTempPath(), "mf-apphost2-logs")); }
        catch { }
    }
}
