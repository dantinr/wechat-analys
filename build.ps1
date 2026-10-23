[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist\WeChatNotes'),
    [switch]$SkipShortcut
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw '请在 Windows 上构建。' }
$wechatNotesProject = Join-Path $PSScriptRoot 'src\WeChatNotes\WeChatNotes.csproj'
$wechatNotesOutput = [IO.Path]::GetFullPath($OutputDirectory)
dotnet publish $wechatNotesProject -c Release -r win-x64 --self-contained false -p:UseAppHost=false -p:ContinuousIntegrationBuild=true "-p:PathMap=$PSScriptRoot=/_/" -o $wechatNotesOutput
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
if (!$SkipShortcut) {
    $wechatNotesShell = New-Object -ComObject WScript.Shell
    $wechatNotesShortcut = $wechatNotesShell.CreateShortcut((Join-Path $wechatNotesOutput '启动微信需求记录.lnk'))
    $wechatNotesShortcut.TargetPath = (Get-Command dotnet).Source
    $wechatNotesShortcut.Arguments = '"' + (Join-Path $wechatNotesOutput 'WeChatNotes.dll') + '"'
    $wechatNotesShortcut.WorkingDirectory = $wechatNotesOutput
    $wechatNotesShortcut.WindowStyle = 1
    $wechatNotesShortcut.Description = '微信需求记录 - 本地采集与需求整理'
    $wechatNotesShortcut.Save()
}
$wechatNotesLauncher = "@echo off`r`ndotnet `"%~dp0WeChatNotes.dll`"`r`nif errorlevel 1 pause`r`n"
[System.IO.File]::WriteAllText((Join-Path $wechatNotesOutput '启动.cmd'), $wechatNotesLauncher, [System.Text.Encoding]::ASCII)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $wechatNotesOutput '使用说明.md') -Force
foreach ($wechatNotesNotice in @('LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $wechatNotesNotice) -Destination $wechatNotesOutput -Force
}
$wechatNotesSource = Join-Path $wechatNotesOutput 'source'
$wechatNotesSourceProject = Join-Path $wechatNotesSource 'src\WeChatNotes'
New-Item -ItemType Directory -Path $wechatNotesSourceProject -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\WeChatNotes') -File | Copy-Item -Destination $wechatNotesSourceProject -Force
foreach ($wechatNotesSourceFile in @('LICENSE', 'README.md', 'THIRD_PARTY_NOTICES.md', 'CONTRIBUTING.md', 'SECURITY.md', 'CHANGELOG.md', 'build.ps1', 'test.ps1', 'package.ps1', 'global.json', '.gitignore', '.gitattributes', '.editorconfig')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $wechatNotesSourceFile) -Destination $wechatNotesSource -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '.github') -Destination $wechatNotesSource -Recurse -Force
Write-Host "程序已生成：$wechatNotesOutput"
