using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Encoder.Memory;

namespace UniCast.Encoder.Effects
{
    /// <summary>
    /// GPU-accelerated video effects engine.
    /// CUDA (NVIDIA), OpenCL (cross-platform), DirectCompute (Windows) desteği.
    /// 
    /// Efektler:
    /// - Blur (Gaussian, Box, Motion)
    /// - Color Correction (Brightness, Contrast, Saturation, Gamma)
    /// - Filters (Sharpen, Edge Detection, Emboss)
    /// - Transformations (Scale, Rotate, Crop)
    /// - Transitions (Fade, Wipe, Dissolve)
    /// - Special (Chroma Key, LUT, Vignette, Film Grain)
    /// </summary>
    public sealed class GpuEffectsEngine : IDisposable
    {
        #region Singleton

        private static readonly Lazy<GpuEffectsEngine> _instance =
            new(() => new GpuEffectsEngine(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static GpuEffectsEngine Instance => _instance.Value;

        #endregion

        #region Properties

        public bool IsInitialized { get; private set; }
        public GpuBackend ActiveBackend { get; private set; }
        public string DeviceName { get; private set; } = "Unknown";
        public double LastEffectTimeMs { get; private set; }

        #endregion

        #region Fields

        private readonly object _lock = new();
        private readonly Stopwatch _timer = new();
        private IGpuBackend? _backend;
        private bool _disposed;

        #endregion

        #region Initialization

        private GpuEffectsEngine()
        {
            Initialize();
        }

        private void Initialize()
        {
            try
            {
                // GPU backend seçimi (öncelik sırasına göre)
                // 1. CUDA (NVIDIA)
                if (TryInitializeCuda())
                {
                    ActiveBackend = GpuBackend.CUDA;
                    IsInitialized = true;
                    return;
                }

                // 2. DirectCompute (Windows)
                if (TryInitializeDirectCompute())
                {
                    ActiveBackend = GpuBackend.DirectCompute;
                    IsInitialized = true;
                    return;
                }

                // 3. OpenCL (cross-platform)
                if (TryInitializeOpenCL())
                {
                    ActiveBackend = GpuBackend.OpenCL;
                    IsInitialized = true;
                    return;
                }

                // 4. CPU fallback
                _backend = new CpuEffectsBackend();
                ActiveBackend = GpuBackend.CPU;
                DeviceName = "CPU (Software)";
                IsInitialized = true;

                Debug.WriteLine($"[GpuEffects] Using backend: {ActiveBackend}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GpuEffects] Init error: {ex.Message}");
                _backend = new CpuEffectsBackend();
                ActiveBackend = GpuBackend.CPU;
                IsInitialized = true;
            }
        }

        private bool TryInitializeCuda()
        {
            try
            {
                // CUDA availability check
                if (NativeMethods.IsCudaAvailable())
                {
                    _backend = new CudaEffectsBackend();
                    DeviceName = _backend.DeviceName;
                    Debug.WriteLine($"[GpuEffects] CUDA initialized: {DeviceName}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GpuEffects] CUDA init failed: {ex.Message}");
            }
            return false;
        }

        private bool TryInitializeDirectCompute()
        {
            try
            {
                _backend = new DirectComputeBackend();
                if (_backend.IsAvailable)
                {
                    DeviceName = _backend.DeviceName;
                    Debug.WriteLine($"[GpuEffects] DirectCompute initialized: {DeviceName}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GpuEffects] DirectCompute init failed: {ex.Message}");
            }
            return false;
        }

        private bool TryInitializeOpenCL()
        {
            try
            {
                if (NativeMethods.IsOpenCLAvailable())
                {
                    _backend = new OpenCLEffectsBackend();
                    if (_backend.IsAvailable)
                    {
                        DeviceName = _backend.DeviceName;
                        Debug.WriteLine($"[GpuEffects] OpenCL initialized: {DeviceName}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GpuEffects] OpenCL init failed: {ex.Message}");
            }
            return false;
        }

        #endregion

        #region Blur Effects

        /// <summary>
        /// Gaussian blur
        /// </summary>
        public bool GaussianBlur(FrameBuffer input, FrameBuffer output, float radius, float sigma = 0)
        {
            if (sigma <= 0) sigma = radius / 3f;
            
            return ApplyEffect(input, output, new GaussianBlurEffect
            {
                Radius = radius,
                Sigma = sigma
            });
        }

        /// <summary>
        /// Box blur (fast)
        /// </summary>
        public bool BoxBlur(FrameBuffer input, FrameBuffer output, int radius)
        {
            return ApplyEffect(input, output, new BoxBlurEffect { Radius = radius });
        }

        /// <summary>
        /// Motion blur
        /// </summary>
        public bool MotionBlur(FrameBuffer input, FrameBuffer output, float angle, float distance)
        {
            return ApplyEffect(input, output, new MotionBlurEffect
            {
                Angle = angle,
                Distance = distance
            });
        }

        /// <summary>
        /// Radial blur
        /// </summary>
        public bool RadialBlur(FrameBuffer input, FrameBuffer output, float centerX, float centerY, float strength)
        {
            return ApplyEffect(input, output, new RadialBlurEffect
            {
                CenterX = centerX,
                CenterY = centerY,
                Strength = strength
            });
        }

        #endregion

        #region Color Effects

        /// <summary>
        /// Brightness, Contrast, Saturation ayarı
        /// </summary>
        public bool ColorCorrection(FrameBuffer input, FrameBuffer output, ColorCorrectionParams parameters)
        {
            return ApplyEffect(input, output, new ColorCorrectionEffect
            {
                Brightness = parameters.Brightness,
                Contrast = parameters.Contrast,
                Saturation = parameters.Saturation,
                Gamma = parameters.Gamma,
                Temperature = parameters.Temperature,
                Tint = parameters.Tint
            });
        }

        /// <summary>
        /// HSL ayarı
        /// </summary>
        public bool HSLAdjust(FrameBuffer input, FrameBuffer output, float hueShift, float saturation, float lightness)
        {
            return ApplyEffect(input, output, new HSLEffect
            {
                HueShift = hueShift,
                Saturation = saturation,
                Lightness = lightness
            });
        }

        /// <summary>
        /// LUT (Look-Up Table) uygula
        /// </summary>
        public bool ApplyLUT(FrameBuffer input, FrameBuffer output, byte[] lutData, float intensity = 1.0f)
        {
            return ApplyEffect(input, output, new LUTEffect
            {
                LutData = lutData,
                Intensity = intensity
            });
        }

        /// <summary>
        /// Color grading (film look)
        /// </summary>
        public bool ColorGrading(FrameBuffer input, FrameBuffer output, ColorGradingPreset preset)
        {
            return ApplyEffect(input, output, new ColorGradingEffect { Preset = preset });
        }

        #endregion

        #region Filter Effects

        /// <summary>
        /// Sharpen
        /// </summary>
        public bool Sharpen(FrameBuffer input, FrameBuffer output, float amount)
        {
            return ApplyEffect(input, output, new SharpenEffect { Amount = amount });
        }

        /// <summary>
        /// Edge detection (Sobel)
        /// </summary>
        public bool EdgeDetection(FrameBuffer input, FrameBuffer output, float threshold = 0.1f)
        {
            return ApplyEffect(input, output, new EdgeDetectionEffect { Threshold = threshold });
        }

        /// <summary>
        /// Emboss
        /// </summary>
        public bool Emboss(FrameBuffer input, FrameBuffer output, float angle = 45f, float strength = 1f)
        {
            return ApplyEffect(input, output, new EmbossEffect
            {
                Angle = angle,
                Strength = strength
            });
        }

        /// <summary>
        /// Noise reduction
        /// </summary>
        public bool NoiseReduction(FrameBuffer input, FrameBuffer output, float strength)
        {
            return ApplyEffect(input, output, new NoiseReductionEffect { Strength = strength });
        }

        #endregion

        #region Special Effects

        /// <summary>
        /// Chroma key (green/blue screen)
        /// </summary>
        public bool ChromaKey(FrameBuffer input, FrameBuffer output, ChromaKeyParams parameters)
        {
            return ApplyEffect(input, output, new ChromaKeyEffect
            {
                KeyColor = parameters.KeyColor,
                Tolerance = parameters.Tolerance,
                Softness = parameters.Softness,
                SpillReduction = parameters.SpillReduction
            });
        }

        /// <summary>
        /// Vignette
        /// </summary>
        public bool Vignette(FrameBuffer input, FrameBuffer output, float intensity, float radius)
        {
            return ApplyEffect(input, output, new VignetteEffect
            {
                Intensity = intensity,
                Radius = radius
            });
        }

        /// <summary>
        /// Film grain
        /// </summary>
        public bool FilmGrain(FrameBuffer input, FrameBuffer output, float intensity, float size)
        {
            return ApplyEffect(input, output, new FilmGrainEffect
            {
                Intensity = intensity,
                Size = size,
                Seed = Environment.TickCount
            });
        }

        /// <summary>
        /// Lens distortion
        /// </summary>
        public bool LensDistortion(FrameBuffer input, FrameBuffer output, float k1, float k2)
        {
            return ApplyEffect(input, output, new LensDistortionEffect { K1 = k1, K2 = k2 });
        }

        /// <summary>
        /// Chromatic aberration
        /// </summary>
        public bool ChromaticAberration(FrameBuffer input, FrameBuffer output, float intensity)
        {
            return ApplyEffect(input, output, new ChromaticAberrationEffect { Intensity = intensity });
        }

        #endregion

        #region Transform Effects

        /// <summary>
        /// Scale
        /// </summary>
        public bool Scale(FrameBuffer input, FrameBuffer output, float scaleX, float scaleY, ScaleFilter filter = ScaleFilter.Bilinear)
        {
            return ApplyEffect(input, output, new ScaleEffect
            {
                ScaleX = scaleX,
                ScaleY = scaleY,
                Filter = filter
            });
        }

        /// <summary>
        /// Rotate
        /// </summary>
        public bool Rotate(FrameBuffer input, FrameBuffer output, float angle)
        {
            return ApplyEffect(input, output, new RotateEffect { Angle = angle });
        }

        /// <summary>
        /// Crop
        /// </summary>
        public bool Crop(FrameBuffer input, FrameBuffer output, int x, int y, int width, int height)
        {
            return ApplyEffect(input, output, new CropEffect
            {
                X = x,
                Y = y,
                Width = width,
                Height = height
            });
        }

        /// <summary>
        /// Flip (horizontal/vertical)
        /// </summary>
        public bool Flip(FrameBuffer input, FrameBuffer output, bool horizontal, bool vertical)
        {
            return ApplyEffect(input, output, new FlipEffect
            {
                Horizontal = horizontal,
                Vertical = vertical
            });
        }

        #endregion

        #region Transition Effects

        /// <summary>
        /// Fade transition
        /// </summary>
        public bool Fade(FrameBuffer from, FrameBuffer to, FrameBuffer output, float progress)
        {
            return ApplyTransition(from, to, output, new FadeTransition { Progress = progress });
        }

        /// <summary>
        /// Wipe transition
        /// </summary>
        public bool Wipe(FrameBuffer from, FrameBuffer to, FrameBuffer output, float progress, WipeDirection direction)
        {
            return ApplyTransition(from, to, output, new WipeTransition
            {
                Progress = progress,
                Direction = direction
            });
        }

        /// <summary>
        /// Dissolve transition
        /// </summary>
        public bool Dissolve(FrameBuffer from, FrameBuffer to, FrameBuffer output, float progress)
        {
            return ApplyTransition(from, to, output, new DissolveTransition
            {
                Progress = progress,
                Seed = Environment.TickCount
            });
        }

        /// <summary>
        /// Zoom transition
        /// </summary>
        public bool ZoomTransition(FrameBuffer from, FrameBuffer to, FrameBuffer output, float progress)
        {
            return ApplyTransition(from, to, output, new ZoomTransition { Progress = progress });
        }

        #endregion

        #region Effect Pipeline

        /// <summary>
        /// Effect chain uygula
        /// </summary>
        public bool ApplyEffectChain(FrameBuffer input, FrameBuffer output, IReadOnlyList<IEffect> effects)
        {
            if (effects.Count == 0)
            {
                input.Data.AsSpan().CopyTo(output.Data);
                return true;
            }

            lock (_lock)
            {
                _timer.Restart();

                try
                {
                    // Temp buffer'lar
                    using var temp1 = FrameBufferPool.Instance.RentFrame(input.Width, input.Height, input.Format);
                    using var temp2 = FrameBufferPool.Instance.RentFrame(input.Width, input.Height, input.Format);

                    var current = input;
                    var next = temp1;

                    for (int i = 0; i < effects.Count; i++)
                    {
                        var isLast = i == effects.Count - 1;
                        var target = isLast ? output : next;

                        if (!_backend!.ApplyEffect(current, target, effects[i]))
                        {
                            return false;
                        }

                        if (!isLast)
                        {
                            current = next;
                            next = (ReferenceEquals(next.Data, temp1.Data)) ? temp2 : temp1;
                        }
                    }

                    _timer.Stop();
                    LastEffectTimeMs = _timer.Elapsed.TotalMilliseconds;

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GpuEffects] Chain error: {ex.Message}");
                    return false;
                }
            }
        }

        private bool ApplyEffect(FrameBuffer input, FrameBuffer output, IEffect effect)
        {
            lock (_lock)
            {
                _timer.Restart();

                try
                {
                    var result = _backend!.ApplyEffect(input, output, effect);

                    _timer.Stop();
                    LastEffectTimeMs = _timer.Elapsed.TotalMilliseconds;

                    return result;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GpuEffects] Effect error: {ex.Message}");
                    return false;
                }
            }
        }

        private bool ApplyTransition(FrameBuffer from, FrameBuffer to, FrameBuffer output, ITransition transition)
        {
            lock (_lock)
            {
                _timer.Restart();

                try
                {
                    var result = _backend!.ApplyTransition(from, to, output, transition);

                    _timer.Stop();
                    LastEffectTimeMs = _timer.Elapsed.TotalMilliseconds;

                    return result;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GpuEffects] Transition error: {ex.Message}");
                    return false;
                }
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _backend?.Dispose();
            IsInitialized = false;
        }

        #endregion
    }

    #region GPU Backend Interfaces

    public enum GpuBackend
    {
        CPU,
        CUDA,
        OpenCL,
        DirectCompute
    }

    public interface IGpuBackend : IDisposable
    {
        bool IsAvailable { get; }
        string DeviceName { get; }
        bool ApplyEffect(FrameBuffer input, FrameBuffer output, IEffect effect);
        bool ApplyTransition(FrameBuffer from, FrameBuffer to, FrameBuffer output, ITransition transition);
    }

    #endregion

    #region Effect Interfaces

    public interface IEffect { }
    public interface ITransition { float Progress { get; } }

    // Blur Effects
    public class GaussianBlurEffect : IEffect { public float Radius; public float Sigma; }
    public class BoxBlurEffect : IEffect { public int Radius; }
    public class MotionBlurEffect : IEffect { public float Angle; public float Distance; }
    public class RadialBlurEffect : IEffect { public float CenterX, CenterY, Strength; }

    // Color Effects
    public class ColorCorrectionEffect : IEffect 
    { 
        public float Brightness, Contrast, Saturation, Gamma, Temperature, Tint; 
    }
    public class HSLEffect : IEffect { public float HueShift, Saturation, Lightness; }
    public class LUTEffect : IEffect { public byte[]? LutData; public float Intensity; }
    public class ColorGradingEffect : IEffect { public ColorGradingPreset Preset; }

    // Filter Effects
    public class SharpenEffect : IEffect { public float Amount; }
    public class EdgeDetectionEffect : IEffect { public float Threshold; }
    public class EmbossEffect : IEffect { public float Angle, Strength; }
    public class NoiseReductionEffect : IEffect { public float Strength; }

    // Special Effects
    public class ChromaKeyEffect : IEffect 
    { 
        public Vector3 KeyColor; 
        public float Tolerance, Softness, SpillReduction; 
    }
    public class VignetteEffect : IEffect { public float Intensity, Radius; }
    public class FilmGrainEffect : IEffect { public float Intensity, Size; public int Seed; }
    public class LensDistortionEffect : IEffect { public float K1, K2; }
    public class ChromaticAberrationEffect : IEffect { public float Intensity; }

    // Transform Effects
    public class ScaleEffect : IEffect { public float ScaleX, ScaleY; public ScaleFilter Filter; }
    public class RotateEffect : IEffect { public float Angle; }
    public class CropEffect : IEffect { public int X, Y, Width, Height; }
    public class FlipEffect : IEffect { public bool Horizontal, Vertical; }

    // Transitions
    public class FadeTransition : ITransition { public float Progress { get; set; } }
    public class WipeTransition : ITransition { public float Progress { get; set; } public WipeDirection Direction; }
    public class DissolveTransition : ITransition { public float Progress { get; set; } public int Seed; }
    public class ZoomTransition : ITransition { public float Progress { get; set; } }

    #endregion

    #region Supporting Types

    public enum ScaleFilter { NearestNeighbor, Bilinear, Bicubic, Lanczos }
    public enum WipeDirection { Left, Right, Up, Down, Circle }
    public enum ColorGradingPreset { None, Cinematic, Vintage, CoolTone, WarmTone, BlackAndWhite, Sepia }

    public class ColorCorrectionParams
    {
        public float Brightness { get; set; } = 0f;
        public float Contrast { get; set; } = 1f;
        public float Saturation { get; set; } = 1f;
        public float Gamma { get; set; } = 1f;
        public float Temperature { get; set; } = 0f;
        public float Tint { get; set; } = 0f;
    }

    public class ChromaKeyParams
    {
        public Vector3 KeyColor { get; set; } = new Vector3(0, 1, 0); // Green
        public float Tolerance { get; set; } = 0.4f;
        public float Softness { get; set; } = 0.1f;
        public float SpillReduction { get; set; } = 0.5f;
    }

    #endregion

    #region Backend Implementations

    /// <summary>
    /// CPU fallback backend
    /// </summary>
    internal class CpuEffectsBackend : IGpuBackend
    {
        public bool IsAvailable => true;
        public string DeviceName => "CPU (Software)";

        public bool ApplyEffect(FrameBuffer input, FrameBuffer output, IEffect effect)
        {
            // CPU implementasyonları
            return effect switch
            {
                BoxBlurEffect blur => ApplyBoxBlur(input, output, blur.Radius),
                ColorCorrectionEffect cc => ApplyColorCorrection(input, output, cc),
                ChromaKeyEffect ck => ApplyChromaKey(input, output, ck),
                FlipEffect flip => ApplyFlip(input, output, flip),
                _ => CopyBuffer(input, output)
            };
        }

        public bool ApplyTransition(FrameBuffer from, FrameBuffer to, FrameBuffer output, ITransition transition)
        {
            return transition switch
            {
                FadeTransition fade => ApplyFade(from, to, output, fade.Progress),
                _ => ApplyFade(from, to, output, transition.Progress)
            };
        }

        private bool CopyBuffer(FrameBuffer input, FrameBuffer output)
        {
            input.Data.AsSpan().CopyTo(output.Data);
            return true;
        }

        private bool ApplyBoxBlur(FrameBuffer input, FrameBuffer output, int radius)
        {
            var src = input.Data;
            var dst = output.Data;
            var w = input.Width;
            var h = input.Height;
            var stride = input.Stride;

            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0, count = 0;

                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int nx = Math.Clamp(x + dx, 0, w - 1);
                            int ny = Math.Clamp(y + dy, 0, h - 1);
                            int idx = ny * stride + nx * 4;

                            b += src[idx];
                            g += src[idx + 1];
                            r += src[idx + 2];
                            a += src[idx + 3];
                            count++;
                        }
                    }

                    int outIdx = y * stride + x * 4;
                    dst[outIdx] = (byte)(b / count);
                    dst[outIdx + 1] = (byte)(g / count);
                    dst[outIdx + 2] = (byte)(r / count);
                    dst[outIdx + 3] = (byte)(a / count);
                }
            });

            return true;
        }

        private bool ApplyColorCorrection(FrameBuffer input, FrameBuffer output, ColorCorrectionEffect cc)
        {
            var src = input.Data;
            var dst = output.Data;

            Parallel.For(0, src.Length / 4, i =>
            {
                int idx = i * 4;
                
                float b = src[idx] / 255f;
                float g = src[idx + 1] / 255f;
                float r = src[idx + 2] / 255f;

                // Brightness & Contrast
                r = (r - 0.5f) * cc.Contrast + 0.5f + cc.Brightness;
                g = (g - 0.5f) * cc.Contrast + 0.5f + cc.Brightness;
                b = (b - 0.5f) * cc.Contrast + 0.5f + cc.Brightness;

                // Saturation
                float grey = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                r = grey + (r - grey) * cc.Saturation;
                g = grey + (g - grey) * cc.Saturation;
                b = grey + (b - grey) * cc.Saturation;

                // Gamma
                r = MathF.Pow(Math.Clamp(r, 0, 1), 1f / cc.Gamma);
                g = MathF.Pow(Math.Clamp(g, 0, 1), 1f / cc.Gamma);
                b = MathF.Pow(Math.Clamp(b, 0, 1), 1f / cc.Gamma);

                dst[idx] = (byte)(Math.Clamp(b, 0, 1) * 255);
                dst[idx + 1] = (byte)(Math.Clamp(g, 0, 1) * 255);
                dst[idx + 2] = (byte)(Math.Clamp(r, 0, 1) * 255);
                dst[idx + 3] = src[idx + 3];
            });

            return true;
        }

        private bool ApplyChromaKey(FrameBuffer input, FrameBuffer output, ChromaKeyEffect ck)
        {
            var src = input.Data;
            var dst = output.Data;

            Parallel.For(0, src.Length / 4, i =>
            {
                int idx = i * 4;
                
                float b = src[idx] / 255f;
                float g = src[idx + 1] / 255f;
                float r = src[idx + 2] / 255f;
                float a = src[idx + 3] / 255f;

                float dist = MathF.Sqrt(
                    MathF.Pow(r - ck.KeyColor.X, 2) +
                    MathF.Pow(g - ck.KeyColor.Y, 2) +
                    MathF.Pow(b - ck.KeyColor.Z, 2));

                float alpha;
                if (dist < ck.Tolerance)
                    alpha = 0;
                else if (dist < ck.Tolerance + ck.Softness)
                    alpha = (dist - ck.Tolerance) / ck.Softness;
                else
                    alpha = 1;

                dst[idx] = src[idx];
                dst[idx + 1] = src[idx + 1];
                dst[idx + 2] = src[idx + 2];
                dst[idx + 3] = (byte)(a * alpha * 255);
            });

            return true;
        }

        private bool ApplyFlip(FrameBuffer input, FrameBuffer output, FlipEffect flip)
        {
            var src = input.Data;
            var dst = output.Data;
            var w = input.Width;
            var h = input.Height;
            var stride = input.Stride;

            Parallel.For(0, h, y =>
            {
                int srcY = flip.Vertical ? (h - 1 - y) : y;
                
                for (int x = 0; x < w; x++)
                {
                    int srcX = flip.Horizontal ? (w - 1 - x) : x;
                    
                    int srcIdx = srcY * stride + srcX * 4;
                    int dstIdx = y * stride + x * 4;

                    dst[dstIdx] = src[srcIdx];
                    dst[dstIdx + 1] = src[srcIdx + 1];
                    dst[dstIdx + 2] = src[srcIdx + 2];
                    dst[dstIdx + 3] = src[srcIdx + 3];
                }
            });

            return true;
        }

        private bool ApplyFade(FrameBuffer from, FrameBuffer to, FrameBuffer output, float progress)
        {
            var srcA = from.Data;
            var srcB = to.Data;
            var dst = output.Data;

            Parallel.For(0, dst.Length, i =>
            {
                dst[i] = (byte)(srcA[i] * (1 - progress) + srcB[i] * progress);
            });

            return true;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// DirectCompute backend (placeholder)
    /// </summary>
    internal class DirectComputeBackend : IGpuBackend
    {
        public bool IsAvailable { get; private set; }
        public string DeviceName { get; private set; } = "DirectCompute";

        public DirectComputeBackend()
        {
            // DirectCompute initialization via Vortice
            IsAvailable = false; // Placeholder
        }

        public bool ApplyEffect(FrameBuffer input, FrameBuffer output, IEffect effect) => false;
        public bool ApplyTransition(FrameBuffer from, FrameBuffer to, FrameBuffer output, ITransition transition) => false;
        public void Dispose() { }
    }

    /// <summary>
    /// CUDA backend (placeholder)
    /// </summary>
    internal class CudaEffectsBackend : IGpuBackend
    {
        public bool IsAvailable => true;
        public string DeviceName { get; private set; } = "CUDA Device";

        public bool ApplyEffect(FrameBuffer input, FrameBuffer output, IEffect effect) => false;
        public bool ApplyTransition(FrameBuffer from, FrameBuffer to, FrameBuffer output, ITransition transition) => false;
        public void Dispose() { }
    }

    /// <summary>
    /// OpenCL backend (placeholder)
    /// </summary>
    internal class OpenCLEffectsBackend : IGpuBackend
    {
        public bool IsAvailable => false;
        public string DeviceName => "OpenCL Device";

        public bool ApplyEffect(FrameBuffer input, FrameBuffer output, IEffect effect) => false;
        public bool ApplyTransition(FrameBuffer from, FrameBuffer to, FrameBuffer output, ITransition transition) => false;
        public void Dispose() { }
    }

    internal static class NativeMethods
    {
        public static bool IsCudaAvailable()
        {
            try
            {
                // nvcuda.dll check
                var handle = LoadLibrary("nvcuda.dll");
                if (handle != IntPtr.Zero)
                {
                    FreeLibrary(handle);
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static bool IsOpenCLAvailable()
        {
            try
            {
                var handle = LoadLibrary("OpenCL.dll");
                if (handle != IntPtr.Zero)
                {
                    FreeLibrary(handle);
                    return true;
                }
            }
            catch { }
            return false;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr LoadLibrary(string dllName);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr handle);
    }

    #endregion
}
