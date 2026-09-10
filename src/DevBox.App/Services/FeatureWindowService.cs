using System.Windows;
using DevBox.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DevBox.App.Services;

public interface IFeatureWindowService
{
    void ShowPhp(Window owner);
    void ShowSitePhp(Window owner);
    void ShowDatabases(Window owner);
    void ShowSsl(Window owner);
    void ShowSetup(Window owner);
    void ShowTools(Window owner);
    void ShowProjects(Window owner);
    void ShowEnvironmentCenter(Window owner);
    void ShowUpdates(Window owner);
    void ShowSettings(Window owner);
}

public sealed class FeatureWindowService(IServiceProvider serviceProvider) : IFeatureWindowService
{
    public void ShowPhp(Window owner) => Show<PhpWindow>(owner);
    public void ShowSitePhp(Window owner) => Show<SitePhpWindow>(owner);
    public void ShowDatabases(Window owner) => Show<DatabaseWindow>(owner);
    public void ShowSsl(Window owner) => Show<SslWindow>(owner);
    public void ShowSetup(Window owner) => Show<FirstRunWindow>(owner);
    public void ShowTools(Window owner) => Show<ToolsWindow>(owner);
    public void ShowProjects(Window owner)
    {
        var window = ActivatorUtilities.CreateInstance<ProjectManagerWindow>(serviceProvider);
        window.Owner = owner;
        window.ShowDialog();
    }
    public void ShowEnvironmentCenter(Window owner)
    {
        var window = new EnvironmentCenterWindow(new EnvironmentCenterViewModel()) { Owner = owner };
        window.ShowDialog();
    }
    public void ShowUpdates(Window owner) => Show<UpdateWindow>(owner);
    public void ShowSettings(Window owner) => Show<SettingsWindow>(owner);

    private void Show<TWindow>(Window owner) where TWindow : Window
    {
        var window = serviceProvider.GetRequiredService<TWindow>();
        window.Owner = owner;
        window.ShowDialog();
    }
}
