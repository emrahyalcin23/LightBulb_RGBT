using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DialogHostAvalonia;
using LightBulb.Utils.Extensions;

namespace LightBulb.Framework;

public class DialogManager : IDisposable
{
    private readonly ViewManager _viewManager;
    private readonly SemaphoreSlim _dialogLock = new(1, 1);

    public DialogManager(ViewManager viewManager)
    {
        _viewManager = viewManager;
    }

    public async Task<T?> ShowDialogAsync<T>(DialogViewModelBase<T> dialog)
    {
        await _dialogLock.WaitAsync();
        try
        {
            await DialogHost.Show(
                dialog,
                // It's fine to await in a void method here because it's an event handler
                // ReSharper disable once AsyncVoidLambda
                async (object _, DialogOpenedEventArgs args) =>
                {
                    await dialog.WaitForCloseAsync();

                    try
                    {
                        args.Session.Close();
                    }
                    catch (InvalidOperationException)
                    {
                        // Dialog host is already processing a close operation
                    }
                }
            );

            // Yield to allow DialogHost to fully reset its state before
            // another dialog is shown (e.g. when dialogs are shown sequentially)
            await Task.Yield();

            return dialog.DialogResult;
        }
        finally
        {
            _dialogLock.Release();
        }
    }

    /// <summary>
    /// Shows a dialog as a standalone top-level window, unconstrained by the main window size.
    /// Useful for dialogs that are taller than the main window (e.g. Settings with many tabs).
    /// </summary>
    public async Task<T?> ShowWindowDialogAsync<T>(DialogViewModelBase<T> dialog)
    {
        await _dialogLock.WaitAsync();
        try
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

            var owner = Application.Current?.ApplicationLifetime?.TryGetMainWindow();
            if (owner is not null)
                await window.ShowDialog(owner);
            else
                window.Show();

            await Task.Yield();
            return dialog.DialogResult;
        }
        finally
        {
            _dialogLock.Release();
        }
    }

    public void Dispose() => _dialogLock.Dispose();
}
