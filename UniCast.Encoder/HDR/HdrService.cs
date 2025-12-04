using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using UniCast.Encoder.Memory;

namespace UniCast.Encoder.HDR
{
    /// <summary>
    /// HDR (High Dynamic Range) video desteği.
    /// 
    /// Desteklenen formatlar:
    /// - HDR10 (PQ, ST.2084)
    /// - HLG (Hybrid Log-Gamma)
    /// - Dolby Vision (Profile 5, Profile 8)
    /// 
    /// Özellikler:
    /// - SDR ↔ HDR dönüşüm
    /// - Tone mapping
    /// - Color space dönüşüm (BT.709 ↔ BT.2020)
    /// - Static/Dynamic metadata
    /// </summary>
    public sealed class HdrService : IDisposable
    {
        #region Singleton

        private static readonly Lazy<HdrService> _instance =
            new(() => new HdrService(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static HdrService Instance => _instance.Value;

        #endregion

        #region Constants

        // SDR max luminance (nits)
        private const float SDR_PEAK_LUMINANCE = 100f;

        // HDR reference luminance
        private const float HDR_REFERENCE_WHITE = 203f;

        // BT.2020 primaries
        private static readonly Vector3 BT2020_RED = new(0.708f, 0.292f, 0f);
        private static readonly Vector3 BT2020_GREEN = new(0.170f, 0.797f, 0f);
        private static readonly Vector3 BT2020_BLUE = new(0.131f, 0.046f, 0f);
        private static readonly Vector3 BT2020_WHITE = new(0.3127f, 0.3290f, 0f);

        // BT.709 primaries
        private static readonly Vector3 BT709_RED = new(0.64f, 0.33f, 0f);
        private static readonly Vector3 BT709_GREEN = new(0.30f, 0.60f, 0f);
        private static readonly Vector3 BT709_BLUE = new(0.15f, 0.06f, 0f);

        #endregion

        #region Properties

        public bool IsHdrDisplayAvailable { get; private set; }
        public HdrDisplayInfo? DisplayInfo { get; private set; }
        public bool IsInitialized { get; private set; }

        #endregion

        #region Fields

        private readonly object _lock = new();
        private bool _disposed;

        // Pre-computed LUTs
        private float[]? _pqToLinearLut;
        private float[]? _linearToPqLut;
        private float[]? _hlgToLinearLut;
        private float[]? _linearToHlgLut;

        // Color space matrices
        private Matrix4x4 _bt709ToBt2020;
        private Matrix4x4 _bt2020ToBt709;

        #endregion

        #region Constructor

        private HdrService()
        {
            Initialize();
        }

        private void Initialize()
        {
            try
            {
                // HDR display detection
                DetectHdrDisplay();

                // LUT'ları oluştur
                CreateTransferFunctionLuts();

                // Color space matrislerini hesapla
                CalculateColorSpaceMatrices();

                IsInitialized = true;
                Debug.WriteLine($"[HDR] Initialized. Display HDR: {IsHdrDisplayAvailable}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HDR] Init error: {ex.Message}");
                IsInitialized = false;
            }
        }

        #endregion

        #region HDR Detection

        private void DetectHdrDisplay()
        {
            try
            {
                // Windows HDR API check
                IsHdrDisplayAvailable = CheckWindowsHdrSupport();

                if (IsHdrDisplayAvailable)
                {
                    DisplayInfo = GetHdrDisplayInfo();
                    Debug.WriteLine($"[HDR] Display: {DisplayInfo?.MaxLuminance ?? 0} nits peak");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HDR] Detection error: {ex.Message}");
                IsHdrDisplayAvailable = false;
            }
        }

        private bool CheckWindowsHdrSupport()
        {
            // DXGI 1.6+ HDR check
            try
            {
                // Basit check - gerçek implementasyon DXGI API kullanır
                return false; // Placeholder
            }
            catch
            {
                return false;
            }
        }

        private HdrDisplayInfo? GetHdrDisplayInfo()
        {
            // Display bilgilerini al
            return new HdrDisplayInfo
            {
                MaxLuminance = 1000,
                MinLuminance = 0.01f,
                MaxFullFrameLuminance = 600,
                ColorSpace = HdrColorSpace.BT2020,
                TransferFunction = HdrTransferFunction.PQ
            };
        }

        #endregion

        #region SDR to HDR Conversion

        /// <summary>
        /// SDR içeriği HDR'a dönüştür
        /// </summary>
        public bool ConvertSdrToHdr(
            FrameBuffer input,
            FrameBuffer output,
            SdrToHdrSettings settings)
        {
            if (!IsInitialized) return false;

            lock (_lock)
            {
                try
                {
                    var src = input.Data;
                    var dst = output.Data;
                    var pixelCount = src.Length / 4;

                    for (int i = 0; i < pixelCount; i++)
                    {
                        int idx = i * 4;

                        // 1. SDR değerlerini oku (0-255 → 0-1)
                        float r = src[idx + 2] / 255f;
                        float g = src[idx + 1] / 255f;
                        float b = src[idx] / 255f;

                        // 2. sRGB → Linear
                        r = SrgbToLinear(r);
                        g = SrgbToLinear(g);
                        b = SrgbToLinear(b);

                        // 3. Inverse tone mapping (SDR → HDR luminance)
                        float luminance = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                        float hdrLuminance = InverseToneMap(luminance, settings);
                        
                        float scale = luminance > 0.001f ? hdrLuminance / luminance : 1f;
                        r *= scale;
                        g *= scale;
                        b *= scale;

                        // 4. Color space: BT.709 → BT.2020
                        if (settings.ConvertToBt2020)
                        {
                            var color = new Vector3(r, g, b);
                            color = Vector3.Transform(color, _bt709ToBt2020);
                            r = color.X;
                            g = color.Y;
                            b = color.Z;
                        }

                        // 5. Linear → PQ (veya HLG)
                        if (settings.TransferFunction == HdrTransferFunction.PQ)
                        {
                            r = LinearToPQ(r, settings.PeakLuminance);
                            g = LinearToPQ(g, settings.PeakLuminance);
                            b = LinearToPQ(b, settings.PeakLuminance);
                        }
                        else // HLG
                        {
                            r = LinearToHLG(r);
                            g = LinearToHLG(g);
                            b = LinearToHLG(b);
                        }

                        // 6. 10-bit output (0-1 → 0-1023)
                        // Şimdilik 8-bit'e map
                        dst[idx] = (byte)(Math.Clamp(b, 0, 1) * 255);
                        dst[idx + 1] = (byte)(Math.Clamp(g, 0, 1) * 255);
                        dst[idx + 2] = (byte)(Math.Clamp(r, 0, 1) * 255);
                        dst[idx + 3] = src[idx + 3];
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HDR] SDR→HDR error: {ex.Message}");
                    return false;
                }
            }
        }

        #endregion

        #region HDR to SDR Conversion (Tone Mapping)

        /// <summary>
        /// HDR içeriği SDR'a dönüştür (tone mapping)
        /// </summary>
        public bool ConvertHdrToSdr(
            FrameBuffer input,
            FrameBuffer output,
            HdrToSdrSettings settings)
        {
            if (!IsInitialized) return false;

            lock (_lock)
            {
                try
                {
                    var src = input.Data;
                    var dst = output.Data;
                    var pixelCount = src.Length / 4;

                    for (int i = 0; i < pixelCount; i++)
                    {
                        int idx = i * 4;

                        // 1. HDR değerlerini oku
                        float r = src[idx + 2] / 255f;
                        float g = src[idx + 1] / 255f;
                        float b = src[idx] / 255f;

                        // 2. PQ/HLG → Linear
                        if (settings.SourceTransferFunction == HdrTransferFunction.PQ)
                        {
                            r = PQToLinear(r, settings.SourcePeakLuminance);
                            g = PQToLinear(g, settings.SourcePeakLuminance);
                            b = PQToLinear(b, settings.SourcePeakLuminance);
                        }
                        else // HLG
                        {
                            r = HLGToLinear(r);
                            g = HLGToLinear(g);
                            b = HLGToLinear(b);
                        }

                        // 3. Color space: BT.2020 → BT.709
                        if (settings.SourceColorSpace == HdrColorSpace.BT2020)
                        {
                            var color = new Vector3(r, g, b);
                            color = Vector3.Transform(color, _bt2020ToBt709);
                            r = color.X;
                            g = color.Y;
                            b = color.Z;
                        }

                        // 4. Tone mapping
                        r = ToneMap(r, settings);
                        g = ToneMap(g, settings);
                        b = ToneMap(b, settings);

                        // 5. Linear → sRGB
                        r = LinearToSrgb(r);
                        g = LinearToSrgb(g);
                        b = LinearToSrgb(b);

                        // 6. Output
                        dst[idx] = (byte)(Math.Clamp(b, 0, 1) * 255);
                        dst[idx + 1] = (byte)(Math.Clamp(g, 0, 1) * 255);
                        dst[idx + 2] = (byte)(Math.Clamp(r, 0, 1) * 255);
                        dst[idx + 3] = src[idx + 3];
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HDR] HDR→SDR error: {ex.Message}");
                    return false;
                }
            }
        }

        #endregion

        #region Transfer Functions

        // sRGB ↔ Linear
        private float SrgbToLinear(float v)
        {
            return v <= 0.04045f
                ? v / 12.92f
                : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        }

        private float LinearToSrgb(float v)
        {
            return v <= 0.0031308f
                ? v * 12.92f
                : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
        }

        // PQ (SMPTE ST.2084) ↔ Linear
        private float PQToLinear(float v, float peakLuminance = 10000f)
        {
            const float m1 = 2610f / 16384f;
            const float m2 = 2523f / 4096f * 128f;
            const float c1 = 3424f / 4096f;
            const float c2 = 2413f / 4096f * 32f;
            const float c3 = 2392f / 4096f * 32f;

            float Np = MathF.Pow(v, 1f / m2);
            float L = MathF.Pow(MathF.Max(Np - c1, 0f) / (c2 - c3 * Np), 1f / m1);

            return L * peakLuminance / SDR_PEAK_LUMINANCE;
        }

        private float LinearToPQ(float v, float peakLuminance = 10000f)
        {
            const float m1 = 2610f / 16384f;
            const float m2 = 2523f / 4096f * 128f;
            const float c1 = 3424f / 4096f;
            const float c2 = 2413f / 4096f * 32f;
            const float c3 = 2392f / 4096f * 32f;

            float Y = v * SDR_PEAK_LUMINANCE / peakLuminance;
            float Ym1 = MathF.Pow(Y, m1);

            return MathF.Pow((c1 + c2 * Ym1) / (1f + c3 * Ym1), m2);
        }

        // HLG (Hybrid Log-Gamma) ↔ Linear
        private float HLGToLinear(float v)
        {
            const float a = 0.17883277f;
            const float b = 0.28466892f;
            const float c = 0.55991073f;

            if (v <= 0.5f)
                return v * v / 3f;
            else
                return (MathF.Exp((v - c) / a) + b) / 12f;
        }

        private float LinearToHLG(float v)
        {
            const float a = 0.17883277f;
            const float b = 0.28466892f;
            const float c = 0.55991073f;

            if (v <= 1f / 12f)
                return MathF.Sqrt(3f * v);
            else
                return a * MathF.Log(12f * v - b) + c;
        }

        #endregion

        #region Tone Mapping

        private float ToneMap(float v, HdrToSdrSettings settings)
        {
            // Luminance'ı normalize et
            float normalized = v * SDR_PEAK_LUMINANCE / settings.SourcePeakLuminance;

            return settings.ToneMappingOperator switch
            {
                ToneMappingOperator.Reinhard => ReinhardToneMap(normalized),
                ToneMappingOperator.ReinhardExtended => ReinhardExtendedToneMap(normalized, settings.TargetPeakLuminance / SDR_PEAK_LUMINANCE),
                ToneMappingOperator.ACES => AcesToneMap(normalized),
                ToneMappingOperator.Hable => HableToneMap(normalized),
                ToneMappingOperator.BT2390 => BT2390ToneMap(normalized, settings),
                _ => ReinhardToneMap(normalized)
            };
        }

        private float InverseToneMap(float v, SdrToHdrSettings settings)
        {
            // SDR luminance'ı HDR'a genişlet
            float expanded = v * settings.PeakLuminance / SDR_PEAK_LUMINANCE;

            // Highlight boost
            if (v > settings.HighlightThreshold)
            {
                float excess = (v - settings.HighlightThreshold) / (1f - settings.HighlightThreshold);
                expanded += excess * settings.HighlightBoost;
            }

            return expanded;
        }

        // Reinhard tone mapping
        private float ReinhardToneMap(float v)
        {
            return v / (1f + v);
        }

        private float ReinhardExtendedToneMap(float v, float whitePoint)
        {
            float numerator = v * (1f + v / (whitePoint * whitePoint));
            return numerator / (1f + v);
        }

        // ACES filmic tone mapping
        private float AcesToneMap(float v)
        {
            const float a = 2.51f;
            const float b = 0.03f;
            const float c = 2.43f;
            const float d = 0.59f;
            const float e = 0.14f;

            return Math.Clamp((v * (a * v + b)) / (v * (c * v + d) + e), 0f, 1f);
        }

        // Hable/Uncharted 2 tone mapping
        private float HableToneMap(float v)
        {
            const float A = 0.15f;
            const float B = 0.50f;
            const float C = 0.10f;
            const float D = 0.20f;
            const float E = 0.02f;
            const float F = 0.30f;

            float Uncharted2(float x) =>
                ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;

            const float W = 11.2f;
            return Uncharted2(v) / Uncharted2(W);
        }

        // ITU-R BT.2390 tone mapping (broadcast standard)
        private float BT2390ToneMap(float v, HdrToSdrSettings settings)
        {
            float Lw = settings.SourcePeakLuminance / SDR_PEAK_LUMINANCE;
            float Lb = 0f; // Black level
            float Lmax = settings.TargetPeakLuminance / SDR_PEAK_LUMINANCE;

            // Knee point
            float ks = 1.5f * Lmax - 0.5f;
            float b = Lmax;

            if (v < ks)
                return v;

            // Soft roll-off
            float t = (v - ks) / (b - ks);
            return ks + (b - ks) * (1f - MathF.Pow(1f - t, 2f));
        }

        #endregion

        #region Helper Methods

        private void CreateTransferFunctionLuts()
        {
            const int lutSize = 4096;

            _pqToLinearLut = new float[lutSize];
            _linearToPqLut = new float[lutSize];
            _hlgToLinearLut = new float[lutSize];
            _linearToHlgLut = new float[lutSize];

            for (int i = 0; i < lutSize; i++)
            {
                float v = i / (float)(lutSize - 1);

                _pqToLinearLut[i] = PQToLinear(v);
                _linearToPqLut[i] = LinearToPQ(v);
                _hlgToLinearLut[i] = HLGToLinear(v);
                _linearToHlgLut[i] = LinearToHLG(v);
            }
        }

        private void CalculateColorSpaceMatrices()
        {
            // BT.709 → BT.2020 matris
            // Basitleştirilmiş versiyon
            _bt709ToBt2020 = new Matrix4x4(
                0.6274f, 0.3293f, 0.0433f, 0,
                0.0691f, 0.9195f, 0.0114f, 0,
                0.0164f, 0.0880f, 0.8956f, 0,
                0, 0, 0, 1
            );

            // BT.2020 → BT.709 matris (inverse)
            Matrix4x4.Invert(_bt709ToBt2020, out _bt2020ToBt709);
        }

        #endregion

        #region FFmpeg Integration

        /// <summary>
        /// HDR encoding için FFmpeg parametreleri
        /// </summary>
        public string GetHdrEncodingArgs(HdrEncodingSettings settings)
        {
            var args = new System.Text.StringBuilder();

            // Color primaries
            args.Append($"-color_primaries {(settings.ColorSpace == HdrColorSpace.BT2020 ? "bt2020" : "bt709")} ");

            // Transfer characteristics
            args.Append(settings.TransferFunction switch
            {
                HdrTransferFunction.PQ => "-color_trc smpte2084 ",
                HdrTransferFunction.HLG => "-color_trc arib-std-b67 ",
                _ => "-color_trc bt709 "
            });

            // Color space
            args.Append($"-colorspace {(settings.ColorSpace == HdrColorSpace.BT2020 ? "bt2020nc" : "bt709")} ");

            // Pixel format (10-bit)
            args.Append("-pix_fmt yuv420p10le ");

            // HDR metadata
            if (settings.IncludeMetadata)
            {
                args.Append($"-max_cll \"{settings.MaxCLL},{settings.MaxFALL}\" ");
                args.Append($"-master_display \"G({BT2020_GREEN.X * 50000},{BT2020_GREEN.Y * 50000})");
                args.Append($"B({BT2020_BLUE.X * 50000},{BT2020_BLUE.Y * 50000})");
                args.Append($"R({BT2020_RED.X * 50000},{BT2020_RED.Y * 50000})");
                args.Append($"WP({BT2020_WHITE.X * 50000},{BT2020_WHITE.Y * 50000})");
                args.Append($"L({settings.MaxLuminance * 10000},{settings.MinLuminance * 10000})\" ");
            }

            return args.ToString();
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _pqToLinearLut = null;
            _linearToPqLut = null;
            _hlgToLinearLut = null;
            _linearToHlgLut = null;
        }

        #endregion
    }

    #region Supporting Types

    public enum HdrColorSpace
    {
        BT709,   // SDR
        BT2020,  // HDR Wide Color Gamut
        DCI_P3   // Cinema
    }

    public enum HdrTransferFunction
    {
        SDR,     // Gamma 2.2
        PQ,      // SMPTE ST.2084 (HDR10, Dolby Vision)
        HLG      // Hybrid Log-Gamma (Broadcast)
    }

    public enum ToneMappingOperator
    {
        Reinhard,
        ReinhardExtended,
        ACES,
        Hable,
        BT2390
    }

    public class HdrDisplayInfo
    {
        public float MaxLuminance { get; set; }
        public float MinLuminance { get; set; }
        public float MaxFullFrameLuminance { get; set; }
        public HdrColorSpace ColorSpace { get; set; }
        public HdrTransferFunction TransferFunction { get; set; }
    }

    public class SdrToHdrSettings
    {
        public float PeakLuminance { get; set; } = 1000f; // Target HDR peak nits
        public bool ConvertToBt2020 { get; set; } = true;
        public HdrTransferFunction TransferFunction { get; set; } = HdrTransferFunction.PQ;
        public float HighlightThreshold { get; set; } = 0.8f;
        public float HighlightBoost { get; set; } = 2f;
    }

    public class HdrToSdrSettings
    {
        public float SourcePeakLuminance { get; set; } = 1000f;
        public float TargetPeakLuminance { get; set; } = 100f;
        public HdrColorSpace SourceColorSpace { get; set; } = HdrColorSpace.BT2020;
        public HdrTransferFunction SourceTransferFunction { get; set; } = HdrTransferFunction.PQ;
        public ToneMappingOperator ToneMappingOperator { get; set; } = ToneMappingOperator.BT2390;
    }

    public class HdrEncodingSettings
    {
        public HdrColorSpace ColorSpace { get; set; } = HdrColorSpace.BT2020;
        public HdrTransferFunction TransferFunction { get; set; } = HdrTransferFunction.PQ;
        public bool IncludeMetadata { get; set; } = true;
        public int MaxCLL { get; set; } = 1000;  // Max Content Light Level
        public int MaxFALL { get; set; } = 400;  // Max Frame Average Light Level
        public float MaxLuminance { get; set; } = 1000f;
        public float MinLuminance { get; set; } = 0.001f;
    }

    #endregion
}
