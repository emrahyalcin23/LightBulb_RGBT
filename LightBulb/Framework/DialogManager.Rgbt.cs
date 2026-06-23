using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using LightBulb.Utils.Extensions;

namespace LightBulb.Framework;

public partial class DialogManager
{
    private readonly ViewManager _viewManager;

    public DialogManager(ViewManager viewManager)
    {
        _viewManager = viewManager;
    }

    public async Task<T?> ShowWindowDialogAsync<T>(DialogViewModelBase<T> dialog)
    {
        var view = _viewManager.TryBindView(dialog);

        var window = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SystemDecorations = SystemDecorations.Full,
            Content = view,
        };

        // When the ViewModel is closed (Save/Cancel) → close the Window
        _ = dialog.WaitForCloseAsync().ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                try { window.Close(); }
                catch { /* already closed — ignore */ }
            }),
            TaskContinuationOptions.ExecuteSynchronously
        );

        // Prefer the currently active window as owner so nested dialogs (e.g. a MessageBox
        // triggered from inside the Settings window) appear on top of their actual parent.
        var owner =
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.Windows.FirstOrDefault(w => w.IsActive)
            ?? Application.Current?.ApplicationLifetime?.TryGetMainWindow();

        if (owner is not null)
            await window.ShowDialog(owner);
        else
            window.Show();

        await Task.Yield();
        return dialog.DialogResult;
    }
}
