from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if text.count(old) != 1:
        raise RuntimeError(f"expected one match in {path}, found {text.count(old)}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


replace_once(
    "src/DevBox.Core/Services/DatabaseManager.cs",
    '''        var backupPath = Path.Combine(tempDirectory, $"{source}-{Guid.NewGuid():N}.sql");
        try
        {
            await BackupAsync(source, backupPath, options, cancellationToken).ConfigureAwait(false);
            await RestoreAsync(destination, backupPath, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception original)
        {
            try
            {
                using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await DropDatabaseAsync(destination, options, rollbackTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    $"Database clone failed and rollback of '{destination}' also failed.",
                    original,
                    rollbackError);
            }
            throw;
        }
''',
    '''        var backupPath = Path.Combine(tempDirectory, $"{source}-{Guid.NewGuid():N}.sql");
        var restoreStarted = false;
        try
        {
            await BackupAsync(source, backupPath, options, cancellationToken).ConfigureAwait(false);
            restoreStarted = true;
            await RestoreAsync(destination, backupPath, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception original)
        {
            if (!restoreStarted)
                throw;

            try
            {
                using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await DropDatabaseAsync(destination, options, rollbackTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    $"Database clone failed and rollback of '{destination}' also failed.",
                    original,
                    rollbackError);
            }
            throw;
        }
''')

replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''            DatabaseBackups = dbFiles.Select(path => Path.GetFileName(path)!).ToArray()
''',
    '''            DatabaseBackups = options.IncludeDatabase
                ? dbFiles.Select(path => Path.GetFileName(path)!).ToArray()
                : Array.Empty<string>()
''')

print("follow-up patch applied")
