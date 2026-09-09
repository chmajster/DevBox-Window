using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ServiceCatalog
{
    public ServiceCatalog(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath { get; }

    public IReadOnlyList<ServiceDefinition> GetDefaultServices()
    {
        var nginxExe = At("runtime", "nginx", "current", "nginx.exe");
        var phpExe = At("runtime", "php", "current", "php-cgi.exe");
        var mysqlExe = At("runtime", "mysql", "current", "bin", "mysqld.exe");
        var mysqlAdminExe = At("runtime", "mysql", "current", "bin", "mysqladmin.exe");

        return new[]
        {
            new ServiceDefinition(
                "nginx", "Nginx", nginxExe,
                new[] { "-p", NormalizePrefix(RootPath), "-c", "config/nginx/nginx.conf" },
                RootPath, 80, "current",
                nginxExe,
                new[] { "-p", NormalizePrefix(RootPath), "-c", "config/nginx/nginx.conf", "-s", "quit" },
                TimeSpan.FromSeconds(4),
                At("logs", "nginx-process.log")),

            new ServiceDefinition(
                "php", "PHP FastCGI", phpExe,
                new[] { "-b", "127.0.0.1:9084", "-c", At("config", "php", "php.ini") },
                RootPath, 9084, "current",
                LogPath: At("logs", "php-process.log")),

            new ServiceDefinition(
                "mysql", "MySQL", mysqlExe,
                new[] { $"--defaults-file={At("config", "mysql", "my.ini")}" },
                RootPath, 3306, "current",
                mysqlAdminExe,
                new[] { "--protocol=tcp", "--host=127.0.0.1", "--port=3306", "--user=root", "--connect-timeout=3", "shutdown" },
                TimeSpan.FromSeconds(6),
                At("logs", "mysql-process.log"))
        };
    }

    private string At(params string[] parts) => Path.Combine(new[] { RootPath }.Concat(parts).ToArray());

    private static string NormalizePrefix(string path) => path.Replace('\\', '/').TrimEnd('/') + "/";
}
