using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using ModularFramework.HostLib;

namespace AppHost2.Pages;

/// <summary>Base cho mọi module view chuyên biệt — quản lý supervisor, auto-start, log timer.</summary>
public abstract class ModuleViewBase : Page
{
    protected ModuleSupervisor? Sup { get; private set; }
    protected string ModuleId { get; private set; } = "";
    protected readonly DispatcherTimer LogTimer;
    protected readonly DispatcherTimer StateTimer;

    protected ModuleViewBase()
    {
        LogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        LogTimer.Tick += (_, _) => OnLogTick();
        StateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        StateTimer.Tick += (_, _) => OnStateTick();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is object[] { Length: 2 } p && p[0] is ModuleSupervisor sup && p[1] is string id)
        {
            Sup = sup;
            ModuleId = id;
        }
        LogTimer.Start();
        StateTimer.Start();
        _ = OnInitAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        LogTimer.Stop();
        StateTimer.Stop();
    }

    /// <summary>Gọi module op, auto-start module nếu chưa chạy.</summary>
    protected async Task<JsonElement> CallOp(string op, JsonElement? args = null)
    {
        if (Sup == null) throw new InvalidOperationException("Supervisor chưa sẵn sàng");
        if (Sup.Modules[ModuleId].State != ModuleRunState.Running)
            await Sup.StartAsync(ModuleId);
        return await Sup.CallAsync($"{ModuleId}.{op}", args ?? JsonSerializer.SerializeToElement(new { }));
    }

    protected virtual void OnLogTick() { }
    protected virtual void OnStateTick() { }
    protected virtual Task OnInitAsync() => Task.CompletedTask;
}