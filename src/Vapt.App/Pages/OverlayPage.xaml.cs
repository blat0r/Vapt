using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vapt.Core;
using Vapt.Core.Perf;

namespace Vapt.Pages;

public sealed partial class OverlayPage : Page
{
    private bool _loading = true;

    public OverlayPage()
    {
        InitializeComponent();
        if (!Elevation.IsAdministrator)
        {
            Body.Children.Insert(1, Ui.AdminBar("overlay", "O FPS é lido do Windows por um canal que só abre como administrador. Sem isso o overlay mostra só GPU e CPU."));
        }

        var o = App.Settings.Overlay;
        var items = new (string Label, Func<bool> Get, Action<bool> Set)[]
        {
            ("FPS", () => o.Fps, v => o.Fps = v),
            ("Tempo de quadro (ms)", () => o.FrameTime, v => o.FrameTime = v),
            ("1% low", () => o.OnePercentLow, v => o.OnePercentLow = v),
            ("Gráfico de quadros", () => o.Graph, v => o.Graph = v),
            ("GPU: uso e temperatura", () => o.Gpu, v => o.Gpu = v),
            ("GPU: clock", () => o.GpuClock, v => o.GpuClock = v),
            ("GPU: consumo", () => o.GpuPower, v => o.GpuPower = v),
            ("VRAM", () => o.Vram, v => o.Vram = v),
            ("CPU: uso e temperatura", () => o.Cpu, v => o.Cpu = v),
            ("CPU: clock", () => o.CpuClock, v => o.CpuClock = v),
            ("RAM", () => o.Ram, v => o.Ram = v),
            ("Hora", () => o.Time, v => o.Time = v),
        };
        for (var i = 0; i < items.Length; i++)
        {
            if (i % 3 == 0)
            {
                Items.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var (label, get, set) = items[i];
            var check = new CheckBox { Content = label, IsChecked = get() };
            check.Checked += (_, _) => { set(true); Save(); };
            check.Unchecked += (_, _) => { set(false); Save(); };
            Grid.SetRow(check, i / 3);
            Grid.SetColumn(check, i % 3);
            Items.Children.Add(check);
        }

        RunToggle.IsOn = GameOverlay.Running;
        StartToggle.IsOn = App.Settings.OverlayAtStart;
        CornerBox.SelectedIndex = (int)o.Corner;
        ScaleSlider.Value = o.Scale;
        _loading = false;
    }

    private static void Save() => App.Settings.Save();

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (RunToggle.IsOn)
        {
            GameOverlay.Options = App.Settings.Overlay;
            GameOverlay.Start();
        }
        else
        {
            GameOverlay.Stop();
        }
    }

    private void OnStartToggle(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            App.Settings.OverlayAtStart = StartToggle.IsOn;
            Save();
        }
    }

    private void OnCorner(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            App.Settings.Overlay.Corner = (OverlayCorner)CornerBox.SelectedIndex;
            Save();
        }
    }

    private void OnScale(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_loading)
        {
            App.Settings.Overlay.Scale = (float)ScaleSlider.Value;
            Save();
        }
    }
}
