using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BeeX.OCR;

internal static class OcrImagePreprocessor
{
    public static OcrImageStats Analyze(Bitmap source)
    {
        using Bitmap working = ConvertToArgb(source);
        Rectangle bounds = new(0, 0, working.Width, working.Height);
        BitmapData data = working.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] pixels = new byte[stride * working.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            int[] histogram = new int[256];
            long totalLuminance = 0;

            for (int y = 0; y < working.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < working.Width; x++)
                {
                    int offset = row + x * 4;
                    int luminance = Luminance(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                    histogram[luminance]++;
                    totalLuminance += luminance;
                }
            }

            int totalPixels = Math.Max(1, working.Width * working.Height);
            int p05 = Percentile(histogram, Math.Max(1, totalPixels * 5 / 100));
            int p95 = Percentile(histogram, Math.Max(1, totalPixels * 95 / 100));
            double average = totalLuminance / (double)totalPixels;

            return new OcrImageStats(average, p05, p95);
        }
        finally
        {
            working.UnlockBits(data);
        }
    }

    public static List<OcrImageCandidate> CreateRecoveryCandidates(Bitmap source, int maxImageDimension, OcrImageStats stats)
    {
        var candidates = new List<OcrImageCandidate>();

        if (stats.LooksDark)
        {
            candidates.Add(new OcrImageCandidate("dark-binary", Prepare(source, maxImageDimension, binarizeForText: true, darkTextSource: true)));
            candidates.Add(new OcrImageCandidate("dark-inverted", Prepare(source, maxImageDimension, enhanceContrast: true, invert: true)));
        }
        else
        {
            candidates.Add(new OcrImageCandidate("contrast", Prepare(source, maxImageDimension, enhanceContrast: true)));
        }

        int leftCrop = GuessLeftNoiseCrop(source);
        if (leftCrop > 0)
        {
            if (stats.LooksDark)
            {
                candidates.Add(new OcrImageCandidate("dark-binary-left", Prepare(source, maxImageDimension, binarizeForText: true, darkTextSource: true, leftCropPixels: leftCrop)));
            }
            else
            {
                candidates.Add(new OcrImageCandidate("contrast-left", Prepare(source, maxImageDimension, enhanceContrast: true, leftCropPixels: leftCrop)));
            }
        }

        return candidates;
    }

    public static Bitmap Prepare(
        Bitmap source,
        int maxImageDimension,
        bool enhanceContrast = false,
        bool invert = false,
        bool binarizeForText = false,
        bool darkTextSource = false,
        int leftCropPixels = 0)
    {
        if (source.Width < 2 || source.Height < 2)
        {
            throw new InvalidOperationException("图片区域太小，无法识别。");
        }

        using Bitmap? cropped = leftCropPixels > 0 ? CropLeft(source, leftCropPixels) : null;
        Bitmap input = cropped ?? source;

        int safeMaxDimension = Math.Clamp(maxImageDimension, 256, 2600);
        double scale = GetScale(input.Width, input.Height, safeMaxDimension);
        int contentWidth = Math.Max(2, (int)Math.Round(input.Width * scale));
        int contentHeight = Math.Max(2, (int)Math.Round(input.Height * scale));
        int padding = Math.Min(16, Math.Max(0, (safeMaxDimension - Math.Max(contentWidth, contentHeight)) / 2));
        int width = contentWidth + padding * 2;
        int height = contentHeight + padding * 2;

        var prepared = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using (Graphics graphics = Graphics.FromImage(prepared))
        {
            graphics.Clear(Color.White);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = scale >= 1.0 ? InterpolationMode.HighQualityBicubic : InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.DrawImage(input, new Rectangle(padding, padding, contentWidth, contentHeight));
        }

        if (invert)
        {
            Invert(prepared);
        }

        if (enhanceContrast)
        {
            StretchGrayscaleContrast(prepared);
        }

        if (binarizeForText)
        {
            BinarizeForText(prepared, darkTextSource);
        }

        return prepared;
    }

    private static double GetScale(int width, int height, int maxImageDimension)
    {
        int maxSide = Math.Max(width, height);
        int minSide = Math.Min(width, height);

        if (maxSide > maxImageDimension)
        {
            return maxImageDimension / (double)maxSide;
        }

        double scale = 1.0;

        if (maxSide < 360 || minSide < 80)
        {
            scale = 3.0;
        }
        else if (maxSide < 650 || minSide < 120)
        {
            scale = 2.0;
        }
        else if (minSide < 160)
        {
            scale = 1.5;
        }

        return Math.Min(scale, maxImageDimension / (double)maxSide);
    }

    private static int GuessLeftNoiseCrop(Bitmap source)
    {
        if (source.Width < 100 || source.Height < 32)
        {
            return 0;
        }

        int maxCrop = Math.Min(52, source.Width / 4);
        if (maxCrop < 18)
        {
            return 0;
        }

        return Math.Clamp(source.Width / 9, 18, maxCrop);
    }

    private static Bitmap CropLeft(Bitmap source, int leftCropPixels)
    {
        int left = Math.Clamp(leftCropPixels, 0, source.Width - 2);
        Rectangle bounds = new(left, 0, source.Width - left, source.Height);
        return source.Clone(bounds, PixelFormat.Format32bppArgb);
    }

    private static Bitmap ConvertToArgb(Bitmap source)
    {
        if (source.PixelFormat == PixelFormat.Format32bppArgb)
        {
            return new Bitmap(source);
        }

        var converted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(converted);
        graphics.Clear(Color.White);
        graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
        return converted;
    }

    private static void Invert(Bitmap bitmap)
    {
        TransformPixels(bitmap, static (red, green, blue) => ((byte)(255 - red), (byte)(255 - green), (byte)(255 - blue)));
    }

    private static void BinarizeForText(Bitmap bitmap, bool sourceIsDark)
    {
        Rectangle bounds = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] pixels = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            int[] histogram = new int[256];
            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int offset = row + x * 4;
                    histogram[Luminance(pixels[offset + 2], pixels[offset + 1], pixels[offset])]++;
                }
            }

            int total = Math.Max(1, bitmap.Width * bitmap.Height);
            int low = Percentile(histogram, Math.Max(1, total * 8 / 100));
            int high = Percentile(histogram, Math.Max(1, total * 92 / 100));
            int threshold = OtsuThreshold(histogram, total);

            if (high - low < 32)
            {
                threshold = (low + high) / 2;
            }

            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int offset = row + x * 4;
                    int luminance = Luminance(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                    bool foreground = sourceIsDark ? luminance >= threshold : luminance <= threshold;
                    byte value = foreground ? (byte)0 : (byte)255;
                    pixels[offset] = value;
                    pixels[offset + 1] = value;
                    pixels[offset + 2] = value;
                    pixels[offset + 3] = 255;
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void StretchGrayscaleContrast(Bitmap bitmap)
    {
        Rectangle bounds = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] pixels = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            int[] histogram = new int[256];
            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int offset = row + x * 4;
                    int luminance = Luminance(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                    histogram[luminance]++;
                }
            }

            int total = bitmap.Width * bitmap.Height;
            int low = Percentile(histogram, Math.Max(1, total / 100));
            int high = Percentile(histogram, Math.Max(1, total - total / 100));

            if (high - low < 24)
            {
                low = 0;
                high = 255;
            }

            double factor = 255.0 / Math.Max(1, high - low);

            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int offset = row + x * 4;
                    int luminance = Luminance(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                    byte gray = (byte)Math.Clamp((int)Math.Round((luminance - low) * factor), 0, 255);
                    pixels[offset] = gray;
                    pixels[offset + 1] = gray;
                    pixels[offset + 2] = gray;
                    pixels[offset + 3] = 255;
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static int Luminance(byte red, byte green, byte blue)
    {
        return (red * 299 + green * 587 + blue * 114) / 1000;
    }

    private static void TransformPixels(Bitmap bitmap, Func<byte, byte, byte, (byte Red, byte Green, byte Blue)> transform)
    {
        Rectangle bounds = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] pixels = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int offset = row + x * 4;
                    (byte red, byte green, byte blue) = transform(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                    pixels[offset] = blue;
                    pixels[offset + 1] = green;
                    pixels[offset + 2] = red;
                    pixels[offset + 3] = 255;
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static int OtsuThreshold(int[] histogram, int total)
    {
        double sum = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            sum += i * histogram[i];
        }

        double sumBackground = 0;
        int weightBackground = 0;
        double bestVariance = -1;
        int threshold = 127;

        for (int i = 0; i < histogram.Length; i++)
        {
            weightBackground += histogram[i];
            if (weightBackground == 0)
            {
                continue;
            }

            int weightForeground = total - weightBackground;
            if (weightForeground == 0)
            {
                break;
            }

            sumBackground += i * histogram[i];
            double meanBackground = sumBackground / weightBackground;
            double meanForeground = (sum - sumBackground) / weightForeground;
            double variance = weightBackground * weightForeground * Math.Pow(meanBackground - meanForeground, 2);

            if (variance > bestVariance)
            {
                bestVariance = variance;
                threshold = i;
            }
        }

        return threshold;
    }

    private static int Percentile(int[] histogram, int targetCount)
    {
        int cumulative = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= targetCount)
            {
                return i;
            }
        }

        return 255;
    }
}
