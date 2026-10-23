[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist\WeChatNotes'),
    [switch]$SkipShortcut,
    [switch]$Standalone
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw '请在 Windows 上构建。' }
if ($Standalone -and !$PSBoundParameters.ContainsKey('OutputDirectory')) {
    $OutputDirectory = Join-Path $PSScriptRoot 'dist\WeChatNotes-exe'
}
$wechatNotesProject = Join-Path $PSScriptRoot 'src\WeChatNotes\WeChatNotes.csproj'
$wechatNotesOutput = [IO.Path]::GetFullPath($OutputDirectory)
$wechatNotesPublishArguments = @('publish', $wechatNotesProject, '-c', 'Release', '-r', 'win-x64', '-p:ContinuousIntegrationBuild=true', "-p:PathMap=$PSScriptRoot=/_/", '-o', $wechatNotesOutput)
if ($Standalone) {
    $wechatNotesPublishArguments += @('--self-contained', 'true', '-p:UseAppHost=true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false')
}
else {
    $wechatNotesPublishArguments += @('--self-contained', 'false', '-p:UseAppHost=false', '-p:PublishSingleFile=false')
}
dotnet @wechatNotesPublishArguments
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
if ($Standalone) {
    $wechatNotesAssets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\WeChatNotes\obj\project.assets.json') -Raw | ConvertFrom-Json
    $wechatNotesRuntimeDependencies = $wechatNotesAssets.project.frameworks.PSObject.Properties.Value | ForEach-Object { $_.downloadDependencies } | Where-Object { $_.name -in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64') }
    foreach ($wechatNotesRuntimeDependency in $wechatNotesRuntimeDependencies) {
        $wechatNotesRuntimeVersion = ($wechatNotesRuntimeDependency.version.Trim('[]') -split ',')[0].Trim()
        $wechatNotesRuntimePackage = $null
        foreach ($wechatNotesPackageFolder in $wechatNotesAssets.packageFolders.PSObject.Properties.Name) {
            $wechatNotesCandidate = Join-Path (Join-Path $wechatNotesPackageFolder $wechatNotesRuntimeDependency.name.ToLowerInvariant()) $wechatNotesRuntimeVersion
            if (Test-Path -LiteralPath $wechatNotesCandidate) { $wechatNotesRuntimePackage = $wechatNotesCandidate; break }
        }
        if (!$wechatNotesRuntimePackage) { throw '未找到独立运行环境的许可证。' }
        $wechatNotesRuntimeNotices = Join-Path $wechatNotesOutput ('licenses\' + $wechatNotesRuntimeDependency.name)
        New-Item -ItemType Directory -Path $wechatNotesRuntimeNotices -Force | Out-Null
        Get-ChildItem -LiteralPath $wechatNotesRuntimePackage -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' } | Copy-Item -Destination $wechatNotesRuntimeNotices -Force
    }
}
if (!$SkipShortcut) {
    $wechatNotesShell = New-Object -ComObject WScript.Shell
    $wechatNotesShortcut = $wechatNotesShell.CreateShortcut((Join-Path $wechatNotesOutput '启动微信需求记录.lnk'))
    if ($Standalone) { $wechatNotesShortcut.TargetPath = Join-Path $wechatNotesOutput 'WeChatNotes.exe' }
    else {
        $wechatNotesShortcut.TargetPath = (Get-Command dotnet).Source
        $wechatNotesShortcut.Arguments = '"' + (Join-Path $wechatNotesOutput 'WeChatNotes.dll') + '"'
    }
    $wechatNotesShortcut.WorkingDirectory = $wechatNotesOutput
    $wechatNotesShortcut.WindowStyle = 1
    $wechatNotesShortcut.Description = '微信需求记录 - 本地采集与需求整理'
    $wechatNotesShortcut.Save()
}
if ($Standalone) { $wechatNotesLauncher = "@echo off`r`n`"%~dp0WeChatNotes.exe`"`r`nif errorlevel 1 pause`r`n" }
else { $wechatNotesLauncher = "@echo off`r`ndotnet `"%~dp0WeChatNotes.dll`"`r`nif errorlevel 1 pause`r`n" }
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
