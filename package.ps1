[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$wechatNotesPackageRoot = Join-Path $PSScriptRoot 'artifacts\packages'
$wechatNotesStage = Join-Path $wechatNotesPackageRoot ('staging-' + [Guid]::NewGuid().ToString('N'))
$wechatNotesApp = Join-Path $wechatNotesStage 'WeChatNotes'
[xml]$wechatNotesProject = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\WeChatNotes\WeChatNotes.csproj') -Raw
$wechatNotesVersion = [string]$wechatNotesProject.Project.PropertyGroup.Version
if ($wechatNotesVersion -notmatch '^\d+\.\d+\.\d+([-.][A-Za-z0-9.]+)?$') { throw '项目版本号无效。' }
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $wechatNotesApp -SkipShortcut
& (Join-Path $PSScriptRoot 'test.ps1') -Application (Join-Path $wechatNotesApp 'WeChatNotes.dll') -Report (Join-Path $wechatNotesPackageRoot 'raw-tests.json')

# Fresh staging only: never zip an existing user's installation or data directory.
$wechatNotesForbidden = @(Get-ChildItem -LiteralPath $wechatNotesApp -Recurse -File | Where-Object {
    $_.Extension -in @('.db', '.sqlite3', '.lnk', '.dmp', '.log') -or $_.Name -match '(?i)(^keys.*\.json$|raw-probe|诊断|本机只读|\.db-(wal|shm)$)'
})
if ($wechatNotesForbidden.Count -gt 0) { throw '发布目录含本机数据或诊断文件，已停止打包。' }
$wechatNotesBinaryZip = Join-Path $wechatNotesPackageRoot "WeChatNotes-$wechatNotesVersion-win-x64.zip"
$wechatNotesSourceZip = Join-Path $wechatNotesPackageRoot "WeChatNotes-$wechatNotesVersion-source.zip"
Compress-Archive -LiteralPath $wechatNotesApp -DestinationPath $wechatNotesBinaryZip -Force
Compress-Archive -LiteralPath (Join-Path $wechatNotesApp 'source') -DestinationPath $wechatNotesSourceZip -Force
$wechatNotesChecksums = foreach ($wechatNotesArchive in @($wechatNotesBinaryZip, $wechatNotesSourceZip)) {
    $wechatNotesHash = Get-FileHash -LiteralPath $wechatNotesArchive -Algorithm SHA256
    '{0}  {1}' -f $wechatNotesHash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($wechatNotesArchive)
}
[IO.File]::WriteAllLines((Join-Path $wechatNotesPackageRoot 'SHA256SUMS.txt'), $wechatNotesChecksums, [Text.UTF8Encoding]::new($false))
Write-Host "发布包已生成：$wechatNotesPackageRoot"
