[CmdletBinding()]
param(
    [string]$Application,
    [string]$Report = (Join-Path $PSScriptRoot 'artifacts\tests\raw-tests.json')
)

$ErrorActionPreference = 'Stop'
if (!$Application) {
    $wechatNotesTestOutput = Join-Path $PSScriptRoot 'artifacts\test-build'
    & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $wechatNotesTestOutput -SkipShortcut
    $Application = Join-Path $wechatNotesTestOutput 'WeChatNotes.dll'
}
$wechatNotesReportPath = [IO.Path]::GetFullPath($Report)
New-Item -ItemType Directory -Path (Split-Path $wechatNotesReportPath) -Force | Out-Null
dotnet $Application --raw-test $wechatNotesReportPath
if ($LASTEXITCODE -ne 0) { throw "合成测试失败，报告：$wechatNotesReportPath" }
$wechatNotesTests = @(Get-Content -LiteralPath $wechatNotesReportPath -Raw | ConvertFrom-Json)
if ($wechatNotesTests.Count -eq 0 -or @($wechatNotesTests | Where-Object { !$_.passed }).Count -gt 0) { throw '测试报告未通过。' }
Write-Host "$($wechatNotesTests.Count) 项合成测试通过；未连接个人微信。"
