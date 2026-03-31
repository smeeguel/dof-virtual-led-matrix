using System.Diagnostics;
using System.Reflection;
using VirtualDofMatrix.App.Rendering;
using VirtualDofMatrix.Core;
using Xunit;
using Xunit.Abstractions;

namespace VirtualDofMatrix.Tests;

public sealed class WpfPrimitiveMatrixRendererOptimizationTests
{
    private readonly ITestOutputHelper _output;

    public WpfPrimitiveMatrixRendererOptimizationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void OptimizedProcessing_ShouldMatchScalar_AndReportBenchmarks()
    {
        var applyMethod = typeof(WpfPrimitiveMatrixRenderer).GetMethod("ApplyColorTransforms", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var thresholdMethod = typeof(WpfPrimitiveMatrixRenderer).GetMethod("ThresholdEmissive", BindingFlags.Static | BindingFlags.NonPublic)!;
        var compositeMethod = typeof(WpfPrimitiveMatrixRenderer).GetMethod("CompositeBloom", BindingFlags.Static | BindingFlags.NonPublic)!;
        var bloomProfileType = typeof(WpfPrimitiveMatrixRenderer).GetNestedType("BloomProfile", BindingFlags.NonPublic)!;
        var mappedField = typeof(WpfPrimitiveMatrixRenderer).GetField("_mappedRgb", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var workingField = typeof(WpfPrimitiveMatrixRenderer).GetField("_workingRgb", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var smoothedField = typeof(WpfPrimitiveMatrixRenderer).GetField("_smoothedRgb", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var thresholdField = typeof(WpfPrimitiveMatrixRenderer).GetField("_thresholdRgb", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var lutField = typeof(WpfPrimitiveMatrixRenderer).GetField("_colorLut", BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (var (width, height) in new[] { (32, 8), (64, 32), (128, 64) })
        {
            foreach (var bloomPreset in new[] { "off", "medium", "high" })
            {
                var matrixCapacity = width * height;
                var channelCapacity = matrixCapacity * 3;
                var mapped = CreatePattern(channelCapacity);
                var baselineSmoothed = CreateSmoothedSeed(channelCapacity);

                var scalarRenderer = new WpfPrimitiveMatrixRenderer();
                var vectorRenderer = new WpfPrimitiveMatrixRenderer();
                mappedField.SetValue(scalarRenderer, (float[])mapped.Clone());
                mappedField.SetValue(vectorRenderer, (float[])mapped.Clone());
                workingField.SetValue(scalarRenderer, new float[channelCapacity]);
                workingField.SetValue(vectorRenderer, new float[channelCapacity]);
                smoothedField.SetValue(scalarRenderer, (float[])baselineSmoothed.Clone());
                smoothedField.SetValue(vectorRenderer, (float[])baselineSmoothed.Clone());
                thresholdField.SetValue(scalarRenderer, new float[channelCapacity]);
                thresholdField.SetValue(vectorRenderer, new float[channelCapacity]);

                var lutScalar = (byte[])lutField.GetValue(scalarRenderer)!;
                var lutVector = (byte[])lutField.GetValue(vectorRenderer)!;
                for (var i = 0; i < 256; i++)
                {
                    lutScalar[i] = (byte)i;
                    lutVector[i] = (byte)i;
                }

                var config = CreateConfig(width, height, bloomPreset);

                WpfPrimitiveMatrixRenderer.ForceScalarProcessingForTests = true;
                applyMethod.Invoke(scalarRenderer, new object[] { config, matrixCapacity });
                var scalarWorking = (float[])workingField.GetValue(scalarRenderer)!;
                var scalarSmoothed = (float[])smoothedField.GetValue(scalarRenderer)!;

                WpfPrimitiveMatrixRenderer.ForceScalarProcessingForTests = false;
                applyMethod.Invoke(vectorRenderer, new object[] { config, matrixCapacity });
                var vectorWorking = (float[])workingField.GetValue(vectorRenderer)!;
                var vectorSmoothed = (float[])smoothedField.GetValue(vectorRenderer)!;

                AssertBitwiseEqual(scalarWorking, vectorWorking);
                AssertBitwiseEqual(scalarSmoothed, vectorSmoothed);

                var scalarThreshold = (float[])scalarWorking.Clone();
                var vectorThreshold = (float[])vectorWorking.Clone();
                var scalarAny = (bool)thresholdMethod.Invoke(null, new object[] { scalarThreshold, matrixCapacity, config.Bloom.Threshold })!;
                var vectorAny = (bool)thresholdMethod.Invoke(null, new object[] { vectorThreshold, matrixCapacity, config.Bloom.Threshold })!;
                Assert.Equal(scalarAny, vectorAny);
                AssertBitwiseEqual(scalarThreshold, vectorThreshold);

                var bloomWidth = Math.Max(1, width / (bloomPreset == "high" ? 1 : 2));
                var bloomHeight = Math.Max(1, height / (bloomPreset == "high" ? 1 : 2));
                var bloomChannels = bloomWidth * bloomHeight * 3;
                var smallBlur = CreatePattern(bloomChannels);
                var wideBlur = CreatePattern(bloomChannels, phase: 0.73f);
                var scalarComposite = (float[])scalarWorking.Clone();
                var vectorComposite = (float[])vectorWorking.Clone();
                var profile = CreateBloomProfile(bloomProfileType, bloomPreset, config);

                WpfPrimitiveMatrixRenderer.ForceScalarProcessingForTests = true;
                compositeMethod.Invoke(null, new object[] { scalarComposite, smallBlur, wideBlur, width, height, bloomWidth, bloomHeight, profile });
                WpfPrimitiveMatrixRenderer.ForceScalarProcessingForTests = false;
                compositeMethod.Invoke(null, new object[] { vectorComposite, smallBlur, wideBlur, width, height, bloomWidth, bloomHeight, profile });
                AssertBitwiseEqual(scalarComposite, vectorComposite);

                var scalarMs = BenchmarkPass(scalarRenderer, applyMethod, config, matrixCapacity, true);
                var vectorMs = BenchmarkPass(vectorRenderer, applyMethod, config, matrixCapacity, false);
                var speedup = scalarMs / Math.Max(0.0001, vectorMs);
                _output.WriteLine($"{width}x{height} bloom={bloomPreset,-6} scalar={scalarMs,8:F2}ms vector={vectorMs,8:F2}ms speedup={speedup,5:F2}x");
            }
        }

        WpfPrimitiveMatrixRenderer.ForceScalarProcessingForTests = false;
    }

    private static double BenchmarkPass(WpfPrimitiveMatrixRenderer renderer, MethodInfo applyMethod, MatrixConfig config, int matrixCapacity, bool forceScalar)
    {
        var iterations = 120;
        WpfPrimitiveMatrixRenderer.ForceScalarProcessingForTests = forceScalar;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            applyMethod.Invoke(renderer, new object[] { config, matrixCapacity });
        }

        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static MatrixConfig CreateConfig(int width, int height, string bloomPreset) =>
        new()
        {
            Width = width,
            Height = height,
            Brightness = 1.0,
            Gamma = 1.0,
            TemporalSmoothing = new TemporalSmoothingConfig
            {
                Enabled = true,
                RiseAlpha = 0.65,
                FallAlpha = 0.35,
            },
            Bloom = new BloomConfig
            {
                Enabled = bloomPreset != "off",
                QualityPreset = bloomPreset,
                Threshold = 0.35,
                SmallStrength = 0.6,
                WideStrength = 0.25,
            },
        };

    private static object CreateBloomProfile(Type bloomProfileType, string preset, MatrixConfig config)
    {
        var args = preset switch
        {
            "medium" => new object[] { true, 2, 2, 4, config.Bloom.Threshold, config.Bloom.SmallStrength, config.Bloom.WideStrength },
            "high" => new object[] { true, 1, 3, 6, config.Bloom.Threshold, config.Bloom.SmallStrength, config.Bloom.WideStrength },
            _ => new object[] { false, 2, 1, 2, config.Bloom.Threshold, config.Bloom.SmallStrength, config.Bloom.WideStrength },
        };

        return Activator.CreateInstance(bloomProfileType, args)!;
    }

    private static float[] CreatePattern(int count, float phase = 0.0f)
    {
        var data = new float[count];
        for (var i = 0; i < count; i++)
        {
            data[i] = (float)((Math.Sin((i * 0.13) + phase) * 0.5 + 0.5) * 255.0);
        }

        return data;
    }

    private static float[] CreateSmoothedSeed(int count)
    {
        var data = new float[count];
        for (var i = 0; i < count; i++)
        {
            data[i] = (i % 17) * 3.2f;
        }

        return data;
    }

    private static void AssertBitwiseEqual(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(expected[i]), BitConverter.SingleToInt32Bits(actual[i]));
        }
    }
}
