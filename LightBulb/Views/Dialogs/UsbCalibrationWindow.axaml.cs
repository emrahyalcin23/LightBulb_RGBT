using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LightBulb.ViewModels.Dialogs;
using LightBulb.Views.Components;

namespace LightBulb.Views.Dialogs;

public partial class UsbCalibrationWindow : UserControl
{
    public UsbCalibrationWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Wire up graph interaction events → ViewModel after the AXAML tree is built.
        var curve = this.FindControl<CalibrationCurveControl>("CurveControl");
        if (curve is null) return;

        curve.GraphPointAddRequested += (rawY, value) =>
        {
            if (DataContext is UsbCalibrationViewModel vm)
                vm.AddPointFromGraph(rawY, value);
        };

        curve.GraphPointRemoveRequested += rawY =>
        {
            if (DataContext is UsbCalibrationViewModel vm)
                vm.RemovePointFromGraph(rawY);
        };

        curve.GraphPointMoved += (oldRawY, newRawY, value) =>
        {
            if (DataContext is UsbCalibrationViewModel vm)
                vm.MovePointFromGraph(oldRawY, newRawY, value);
        };
    }
}
