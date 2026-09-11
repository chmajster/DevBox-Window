from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:120]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1) Snapshot restore must never delete a pre-existing database restore directory
# when overwrite=false. Track ownership of the destination before rollback cleanup.
replace_once(
    "src/DevBox.Core/Services/ProjectSnapshotService.cs",
    '''        string? databaseDestination = null;\n        string? databasePrevious = null;\n''',
    '''        string? databaseDestination = null;\n        string? databasePrevious = null;\n        var databaseDestinationOwned = false;\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectSnapshotService.cs",
    '''                    Directory.CreateDirectory(Path.GetDirectoryName(databaseDestination)!);\n                    Directory.Move(databaseStaging, databaseDestination);\n''',
    '''                    Directory.CreateDirectory(Path.GetDirectoryName(databaseDestination)!);\n                    Directory.Move(databaseStaging, databaseDestination);\n                    databaseDestinationOwned = true;\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectSnapshotService.cs",
    '''                if (databaseDestination is not null)\n                {\n                    var databasePath = databaseDestination;\n                    rollbackActions.Add(() => TryDeleteDirectory(databasePath));\n                }\n''',
    '''                if (databaseDestinationOwned && databaseDestination is not null)\n                {\n                    var databasePath = databaseDestination;\n                    rollbackActions.Add(() => TryDeleteDirectory(databasePath));\n                }\n''')

# 2) Project export with duplicate database basenames creates an archive that cannot
# be imported safely because both entries resolve to database/<same-name>. Reject it.
replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''        var dbFiles = databaseBackups?.Where(File.Exists).Select(Path.GetFullPath).ToArray() ?? Array.Empty<string>();\n        Directory.CreateDirectory(_exportRoot);\n''',
    '''        var dbFiles = databaseBackups?.Where(File.Exists).Select(Path.GetFullPath).ToArray() ?? Array.Empty<string>();\n        if (options.IncludeDatabase)\n        {\n            var duplicateBackup = dbFiles\n                .GroupBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)\n                .FirstOrDefault(group => group.Count() > 1);\n            if (duplicateBackup is not null)\n                throw new ArgumentException($"Database backup list contains duplicate file name '{duplicateBackup.Key}'.", nameof(databaseBackups));\n        }\n        Directory.CreateDirectory(_exportRoot);\n''')

# 3) Addon uninstall was destructive before config cleanup. If deleting the owned
# nginx config fails, restore the addon directory from trash instead of leaving a
# half-uninstalled addon. Nonessential temp/backup cleanup is best-effort.
replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''            var backupPath = SwapInStagingDirectory(stagingPath, addon.InstallPath);\n            try\n            {\n                ConfigureAddon(addon);\n                DeleteDirectoryIfExists(backupPath);\n            }\n            catch\n            {\n                RollbackInstallation(addon.InstallPath, backupPath);\n                if (backupPath is null)\n                {\n                    DeleteAddonNginxConfig(addon);\n                }\n                throw;\n            }\n        }\n        finally\n        {\n            DeleteDirectoryIfExists(tempRoot);\n        }\n''',
    '''            var backupPath = SwapInStagingDirectory(stagingPath, addon.InstallPath);\n            try\n            {\n                ConfigureAddon(addon);\n            }\n            catch\n            {\n                RollbackInstallation(addon.InstallPath, backupPath);\n                if (backupPath is null)\n                {\n                    DeleteAddonNginxConfig(addon);\n                }\n                throw;\n            }\n\n            TryDeleteDirectory(backupPath);\n        }\n        finally\n        {\n            TryDeleteDirectory(tempRoot);\n        }\n''')
replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''        if (Directory.Exists(addon.InstallPath))\n        {\n            var trashRoot = Path.Combine(_rootPath, "tmp", "addons", "trash");\n            Directory.CreateDirectory(trashRoot);\n            var trashPath = Path.Combine(trashRoot, $"{addon.Key}-{Guid.NewGuid():N}");\n            Directory.Move(addon.InstallPath, trashPath);\n            Directory.Delete(trashPath, recursive: true);\n        }\n\n        DeleteAddonNginxConfig(addon);\n        return Task.CompletedTask;\n''',
    '''        string? trashPath = null;\n        if (Directory.Exists(addon.InstallPath))\n        {\n            var trashRoot = Path.Combine(_rootPath, "tmp", "addons", "trash");\n            Directory.CreateDirectory(trashRoot);\n            trashPath = Path.Combine(trashRoot, $"{addon.Key}-{Guid.NewGuid():N}");\n            Directory.Move(addon.InstallPath, trashPath);\n        }\n\n        try\n        {\n            DeleteAddonNginxConfig(addon);\n        }\n        catch\n        {\n            if (trashPath is not null && Directory.Exists(trashPath) && !Directory.Exists(addon.InstallPath))\n                Directory.Move(trashPath, addon.InstallPath);\n            throw;\n        }\n\n        TryDeleteDirectory(trashPath);\n        return Task.CompletedTask;\n''')
replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''    private static void DeleteDirectoryIfExists(string? path)\n    {\n        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))\n        {\n            return;\n        }\n        Directory.Delete(path, recursive: true);\n    }\n''',
    '''    private static void DeleteDirectoryIfExists(string? path)\n    {\n        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))\n        {\n            return;\n        }\n        Directory.Delete(path, recursive: true);\n    }\n\n    private static void TryDeleteDirectory(string? path)\n    {\n        try\n        {\n            DeleteDirectoryIfExists(path);\n        }\n        catch (IOException)\n        {\n        }\n        catch (UnauthorizedAccessException)\n        {\n        }\n    }\n''')

# 4) SiteManager.Create created a new document-root directory before persisting the
# site. If metadata/config persistence failed it removed index.php but leaked the
# newly-created directory. Remove only directories created by this call and only
# when they are empty, so concurrent/user-created data is never deleted.
replace_once(
    "src/DevBox.Core/Services/SiteManager.cs",
    '''        var sites = GetSites().ToList();\n        ValidateNewSite(sites, normalizedName, normalizedDomain);\n\n        Directory.CreateDirectory(root);\n''',
    '''        var sites = GetSites().ToList();\n        ValidateNewSite(sites, normalizedName, normalizedDomain);\n\n        var rootExisted = Directory.Exists(root);\n        Directory.CreateDirectory(root);\n''')
replace_once(
    "src/DevBox.Core/Services/SiteManager.cs",
    '''        catch\n        {\n            if (scaffoldedIndex)\n                TryDeleteFile(indexPath);\n            throw;\n        }\n''',
    '''        catch\n        {\n            if (scaffoldedIndex)\n                TryDeleteFile(indexPath);\n            if (!rootExisted)\n                TryDeleteEmptyDirectory(root);\n            throw;\n        }\n''')
replace_once(
    "src/DevBox.Core/Services/SiteManager.cs",
    '''    private static void TryDeleteFile(string path)\n    {\n        try\n        {\n            if (File.Exists(path))\n                File.Delete(path);\n        }\n        catch (IOException)\n        {\n        }\n        catch (UnauthorizedAccessException)\n        {\n        }\n    }\n\n    private void ValidateLoadedSite(SiteDefinition? site)\n''',
    '''    private static void TryDeleteFile(string path)\n    {\n        try\n        {\n            if (File.Exists(path))\n                File.Delete(path);\n        }\n        catch (IOException)\n        {\n        }\n        catch (UnauthorizedAccessException)\n        {\n        }\n    }\n\n    private static void TryDeleteEmptyDirectory(string path)\n    {\n        try\n        {\n            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())\n                Directory.Delete(path, recursive: false);\n        }\n        catch (IOException)\n        {\n        }\n        catch (UnauthorizedAccessException)\n        {\n        }\n    }\n\n    private void ValidateLoadedSite(SiteDefinition? site)\n''')

# Regression: existing deterministic snapshot database restore directory must be
# preserved when overwrite=false.
snapshot_tests = Path("tests/DevBox.Tests/SnapshotHardeningTests.cs")
text = snapshot_tests.read_text(encoding="utf-8")
marker = '''    [Fact]\n    public async Task RestoreAsync_RejectsArchiveWithoutSnapshotMetadata()\n'''
insert = '''    [Fact]\n    public async Task RestoreAsync_ExistingDatabaseRestoreDirectoryWithoutOverwrite_IsPreserved()\n    {\n        var root = TemporaryRoot();\n        try\n        {\n            var source = CreateProject(root, "snapshot-db-source");\n            var backup = Path.Combine(root, "database.sql");\n            await File.WriteAllTextAsync(backup, "-- snapshot database payload");\n            var service = new ProjectSnapshotService(root);\n            var snapshot = await service.CreateAsync(\n                source,\n                new ProjectSnapshotOptions(IncludeDatabase: true),\n                [backup]);\n\n            var snapshotKey = new string(Path.GetFileNameWithoutExtension(snapshot.SnapshotPath)\n                .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')\n                .ToArray());\n            var existingDatabaseDirectory = Path.Combine(\n                root, "backups", "snapshot-restores", "snapshot-db-copy", snapshotKey);\n            Directory.CreateDirectory(existingDatabaseDirectory);\n            var markerFile = Path.Combine(existingDatabaseDirectory, "keep.txt");\n            await File.WriteAllTextAsync(markerFile, "keep-existing-backup");\n\n            await Assert.ThrowsAsync<InvalidOperationException>(() =>\n                service.RestoreAsync(snapshot.SnapshotPath, "snapshot-db-copy", overwrite: false));\n\n            Assert.True(File.Exists(markerFile));\n            Assert.Equal("keep-existing-backup", await File.ReadAllTextAsync(markerFile));\n            Assert.False(Directory.Exists(Path.Combine(root, "www", "snapshot-db-copy")));\n        }\n        finally\n        {\n            DeleteRoot(root);\n        }\n    }\n\n'''
if marker not in text or "ExistingDatabaseRestoreDirectoryWithoutOverwrite" in text:
    raise RuntimeError("SnapshotHardeningTests insertion marker mismatch")
snapshot_tests.write_text(text.replace(marker, insert + marker, 1), encoding="utf-8")

# Regression: duplicate DB basenames are rejected before creating a broken export.
bug_tests = Path("tests/DevBox.Tests/BugAuditRegressionTests.cs")
text = bug_tests.read_text(encoding="utf-8")
marker = '''    [Fact]\n    public async Task ProjectImport_RejectsOversizedTransferMetadata()\n'''
insert = '''    [Fact]\n    public async Task ProjectExport_DuplicateDatabaseBackupNames_AreRejected()\n    {\n        var root = TemporaryRoot();\n        try\n        {\n            var project = CreateProject(root, "duplicate-db-export");\n            var firstDirectory = Path.Combine(root, "db-a");\n            var secondDirectory = Path.Combine(root, "db-b");\n            Directory.CreateDirectory(firstDirectory);\n            Directory.CreateDirectory(secondDirectory);\n            var first = Path.Combine(firstDirectory, "backup.sql");\n            var second = Path.Combine(secondDirectory, "backup.sql");\n            await File.WriteAllTextAsync(first, "-- first");\n            await File.WriteAllTextAsync(second, "-- second");\n            var destination = Path.Combine(root, "duplicate.devbox-project.zip");\n            var service = new ProjectTransferService(root);\n\n            var error = await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync(\n                project,\n                new ProjectSnapshotOptions(IncludeDatabase: true),\n                [first, second],\n                destination));\n\n            Assert.Contains("duplicate file name", error.Message, StringComparison.OrdinalIgnoreCase);\n            Assert.False(File.Exists(destination));\n        }\n        finally\n        {\n            DeleteRoot(root);\n        }\n    }\n\n'''
if marker not in text or "DuplicateDatabaseBackupNames" in text:
    raise RuntimeError("BugAuditRegressionTests insertion marker mismatch")
bug_tests.write_text(text.replace(marker, insert + marker, 1), encoding="utf-8")

# Regression: failed vhost deletion must roll the addon directory back from trash.
addon_tests = Path("tests/DevBox.Tests/AddonInstallerTests.cs")
text = addon_tests.read_text(encoding="utf-8")
marker = '''    [Fact]\n    public async Task UninstallAsync_PathOutsideWww_IsRejected()\n'''
insert = '''    [Fact]\n    public async Task UninstallAsync_VhostDeletionFailure_RestoresAddonDirectory()\n    {\n        var root = TempRoot();\n        string? vhost = null;\n        try\n        {\n            var addon = Definition(root, new string('0', 64));\n            Directory.CreateDirectory(addon.InstallPath);\n            File.WriteAllText(addon.EntryPointPath, "keep");\n            using var installer = new AddonInstaller(root, new HttpClient(new StaticResponseHandler(Array.Empty<byte>())));\n            await installer.RepairAsync(addon);\n            vhost = Path.Combine(root, "config", "nginx", "sites-enabled", "phpmyadmin.test.conf");\n            File.SetAttributes(vhost, File.GetAttributes(vhost) | FileAttributes.ReadOnly);\n\n            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => installer.UninstallAsync(addon));\n\n            Assert.True(Directory.Exists(addon.InstallPath));\n            Assert.True(File.Exists(addon.EntryPointPath));\n            Assert.Equal("keep", File.ReadAllText(addon.EntryPointPath));\n        }\n        finally\n        {\n            if (vhost is not null && File.Exists(vhost))\n                File.SetAttributes(vhost, FileAttributes.Normal);\n            DeleteRoot(root);\n        }\n    }\n\n'''
if marker not in text or "VhostDeletionFailure_RestoresAddonDirectory" in text:
    raise RuntimeError("AddonInstallerTests insertion marker mismatch")
addon_tests.write_text(text.replace(marker, insert + marker, 1), encoding="utf-8")

# Regression: failed site persistence must not leak a just-created document root.
site_tests = Path("tests/DevBox.Tests/SiteManagerTests.cs")
text = site_tests.read_text(encoding="utf-8")
marker = '''    [Fact]\n    public void Delete_RemovesMetadataAndVhostButKeepsFilesByDefault()\n'''
insert = '''    [Fact]\n    public void Create_PersistenceFailure_RemovesNewEmptyDocumentRoot()\n    {\n        var root = TemporaryRoot();\n        try\n        {\n            var config = Path.Combine(root, "config");\n            Directory.CreateDirectory(config);\n            Directory.CreateDirectory(Path.Combine(config, "sites.json"));\n            var manager = new SiteManager(root);\n            var documentRoot = Path.Combine(root, "www", "failed-site");\n\n            var error = Record.Exception(() => manager.Create("failed-site"));\n\n            Assert.NotNull(error);\n            Assert.False(Directory.Exists(documentRoot));\n            Assert.False(File.Exists(manager.GetNginxConfigPath("failed-site.test")));\n        }\n        finally\n        {\n            DeleteRoot(root);\n        }\n    }\n\n'''
if marker not in text or "PersistenceFailure_RemovesNewEmptyDocumentRoot" in text:
    raise RuntimeError("SiteManagerTests insertion marker mismatch")
site_tests.write_text(text.replace(marker, insert + marker, 1), encoding="utf-8")
