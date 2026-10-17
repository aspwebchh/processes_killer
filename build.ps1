<#
.SYNOPSIS
    编译 ProcessesKiller 项目并输出到指定的 bin 目录。

.DESCRIPTION
    自动定位系统中的 MSBuild 工具，将 ProcessesKiller WPF 应用程序直接编译到
    D:\dev\processes_killer\bin\Release（或指定的配置目录）。
    编译前会自动检查目标程序是否正在运行以避免文件锁定冲突。

.PARAMETER Configuration
    编译配置，可选 'Release' 或 'Debug'，默认为 'Release'。

.PARAMETER Rebuild
    执行全新完整重新编译 (/t:Rebuild)。

.PARAMETER Clean
    仅清理编译产物 (/t:Clean)。

.PARAMETER RunTests
    编译完成后自动运行项目测试套件 (tests\Run-Tests.ps1)。

.EXAMPLE
    .\build.ps1
    直接编译 Release 版本到 bin\Release。

.EXAMPLE
    .\build.ps1 -Rebuild
    强制重新编译 Release 版本。

.EXAMPLE
    .\build.ps1 -Configuration Debug
    编译 Debug 版本到 bin\Debug。

.EXAMPLE
    .\build.ps1 -RunTests
    编译 Release 版本并运行完整测试。
#>

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Rebuild,

    [switch]$Clean,

    [switch]$RunTests
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$projectPath = Join-Path $repoRoot 'ProcessesKiller.csproj'
$outputDir = Join-Path $repoRoot "bin\$Configuration"
$targetExe = Join-Path $outputDir 'ProcessesKiller.exe'

if (-not (Test-Path $projectPath)) {
    throw "未找到项目文件: $projectPath"
}

# 1. 定位 MSBuild
$msbuild = $null

$pathMsbuild = Get-Command msbuild.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if ($pathMsbuild -and (Test-Path $pathMsbuild)) {
    $msbuild = $pathMsbuild
}

if (-not $msbuild) {
    $vswhereCandidates = @()
    if (${env:ProgramFiles(x86)}) {
        $vswhereCandidates += (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe')
    }
    if ($env:ProgramFiles) {
        $vswhereCandidates += (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe')
    }

    foreach ($vp in $vswhereCandidates) {
        if (Test-Path $vp) {
            $found = & $vp -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
            if ($found -and (Test-Path $found)) {
                $msbuild = $found
                break
            }
        }
    }
}

if (-not $msbuild) {
    $commonVsPaths = @(
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($candidate in $commonVsPaths) {
        if (Test-Path $candidate) {
            $msbuild = $candidate
            break
        }
    }
}

if (-not $msbuild) {
    throw "未找到 MSBuild.exe。请确认已安装 Visual Studio（含 .NET 桌面开发组件）或 Visual Studio Build Tools。"
}

# 2. 检查目标可执行文件是否正在运行（避免文件锁定错误 MSB3027）
if (-not $Clean) {
    $targetExeFullName = [System.IO.Path]::GetFullPath($targetExe)
    $runningProcesses = Get-Process -Name 'ProcessesKiller' -ErrorAction SilentlyContinue
    if ($runningProcesses) {
        foreach ($p in $runningProcesses) {
            $procPath = $null
            try {
                $procPath = $p.Path
            } catch {
                Write-Warning "检测到正在运行的 ProcessesKiller (PID: $($p.Id))，无法读取完整路径。若为本次输出文件可能导致编译写入失败。"
            }

            if ($procPath -and [string]::Equals($procPath, $targetExeFullName, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "目标程序正在运行中 (PID: $($p.Id)，路径: $procPath)。请先关闭或结束该进程后再执行编译。"
            }
        }
    }
}

# 3. 确定构建目标
$target = if ($Clean) {
    'Clean'
} elseif ($Rebuild) {
    'Rebuild'
} else {
    'Build'
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " ProcessesKiller 构建脚本" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "MSBuild 路径 : $msbuild"
Write-Host "目标项目     : $projectPath"
Write-Host "编译配置     : $Configuration"
Write-Host "构建目标     : $target"
Write-Host "输出目录     : $outputDir"
Write-Host "------------------------------------------"

$msbuildArgs = @(
    $projectPath,
    "/t:$target",
    "/p:Configuration=$Configuration",
    "/p:Platform=AnyCPU",
    "/p:OutputPath=$outputDir/",
    "/v:minimal",
    "/nologo"
)

& $msbuild @msbuildArgs
if ($LASTEXITCODE -ne 0 -and -not $Clean -and -not $Rebuild) {
    Start-Sleep -Milliseconds 500
    & $msbuild @msbuildArgs
}
if ($LASTEXITCODE -ne 0) {
    throw "构建失败，MSBuild 返回错误码: $LASTEXITCODE"
}

if ($Clean) {
    Write-Host "`n清理完成: $outputDir" -ForegroundColor Green
    return
}

# 4. 产物校验
if (-not (Test-Path $targetExe)) {
    throw "未在预期位置找到生成的主程序: $targetExe"
}

$fileItem = Get-Item $targetExe
$configFile = "$targetExe.config"
$hasConfig = Test-Path $configFile

Write-Host "`n------------------------------------------"
Write-Host "构建成功!" -ForegroundColor Green
Write-Host "主程序路径 : $($fileItem.FullName)" -ForegroundColor Green
Write-Host "程序大小   : $([math]::Round($fileItem.Length / 1KB, 2)) KB ($($fileItem.Length) 字节)" -ForegroundColor Green
Write-Host "修改时间   : $($fileItem.LastWriteTime)" -ForegroundColor Green
if ($hasConfig) {
    Write-Host "配置文件   : $configFile (已同步)" -ForegroundColor Green
} else {
    Write-Warning "未在输出目录找到配置文件: $configFile"
}
Write-Host "==========================================" -ForegroundColor Cyan

# 5. 可选执行测试套件
if ($RunTests) {
    $testsScript = Join-Path $repoRoot 'tests\Run-Tests.ps1'
    if (Test-Path $testsScript) {
        Write-Host "`n开始执行自动化测试套件..." -ForegroundColor Cyan
        & $testsScript
        if ($LASTEXITCODE -ne 0) {
            throw "测试执行未通过。"
        }
        Write-Host "`n测试全部通过!" -ForegroundColor Green
    } else {
        Write-Warning "未找到测试脚本: $testsScript"
    }
}