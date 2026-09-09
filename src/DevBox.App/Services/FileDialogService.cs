using Microsoft.Win32;

namespace DevBox.App.Services;

public interface IFileDialogService
{
    string? OpenSqlFile();
    string? SaveSqlFile(string suggestedFileName);
}

public sealed class FileDialogService : IFileDialogService
{
    public string? OpenSqlFile()
    {
        var dialog = new OpenFileDialog
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
        var dialog = new SaveFileDialog
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
}
