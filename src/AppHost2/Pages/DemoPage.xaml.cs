using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using ModularFramework.HostLib;

namespace AppHost2.Pages;

public sealed partial class DemoPage : Page
{
    private ModuleSupervisor? _sup;

    public DemoPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _sup = e.Parameter as ModuleSupervisor;
    }

    private async void BoomBtn_Click(object sender, RoutedEventArgs e) => await DemoOp("boom");
    private async void ExitBtn_Click(object sender, RoutedEventArgs e) => await DemoOp("exit");
    private async void HangBtn_Click(object sender, RoutedEventArgs e) => await DemoOp("hang");

    private async Task DemoOp(string op)
    {
        try
        {
            if (_sup == null) { DemoStatus.Text = "Supervisor chưa sẵn sàng"; return; }
            if (_sup.Modules["crashy"].State != ModuleRunState.Running)
                await _sup.StartAsync("crashy");
            var result = await _sup.CallAsync("crashy." + op, JsonSerializer.SerializeToElement(new { }));
            DemoStatus.Text = $"crashy {op} → {result}";
        }
        catch (Exception ex)
        {
            DemoStatus.Text = $"crashy {op} → {ex.GetType().Name} (module đã chết, supervisor đang restart...)";
        }
    }
}
