# Сборка Type9 в один самодостаточный исполняемый файл (установленный .NET не нужен).
#   .\build\build.ps1
# Результат: dist\Type9.exe
param([string] $Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src\Type9\Type9.csproj'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

# Иконка генерируется из векторного логотипа самим приложением
$ico = Join-Path $root 'src\Type9\Resources\type9.ico'
if (-not (Test-Path $ico)) {
    dotnet build $proj -c $Configuration -nologo -v q
    $exe = Join-Path $root "src\Type9\bin\$Configuration\net10.0-windows\Type9.exe"
    Start-Process $exe -ArgumentList "--export-icon `"$ico`"" -Wait
}

$out = Join-Path $root 'artifacts\full'
# ReadyToRun — заранее скомпилированный код: быстрый старт и мгновенная первая подсказка без JIT.
# Без EnableCompressionInSingleFile: файл крупнее, но сборки отображаются в память, а не распаковываются.
dotnet publish $proj -c $Configuration -r win-x64 -nologo --self-contained true `
    -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:GenerateDocumentationFile=false -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }
Copy-Item (Join-Path $out 'Type9.exe') (Join-Path $dist 'Type9.exe') -Force

Get-ChildItem $dist -Filter *.exe | ForEach-Object { '{0,-12} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
