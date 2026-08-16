using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppHost2.Pages;

public sealed partial class ActivationView : ModuleViewBase
{
    public class MethodItem
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Icon { get; set; } = "\uE9A2";
    }

    private readonly ObservableCollection<MethodItem> _methods = new();

    public ActivationView()
    {
        InitializeComponent();
        MethodsList.ItemsSource = _methods;
        _ = LoadStatus();
        _ = LoadMethods();
    }

    private async Task LoadStatus()
    {
        StatusRing.IsActive = true;
        try
        {
            var status = await CallOp("status");
            if (status.TryGetProperty("activated", out var a) && a.GetBoolean())
            {
                ActivationState.Text = "✅ Đã kích hoạt";
                ActivationState.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SeaGreen);
            }
            else if (status.TryGetProperty("args", out var args))
            {
                ActivationState.Text = $"⚪ {args.GetString()}";
                ActivationState.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DimGray);
            }
            else
            {
                ActivationState.Text = "⚪ Không rõ";
            }

            var edition = await CallOp("edition");
            if (edition.TryGetProperty("args", out var ed))
                EditionText.Text = ed.GetString() ?? "";

            if (status.TryGetProperty("partialProductKey", out var pk) && pk.ValueKind == JsonValueKind.String)
                PartialKey.Text = $"Key: ...{pk.GetString()}";
        }
        catch (Exception ex) { ActivationState.Text = $"Lỗi: {ex.Message}"; }
        StatusRing.IsActive = false;
    }

    private async Task LoadMethods()
    {
        try
        {
            var methods = await CallOp("listMethods");
            _methods.Clear();
            if (methods.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in methods.EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.String)
                    {
                        var name = m.GetString() ?? "";
                        _methods.Add(new MethodItem
                        {
                            Name = name,
                            Description = name switch
                            {
                                "hwid" => "Digital License — vĩnh viễn, không cần Internet sau khi activate",
                                "ohook" => "Office — hook sppc.dll",
                                "kms" => "Online KMS — 180 ngày, cần renewal task",
                                "tsforge" => "TSforge — KMS4k mặc định (3.12+), sống qua hardware change",
                                _ => "",
                            },
                            Icon = name switch
                            {
                                "hwid" => "\uE9A2",
                                "ohook" => "\uEB99",
                                "kms" => "\uE9F9",
                                "tsforge" => "\uE9A2",
                                _ => "\uE9A2",
                            },
                        });
                    }
                }
            }
        }
        catch { }
    }

    private async void ScanTracesBtn_Click(object sender, RoutedEventArgs e)
    {
        TraceRing.IsActive = true;
        try
        {
            var r = await CallOp("traces");
            var kmsSuspected = r.TryGetProperty("kmsSuspected", out var k) && k.GetBoolean();
            var tasks = r.TryGetProperty("kmsRenewalTasks", out var t) && t.GetArrayLength() > 0;
            var dir = r.TryGetProperty("kmsDataDirExists", out var d) && d.GetBoolean();
            var sppc = r.TryGetProperty("sppcModified", out var s) && s.GetBoolean();
            TracesResult.Text = $"KMS bị nghi: {(kmsSuspected ? "CÓ ⚠" : "không ✓")}\nRenewal tasks: {(tasks ? "CÓ ⚠" : "không ✓")}\nThư mục KMS: {(dir ? "CÓ ⚠" : "không ✓")}\nSppc.dll bị đổi (Ohook): {(sppc ? "CÓ ⚠" : "không ✓")}";
        }
        catch (Exception ex) { TracesResult.Text = $"Lỗi: {ex.Message}"; }
        TraceRing.IsActive = false;
    }

    private async void CleanKmsBtn_Click(object sender, RoutedEventArgs e)
    {
        TraceRing.IsActive = true;
        try
        {
            var r = await CallOp("cleanKms", JsonSerializer.SerializeToElement(new { dryRun = true }));
            if (r.TryGetProperty("args", out var a))
                TracesResult.Text = $"📋 Dry-run:\n{a.GetString()}";
            else
                TracesResult.Text = JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) { TracesResult.Text = $"Lỗi: {ex.Message}"; }
        TraceRing.IsActive = false;
    }

    private async void ActivateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string method)
        {
            try
            {
                var r = await CallOp("activate", JsonSerializer.SerializeToElement(new { method }));
                if (r.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                    ActivationState.Text = $"✅ {method}: thành công";
                else
                    ActivationState.Text = $"⚠ {method}: {r.GetRawText()}";
            }
            catch (Exception ex) { ActivationState.Text = $"Lỗi: {ex.Message}"; }
            await LoadStatus();
        }
    }
}