# Build Glance-offline-mt.zip from onnx-community Opus-MT quantized assets.
# Usage: pwsh packaging/offline-mt/build-pack.ps1
$ErrorActionPreference = "Stop"
$OutRoot = Join-Path $PSScriptRoot "staging"
$ZipOut = Join-Path $PSScriptRoot "Glance-offline-mt.zip"
$Hf = "https://huggingface.co"

function Get-HfFile([string]$Repo, [string]$RelPath, [string]$Dest, [long]$MinBytes = 1000) {
    $url = "$Hf/$Repo/resolve/main/$RelPath"
    New-Item -ItemType Directory -Force -Path (Split-Path $Dest) | Out-Null
    for ($i = 1; $i -le 8; $i++) {
        Write-Host "GET ($i) $url"
        # -C - resumes partial downloads (HF drops are common on large ONNX).
        curl.exe -L --ssl-no-revoke --retry 5 --retry-all-errors --retry-delay 2 -C - -o $Dest $url
        $len = if (Test-Path $Dest) { (Get-Item $Dest).Length } else { 0 }
        if ($LASTEXITCODE -eq 0 -and $len -ge $MinBytes) { return }
        Start-Sleep (3 * $i)
    }
    throw "download failed: $url"
}

if (Test-Path $OutRoot) { Remove-Item $OutRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null

$dirs = @{
    "en-zh" = "onnx-community/opus-mt-en-zh"
    "zh-en" = "onnx-community/opus-mt-zh-en"
}

foreach ($pair in $dirs.GetEnumerator()) {
    $dir = Join-Path $OutRoot $pair.Key
    $repo = $pair.Value
    Get-HfFile $repo "onnx/encoder_model_quantized.onnx" (Join-Path $dir "encoder_model_quantized.onnx") 40MB
    Get-HfFile $repo "onnx/decoder_model_merged_quantized.onnx" (Join-Path $dir "decoder_model_merged_quantized.onnx") 180MB
    Get-HfFile $repo "source.spm" (Join-Path $dir "source.spm") 700KB
    Get-HfFile $repo "target.spm" (Join-Path $dir "target.spm") 700KB
    Get-HfFile $repo "vocab.json" (Join-Path $dir "vocab.json") 1MB
}

@{
    version = "1"
    models  = @("en-zh", "zh-en")
    source  = "onnx-community/opus-mt-en-zh + onnx-community/opus-mt-zh-en"
    license = "Apache-2.0 / CC-BY-4.0 (Helsinki-NLP OPUS-MT)"
} | ConvertTo-Json | Set-Content (Join-Path $OutRoot "pack.json") -Encoding utf8

if (Test-Path $ZipOut) { Remove-Item $ZipOut -Force }
Compress-Archive -Path (Join-Path $OutRoot "*") -DestinationPath $ZipOut -CompressionLevel Optimal
Get-Item $ZipOut | Format-List FullName, Length
Write-Host "Done. Upload with:"
Write-Host "  gh release create offline-mt-v1 -R vce27/Glance --title `"Glance offline MT v1`" --notes `"Opus-MT EN↔ZH quantized ONNX pack`" `"$ZipOut`""
