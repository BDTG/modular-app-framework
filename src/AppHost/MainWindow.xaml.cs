using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ModularFramework.HostLib;

namespace AppHost;

public partial class MainWindow : Window
{
    private ModuleSupervisor? _sup;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _logTimer;
    private string? _selectedId;
    private string _lastLogTail = "";

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => ModuleGrid.Items.Refresh();
        _refreshTimer.Start();

        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _logTimer.Tick += (_, _) => RefreshLog();
        _logTimer.Start();

        Loaded += (_, _) =>
        {
            try
            {
                var sup = EnsureSupervisor();
                ModuleGrid.ItemsSource = sup.Modules.Values.ToList();
                StatusText.Text = $"Đã scan {sup.Modules.Count} module — bấm '▶ Start tất cả' để chạy";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        };
    }

    private ModuleSupervisor EnsureSupervisor()
    {
        if (_sup != null) return _sup;
        var modulesRoot = ModulesRootBox.Text.Trim();
        var logsRoot = Path.Combine(Path.GetTempPath(), "mf-apphost-logs");
        var moduleHostExe = Path.Combine(AppContext.BaseDirectory, "modulehost", "ModuleHost.exe");
        if (!File.Exists(moduleHostExe))
            throw new InvalidOperationException($"Thiếu ModuleHost.exe: {moduleHostExe} — build lại HostLib.");
        _sup = new ModuleSupervisor(moduleHostExe, modulesRoot, logsRoot);
        _sup.StateChanged += inst =>
            Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = $"{inst.Manifest.Id}: {inst.State}" + (inst.LastError is null ? "" : $" — {inst.LastError}");
                if (_selectedId == inst.Manifest.Id) RefreshLog();
            });
        ModuleGrid.ItemsSource = _sup.Modules.Values.ToList();
        return _sup;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _sup?.Dispose();
        _sup = null;
        EnsureSupervisor();
        ModuleGrid.ItemsSource = _sup.Modules.Values.ToList();
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sup = EnsureSupervisor();
            foreach (var id in sup.Modules.Keys.ToList())
                await sup.StartAsync(id);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Lỗi"); }
    }

    private async void StopAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sup == null) return;
            foreach (var id in _sup.Modules.Keys.ToList())
                await _sup.StopAsync(id);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Lỗi"); }
    }

    private void ModuleGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ModuleGrid.SelectedItem is ModuleInstance inst)
        {
            _selectedId = inst.Manifest.Id;
            LogTitle.Text = $"Log: {inst.Manifest.Id} ({inst.LogFile})";
            RefreshLog();
        }
    }

    private void RefreshLog()
    {
        if (_selectedId == null || _sup == null || !_sup.Modules.TryGetValue(_selectedId, out var inst)) return;
        try
        {
            if (!File.Exists(inst.LogFile)) { LogBox.Text = "(chưa có log)"; return; }
            var tail = string.Join("\n", File.ReadLines(inst.LogFile).TakeLast(200));
            if (tail != _lastLogTail)
            {
                _lastLogTail = tail;
                LogBox.Text = tail;
                LogBox.ScrollToEnd();
            }
        }
        catch { }
    }

    private async void Boom_Click(object sender, RoutedEventArgs e) => await DemoOp("boom");
    private async void Exit_Click(object sender, RoutedEventArgs e) => await DemoOp("exit");
    private async void Hang_Click(object sender, RoutedEventArgs e) => await DemoOp("hang");

    private async Task DemoOp(string op)
    {
        try
        {
            var sup = EnsureSupervisor();
            var result = await sup.CallAsync("crashy." + op, JsonSerializer.SerializeToElement(new { }));
            StatusText.Text = $"crashy {op} → {result}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"crashy {op} → {ex.GetType().Name} (module đã chết, supervisor đang restart...)";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _sup?.Dispose();
        base.OnClosed(e);
    }
}
