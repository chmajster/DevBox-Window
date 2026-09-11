from pathlib import Path

source = Path('.github/workflows/release.yml')
text = source.read_text(encoding='utf-8')

replacements = [
('''          $currentText = $match.Groups['version'].Value\n          $parts = $currentText.Split('.')\n          $major = [int]$parts[0]\n          $minor = [int]$parts[1]\n          $patch = [int]$parts[2]\n''', '''          $sourceVersion = $match.Groups['version'].Value\n          git fetch origin --tags --force\n          $latestTag = git tag --list 'v*' --sort=-v:refname | Where-Object { $_ -match '^v\\d+\\.\\d+\\.\\d+$' } | Select-Object -First 1\n          $currentText = if ([string]::IsNullOrWhiteSpace($latestTag)) { '0.0.0' } else { $latestTag.Substring(1) }\n          $parts = $currentText.Split('.')\n          $major = [int]$parts[0]\n          $minor = [int]$parts[1]\n          $patch = [int]$parts[2]\n'''),
('''          git fetch origin --tags --force\n          $existingTag = git tag --list $releaseTag\n''', '''          $existingTag = git tag --list $releaseTag\n'''),
('''          "- Current version: ``$currentText``" >> $env:GITHUB_STEP_SUMMARY\n''', '''          "- Latest published version: ``$currentText``" >> $env:GITHUB_STEP_SUMMARY\n          "- Source development version: ``$sourceVersion``" >> $env:GITHUB_STEP_SUMMARY\n'''),
('''            'Current application version: `\\d+\\.\\d+\\.\\d+`\\.',\n            ('Current application version: `' + $env:DEVBOX_VERSION + '`.'),\n''', '''            'Current application version: `\\d+\\.\\d+\\.\\d+`(?: \\(development; latest published release remains `\\d+\\.\\d+\\.\\d+`\\))?\\.',\n            ('Current application version: `' + $env:DEVBOX_VERSION + '`.'),\n'''),
('''      - name: Build installer\n        shell: pwsh\n        run: |\n          $iscc = "${env:ProgramFiles(x86)}\\Inno Setup 6\\ISCC.exe"\n          & $iscc "/DMyAppVersion=$env:DEVBOX_VERSION" "/DPublishDir=$pwd\\publish\\win-x64" "/DOutputDir=$pwd\\artifacts" "installer\\DevBox.iss"\n          if ($LASTEXITCODE -ne 0) {\n            throw "Inno Setup failed with exit code $LASTEXITCODE"\n          }\n''', '''      - name: Build installers\n        shell: pwsh\n        run: |\n          $iscc = "${env:ProgramFiles(x86)}\\Inno Setup 6\\ISCC.exe"\n          & $iscc "/DMyAppVersion=$env:DEVBOX_VERSION" "/DMyAppRid=win-x64" "/DMyArchitecturesAllowed=x64compatible" "/DMyArchitecturesInstallMode=x64compatible" "/DPublishDir=$pwd\\publish\\win-x64" "/DOutputDir=$pwd\\artifacts" "installer\\DevBox.iss"\n          if ($LASTEXITCODE -ne 0) { throw "Inno Setup x64 build failed with exit code $LASTEXITCODE" }\n          & $iscc "/DMyAppVersion=$env:DEVBOX_VERSION" "/DMyAppRid=win-arm64" "/DMyArchitecturesAllowed=arm64" "/DMyArchitecturesInstallMode=arm64" "/DPublishDir=$pwd\\publish\\win-arm64" "/DOutputDir=$pwd\\artifacts" "installer\\DevBox.iss"\n          if ($LASTEXITCODE -ne 0) { throw "Inno Setup ARM64 build failed with exit code $LASTEXITCODE" }\n'''),
('''          $installer = "artifacts/DevBox-$env:DEVBOX_VERSION-win-x64-setup.exe"\n          & $signtool.FullName sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com /f $env:SIGNING_CERTIFICATE_PATH /p $env:SIGNING_CERTIFICATE_PASSWORD $installer\n          if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed for installer.' }\n''', '''          $installers = @(\n            "artifacts/DevBox-$env:DEVBOX_VERSION-win-x64-setup.exe",\n            "artifacts/DevBox-$env:DEVBOX_VERSION-win-arm64-setup.exe"\n          )\n          foreach ($installer in $installers) {\n            & $signtool.FullName sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com /f $env:SIGNING_CERTIFICATE_PATH /p $env:SIGNING_CERTIFICATE_PASSWORD $installer\n            if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $installer" }\n          }\n''')
]

for old, new in replacements:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'Expected one release workflow match, found {count}: {old[:80]!r}')
    text = text.replace(old, new, 1)

Path('.github/release-updated.yml').write_text(text, encoding='utf-8', newline='\n')
