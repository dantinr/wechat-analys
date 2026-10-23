[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$silkOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $silkOutput -Force | Out-Null
$silkInputs = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'silk') -Recurse -File | Sort-Object FullName)
$silkInputs += Get-Item -LiteralPath (Join-Path $PSScriptRoot 'decoder.c'), $PSCommandPath
$silkHashes = $silkInputs | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
$silkStamp = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($silkHashes -join "`n"))))
$silkDll = Join-Path $silkOutput 'WeChatNotesSilk.dll'
$silkStampPath = Join-Path $silkOutput 'source.sha256'
if ((Test-Path -LiteralPath $silkDll) -and (Test-Path -LiteralPath $silkStampPath) -and (Get-Content -LiteralPath $silkStampPath -Raw).Trim() -eq $silkStamp) { return }

$silkVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $silkVswhere)) { throw '构建语音解码组件需要 Visual Studio C++ 桌面开发工作负载。' }
$silkVs = & $silkVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$silkVs) { throw '未找到 MSVC x64 工具，请安装 Visual Studio C++ 桌面开发工作负载。' }
$silkVcvars = Join-Path $silkVs 'VC/Auxiliary/Build/vcvars64.bat'
$silkSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'silk/src') -File -Filter '*.c' | Sort-Object Name)
$silkSources += Get-Item -LiteralPath (Join-Path $PSScriptRoot 'decoder.c')
$silkArguments = @('/nologo', '/LD', '/O2', '/MT', '/TC', '/D_CRT_SECURE_NO_WARNINGS', '/DSKP_SILK_BUILD', '/W0',
    ('/I"' + (Join-Path $PSScriptRoot 'silk/interface') + '"'), ('/I"' + (Join-Path $PSScriptRoot 'silk/src') + '"'),
    ('/Fo"' + $silkOutput + '\\"'), ('/Fe"' + $silkDll + '"'))
$silkArguments += $silkSources | ForEach-Object { '"' + $_.FullName + '"' }
$silkResponse = Join-Path $silkOutput 'compile.rsp'
[IO.File]::WriteAllLines($silkResponse, $silkArguments, [Text.Encoding]::Unicode)
$silkBatch = Join-Path $silkOutput 'compile.cmd'
$silkBatchText = '@echo off' + [Environment]::NewLine + 'chcp 65001 >nul' + "`r`ncall `"" + $silkVcvars.Replace('%', '%%') + "`" >nul`r`nif errorlevel 1 exit /b 1`r`ncl.exe @`"" + $silkResponse.Replace('%', '%%') + "`"`r`nexit /b %errorlevel%`r`n"
[IO.File]::WriteAllText($silkBatch, $silkBatchText, [Text.Encoding]::Default)
$silkBuildLog = & $env:ComSpec /d /c "`"$silkBatch`"" 2>&1
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $silkDll)) { $silkBuildLog | Write-Host; throw 'SILK 解码组件编译失败。' }
[IO.File]::WriteAllText($silkStampPath, $silkStamp, [Text.Encoding]::ASCII)
Write-Host 'SILK x64 解码组件已从附带源码编译。'
