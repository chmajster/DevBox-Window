namespace DevBox.App.Services;

public interface IFileDialogService
{
    string? OpenSqlFile();
    string? SaveSqlFile(string suggestedFileName);
    string? OpenXdebugDll();
    string? SelectFolder(string description, string? initialPath = null);
}

public sealed class FileDialogService : IFileDialogService
{
    public string? OpenSqlFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select SQL backup",
            Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveSqlFile(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save database backup",
            Filter = "SQL files (*.sql)|*.sql",
            FileName = suggestedFileName,
            AddExtension = true,
            DefaultExt = ".sql",
            OverwritePrompt = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? OpenXdebugDll()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Xdebug PHP extension",
            Filter = "Xdebug DLL (php_xdebug*.dll)|php_xdebug*.dll|DLL files (*.dll)|*.dll",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SelectFolder(string description, string? initialPath = null)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = !string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath)
                ? initialPath
                : string.Empty
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}
