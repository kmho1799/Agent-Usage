$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $projectRoot "publish"
$compilerPaths = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
)
$compiler = $compilerPaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $compiler)
{
    throw "Inno Setup was not found."
}

dotnet publish (Join-Path $projectRoot "AgentUsage.csproj") -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0)
{
    exit $LASTEXITCODE
}

& $compiler (Join-Path $PSScriptRoot "AgentUsage.iss")
exit $LASTEXITCODE
