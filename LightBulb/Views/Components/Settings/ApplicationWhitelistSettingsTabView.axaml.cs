using System;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LightBulb.Framework;
using LightBulb.Models;
using LightBulb.ViewModels.Components.Settings;
using PowerKit;
using PowerKit.Extensions;

namespace LightBulb.Views.Components.Settings;

public partial class ApplicationWhitelistSettingsTabView
    : UserControl<ApplicationWhitelistSettingsTabViewModel>,
        IDisposable
{
    private IDisposable _eventSubscription = Disposable.Null;

    public ApplicationWhitelistSettingsTabView() => InitializeComponent();

    private void UserControl_OnLoaded(object? sender, RoutedEventArgs args)
    {
        DataContext.RefreshApplicationsCommand.Execute(null);

        _eventSubscription = DataContext.WatchProperty(
            o => o.WhitelistedApplications,
            // This hack is required to avoid having to use an ObservableCollection<T> on the view model
            _ =>
                WhitelistedApplicationsListBox.SelectedItems = new AvaloniaList<object>(
                    DataContext.WhitelistedApplications ?? []
                ),
            true
        );
    }

    private void UserControl_OnUnloaded(object? sender, RoutedEventArgs args) =>
        _eventSubscription.Dispose();

    // This hack is required to avoid having to use an ObservableCollection<T> on the view model
    private void WhitelistedApplicationsListBox_OnSelectionChanged(
        object? sender,
        SelectionChangedEventArgs args
    )
    {
        var applications = WhitelistedApplicationsListBox
            .SelectedItems?.Cast<ExternalApplication>()
            .ToArray();

        // Don't update the view model if the list hasn't changed.
        // This is important to avoid potential infinite loops.
        if (
            applications is not null
            && DataContext.WhitelistedApplications is not null
            && applications.SequenceEqual(DataContext.WhitelistedApplications)
        )
        {
            return;
        }

        DataContext.WhitelistedApplications = applications;
    }

    public void Dispose() => _eventSubscription.Dispose();
}
