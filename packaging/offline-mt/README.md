# Glance offline MT pack (英↔中)

Opus-MT quantized ONNX models for Glance’s **离线** text engine.

## Build

```powershell
pwsh packaging/offline-mt/build-pack.ps1
```

Produces `Glance-offline-mt.zip` with:

```
en-zh/encoder_model_quantized.onnx
en-zh/decoder_model_merged_quantized.onnx
en-zh/source.spm
en-zh/target.spm
en-zh/vocab.json
zh-en/...
pack.json
```

## Publish

```powershell
gh release create offline-mt-v1 -R vce27/Glance `
  --title "Glance offline MT v1" `
  --notes "Opus-MT EN↔ZH quantized ONNX language pack for Glance offline engine." `
  packaging/offline-mt/Glance-offline-mt.zip
```

App download URL (hardcoded):

`https://github.com/vce27/Glance/releases/download/offline-mt-v1/Glance-offline-mt.zip`

## License

Weights from Helsinki-NLP OPUS-MT via onnx-community exports (Apache-2.0 / CC-BY-4.0).
