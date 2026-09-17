# Isolated Windows preview regression

This opt-in executable hosts actual application preview controls in a separate MAUI/WinUI window. It references an explicitly supplied compiled `RichChat.dll`; it never calls the production `MauiProgram`, production application, single-instance registration, account services, secure storage, cache services, or network.

Only with explicit current authorization for visible native testing, run from the repository root after building the application into an isolated directory:

```powershell
pwsh ./scripts/test-image-preview.ps1 -AppBinaryDirectory './.verify/preview-isolated-fix/Debug/net10.0-windows10.0.19041.0/win-x64' -RunVisibleNativeTests
```

Fixtures and diagnostics stay under a unique `.verify/preview-native-*` directory. The runner terminates only its retained child process on timeout. Without the explicit switch, it fails before building or launching anything. This host is excluded from ordinary Fast/Full verification.

`fixed` validates decoded PNG/JPEG/4096x3072 pixels, the actual transparent GIF frame renderer, idle rendering, programmatic zoom/pan/reset, resizing, 20 open/unload cycles with detached input, and cancellation when a delayed image closes. Programmatic transform checks do not validate physical pointer routing or mouse capture outside the window.

Against a preserved older application assembly, diagnostic modes isolate the former media-control implementation:

- `old`: original `IsPreview=true` input and native clipping.
- `render-only`: `IsPreview=false`.
- `render-preview`: `IsPreview=true`, then detach old input and restore the prior clip after loading.
- `events-only`: retain old pointer events; remove the old size handler and native clip.
- `clip-only`: remove old input and explicitly set only a native rectangle clip.
- `transform-only`: remove old input and clip, then apply MAUI scale and translation.

Use `-ExpectCrash` only when intentionally reproducing an old crash. A nonzero exit alone is not a root-cause assertion: compare phase logs and Windows event HRESULT/module evidence. Old probes modify private state through reflection for controlled isolation, and require the corresponding historical fields and methods.
