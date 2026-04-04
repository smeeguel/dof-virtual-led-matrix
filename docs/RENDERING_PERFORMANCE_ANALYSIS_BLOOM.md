# Bloom Rendering Performance Analysis (Refactored Pipeline)

Date: 2026-04-04  
Scope: `GpuInstancedMatrixRenderer` bloom path and CPU bloom path in `MatrixFrameRasterComposer`.

## 1) What the current refactored bloom does

The refactored bloom path is structurally the same on CPU and GPU:

1. Early-out when bloom disabled or both strengths are zero.
2. Early-out when there are no lit LEDs.
3. Build emissive downsample buffer from rendered BGRA pixels using threshold + soft-knee weighting.
4. Duplicate emissive into near/far lanes.
5. Separable box blur both lanes.
6. Composite near+far back onto full-resolution surface using bilinear sampling and ROI bounds.

This structure is visible in both CPU (`ApplyBloomIfEnabled`) and GPU (`ApplyBloomIfEnabled` + `TryApplyGpuBloom`) implementations.

## 2) Hotspot analysis

## CPU bloom hotspots

### A. Per-frame full clear of bloom source buffer
`DownsampleEmissive` clears the entire `_screenBloomSourceRgb` array every frame before recomputing values. This is O(downsamplePixels) memory bandwidth every frame, even when only a tiny ROI is active.

### B. Near/far lane duplication copies the full buffer twice
Both CPU and GPU-fallback paths do:
- `Array.Copy(source -> near)`
- `Array.Copy(source -> far)`

This is two additional full-array memory passes before blur.

### C. Composite performs 6 bilinear samples per output pixel
`CompositeBloom` samples near RGB (3 channels) + far RGB (3 channels) separately using `SampleBilinear`. This is expensive for large ROI, and has poor cache locality because channels are interleaved in `float[]`.

### D. Blur is bandwidth-heavy with float RGB AoS layout
Separable blur is O(N) by sliding window (good), but each iteration touches 3 channel values in an interleaved layout. For CPU SIMD/cache, SoA is generally better than AoS for this operation pattern.

## GPU bloom hotspots

### E. GPU bloom currently round-trips through CPU memory
`TryApplyGpuBloom` uploads `_bgra` to `_gpuBaseTexture`, runs shader passes, then copies the composited GPU texture to a staging texture and maps it back to CPU (`CopyResource` + `Map(Read)`), then writes to `WriteableBitmap`.

This readback is usually the dominant synchronization stall; it drains GPU/CPU parallelism and often introduces frame pacing jitter.

### F. Frequent constant-buffer map/unmap per pass
Every pass updates constants through map/unmap. This is not catastrophic, but still adds driver overhead.

### G. Fixed blur radius clamp and dual-lane pipeline always paid
Even with small near/far radii, the code still executes both blur lanes and both passes, unless bloom strengths are zero.

## 3) Big-win optimization proposals

## GPU renderer bloom (largest wins first)

### 1. Eliminate GPU readback by presenting GPU output directly (major)
**Current:** GPU -> CPU readback -> `WriteableBitmap`.  
**Proposed:** render/composite directly into a swapchain-backed surface (`D3DImage`, `SwapChainPanel` equivalent for WPF interop, or shared DXGI surface path), so final frame stays on GPU.

**Why it wins:** removes the heaviest synchronization point and large memory copy every frame.  
**Expected impact:** typically the single biggest win; often 2x+ frame-time reduction in GPU bloom mode on mid-tier hardware.

### 2. Move base dot rasterization to GPU and keep bloom fully GPU-native (major)
Currently dots are rasterized on CPU into `_bgra`, then uploaded. Shift dot sprite/instanced quad rasterization to GPU, feeding only logical LED colors as input.

**Why it wins:** eliminates CPU raster cost and upload cost, and enables true end-to-end GPU pipeline.

### 3. Merge near/far blur into shared mip/dual-kernel path (medium-high)
Instead of two separate blur lanes from the same bright-pass texture, derive near/far glow via:
- one blur pyramid (mips), or
- one wide blur + one narrow tweak pass.

**Why it wins:** fewer full-screen passes and less bandwidth.

### 4. Avoid per-pass map/unmap constants with cached CB ring or push-style updates (small-medium)
Batch/pack constants and update once per frame where possible.

## CPU renderer bloom (largest wins first)

### 1. ROI-scoped processing with dirty-rect tracking (major)
Track active emissive bounds from dot raster stage and process only that ROI in:
- downsample extraction,
- near/far blur windows,
- compositing.

The compositor already uses a padded ROI; extend this principle to all earlier stages and avoid full-buffer clears/copies.

**Why it wins:** matrix content is usually sparse (many dark pixels), so ROI can be much smaller than full surface.

### 2. Remove full-buffer duplication for near/far lanes (major)
Use one immutable source buffer and blur into near/far destinations directly, or reuse ping/pong scratch with pass ordering.

**Why it wins:** removes two full-array copies per frame and improves memory bandwidth headroom.

### 3. SIMD-friendly data layout for blur/composite (medium-high)
Switch bloom buffers from AoS (`RGBRGB...`) to SoA (`RRR...`, `GGG...`, `BBB...`) or vectorized structs aligned for `Vector<float>`.

**Why it wins:** better vectorization and cache-line utilization during separable blur and bilinear sampling.

### 4. Fast-path for low radii and low strengths (medium)
Skip far lane when effective far radius=0 or far strength is below epsilon; similarly skip near lane when negligible.

**Why it wins:** common quality settings may not need both lanes every frame.

## 4) Suggested implementation order

1. **GPU:** remove readback/present directly from GPU surface.  
2. **CPU:** eliminate near/far full-buffer copies and add ROI-scoped downsample/blur.  
3. **CPU:** introduce SoA/SIMD bloom buffers.  
4. **GPU:** consolidate blur passes (mip or shared-kernel strategy).

This ordering maximizes user-visible frame-time improvements earliest.

## 5) Validation plan

For each change, capture:
- avg / p95 frame time,
- max stutter frame,
- CPU usage,
- dropped-frame count,
- test matrices: 32x8, 128x32, 128x64,
- content patterns: sparse flashes vs full-white stress.

Recommend adding lightweight per-stage timers around:
- downsample,
- blur near,
- blur far,
- composite,
- GPU readback (until removed).

## 6) Risk notes

- Direct GPU presentation in WPF requires careful interop and device-loss handling.
- ROI optimizations must preserve halo continuity at ROI edges (pad by blur radius).
- SIMD layout changes should be introduced behind tests that compare output images against baseline tolerances.
