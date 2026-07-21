using System;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ZLauncher.Installer.ViewModels;

namespace ZLauncher.Installer.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.RequestBrowseFolder -= OnRequestBrowseFolder;
            _vm.RequestClose -= OnRequestClose;
        }

        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _vm.RequestBrowseFolder += OnRequestBrowseFolder;
            _vm.RequestClose += OnRequestClose;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.RequestBrowseFolder -= OnRequestBrowseFolder;
            _vm.RequestClose -= OnRequestClose;
        }
    }

    private async void OnRequestBrowseFolder(object? sender, EventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Папка установки ZLauncher",
                AllowMultiple = false
            });

            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
                _vm?.ApplyPickedFolder(path);
        }
        catch
        {
            // ignore
        }
    }

    private void OnRequestClose(object? sender, EventArgs e)
    {
        Close();
    }
}
