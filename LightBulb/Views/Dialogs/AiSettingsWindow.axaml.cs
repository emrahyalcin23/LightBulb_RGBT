using Avalonia.Markup.Xaml;
using LightBulb.ViewModels.Dialogs;

namespace LightBulb.Views.Dialogs;

public partial class AiSettingsWindow : LightBulb.Framework.Window<AiSettingsViewModel>
{
    public AiSettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnClosed(System.EventArgs e)
    {
        base.OnClosed(e);
        if (base.DataContext is AiSettingsViewModel vm)
            vm.Dispose();
    }
}
