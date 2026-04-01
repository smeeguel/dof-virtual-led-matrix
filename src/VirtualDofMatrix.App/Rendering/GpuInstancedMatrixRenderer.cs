using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using VirtualDofMatrix.Core;
using static Vortice.Direct3D11.D3D11;

namespace VirtualDofMatrix.App.Rendering;

public sealed class GpuInstancedMatrixRenderer : IMatrixRenderer
{
    private const int ColorChannels = 3;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Buffer? _instanceBuffer;
    private ID3D11Texture2D? _frameTexture;
    private ID3D11ShaderResourceView? _frameSrv;
    private readonly object _gate = new();
    private Image? _host;
    private WriteableBitmap? _fallbackBitmap;
    private int[] _logicalToRaster = Array.Empty<int>();
    private Rgb24[] _stagedFrame = Array.Empty<Rgb24>();
    private float[] _mappedRgb = Array.Empty<float>();
    private float[] _workingRgb = Array.Empty<float>();
    private float[] _smoothedRgb = Array.Empty<float>();
    private float[] _thresholdRgb = Array.Empty<float>();
    private float[] _smallBlurRgb = Array.Empty<float>();
    private float[] _wideBlurRgb = Array.Empty<float>();
    private float[] _blurScratchRgb = Array.Empty<float>();
    private readonly byte[] _colorLut = new byte[256];
    private int _downsampleWidth;
    private int _downsampleHeight;
    private DotStyleConfig? _style;
    private double _lutBrightness = double.NaN;
    private double _lutGamma = double.NaN;
    private bool _lutSoftKneeEnabled;
    private double _lutSoftKneeStart = double.NaN;
    private double _lutSoftKneeStrength = double.NaN;
    private int _width;
    private int _height;

    public string BackendName => "gpu";

    public bool UsesImageHost => true;

    public void Initialize(MatrixRendererSurface renderSurface, int width, int height, DotStyleConfig dotStyleConfig)
    {
        _host = renderSurface.BitmapHost;
        renderSurface.PrimitiveCanvas.Children.Clear();
        _width = width;
        _height = height;
        _style = dotStyleConfig;
        _logicalToRaster = MatrixFrameIndexMap.BuildLogicalToRasterMap(width, height, dotStyleConfig.Mapping);
        EnsureWorkingBuffers(width * height);

        var hr = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 },
            out _device,
            out _,
            out _context);

        if (hr.Failure || _device is null || _context is null)
        {
            throw new InvalidOperationException($"GPU renderer initialization failed: HRESULT=0x{hr.Code:X8}");
        }

        var instanceData = new uint[checked(width * height)];
        for (var i = 0; i < instanceData.Length; i++)
        {
            instanceData[i] = (uint)i;
        }

        var byteWidth = (uint)(instanceData.Length * sizeof(uint));
        var instanceDesc = new BufferDescription(byteWidth, BindFlags.VertexBuffer, ResourceUsage.Default);
        _instanceBuffer = _device.CreateBuffer(instanceDesc);

        _frameTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.R8G8B8A8_UNorm,
            BindFlags = BindFlags.ShaderResource,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        _frameSrv = _device.CreateShaderResourceView(_frameTexture);

        _fallbackBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _host.Source = _fallbackBitmap;
        _host.Stretch = Stretch.Fill;

        Console.WriteLine($"[renderer] gpu initialized adapter=hardware leds={width * height}");
    }

    public void UpdateFrame(ReadOnlySpan<Rgb24> logicalFrame)
    {
        lock (_gate)
        {
            _stagedFrame = logicalFrame.ToArray();
        }
    }

    public void Resize(double viewportWidth, double viewportHeight)
    {
    }

    public void Render()
    {
        if (_context is null || _frameTexture is null || _fallbackBitmap is null)
        {
            return;
        }

        Rgb24[] frame;
        lock (_gate)
        {
            frame = _stagedFrame;
        }

        var data = BuildEnhancedBgraFrame(frame);

        var map = _context.Map(_frameTexture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.Copy(data, 0, map.DataPointer, data.Length);
        _context.Unmap(_frameTexture, 0);

        _context.DrawInstanced(4u, (uint)(_width * _height), 0u, 0u);

        _fallbackBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, _width, _height), data, _width * 4, 0);
    }

    public void Dispose()
    {
        _frameSrv?.Dispose();
        _frameTexture?.Dispose();
        _instanceBuffer?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }

    private byte[] BuildEnhancedBgraFrame(Rgb24[] frame)
    {
        if (_style is null)
        {
            return GpuFrameUpload.BuildBgraFrame(frame, _logicalToRaster, _width, _height);
        }

        var matrixCapacity = _width * _height;
        EnsureWorkingBuffers(matrixCapacity);
        Array.Clear(_mappedRgb, 0, _mappedRgb.Length);

        var count = Math.Min(frame.Length, matrixCapacity);
        for (var logical = 0; logical < count; logical++)
        {
            var mapped = logical * ColorChannels;
            _mappedRgb[mapped] = frame[logical].R;
            _mappedRgb[mapped + 1] = frame[logical].G;
            _mappedRgb[mapped + 2] = frame[logical].B;
        }

        if (_style.Visual.FlatShading)
        {
            Array.Copy(_mappedRgb, _workingRgb, matrixCapacity * ColorChannels);
        }
        else
        {
            BuildColorLutIfNeeded();
            ApplyColorTransforms(matrixCapacity);
            ApplyBloomIfEnabled(matrixCapacity);
        }

        var destination = new byte[checked(_width * _height * 4)];
        for (var logical = 0; logical < matrixCapacity; logical++)
        {
            var raster = _logicalToRaster[logical];
            if ((uint)raster >= (uint)matrixCapacity)
            {
                continue;
            }

            var srcOffset = logical * ColorChannels;
            var dstOffset = raster * 4;
            destination[dstOffset] = (byte)Math.Clamp(_workingRgb[srcOffset + 2], 0f, 255f);
            destination[dstOffset + 1] = (byte)Math.Clamp(_workingRgb[srcOffset + 1], 0f, 255f);
            destination[dstOffset + 2] = (byte)Math.Clamp(_workingRgb[srcOffset], 0f, 255f);
            destination[dstOffset + 3] = 255;
        }

        return destination;
    }

    private void EnsureWorkingBuffers(int matrixCapacity)
    {
        var channelCapacity = matrixCapacity * ColorChannels;
        if (_workingRgb.Length == channelCapacity)
        {
            return;
        }

        _mappedRgb = new float[channelCapacity];
        _workingRgb = new float[channelCapacity];
        _smoothedRgb = new float[channelCapacity];
        _thresholdRgb = new float[channelCapacity];
    }

    private void BuildColorLutIfNeeded()
    {
        if (_style is null)
        {
            return;
        }

        var toneMapping = _style.ToneMapping;
        if (_lutBrightness.Equals(_style.Brightness) &&
            _lutGamma.Equals(_style.Gamma) &&
            _lutSoftKneeEnabled == toneMapping.Enabled &&
            _lutSoftKneeStart.Equals(toneMapping.KneeStart) &&
            _lutSoftKneeStrength.Equals(toneMapping.Strength))
        {
            return;
        }

        _lutBrightness = _style.Brightness;
        _lutGamma = _style.Gamma;
        _lutSoftKneeEnabled = toneMapping.Enabled;
        _lutSoftKneeStart = toneMapping.KneeStart;
        _lutSoftKneeStrength = toneMapping.Strength;
        for (var channel = 0; channel < 256; channel++)
        {
            _colorLut[channel] = ApplyToneMap((byte)channel, _style.Brightness, _style.Gamma, toneMapping);
        }
    }

    private void ApplyColorTransforms(int matrixCapacity)
    {
        if (_style is null)
        {
            return;
        }

        var smoothing = _style.TemporalSmoothing;
        var smoothingEnabled = smoothing.Enabled;
        var riseAlpha = Clamp01(smoothing.RiseAlpha);
        var fallAlpha = Clamp01(smoothing.FallAlpha);
        for (var i = 0; i < matrixCapacity; i++)
        {
            var offset = i * ColorChannels;
            ApplyChannelTransform(offset, riseAlpha, fallAlpha, smoothingEnabled);
            ApplyChannelTransform(offset + 1, riseAlpha, fallAlpha, smoothingEnabled);
            ApplyChannelTransform(offset + 2, riseAlpha, fallAlpha, smoothingEnabled);
        }
    }

    private void ApplyChannelTransform(int channelOffset, double riseAlpha, double fallAlpha, bool smoothingEnabled)
    {
        var target = _colorLut[(byte)_mappedRgb[channelOffset]];
        if (!smoothingEnabled)
        {
            _smoothedRgb[channelOffset] = target;
            _workingRgb[channelOffset] = target;
            return;
        }

        var current = _smoothedRgb[channelOffset];
        var delta = target - current;
        var alpha = delta >= 0 ? riseAlpha : fallAlpha;
        var next = current + ((float)alpha * delta);
        _smoothedRgb[channelOffset] = next;
        _workingRgb[channelOffset] = next;
    }

    private void ApplyBloomIfEnabled(int matrixCapacity)
    {
        if (_style is null || !_style.Bloom.Enabled || (_style.Bloom.SmallStrength <= 0.0 && _style.Bloom.WideStrength <= 0.0))
        {
            return;
        }

        var profile = ResolveBloomProfile(_style.Bloom);
        Array.Copy(_workingRgb, _thresholdRgb, matrixCapacity * ColorChannels);
        if (!ThresholdEmissive(_thresholdRgb, matrixCapacity, profile.Threshold))
        {
            return;
        }

        Downsample(_thresholdRgb, _width, _height, profile.ScaleDivisor);
        Array.Copy(_smallBlurRgb, _wideBlurRgb, _smallBlurRgb.Length);
        BoxBlurRgbSeparable(_smallBlurRgb, _downsampleWidth, _downsampleHeight, profile.SmallRadius);
        BoxBlurRgbSeparable(_wideBlurRgb, _downsampleWidth, _downsampleHeight, profile.WideRadius);
        CompositeBloom(_workingRgb, _smallBlurRgb, _wideBlurRgb, _width, _height, _downsampleWidth, _downsampleHeight, profile);
    }

    private void Downsample(float[] source, int width, int height, int scaleDivisor)
    {
        _downsampleWidth = Math.Max(1, width / scaleDivisor);
        _downsampleHeight = Math.Max(1, height / scaleDivisor);
        var downsamplePixels = _downsampleWidth * _downsampleHeight * ColorChannels;
        if (_smallBlurRgb.Length != downsamplePixels)
        {
            _smallBlurRgb = new float[downsamplePixels];
            _wideBlurRgb = new float[downsamplePixels];
            _blurScratchRgb = new float[downsamplePixels];
        }

        Array.Clear(_smallBlurRgb, 0, _smallBlurRgb.Length);
        for (var y = 0; y < _downsampleHeight; y++)
        {
            for (var x = 0; x < _downsampleWidth; x++)
            {
                var srcX = Math.Min(width - 1, x * scaleDivisor);
                var srcY = Math.Min(height - 1, y * scaleDivisor);
                var srcOffset = ((srcY * width) + srcX) * ColorChannels;
                var dstOffset = ((y * _downsampleWidth) + x) * ColorChannels;
                _smallBlurRgb[dstOffset] = source[srcOffset];
                _smallBlurRgb[dstOffset + 1] = source[srcOffset + 1];
                _smallBlurRgb[dstOffset + 2] = source[srcOffset + 2];
            }
        }
    }

    private void BoxBlurRgbSeparable(float[] rgb, int width, int height, int radius)
    {
        if (radius <= 0)
        {
            return;
        }

        if (_blurScratchRgb.Length != rgb.Length)
        {
            _blurScratchRgb = new float[rgb.Length];
        }

        HorizontalBlurRgb(rgb, _blurScratchRgb, width, height, radius);
        VerticalBlurRgb(_blurScratchRgb, rgb, width, height, radius);
    }

    private static void HorizontalBlurRgb(float[] source, float[] destination, int width, int height, int radius)
    {
        for (var y = 0; y < height; y++)
        {
            float sumR = 0, sumG = 0, sumB = 0;
            var samples = 0;
            for (var sx = 0; sx <= Math.Min(width - 1, radius); sx++)
            {
                var sampleOffset = ((y * width) + sx) * ColorChannels;
                sumR += source[sampleOffset];
                sumG += source[sampleOffset + 1];
                sumB += source[sampleOffset + 2];
                samples++;
            }

            for (var x = 0; x < width; x++)
            {
                var dstOffset = ((y * width) + x) * ColorChannels;
                destination[dstOffset] = sumR / Math.Max(1, samples);
                destination[dstOffset + 1] = sumG / Math.Max(1, samples);
                destination[dstOffset + 2] = sumB / Math.Max(1, samples);

                var removeX = x - radius;
                if (removeX >= 0)
                {
                    var removeOffset = ((y * width) + removeX) * ColorChannels;
                    sumR -= source[removeOffset];
                    sumG -= source[removeOffset + 1];
                    sumB -= source[removeOffset + 2];
                    samples--;
                }

                var addX = x + radius + 1;
                if (addX < width)
                {
                    var addOffset = ((y * width) + addX) * ColorChannels;
                    sumR += source[addOffset];
                    sumG += source[addOffset + 1];
                    sumB += source[addOffset + 2];
                    samples++;
                }
            }
        }
    }

    private static void VerticalBlurRgb(float[] source, float[] destination, int width, int height, int radius)
    {
        for (var x = 0; x < width; x++)
        {
            float sumR = 0, sumG = 0, sumB = 0;
            var samples = 0;
            for (var sy = 0; sy <= Math.Min(height - 1, radius); sy++)
            {
                var sampleOffset = ((sy * width) + x) * ColorChannels;
                sumR += source[sampleOffset];
                sumG += source[sampleOffset + 1];
                sumB += source[sampleOffset + 2];
                samples++;
            }

            for (var y = 0; y < height; y++)
            {
                var dstOffset = ((y * width) + x) * ColorChannels;
                destination[dstOffset] = sumR / Math.Max(1, samples);
                destination[dstOffset + 1] = sumG / Math.Max(1, samples);
                destination[dstOffset + 2] = sumB / Math.Max(1, samples);

                var removeY = y - radius;
                if (removeY >= 0)
                {
                    var removeOffset = ((removeY * width) + x) * ColorChannels;
                    sumR -= source[removeOffset];
                    sumG -= source[removeOffset + 1];
                    sumB -= source[removeOffset + 2];
                    samples--;
                }

                var addY = y + radius + 1;
                if (addY < height)
                {
                    var addOffset = ((addY * width) + x) * ColorChannels;
                    sumR += source[addOffset];
                    sumG += source[addOffset + 1];
                    sumB += source[addOffset + 2];
                    samples++;
                }
            }
        }
    }

    private static void CompositeBloom(float[] target, float[] smallBlur, float[] wideBlur, int width, int height, int bloomWidth, int bloomHeight, BloomProfile profile)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var targetOffset = ((y * width) + x) * ColorChannels;
                var bloomX = Math.Min(bloomWidth - 1, x / profile.ScaleDivisor);
                var bloomY = Math.Min(bloomHeight - 1, y / profile.ScaleDivisor);
                var bloomOffset = ((bloomY * bloomWidth) + bloomX) * ColorChannels;
                target[targetOffset] = Math.Clamp(target[targetOffset] + (smallBlur[bloomOffset] * (float)profile.SmallStrength) + (wideBlur[bloomOffset] * (float)profile.WideStrength), 0f, 255f);
                target[targetOffset + 1] = Math.Clamp(target[targetOffset + 1] + (smallBlur[bloomOffset + 1] * (float)profile.SmallStrength) + (wideBlur[bloomOffset + 1] * (float)profile.WideStrength), 0f, 255f);
                target[targetOffset + 2] = Math.Clamp(target[targetOffset + 2] + (smallBlur[bloomOffset + 2] * (float)profile.SmallStrength) + (wideBlur[bloomOffset + 2] * (float)profile.WideStrength), 0f, 255f);
            }
        }
    }

    private static BloomProfile ResolveBloomProfile(BloomConfig bloom)
    {
        var preset = bloom.QualityPreset.Trim().ToLowerInvariant();
        var profile = preset switch
        {
            "low" => new BloomProfile(true, 2, 1, 2, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
            "medium" => new BloomProfile(true, 2, 2, 4, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
            "high" => new BloomProfile(true, 1, 3, 6, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
            _ => new BloomProfile(true, bloom.BufferScaleDivisor, bloom.SmallRadius, bloom.WideRadius, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
        };

        return profile with
        {
            ScaleDivisor = Math.Clamp(profile.ScaleDivisor, 1, 4),
            SmallRadius = Math.Clamp(profile.SmallRadius, 1, 8),
            WideRadius = Math.Clamp(profile.WideRadius, Math.Max(1, profile.SmallRadius), 16),
            Threshold = Clamp01(profile.Threshold),
        };
    }

    private static bool ThresholdEmissive(float[] source, int matrixCapacity, double threshold)
    {
        var cutoff = (float)(Clamp01(threshold) * 255.0);
        var anyActive = false;
        for (var i = 0; i < matrixCapacity; i++)
        {
            var offset = i * ColorChannels;
            var peak = Math.Max(source[offset], Math.Max(source[offset + 1], source[offset + 2]));
            if (peak < cutoff)
            {
                source[offset] = source[offset + 1] = source[offset + 2] = 0f;
            }
            else
            {
                anyActive = true;
            }
        }

        return anyActive;
    }

    private static byte ApplyToneMap(byte channel, double brightness, double gamma, ToneMappingConfig toneMapping)
    {
        var normalized = channel / 255.0;
        var adjusted = Math.Pow(Math.Clamp(normalized, 0.0, 1.0), Math.Clamp(gamma, 0.1, 5.0));
        var scaled = adjusted * Math.Clamp(brightness, 0.0, 4.0);
        if (toneMapping.Enabled)
        {
            var kneeStart = Math.Clamp(toneMapping.KneeStart, 0.5, 0.99);
            var strength = Math.Clamp(toneMapping.Strength, 0.0, 8.0);
            if (scaled > kneeStart)
            {
                var excess = scaled - kneeStart;
                scaled = kneeStart + (excess / (1.0 + (strength * excess)));
            }
        }

        return (byte)Math.Round(Math.Clamp(scaled, 0.0, 1.0) * 255.0);
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    private sealed record BloomProfile(bool Enabled, int ScaleDivisor, int SmallRadius, int WideRadius, double Threshold, double SmallStrength, double WideStrength);
}
