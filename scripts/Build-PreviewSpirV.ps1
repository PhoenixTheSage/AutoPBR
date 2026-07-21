param(
    [string]$GlslangValidator = "glslangValidator"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$shaderRoot = Join-Path $repo "src/AutoPBR.App/Rendering/Shaders"
$outputRoot = Join-Path $shaderRoot "spv"
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$assets = @(
    @{
        Source = "genesis_indirect_compact.comp"
        Stage = "comp"
        Output = "genesis_indirect_compact.comp.spv"
    }
)

foreach ($asset in $assets) {
    $source = Join-Path $shaderRoot $asset.Source
    $output = Join-Path $outputRoot $asset.Output
    & $GlslangValidator -G --target-env opengl --auto-map-locations --auto-map-bindings -S $asset.Stage -o $output $source
    if ($LASTEXITCODE -ne 0) {
        throw "glslangValidator failed for $($asset.Source) with exit code $LASTEXITCODE"
    }
}

Write-Host "Built $($assets.Count) OpenGL SPIR-V preview shader asset(s) in $outputRoot"
