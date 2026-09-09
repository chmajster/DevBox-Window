using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace DevBox.App.Services;

public interface IFeatureWindowService
{
    void ShowPhp(Window owner);
    void ShowDatabases(Window owner);
    void ShowSsl(Window owner);
    void ShowSetup(Window owner);
}

public sealed class FeatureWindowService(IServiceProvider serviceProvider) : IFeatureWindowService
{
    public void ShowPhp(Window owner) => Show<PhpWindow>(owner);
    public void ShowDatabases(Window owner) => Show<DatabaseWindow>(owner);
    public void ShowSsl(Window owner) => Show<SslWindow>(owner);
    public void ShowSetup(Window owner) => Show<FirstRunWindow>(owner);

    private void Show<TWindow>(Window owner) where TWindow : Window
    {
        var window = serviceProvider.GetRequiredService<TWindow>();
        window.Owner = owner;
        window.ShowDialog();
    }
}
