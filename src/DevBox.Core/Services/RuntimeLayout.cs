namespace DevBox.Core.Services;

public static class RuntimeLayout
{
    public static void EnsureInitialized(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var directories = new[]
        {
            "config/nginx/sites-enabled",
            "config/php",
            "config/mysql",
            "logs",
            "tmp",
            "www",
            "www/phpmyadmin",
            "data/mysql",
            "runtime/nginx/current",
            "runtime/php/current",
            "runtime/mysql/current"
        };

        foreach (var relative in directories)
        {
            Directory.CreateDirectory(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        WriteIfMissing(Path.Combine(rootPath, "config", "nginx", "nginx.conf"), NginxConfig);
        WriteIfMissing(Path.Combine(rootPath, "config", "nginx", "fastcgi_params"), FastCgiParams);
        WriteIfMissing(Path.Combine(rootPath, "config", "nginx", "sites-enabled", "phpmyadmin.test.conf"), PhpMyAdminSiteConfig);
        WriteIfMissing(Path.Combine(rootPath, "config", "php", "php.ini"), PhpIni);
        WriteIfMissing(Path.Combine(rootPath, "config", "mysql", "my.ini"), MySqlIni);
        WriteIfMissing(Path.Combine(rootPath, "www", "index.html"), IndexHtml);
    }

    private static void WriteIfMissing(string path, string content)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, content.Replace("\n", Environment.NewLine));
        }
    }

    private const string NginxConfig = """
worker_processes  1;
pid tmp/nginx.pid;
error_log logs/nginx-error.log;

events {
    worker_connections 1024;
}

http {
    default_type application/octet-stream;
    access_log logs/nginx-access.log;
    sendfile on;
    keepalive_timeout 65;

    include config/nginx/sites-enabled/*.conf;

    server {
        listen 80 default_server;
        server_name localhost;
        root www;
        index index.html index.php;

        location / {
            try_files $uri $uri/ =404;
        }

        location ~ \.php$ {
            include config/nginx/fastcgi_params;
            fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;
            fastcgi_pass 127.0.0.1:9084;
        }
    }
}
""";

    private const string PhpMyAdminSiteConfig = """
server {
    listen 80;
    server_name phpmyadmin.test;
    root www/phpmyadmin;
    index index.php index.html;

    location / {
        try_files $uri $uri/ /index.php?$query_string;
    }

    location ~ \.php$ {
        include config/nginx/fastcgi_params;
        fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;
        fastcgi_pass 127.0.0.1:9084;
    }
}
""";

    private const string FastCgiParams = """
fastcgi_param QUERY_STRING $query_string;
fastcgi_param REQUEST_METHOD $request_method;
fastcgi_param CONTENT_TYPE $content_type;
fastcgi_param CONTENT_LENGTH $content_length;
fastcgi_param REQUEST_URI $request_uri;
fastcgi_param DOCUMENT_URI $document_uri;
fastcgi_param DOCUMENT_ROOT $document_root;
fastcgi_param SERVER_PROTOCOL $server_protocol;
fastcgi_param REQUEST_SCHEME $scheme;
fastcgi_param HTTPS $https if_not_empty;
fastcgi_param GATEWAY_INTERFACE CGI/1.1;
fastcgi_param SERVER_SOFTWARE nginx/$nginx_version;
fastcgi_param REMOTE_ADDR $remote_addr;
fastcgi_param REMOTE_PORT $remote_port;
fastcgi_param SERVER_ADDR $server_addr;
fastcgi_param SERVER_PORT $server_port;
fastcgi_param SERVER_NAME $server_name;
fastcgi_param REDIRECT_STATUS 200;
""";

    private const string PhpIni = """
[PHP]
display_errors=On
log_errors=On
error_log=logs/php-error.log
memory_limit=256M
upload_max_filesize=64M
post_max_size=64M
max_execution_time=120
date.timezone=UTC
extension_dir=runtime/php/current/ext
extension=mysqli
extension=pdo_mysql
extension=mbstring
extension=curl
extension=openssl
""";

    private const string MySqlIni = """
[mysqld]
basedir=runtime/mysql/current
datadir=data/mysql
port=3306
bind-address=127.0.0.1
log-error=logs/mysql-error.log
""";

    private const string IndexHtml = """
<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>DevBox Windows</title></head>
<body><h1>DevBox Windows</h1><p>Nginx is running.</p></body>
</html>
""";
}
