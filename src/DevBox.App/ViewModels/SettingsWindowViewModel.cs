using DevBox.App.Services;

namespace DevBox.App.ViewModels;

public sealed class SettingsWindowViewModel : ObservableObject
{
    private readonly IAppSettingsService _settings;
    private readonly IDialogService _dialogs;
    private bool _startWithWindows;
    private bool _minimizeToTray;
    private bool _startServicesAutomatically;

    public SettingsWindowViewModel(IAppSettingsService settings, IDialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        _startWithWindows = settings.Current.StartWithWindows;
        _minimizeToTray = settings.Current.MinimizeToTray;
        _startServicesAutomatically = settings.Current.StartServicesAutomatically;
        SaveCommand = new RelayCommand(Save);
    }

    public bool StartWithWindows { get => _startWithWindows; set => SetProperty(ref _startWithWindows, value); }
    public bool MinimizeToTray { get => _minimizeToTray; set => SetProperty(ref _minimizeToTray, value); }
    public bool StartServicesAutomatically { get => _startServicesAutomatically; set => SetProperty(ref _startServicesAutomatically, value); }
    public RelayCommand SaveCommand { get; }

    private void Save()
    {
        try
        {
            if (_settings.Current.StartWithWindows != StartWithWindows)
            {
                _settings.SetStartWithWindows(StartWithWindows);
            }
            _settings.Current.MinimizeToTray = MinimizeToTray;
            _settings.Current.StartServicesAutomatically = StartServicesAutomatically;
            _settings.Save();
            _dialogs.Info("Settings saved", "DevBox settings were saved.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _dialogs.Error("Settings save failed", ex.Message);
        }
    }
}
