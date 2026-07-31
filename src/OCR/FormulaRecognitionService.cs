using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Sdcb.PaddleInference;

namespace BeeX.OCR;

/// <summary>
/// Runs the PP-FormulaNet_plus-S inference graph directly with Sdcb.PaddleInference and outputs LaTeX.
/// Preprocessing aligns with PaddleX's UniMERNetImgDecode + UniMERNetTestTransform + LatexImageFormat:
/// crop whitespace -> scale proportionally into 384x384 -> center with black padding -> grayscale normalize (x/255-0.7931)/0.1738 -> [1,1,384,384].
/// </summary>
internal sealed class FormulaRecognitionService : IDisposable
{
    private const int InputSize = 384;
    private const double NormalizeMean = 0.7931;
    private const double NormalizeStd = 0.1738;

    private readonly object _lock = new();
    private PaddlePredictor? _predictor;
    private FormulaTokenizer? _tokenizer;

    public string Recognize(Bitmap bitmap)
    {
        lock (_lock)
        {
            EnsureLoaded();

            float[] input = Preprocess(bitmap);
            using (PaddleTensor inputTensor = _predictor!.GetInputTensor(_predictor.InputNames[0]))
            {
                inputTensor.Shape = [1, 1, InputSize, InputSize];
                inputTensor.SetData(input);
            }

            if (!_predictor.Run())
            {
                throw new InvalidOperationException("公式模型推理失败。");
            }

            using PaddleTensor outputTensor = _predictor.GetOutputTensor(_predictor.OutputNames[0]);
            long[] tokenIds = ReadTokenIds(outputTensor);
            return _tokenizer!.Decode(tokenIds);
        }
    }

    public void WarmUp()
    {
        lock (_lock)
        {
            EnsureLoaded();
        }
    }

    private void EnsureLoaded()
    {
        if (_predictor != null)
        {
            return;
        }

        string modelDirectory = PaddleModelStore.GetFormulaModelDirectory();
        _tokenizer = FormulaTokenizer.Load(modelDirectory);

        string modelFile = Path.Combine(modelDirectory, "inference.json");
        if (!File.Exists(modelFile))
        {
            modelFile = Path.Combine(modelDirectory, "inference.pdmodel");
        }

        // The formula model is a PIR-exported graph: memory_optimize_pass causes CreatePredictor to fail, so it must be disabled.
        // It can only run on the openblas runtime (BeeX_Formula sidecar): the MKL runtime with oneDNN throws dnnl::error at
        // onednn_op.scale, and the oneDNN substitution cannot be disabled via switches/DeletePass/FLAGS/allowlist;
        // the ONNX backend also cannot parse the PIR format (all four paths have been empirically ruled out)
        PaddleConfig config = PaddleConfig.FromModelFiles(modelFile, Path.Combine(modelDirectory, "inference.pdiparams"));
        config.MemoryOptimized = false;
        config.CpuMathThreadCount = Environment.ProcessorCount;
        _predictor = config.CreatePredictor();
    }

    private static long[] ReadTokenIds(PaddleTensor tensor)
    {
        int[] shape = tensor.Shape;
        int total = 1;
        foreach (int dim in shape)
        {
            total *= Math.Max(1, dim);
        }

        if (total <= 0)
        {
            return [];
        }

        return tensor.DataType switch
        {
            PaddleDataType.Int64 => tensor.GetData<long>(),
            PaddleDataType.Int32 => Array.ConvertAll(tensor.GetData<int>(), static value => (long)value),
            PaddleDataType.Float32 => ArgMaxPerStep(tensor.GetData<float>(), shape),
            _ => throw new InvalidOperationException("公式模型输出类型不支持：" + tensor.DataType)
        };
    }

    /// <summary>Fallback: if the exported graph outputs logits ([batch, seq, vocab]), take argmax per step.</summary>
    private static long[] ArgMaxPerStep(float[] logits, int[] shape)
    {
        if (shape.Length < 3)
        {
            throw new InvalidOperationException("公式模型输出形状不支持：[" + string.Join(",", shape) + "]");
        }

        int steps = shape[^2];
        int vocab = shape[^1];
        var ids = new long[steps];
        for (int step = 0; step < steps; step++)
        {
            int offset = step * vocab;
            int best = 0;
            float bestValue = float.MinValue;
            for (int i = 0; i < vocab; i++)
            {
                if (logits[offset + i] > bestValue)
                {
                    bestValue = logits[offset + i];
                    best = i;
                }
            }

            ids[step] = best;
        }

        return ids;
    }

    private static float[] Preprocess(Bitmap bitmap)
    {
        using Bitmap rgb = ToRgbOnWhite(bitmap);
        Rectangle content = FindContentBounds(rgb);
        using Bitmap canvas = new(InputSize, InputSize, PixelFormat.Format32bppArgb);

        double scale = Math.Min(
            InputSize / (double)content.Width,
            InputSize / (double)content.Height);
        int targetWidth = Math.Max(1, (int)Math.Round(content.Width * scale));
        int targetHeight = Math.Max(1, (int)Math.Round(content.Height * scale));
        int padX = (InputSize - targetWidth) / 2;
        int padY = (InputSize - targetHeight) / 2;

        using (Graphics graphics = Graphics.FromImage(canvas))
        {
            // ImageOps.expand defaults to 0 (black) padding, consistent with the training side
            graphics.Clear(Color.Black);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(rgb, new Rectangle(padX, padY, targetWidth, targetHeight), content, GraphicsUnit.Pixel);
        }

        return NormalizeToTensor(canvas);
    }

    private static Bitmap ToRgbOnWhite(Bitmap bitmap)
    {
        var converted = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(converted);
        graphics.Clear(Color.White);
        graphics.DrawImage(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        return converted;
    }

    /// <summary>Aligns crop_margin: after grayscale stretching, find the bounding box of content pixels below 200.</summary>
    private static Rectangle FindContentBounds(Bitmap bitmap)
    {
        Rectangle full = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] pixels = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            byte[] gray = new byte[bitmap.Width * bitmap.Height];
            byte min = 255, max = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int offset = row + x * 4;
                    byte value = (byte)((pixels[offset + 2] * 299 + pixels[offset + 1] * 587 + pixels[offset] * 114) / 1000);
                    gray[y * bitmap.Width + x] = value;
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
            }

            if (max == min)
            {
                return full;
            }

            int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
            double range = max - min;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    double normalized = (gray[y * bitmap.Width + x] - min) / range * 255.0;
                    if (normalized < 200.0)
                    {
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
                }
            }

            if (right < left || bottom < top)
            {
                return full;
            }

            int width = right - left + 1;
            int height = bottom - top + 1;
            if (Math.Max(width, height) / (double)Math.Max(1, Math.Min(width, height)) > 200.0)
            {
                return full;
            }

            return new Rectangle(left, top, width, height);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static float[] NormalizeToTensor(Bitmap canvas)
    {
        Rectangle full = new(0, 0, InputSize, InputSize);
        BitmapData data = canvas.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] pixels = new byte[stride * InputSize];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            var tensor = new float[InputSize * InputSize];
            for (int y = 0; y < InputSize; y++)
            {
                int row = y * stride;
                for (int x = 0; x < InputSize; x++)
                {
                    int offset = row + x * 4;
                    // cv2 RGB2GRAY weights
                    double gray = (pixels[offset + 2] * 0.299 + pixels[offset + 1] * 0.587 + pixels[offset] * 0.114) / 255.0;
                    tensor[y * InputSize + x] = (float)((gray - NormalizeMean) / NormalizeStd);
                }
            }

            return tensor;
        }
        finally
        {
            canvas.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _predictor?.Dispose();
            _predictor = null;
        }
    }
}
