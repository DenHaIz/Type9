# Сборка Type9 в один исполняемый файл.
#   .\build\build.ps1            — оба варианта
#   .\build\build.ps1 -Variant full
# Результат: dist\Type9.exe (самодостаточный, .NET не нужен) и dist\Type9-lite.exe (нужен .NET Desktop Runtime 10).
param(
    [ValidateSet('all', 'full', 'lite')] [string] $Variant = 'all',
    [string] $Configuration = 'Release'
)
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

$common = @('-c', $Configuration, '-r', 'win-x64', '-nologo', '-p:PublishSingleFile=true', '-p:DebugType=none', '-p:GenerateDocumentationFile=false')

if ($Variant -in 'all', 'full') {
    $out = Join-Path $root 'artifacts\full'
    # Без EnableCompressionInSingleFile: файл крупнее (~150 МБ), но сборки отображаются в память, а не
    # распаковываются — резидентная программа держит ~20 МБ вместо ~125 МБ.
    dotnet publish $proj @common --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
    Copy-Item (Join-Path $out 'Type9.exe') (Join-Path $dist 'Type9.exe') -Force
}
if ($Variant -in 'all', 'lite') {
    $out = Join-Path $root 'artifacts\lite'
    dotnet publish $proj @common --self-contained false -o $out
    Copy-Item (Join-Path $out 'Type9.exe') (Join-Path $dist 'Type9-lite.exe') -Force
}

Get-ChildItem $dist -Filter *.exe | ForEach-Object { '{0,-16} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
