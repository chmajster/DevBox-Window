$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($scriptPath in @('scripts/round13-patch.ps1', 'scripts/round13-extra.ps1')) {
    $lines = [System.Collections.Generic.List[string]](Get-Content -LiteralPath $scriptPath)
    $signature = 'function Replace-Required([string]$Text, [string]$Old, [string]$New, [string]$Label) {'
    $index = $lines.IndexOf($signature)
    if ($index -lt 0) {
        throw "Replace-Required helper was not found in $scriptPath"
    }
    $lines.Insert($index + 1, '    $Old = $Old.Replace("`r`n", "`n")')
    $lines.Insert($index + 2, '    $New = $New.Replace("`r`n", "`n")')
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($scriptPath, ($lines -join "`n") + "`n", $utf8)
}

Write-Host 'Normalized round 13 patch helpers for the Windows runner.'
