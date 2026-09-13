$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$path = 'src/DevBox.Core/Services/EnvironmentLockService.cs'
$text = (Get-Content -LiteralPath $path -Raw).Replace("`r`n", "`n")
$old = @'
        cancellationToken.ThrowIfCancellationRequested();
        if (!desired.Database.Engine.Equals("none", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(desired.Database.Version))
        {
            var database = _databaseRuntimes.GetInstances(desired.Database.Engine)
'@
$new = @'
        if (!desired.Database.Engine.Equals("none", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(desired.Database.Version))
        {
            var database = _databaseRuntimes.GetInstances(desired.Database.Engine)
'@
$old = $old.Replace("`r`n", "`n")
$new = $new.Replace("`r`n", "`n")
if (-not $text.Contains($old)) {
    throw 'GetDrift cancellation correction anchor was not found.'
}
$text = $text.Replace($old, $new)
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($path, $text, $utf8)
Write-Host 'Removed the accidental cancellation checkpoint from synchronous GetDrift.'
