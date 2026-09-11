from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:100]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


# Fix the syntax left after removing the destructive cancellation catch: the
# stop sequence no longer needs a nested try block once cancellation is allowed
# to propagate naturally while the managed process remains tracked.
replace_once(
    "src/DevBox.Core/Services/ProcessManager.cs",
    '''            try\n            {\n                var timeout = definition.ShutdownTimeout ?? TimeSpan.FromSeconds(5);\n''',
    '''            var timeout = definition.ShutdownTimeout ?? TimeSpan.FromSeconds(5);\n''')
replace_once(
    "src/DevBox.Core/Services/ProcessManager.cs",
    '''                }\n            }\n            AppendLog(managed, "APP", "Stopped.");\n''',
    '''                }\n            AppendLog(managed, "APP", "Stopped.");\n''')

# Optional managed-service configuration must not prevent the core application
# from starting. The main dashboard falls back to Nginx/PHP/MySQL and reports
# the invalid optional configuration.
replace_once(
    "src/DevBox.App/ViewModels/MainWindowViewModel.cs",
    '''        _definitions = _serviceCatalog.GetDefaultServices().ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);\n        _addonDefinitions = _addonCatalog.GetDefaultAddons().ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);\n''',
    '''        try\n        {\n            _definitions = _serviceCatalog.GetDefaultServices().ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);\n        }\n        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)\n        {\n            Trace.TraceError($"Managed service configuration is invalid; continuing with core services: {ex}");\n            _definitions = _serviceCatalog.GetCoreServices().ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);\n            _dialogs.Warning(\n                "Managed service configuration invalid",\n                $"Optional managed services were ignored so DevBox can continue with Nginx, PHP and MySQL. {ex.Message}");\n        }\n        _addonDefinitions = _addonCatalog.GetDefaultAddons().ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);\n''')

# Core feature windows must not parse optional services.json at all.
feature = Path("src/DevBox.App/ViewModels/FeatureViewModels.cs")
text = feature.read_text(encoding="utf-8")
replacements = {
    'serviceCatalog.GetDefaultServices().First(service => service.Key == "php")': 'serviceCatalog.GetCoreServices().First(service => service.Key == "php")',
    'serviceCatalog.GetDefaultServices().First(service => service.Key == "mysql")': 'serviceCatalog.GetCoreServices().First(service => service.Key == "mysql")',
    'serviceCatalog.GetDefaultServices().ToDictionary(service => service.Key, StringComparer.OrdinalIgnoreCase)': 'serviceCatalog.GetCoreServices().ToDictionary(service => service.Key, StringComparer.OrdinalIgnoreCase)',
}
for old, new in replacements.items():
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"FeatureViewModels.cs: expected one match for {old!r}, got {count}")
    text = text.replace(old, new, 1)
feature.write_text(text, encoding="utf-8")

replace_once(
    "src/DevBox.App/ViewModels/SitePhpWindowViewModel.cs",
    '''        _nginx = serviceCatalog.GetDefaultServices().First(service => service.Key == "nginx");\n''',
    '''        _nginx = serviceCatalog.GetCoreServices().First(service => service.Key == "nginx");\n''')

# Tray autostart also falls back to core services instead of aborting all startup
# when an optional service manifest is malformed.
replace_once(
    "src/DevBox.App/Services/TrayService.cs",
    '''        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)\n        {\n            ShowError("Service configuration invalid", ex.Message);\n            return;\n        }\n''',
    '''        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)\n        {\n            definitions = _catalog.GetCoreServices();\n            ShowError(\n                "Managed service configuration invalid",\n                $"Optional managed services were ignored; core services will continue. {ex.Message}");\n        }\n''')

# Diagnostics should expose the bad optional-service configuration as a warning,
# not throw and take down the dashboard during construction.
replace_once(
    "src/DevBox.Core/Services/DiagnosticsService.cs",
    '''        foreach (var service in _serviceCatalog.GetDefaultServices())\n        {\n''',
    '''        IReadOnlyList<ServiceDefinition> services;\n        try\n        {\n            services = _serviceCatalog.GetDefaultServices();\n        }\n        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)\n        {\n            results.Add(new DiagnosticCheck(\n                "Configuration",\n                "Managed services configuration",\n                false,\n                $"Optional managed services were ignored: {ex.Message}",\n                DiagnosticSeverity.Warning));\n            services = _serviceCatalog.GetCoreServices();\n        }\n\n        foreach (var service in services)\n        {\n''')

# Regression test: invalid optional services are diagnosed while core runtime
# checks remain available.
test = Path("tests/DevBox.Tests/StartupRuntimeAuditTests.cs")
text = test.read_text(encoding="utf-8")
marker = '''    [Fact]\n    public void RuntimeCatalog_RejectsArchiveRootTraversal()\n'''
insert = '''    [Fact]\n    public void Diagnostics_InvalidManagedServiceManifest_FallsBackToCoreServices()\n    {\n        var root = Path.Combine(Path.GetTempPath(), "devbox-invalid-services", Guid.NewGuid().ToString("N"));\n        try\n        {\n            RuntimeLayout.EnsureInitialized(root);\n            File.WriteAllText(Path.Combine(root, "config", "services.json"), "{ invalid json");\n            var catalog = new ServiceCatalog(root);\n            using var processes = new ProcessManager();\n            var diagnostics = new DiagnosticsService(root, catalog, processes);\n\n            var checks = diagnostics.Run();\n\n            Assert.Contains(checks, check => check.Name == "Managed services configuration" && !check.Success);\n            Assert.Contains(checks, check => check.Name == "Nginx executable");\n            Assert.Contains(checks, check => check.Name == "PHP FastCGI executable");\n            Assert.Contains(checks, check => check.Name == "MySQL executable");\n        }\n        finally\n        {\n            if (Directory.Exists(root)) Directory.Delete(root, true);\n        }\n    }\n\n'''
if marker not in text or "Diagnostics_InvalidManagedServiceManifest_FallsBackToCoreServices" in text:
    raise RuntimeError("StartupRuntimeAuditTests diagnostics insertion marker mismatch")
test.write_text(text.replace(marker, insert + marker, 1), encoding="utf-8")
