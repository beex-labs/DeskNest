using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Management;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;

namespace BeeX.OCR;

internal sealed class OcrService : IDisposable
{
    private const int MaxPrimaryImageDimension = 1280;
    private const int MaxRecoveryImageDimension = 1600;
    private const int RecognizeBatchSize = 16;
    private readonly Dictionary<string, PaddleOcrAll> _engineCache = new();
    private readonly object _engineLock = new();

    private static readonly Lazy<bool> _nvidiaGpuAvailable = new(IsNvidiaGpuAvailableCore);

    public IReadOnlyList<OcrLanguageOption> GetAvailableLanguages()
    {
        return
        [
            new("PaddleOCR 中文 V5 Server（中英混合）", "paddle:chinese-v5")
        ];
    }

    public async Task<string> RecognizeAsync(Bitmap bitmap, string? languageTag)
    {
        return (await RecognizeDetailedAsync(bitmap, languageTag)).Text;
    }

    public async Task<OcrRecognitionReport> RecognizeDetailedAsync(Bitmap bitmap, string? languageTag)
    {
        return await Task.Run(() => RecognizeCore(bitmap, languageTag));
    }

    public async Task WarmUpAsync(string? languageTag)
    {
        await Task.Run(() =>
        {
            using Bitmap bitmap = CreateWarmUpBitmap();
            _ = RecognizeCore(bitmap, languageTag);
        });
    }

    public async Task LoadEngineAsync(string? languageTag)
    {
        await Task.Run(() => GetEngine(languageTag));
    }

    /// <summary>Returns the bounding rectangle and text of each text box (used by the sidecar OCRPOS command).</summary>
    public List<(int X, int Y, int Width, int Height, string Text)> RecognizeWithPositions(Bitmap bitmap)
    {
        PaddleOcrResult result = RunRaw(bitmap);
        var blocks = new List<(int, int, int, int, string)>();
        foreach (var region in result.Regions)
        {
            Point2f[] points = region.Rect.Points();
            int x = (int)Math.Round(points.Min(p => p.X));
            int y = (int)Math.Round(points.Min(p => p.Y));
            int right = (int)Math.Round(points.Max(p => p.X));
            int bottom = (int)Math.Round(points.Max(p => p.Y));
            blocks.Add((x, y, Math.Max(1, right - x), Math.Max(1, bottom - y), region.Text ?? ""));
        }
        return blocks;
    }

    /// <summary>Returns raw recognition results with coordinates (for scenarios that need position info, such as table reconstruction).</summary>
    internal PaddleOcrResult RunRaw(Bitmap bitmap)
    {
        PaddleOcrAll engine = GetEngine(null);
        lock (_engineLock)
        {
            using Mat mat = BitmapToMat(bitmap);
            return engine.Run(mat, RecognizeBatchSize);
        }
    }

    /// <summary>Runs raw recognition on an already-converted Mat (the caller owns the Mat lifecycle).</summary>
    internal PaddleOcrResult RunRaw(Mat mat)
    {
        PaddleOcrAll engine = GetEngine(null);
        lock (_engineLock)
        {
            return engine.Run(mat, RecognizeBatchSize);
        }
    }

    private OcrRecognitionReport RecognizeCore(Bitmap bitmap, string? languageTag)
    {
        using Bitmap? primaryBitmap = CreatePrimaryBitmapIfNeeded(bitmap);
        Bitmap sourceBitmap = primaryBitmap ?? bitmap;
        OcrImageStats stats = OcrImagePreprocessor.Analyze(sourceBitmap);
        OcrCandidateResult source = RecognizeCandidate(languageTag, "source", sourceBitmap);
        OcrCandidateResult best = source;
        var candidates = new List<OcrCandidateInfo>
        {
            best.ToInfo()
        };

        // The server recognition model has high accuracy: when the primary result is strong enough, accept it directly to avoid unconditionally triggering
        // the multi-candidate recovery pipeline on dark/low-contrast screenshots (each candidate is a full recognition pass, the main time cost, up to 3 extra runs)
        bool needsRecovery = best.Score < 70 || string.IsNullOrWhiteSpace(best.Text);
        if (!needsRecovery && (stats.LooksDark || stats.LowContrast))
        {
            needsRecovery = !OcrRecognitionScore.IsStrongResult(best.Text);
        }

        if (needsRecovery)
        {
            using ListDisposer<OcrImageCandidate> recoveryCandidates = new(OcrImagePreprocessor.CreateRecoveryCandidates(bitmap, MaxRecoveryImageDimension, stats));

            foreach (OcrImageCandidate candidate in recoveryCandidates.Items)
            {
                OcrCandidateResult current = RecognizeCandidate(languageTag, candidate.Name, candidate.Bitmap);
                candidates.Add(current.ToInfo());
                if (current.Score > best.Score)
                {
                    best = current;
                }

                // Exit early once a strong-enough result is obtained; do not run the remaining candidates
                if (OcrRecognitionScore.IsStrongResult(best.Text))
                {
                    break;
                }
            }
        }

        if (best.CandidateName != source.CandidateName &&
            source.Score >= 180 &&
            best.Score - source.Score < 80)
        {
            best = source;
        }

        return new OcrRecognitionReport(best.Text, best.CandidateName, best.Score, candidates);
    }

    private static Bitmap? CreatePrimaryBitmapIfNeeded(Bitmap bitmap)
    {
        int maxSide = Math.Max(bitmap.Width, bitmap.Height);
        if (maxSide <= MaxPrimaryImageDimension)
        {
            return null;
        }

        return OcrImagePreprocessor.Prepare(bitmap, MaxPrimaryImageDimension);
    }

    private OcrCandidateResult RecognizeCandidate(string? languageTag, string candidateName, Bitmap bitmap)
    {
        PaddleOcrAll engine = GetEngine(languageTag);

        lock (_engineLock)
        {
            using Mat mat = BitmapToMat(bitmap);
            PaddleOcrResult result = engine.Run(mat, RecognizeBatchSize);
            string text = OcrLayoutBuilder.Build(result);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = OcrTextPostProcessor.Clean(result.Text);
            }
            else
            {
                text = OcrTextPostProcessor.CleanLayoutText(text);
            }

            text = text.Replace("\u53e6\u5202", "\u522b", StringComparison.Ordinal);
            return new OcrCandidateResult(candidateName, text, OcrRecognitionScore.Score(text));
        }
    }

    private PaddleOcrAll GetEngine(string? languageTag)
    {
        string key = string.IsNullOrWhiteSpace(languageTag) ? "paddle:chinese-v5" : languageTag;

        lock (_engineLock)
        {
            if (_engineCache.TryGetValue(key, out PaddleOcrAll? cached))
            {
                return cached;
            }

            FullOcrModel model = GetModel();
            Action<PaddleConfig> device;
            try
            {
                device = CreateDevice();
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    "OCR 引擎初始化失败：当前版本需要 NVIDIA GPU 和 CUDA 驱动。请安装 GPU 驱动，或使用 CPU 版本。",
                    ex);
            }

            PaddleOcrAll engine;
            try
            {
                engine = new PaddleOcrAll(model, device)
                {
                    AllowRotateDetection = false,
                    Enable180Classification = false
                };
            }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
            {
#if CUDA_RUNTIME
                throw new InvalidOperationException(
                    "OCR 引擎初始化失败：当前 CUDA 版本需要 NVIDIA GPU 和 CUDA 驱动。请安装 GPU 驱动，或使用 CPU 版本。",
                    ex);
#else
                throw;
#endif
            }

            engine.Detector.MaxSize = 960;
            _engineCache[key] = engine;
            return engine;
        }
    }

    private static Action<PaddleConfig> CreateDevice()
    {
#if CUDA_RUNTIME
        if (IsNvidiaGpuAvailable())
        {
            try
            {
                return PaddleDevice.Gpu();
            }
            catch
            {
                // GPU runtime unavailable; report an error on fallback (the CUDA build has no CPU library)
            }
        }
        // The CUDA build has no GPU available
        throw new InvalidOperationException(
            "此版本需要 NVIDIA GPU 和 CUDA 驱动。请安装 GPU 驱动，或使用 CPU 版本。");
#else
        // CPU build (MKL or openblas): does not use the GPU
        return PaddleDevice.Blas(cpuMathThreadCount: Environment.ProcessorCount);
#endif
    }

    private static bool IsNvidiaGpuAvailable() => _nvidiaGpuAvailable.Value;

    private static bool IsNvidiaGpuAvailableCore()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
            {
                string name = obj["Name"]?.ToString() ?? "";
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // WMI query failed; conservatively fall back to CPU
        }
        return false;
    }

    private static FullOcrModel GetModel()
    {
        PaddleModelPaths paths = PaddleModelStore.GetOcrModelPaths();
        DetectionModel detection = DetectionModel.FromDirectory(paths.DetectionDirectory, ModelVersion.V5);
        RecognizationModel recognition = RecognizationModel.FromDirectoryV5(paths.RecognitionDirectory);
        return new FullOcrModel(detection, recognition);
    }

    private static Mat BitmapToMat(Bitmap bitmap)
    {
        using Bitmap working = EnsureArgb(bitmap);
        Rectangle bounds = new(0, 0, working.Width, working.Height);
        BitmapData data = working.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            using Mat bgra = Mat.FromPixelData(working.Height, working.Width, MatType.CV_8UC4, data.Scan0, data.Stride);
            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            return bgr;
        }
        finally
        {
            working.UnlockBits(data);
        }
    }

    private static Bitmap EnsureArgb(Bitmap bitmap)
    {
        if (bitmap.PixelFormat == PixelFormat.Format32bppArgb)
        {
            return new Bitmap(bitmap);
        }

        var converted = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(converted);
        graphics.Clear(Color.White);
        graphics.DrawImage(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        return converted;
    }

    private static Bitmap CreateWarmUpBitmap()
    {
        var bitmap = new Bitmap(320, 90, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        using var brush = new SolidBrush(Color.Black);
        using var font = new Font("Microsoft YaHei UI", 22, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.DrawString("BeeX OCR 123", font, brush, new PointF(12, 22));
        return bitmap;
    }

    public void Dispose()
    {
        lock (_engineLock)
        {
            foreach (PaddleOcrAll engine in _engineCache.Values)
            {
                engine.Dispose();
            }

            _engineCache.Clear();
        }
    }

    private readonly record struct OcrCandidateResult(string CandidateName, string Text, int Score)
    {
        public OcrCandidateInfo ToInfo()
        {
            return new OcrCandidateInfo(CandidateName, Text, Score);
        }
    }

    private sealed class ListDisposer<T> : IDisposable where T : IDisposable
    {
        public ListDisposer(List<T> items)
        {
            Items = items;
        }

        public List<T> Items { get; }

        public void Dispose()
        {
            foreach (T item in Items)
            {
                item.Dispose();
            }
        }
    }
}
