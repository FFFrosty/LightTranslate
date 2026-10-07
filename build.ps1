param(
    [string]$Output = (Join-Path $PSScriptRoot 'artifacts\LightTranslate')
)

$ErrorActionPreference = 'Stop'

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

$outputPath = [IO.Path]::GetFullPath($Output)
$coreTests = Join-Path $PSScriptRoot 'tests\CoreTests\CherryTranslate.CoreTests.csproj'
$uiTests = Join-Path $PSScriptRoot 'tests\UiTests\CherryTranslate.UiTests.csproj'
$appProject = Join-Path $PSScriptRoot 'src\App\CherryTranslate.App.csproj'

Invoke-DotNet `
    -Arguments @('run', '--project', $coreTests, '-c', 'Release') `
    -FailureMessage 'Core tests failed.'

Invoke-DotNet `
    -Arguments @('run', '--project', $uiTests, '-c', 'Release') `
    -FailureMessage 'UI tests failed.'

Invoke-DotNet `
    -Arguments @('publish', $appProject, '-c', 'Release', '--self-contained', 'false', '-o', $outputPath) `
    -FailureMessage 'Publish failed.'

Copy-Item `
    -LiteralPath (Join-Path $PSScriptRoot 'README.md') `
    -Destination (Join-Path $outputPath '使用说明.md') `
    -Force

Copy-Item `
    -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') `
    -Destination (Join-Path $outputPath 'LICENSE') `
    -Force

Copy-Item `
    -LiteralPath (Join-Path $PSScriptRoot 'docs') `
    -Destination $outputPath `
    -Recurse -Force

Write-Output "Published to $outputPath"
