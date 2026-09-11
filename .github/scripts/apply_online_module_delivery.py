from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"Expected one match in {path}, found {count}: {old[:120]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8", newline="\n")


# Manual Release: default to online PHP/phpMyAdmin delivery. PHP is omitted from
# release artifacts when online mode is enabled; phpMyAdmin is already an
# on-demand addon and is never fetched during packaging.
replace_once(
    ".github/workflows/release.yml",
    """          - patch\n          - minor\n          - major\n\npermissions:\n""",
    """          - patch\n          - minor\n          - major\n      online_php_phpmyadmin:\n        description: 'Keep PHP/phpMyAdmin online-only (PHP is not bundled; phpMyAdmin stays on-demand)'\n        required: true\n        default: true\n        type: boolean\n\npermissions:\n""",
)
replace_once(
    ".github/workflows/release.yml",
    """      - name: Bundle Nginx, PHP and MySQL runtimes\n        shell: pwsh\n        run: |\n          & ./packaging/Prepare-BundledRuntimes.ps1 -PublishDirectories \"publish/win-x64\",\"publish/win-arm64\"\n""",
    """      - name: Prepare bundled and online runtimes\n        shell: pwsh\n        env:\n          ONLINE_PHP_PHPMYADMIN: ${{ inputs.online_php_phpmyadmin }}\n        run: |\n          $onlineModules = $env:ONLINE_PHP_PHPMYADMIN -eq 'true'\n          & ./packaging/Prepare-BundledRuntimes.ps1 `\n            -PublishDirectories \"publish/win-x64\",\"publish/win-arm64\" `\n            -SkipPhp:$onlineModules\n\n          \"### Module delivery\" >> $env:GITHUB_STEP_SUMMARY\n          if ($onlineModules) {\n            \"- PHP: downloaded on demand from the verified runtime catalog; not downloaded during release.\" >> $env:GITHUB_STEP_SUMMARY\n          } else {\n            \"- PHP: bundled in the release.\" >> $env:GITHUB_STEP_SUMMARY\n          }\n          \"- phpMyAdmin: downloaded on demand from the verified ADDONS catalog; never downloaded during release.\" >> $env:GITHUB_STEP_SUMMARY\n""",
)

# Packaging switch used by Manual Release. It prevents even the release runner
# from downloading PHP when online delivery is selected.
replace_once(
    "packaging/Prepare-BundledRuntimes.ps1",
    """param(\n    [Parameter(Mandatory = $true)]\n    [string[]] $PublishDirectories\n)\n""",
    """param(\n    [Parameter(Mandatory = $true)]\n    [string[]] $PublishDirectories,\n\n    [switch] $SkipPhp\n)\n""",
)
replace_once(
    "packaging/Prepare-BundledRuntimes.ps1",
    """    }\n)\n\nforeach ($publishDirectory in $PublishDirectories) {\n""",
    """    }\n)\n\nif ($SkipPhp) {\n    Write-Host 'PHP will be delivered on demand and will not be downloaded or bundled by this release.'\n    $packages = @($packages | Where-Object { $_.Key -ne 'php' })\n}\n\nforeach ($publishDirectory in $PublishDirectories) {\n""",
)

# Register the runtime platform catalog/downloader and inject it into the main VM.
replace_once(
    "src/DevBox.App/App.xaml.cs",
    """        services.AddSingleton<IRuntimeManager>(_ => new RuntimeManager(DevBoxRoot));\n        services.AddSingleton(new RuntimeCatalog());\n""",
    """        services.AddSingleton<IRuntimeManager>(_ => new RuntimeManager(DevBoxRoot));\n        services.AddSingleton(_ => new RuntimePlatformService(DevBoxRoot));\n        services.AddSingleton(new RuntimeCatalog());\n""",
)
replace_once(
    "src/DevBox.App/App.xaml.cs",
    """            provider.GetRequiredService<SiteManager>(),\n            provider.GetRequiredService<IRuntimeManager>(),\n            provider.GetRequiredService<DiagnosticsService>(),\n""",
    """            provider.GetRequiredService<SiteManager>(),\n            provider.GetRequiredService<IRuntimeManager>(),\n            provider.GetRequiredService<RuntimePlatformService>(),\n            provider.GetRequiredService<DiagnosticsService>(),\n""",
)

# Runtime rows now represent both installed packages and verified online catalog
# entries. This is what makes the Download button meaningful before PHP exists.
replace_once(
    "src/DevBox.App/ViewModels/Rows.cs",
    """public sealed class RuntimeRowViewModel(RuntimeInstallation runtime)\n{\n    public string Key { get; } = runtime.Key;\n    public string Version { get; } = runtime.Version;\n    public string Status { get; } = !runtime.IsValid ? \"Broken\" : runtime.IsActive ? \"Active\" : \"Installed\";\n    public string InstallPath { get; } = runtime.InstallPath;\n}\n""",
    """public sealed class RuntimeRowViewModel\n{\n    public RuntimeRowViewModel(RuntimeVersionStatus status, string rootPath)\n    {\n        ArgumentNullException.ThrowIfNull(status);\n        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);\n\n        Key = status.Package.Key;\n        Name = status.Package.DisplayName;\n        Version = status.Package.Version;\n        IsInstalled = status.Installed;\n        CanDownload = !status.Installed &&\n                      !string.IsNullOrWhiteSpace(status.Package.DownloadUrl) &&\n                      !string.IsNullOrWhiteSpace(status.Package.Sha256);\n        CanActivate = status.Installed && status.Valid && !status.Active;\n        CanRemove = status.Installed;\n        Status = !status.Installed\n            ? CanDownload ? \"Available online\" : \"Not installed\"\n            : !status.Valid ? \"Broken\" : status.Active ? \"Active\" : \"Installed\";\n        InstallPath = status.Installed\n            ? Path.Combine(Path.GetFullPath(rootPath), \"runtime\", Key, Version)\n            : status.Package.DownloadUrl ?? \"No verified online package configured\";\n    }\n\n    public RuntimeRowViewModel(RuntimeInstallation runtime)\n    {\n        ArgumentNullException.ThrowIfNull(runtime);\n        Key = runtime.Key;\n        Name = runtime.Key;\n        Version = runtime.Version;\n        Status = !runtime.IsValid ? \"Broken\" : runtime.IsActive ? \"Active\" : \"Installed\";\n        InstallPath = runtime.InstallPath;\n        IsInstalled = true;\n        CanDownload = false;\n        CanActivate = runtime.IsValid && !runtime.IsActive;\n        CanRemove = true;\n    }\n\n    public string Key { get; }\n    public string Name { get; }\n    public string Version { get; }\n    public string Status { get; }\n    public string InstallPath { get; }\n    public bool IsInstalled { get; }\n    public bool CanDownload { get; }\n    public bool CanActivate { get; }\n    public bool CanRemove { get; }\n}\n""",
)

# phpMyAdmin is an online addon: make the primary action explicit.
replace_once(
    "src/DevBox.App/ViewModels/Rows.cs",
    '    private string _installAction = "Install";\n',
    '    private string _installAction = "Download";\n',
)
replace_once(
    "src/DevBox.App/ViewModels/Rows.cs",
    '        InstallAction = installed ? "Reinstall" : "Install";\n',
    '        InstallAction = installed ? "Reinstall" : "Download";\n',
)
replace_once(
    "src/DevBox.App/ViewModels/Rows.cs",
    '        InstallAction = "Retry";\n',
    '        InstallAction = "Retry download";\n',
)

# Main VM: use the catalog-backed downloader, expose the command, and include
# online entries in the runtime list without losing custom installed versions.
replace_once(
    "src/DevBox.App/ViewModels/MainWindowViewModel.cs",
    """    private readonly SiteManager _siteManager;\n    private readonly IRuntimeManager _runtimeManager;\n    private readonly DiagnosticsService _diagnosticsService;\n""",
    """    private readonly SiteManager _siteManager;\n    private readonly IRuntimeManager _runtimeManager;\n    private readonly RuntimePlatformService _runtimePlatformService;\n    private readonly DiagnosticsService _diagnosticsService;\n""",
)
replace_once(
    "src/DevBox.App/ViewModels/MainWindowViewModel.cs",
    """        SiteManager siteManager,\n        IRuntimeManager runtimeManager,\n        DiagnosticsService diagnosticsService,\n""",
    """        SiteManager siteManager,\n        IRuntimeManager runtimeManager,\n        RuntimePlatformService runtimePlatformService,\n        DiagnosticsService diagnosticsService,\n""",
)
replace_once(
    "src/DevBox.App/ViewModels/MainWindowViewModel.cs",
    """        _siteManager = siteManager;\n        _runtimeManager = runtimeManager;\n        _diagnosticsService = diagnosticsService;\n""",
    """        _siteManager = siteManager;\n        _runtimeManager = runtimeManager;\n        _runtimePlatformService = runtimePlatformService;\n        _diagnosticsService = diagnosticsService;\n""",
)
replace_once(
    "src/DevBox.App/ViewModels/MainWindowViewModel.cs",
    """        ClearLogCommand = new RelayCommand(ClearSelectedLog, () => SelectedLog is not null);\n        ActivateRuntimeCommand = new AsyncRelayCommand(ActivateRuntimeAsync);\n        RemoveRuntimeCommand = new AsyncRelayCommand(RemoveRuntimeAsync);\n""",
    """        ClearLogCommand = new RelayCommand(ClearSelectedLog, () => SelectedLog is not null);\n        DownloadRuntimeCommand = new AsyncRelayCommand(DownloadRuntimeAsync);\n        ActivateRuntimeCommand = new AsyncRelayCommand(ActivateRuntimeAsync);\n        RemoveRuntimeCommand = new AsyncRelayCommand(RemoveRuntimeAsync);\n""",
)
replace_once(
    "src/DevBox.App/ViewModels/MainWindowViewModel.cs",
    """    public RelayCommand ClearLogCommand { get; }\n    public AsyncRelayCommand ActivateRuntimeCommand { get; }\n    public AsyncRelayCommand RemoveRuntimeCommand { get; }\n""",
    """    public RelayCommand ClearLogCommand { get; }\n    public AsyncRelayCommand DownloadRuntimeCommand { get; }\n    public AsyncRelayCommand ActivateRuntimeCommand { get; }\n    public AsyncRelayCommand RemoveRuntimeCommand { get; }\n""",
)
replace_once(
    "src/DevBox.App/ViewModels/MainWindowViewModel.cs",
    '        if (!TryBeginAddonOperation(parameter, "Installing...", out var addon)) return;\n',
    '        if (!TryBeginAddonOperation(parameter, "Downloading...", out var addon)) return;\n',
)
replace_once(
    "src/DevBox.App/ViewModels/MainWindowViewModel.cs",
    """            await _addonInstaller.InstallAsync(addon);\n            RuntimeLayout.EnsureInitialized(_rootPath);\n            var phpConfigChanged = _phpExtensionInspector.EnsureConfigured(addon.RequiredPhpExtensions);\n            var domain = new Uri(addon.LocalUrl).Host;\n            var hostConfigured = await _hostMappingService.EnsureAsync(domain);\n            if (phpConfigChanged) await RestartIfRunningAsync(\"php\");\n            await RestartIfRunningAsync(\"nginx\");\n            await RefreshAddonHealthAsync();\n            _dialogs.Info($\"{addon.DisplayName} installed\", hostConfigured\n                ? $\"{addon.DisplayName} {addon.Version} installed and configured.\"\n                : $\"{addon.DisplayName} installed, but the hosts mapping is missing.\");\n""",
    """            await _addonInstaller.InstallAsync(addon);\n            RuntimeLayout.EnsureInitialized(_rootPath);\n            var phpCheck = await _phpExtensionInspector.CheckAsync(addon.RequiredPhpExtensions);\n            var phpConfigChanged = phpCheck.RuntimeAvailable &&\n                                   _phpExtensionInspector.EnsureConfigured(addon.RequiredPhpExtensions);\n            var domain = new Uri(addon.LocalUrl).Host;\n            var hostConfigured = await _hostMappingService.EnsureAsync(domain);\n            if (phpConfigChanged) await RestartIfRunningAsync(\"php\");\n            await RestartIfRunningAsync(\"nginx\");\n            await RefreshAddonHealthAsync();\n\n            if (!phpCheck.RuntimeAvailable)\n            {\n                _dialogs.Warning(\n                    $\"{addon.DisplayName} downloaded\",\n                    $\"{addon.DisplayName} {addon.Version} was downloaded and configured, but PHP is not installed. Open Runtimes and click Download for PHP.\");\n            }\n            else\n            {\n                _dialogs.Info($\"{addon.DisplayName} installed\", hostConfigured\n                    ? $\"{addon.DisplayName} {addon.Version} installed and configured.\"\n                    : $\"{addon.DisplayName} installed, but the hosts mapping is missing.\");\n            }\n""",
)

runtime_method_anchor = """    private async Task ActivateRuntimeAsync(object? parameter)\n    {\n"""
runtime_download_method = """    private async Task DownloadRuntimeAsync(object? parameter)\n    {\n        if (parameter is not RuntimeRowViewModel runtime || !runtime.CanDownload) return;\n\n        try\n        {\n            await _runtimePlatformService.InstallAsync(runtime.Key, runtime.Version);\n\n            var executableRelativePath = RuntimeExecutable(runtime.Key);\n            var installations = _runtimeManager.GetInstalled(runtime.Key, executableRelativePath);\n            var activated = false;\n            if (!installations.Any(item => item.IsActive))\n            {\n                await _runtimePlatformService.ActivateAsync(runtime.Key, runtime.Version);\n                activated = true;\n            }\n\n            RefreshRuntimes();\n            RefreshStatuses();\n            _dialogs.Info(\n                $\"{runtime.Name} downloaded\",\n                activated\n                    ? $\"{runtime.Name} {runtime.Version} was downloaded, verified and activated.\"\n                    : $\"{runtime.Name} {runtime.Version} was downloaded and verified.\");\n        }\n        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException)\n        {\n            RefreshRuntimes();\n            RefreshStatuses();\n            _dialogs.Error($\"{runtime.Name} download failed\", ex.Message);\n        }\n    }\n\n""" + runtime_method_anchor
replace_once(
    "src/DevBox.App/ViewModels/MainWindowViewModel.cs",
    runtime_method_anchor,
    runtime_download_method,
)
replace_once(
    "src/DevBox.App/ViewModels/MainWindowViewModel.cs",
    """    private void RefreshRuntimes()\n    {\n        Runtimes.Clear();\n        foreach (var descriptor in new[]\n        {\n            (Key: \"php\", Executable: \"php-cgi.exe\"),\n            (Key: \"nginx\", Executable: \"nginx.exe\"),\n            (Key: \"mysql\", Executable: Path.Combine(\"bin\", \"mysqld.exe\"))\n        })\n        {\n            foreach (var runtime in _runtimeManager.GetInstalled(descriptor.Key, descriptor.Executable))\n            {\n                Runtimes.Add(new RuntimeRowViewModel(runtime));\n            }\n        }\n    }\n""",
    """    private void RefreshRuntimes()\n    {\n        Runtimes.Clear();\n        var supportedKeys = new HashSet<string>([\"php\", \"nginx\", \"mysql\"], StringComparer.OrdinalIgnoreCase);\n        var represented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);\n\n        foreach (var status in _runtimePlatformService.GetStatuses()\n                     .Where(item => supportedKeys.Contains(item.Package.Key)))\n        {\n            Runtimes.Add(new RuntimeRowViewModel(status, _rootPath));\n            represented.Add($\"{status.Package.Key}|{status.Package.Version}\");\n        }\n\n        foreach (var descriptor in new[]\n        {\n            (Key: \"php\", Executable: \"php-cgi.exe\"),\n            (Key: \"nginx\", Executable: \"nginx.exe\"),\n            (Key: \"mysql\", Executable: Path.Combine(\"bin\", \"mysqld.exe\"))\n        })\n        {\n            foreach (var runtime in _runtimeManager.GetInstalled(descriptor.Key, descriptor.Executable))\n            {\n                if (represented.Add($\"{runtime.Key}|{runtime.Version}\"))\n                {\n                    Runtimes.Add(new RuntimeRowViewModel(runtime));\n                }\n            }\n        }\n    }\n""",
)

# Add Download action in Runtimes and make unsupported actions visibly disabled.
replace_once(
    "src/DevBox.App/MainWindow.xaml",
    """                                        <StackPanel Grid.Column=\"4\" Orientation=\"Horizontal\"><Button Content=\"Activate\" Style=\"{StaticResource PrimaryButton}\" Command=\"{Binding DataContext.ActivateRuntimeCommand, RelativeSource={RelativeSource AncestorType=Window}}\" CommandParameter=\"{Binding}\"/><Button Content=\"Remove\" Style=\"{StaticResource DangerButton}\" Command=\"{Binding DataContext.RemoveRuntimeCommand, RelativeSource={RelativeSource AncestorType=Window}}\" CommandParameter=\"{Binding}\"/></StackPanel>\n""",
    """                                        <StackPanel Grid.Column=\"4\" Orientation=\"Horizontal\">\n                                            <Button Content=\"Download\" Style=\"{StaticResource PrimaryButton}\" IsEnabled=\"{Binding CanDownload}\" Command=\"{Binding DataContext.DownloadRuntimeCommand, RelativeSource={RelativeSource AncestorType=Window}}\" CommandParameter=\"{Binding}\"/>\n                                            <Button Content=\"Activate\" IsEnabled=\"{Binding CanActivate}\" Command=\"{Binding DataContext.ActivateRuntimeCommand, RelativeSource={RelativeSource AncestorType=Window}}\" CommandParameter=\"{Binding}\"/>\n                                            <Button Content=\"Remove\" IsEnabled=\"{Binding CanRemove}\" Style=\"{StaticResource DangerButton}\" Command=\"{Binding DataContext.RemoveRuntimeCommand, RelativeSource={RelativeSource AncestorType=Window}}\" CommandParameter=\"{Binding}\"/>\n                                        </StackPanel>\n""",
)
replace_once(
    "src/DevBox.App/MainWindow.xaml",
    """                <StackPanel Grid.Row=\"0\" Margin=\"0,0,0,20\"><TextBlock Text=\"Runtimes\" FontSize=\"27\" FontWeight=\"SemiBold\"/><TextBlock Text=\"Versioned PHP, Nginx and MySQL installations with atomic current-version switching.\" Foreground=\"#6B7280\" Margin=\"0,5,0,0\"/></StackPanel>\n""",
    """                <StackPanel Grid.Row=\"0\" Margin=\"0,0,0,20\"><TextBlock Text=\"Runtimes\" FontSize=\"27\" FontWeight=\"SemiBold\"/><TextBlock Text=\"Download verified PHP/Nginx packages on demand or manage installed runtime versions.\" Foreground=\"#6B7280\" Margin=\"0,5,0,0\"/></StackPanel>\n""",
)

# Regression tests for the visible online/download state and phpMyAdmin action label.
test_path = Path("tests/DevBox.Tests/OnlineModuleDeliveryTests.cs")
test_path.write_text(
    """using DevBox.App.ViewModels;\nusing DevBox.Core.Models;\n\nnamespace DevBox.Tests;\n\npublic sealed class OnlineModuleDeliveryTests\n{\n    [Fact]\n    public void RuntimeRow_RemotePhpPackage_ExposesDownloadAction()\n    {\n        var package = new RuntimePackageEntry\n        {\n            Key = \"php\",\n            DisplayName = \"PHP\",\n            Version = \"8.5.10\",\n            Architecture = \"x64\",\n            ExecutableRelativePath = \"php-cgi.exe\",\n            DownloadUrl = \"https://example.test/php.zip\",\n            Sha256 = new string('a', 64),\n            Recommended = true\n        };\n\n        var row = new RuntimeRowViewModel(\n            new RuntimeVersionStatus(package, Installed: false, Active: false, Valid: false, RuntimeSupportState.Current),\n            Path.GetTempPath());\n\n        Assert.Equal(\"Available online\", row.Status);\n        Assert.True(row.CanDownload);\n        Assert.False(row.CanActivate);\n        Assert.False(row.CanRemove);\n        Assert.Equal(package.DownloadUrl, row.InstallPath);\n    }\n\n    [Fact]\n    public void AddonRow_NotInstalled_UsesDownloadLabel()\n    {\n        var addon = new AddonDefinition(\n            \"phpmyadmin\",\n            \"phpMyAdmin\",\n            \"Database UI\",\n            Path.Combine(Path.GetTempPath(), \"www\", \"phpmyadmin\"),\n            Path.Combine(Path.GetTempPath(), \"www\", \"phpmyadmin\", \"index.php\"),\n            \"http://phpmyadmin.test\",\n            [\"mysqli\"],\n            \"5.2.3\",\n            \"https://example.test/phpmyadmin.zip\",\n            new string('b', 64),\n            \"phpMyAdmin-5.2.3-all-languages\");\n\n        var row = new AddonRowViewModel(addon);\n        row.ApplyInstallation(installed: false);\n\n        Assert.Equal(\"Download\", row.InstallAction);\n        Assert.Equal(\"Not installed\", row.Status);\n    }\n}\n""",
    encoding="utf-8",
    newline="\n",
)

# Minimal documentation note in changelog without touching README/version metadata,
# avoiding conflicts with an in-flight release commit.
changelog = Path("CHANGELOG.md")
text = changelog.read_text(encoding="utf-8")
marker = "## Unreleased\n"
if marker in text and "online-only PHP/phpMyAdmin" not in text:
    text = text.replace(
        marker,
        marker + "\n- Manual Release can keep PHP/phpMyAdmin online-only; PHP is omitted from release packaging and both can be downloaded on demand from verified catalogs.\n- Runtimes now exposes a verified Download action for PHP, while phpMyAdmin uses Download/Reinstall in ADDONS and remains installable even before PHP is present.\n",
        1,
    )
    changelog.write_text(text, encoding="utf-8", newline="\n")
