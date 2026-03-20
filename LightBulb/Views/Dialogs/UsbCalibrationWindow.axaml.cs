using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LightBulb.ViewModels.Dialogs;
using LightBulb.Views.Components;

namespace LightBulb.Views.Dialogs;

public partial class UsbCalibrationWindow : UserControl
{
    private bool _eventsSubscribed;

    public UsbCalibrationWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_eventsSubscribed) return;

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

        _eventsSubscribed = true;
    }
}
