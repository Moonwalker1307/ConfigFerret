using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ConfigFerret.Desktop.ViewModels;

namespace ConfigFerret.Desktop.Views;

/// <summary>The main window.</summary>
public partial class MainWindow : Window, IDialogService
{
    /// <summary>Creates the window.</summary>
    public MainWindow()
    {
        this.InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Dialogs = this;
                App.ApplyTheme(viewModel.IsDarkTheme);
                viewModel.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    /// <inheritdoc />
    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task<string?> SaveFileAsync(string title, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName });
        return file?.TryGetLocalPath();
    }

    private static void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsDarkTheme) && sender is MainWindowViewModel viewModel)
        {
            App.ApplyTheme(viewModel.IsDarkTheme);
        }
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { IsGitHubConnected: true } viewModel)
        {
            viewModel.ShowConnections = false;
        }
    }
}
