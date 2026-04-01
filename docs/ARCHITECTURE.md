# Architecture

## Renderer pipeline

The app now uses a renderer backend abstraction (`IMatrixRenderer`) at the app/UI boundary.

Protocol/parsing in `VirtualDofMatrix.Core` remains unchanged: serial host emits `FramePresentation` snapshots. The window converts payload bytes to `Rgb24[]` and hands frames to renderer backends through:

- `Initialize(renderSurface, width, height, dotStyleConfig)`
- `UpdateFrame(ReadOnlySpan<Rgb24>)`
- `Resize(viewportWidth, viewportHeight)`
- `Render()`
- `Dispose()`

## GPU-first backend

Default backend is `gpu` (`GpuInstancedMatrixRenderer`). It:

- creates a static instance buffer sized to `width * height`
- builds logical-to-raster mapping once using `MatrixFrameIndexMap`
- uploads dynamic frame colors each frame via `GpuFrameUpload.BuildBgraFrame`
- issues one `DrawInstanced` call for all dots

CPU bitmap (`WriteableBitmapMatrixRenderer`) remains available for diagnostics/fallback.
