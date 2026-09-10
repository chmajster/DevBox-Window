using System.ComponentModel;
using System.Diagnostics;
using DevBox.App.Services;
using DevBox.Core.Services;

namespace DevBox.App.ViewModels;

public sealed class UpdateWindowViewModel : ObservableObject
{
    private readonly ApplicationUpdateService _updates;
    private readonly IShellService _shell;
    private readonly IDialogService _dialogs;
    private string _currentVersion;
    private string _latestVersion = "—";
    private string _status = "Not checked";
    private string? _releaseUrl;
    private bool _updateAvailable;

    public UpdateWindowViewModel(ApplicationUpdateService updates, IShellService shell, IDialogService dialogs)
    {
        _updates = updates;
        _shell = shell;
        _dialogs = dialogs;
        _currentVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        CheckCommand = new AsyncRelayCommand(CheckAsync);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, () => UpdateAvailable);
        OpenReleaseCommand = new RelayCommand(OpenRelease, () => !string.IsNullOrWhiteSpace(ReleaseUrl));
    }

    public string CurrentVersion { get => _currentVersion; private set => SetProperty(ref _currentVersion, value); }
    public string LatestVersion { get => _latestVersion; private set => SetProperty(ref _latestVersion, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? ReleaseUrl
    {
        get => _releaseUrl;
        private set
        {
            if (SetProperty(ref _releaseUrl, value))
                OpenReleaseCommand.RaiseCanExecuteChanged();
        }
    }
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set
        {
            if (SetProperty(ref _updateAvailable, value))
                InstallUpdateCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand InstallUpdateCommand { get; }
    public RelayCommand OpenReleaseCommand { get; }

    public async Task CheckAsync()
    {
        try
        {
            Status = "Checking GitHub Releases...";
            var result = await _updates.CheckAsync();
            CurrentVersion = result.CurrentVersion.ToString(3);
            LatestVersion = result.LatestVersion.ToString(3);
            ReleaseUrl = result.ReleaseUrl;
            UpdateAvailable = result.UpdateAvailable;
            Status = result.UpdateAvailable ? "Update available" : "You are up to date";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException)
        {
            Status = "Update check failed";
            _dialogs.Error("Update check failed", ex.Message);
        }
    }

    private async Task InstallUpdateAsync()
    {
        try
        {
            Status = "Downloading and verifying update...";
            var current = Version.TryParse(CurrentVersion, out var parsed) ? parsed : new Version(0, 0, 0);
            using var selfUpdater = new ApplicationSelfUpdateService(App.DevBoxRoot);
            var package = await selfUpdater.DownloadLatestInstallerAsync(current);
            Status = $"Verified DevBox {package.Version.ToString(3)}. Starting installer...";

            var startInfo = new ProcessStartInfo(package.InstallerPath)
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("/VERYSILENT");
            startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
            startInfo.ArgumentList.Add("/NORESTART");
            startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");
            startInfo.ArgumentList.Add("/TASKS=launchafterinstall");

            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException("Windows refused to start the verified DevBox installer.");

            App.RequestExit();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            Status = "Update installation failed";
            _dialogs.Error("Update installation failed", ex.Message);
        }
    }

    private void OpenRelease()
    {
        if (ReleaseUrl is null)
            return;
        try
        {
            _shell.Open(ReleaseUrl);
        }
        catch (Win32Exception ex)
        {
            _dialogs.Error("Unable to open release", ex.Message);
        }
    }
}
