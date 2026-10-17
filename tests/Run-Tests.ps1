$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path ([System.IO.Path]::GetTempPath()) ('ProcessesKillerTests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testOutput | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $testOutput 'ProcessesKiller.ServiceTests.exe'
& $compiler /nologo /warnaserror+ /target:exe "/out:$testExe" /reference:System.dll /reference:System.Core.dll (Join-Path $repositoryRoot 'PortQueryService.cs') (Join-Path $repositoryRoot 'ProcessTerminationService.cs') (Join-Path $PSScriptRoot 'ServiceTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Service tests failed.' }
Write-Output "Test executable: $testExe"
$buildTool = & 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe' -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $buildTool) { throw 'Visual Studio MSBuild was not found.' }
& $buildTool (Join-Path $repositoryRoot 'ProcessesKiller.csproj') /nologo /v:minimal /p:Configuration=Debug "/p:OutDir=$testOutput\" "/p:BaseIntermediateOutputPath=$testOutput\obj\" "/p:IntermediateOutputPath=$testOutput\obj\Debug\" /p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
    Start-Sleep -Milliseconds 500
    & $buildTool (Join-Path $repositoryRoot 'ProcessesKiller.csproj') /nologo /v:minimal /p:Configuration=Debug "/p:OutDir=$testOutput\" "/p:BaseIntermediateOutputPath=$testOutput\obj\" "/p:IntermediateOutputPath=$testOutput\obj\Debug\" /p:UseSharedCompilation=false
}
if ($LASTEXITCODE -ne 0) { throw 'Application test build failed.' }
$windowTestExe = Join-Path $testOutput 'ProcessesKiller.WindowTests.exe'
$wpfRuntime = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$wpfReferences = @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll') | ForEach-Object { "/reference:$(Join-Path $wpfRuntime $_)" }
& $compiler /nologo /warnaserror+ /target:exe "/out:$windowTestExe" /reference:System.dll /reference:System.Core.dll /reference:System.Xaml.dll "/reference:$(Join-Path $testOutput 'ProcessesKiller.exe')" $wpfReferences (Join-Path $PSScriptRoot 'WindowTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'WPF component test compilation failed.' }
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'App.config') -Destination "$windowTestExe.config"
& $windowTestExe $testOutput
if ($LASTEXITCODE -ne 0) { throw 'WPF component tests failed.' }
