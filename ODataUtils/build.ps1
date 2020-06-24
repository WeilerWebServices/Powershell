Push-Location $PSScriptRoot

Push-Location 'src\PowerShell.Cmdletization.OData'

dotnet restore
dotnet build

Pop-Location

rmdir 'Microsoft.PowerShell.ODataUtils' -Recurse -Force -ErrorAction SilentlyCOntinue

$moduleVersionLine = Select-String -Path 'src\ModuleGeneration\Microsoft.PowerShell.ODataUtils.psd1' -Pattern 'ModuleVersion' -SimpleMatch
$moduleVersion = $moduleVersionLine.Line.Split("'")[1]
$moduleDir = mkdir "Microsoft.PowerShell.ODataUtils\$moduleVersion"
copy 'src\ModuleGeneration\*' $moduleDir -Recurse -Force

$moduleDirCoreCLR = Join-Path $moduleDir 'CoreCLR'
$moduleDirFullCLR = Join-Path $moduleDir 'FullCLR'
mkdir $moduleDirCoreCLR | Out-Null
mkdir $moduleDirFullCLR | Out-Null

copy 'src\PowerShell.Cmdletization.OData\bin\Debug\net451\PowerShell.Cmdletization.OData.dll' $moduleDirFullCLR
copy 'src\PowerShell.Cmdletization.OData\bin\Debug\netstandard1.6\PowerShell.Cmdletization.OData.dll' $moduleDirCoreCLR

Pop-Location