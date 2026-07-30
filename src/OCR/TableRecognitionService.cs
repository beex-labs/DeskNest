using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using OpenCvSharp;
using Sdcb.PaddleOCR;

namespace BeeX.OCR;

/// <summary>
/// 表格识别 v3：优先用 OpenCV 框线检测还原真实网格（列/行边界=表格线位置，
/// 两格之间缺失分隔线=合并单元格，确定性远高于文本框启发式）；
/// 无框线表格回退 v2 文本框聚类启发式。输出带 colspan/rowspan 的 HTML。
/// </summary>
internal sealed class TableRecognitionService : IDisposable
{
    private sealed record Box(string Text, double X, double Y, double W, double H)
    {
        public double CenterX => X + W / 2;
        public double CenterY => Y + H / 2;
    }

    private readonly OcrService _ocrService;

    public TableRecognitionService(OcrService ocrService)
    {
        _ocrService = ocrService;
    }

    private const int TableMaxImageDimension = 1600;

    private sealed record GridDetectionResult(
        List<int> YBounds, List<int> XBounds,
        bool[,] MergeRight, bool[,] MergeDown,
        List<(int R, int C, int RS, int CS)> Cells);

    public string RecognizeHtml(Bitmap bitmap)
    {
        // 降采样：最大边 > 1600px 时按比例缩小，加速 OCR + OpenCV
        double scale = 1.0;
        int maxSide = Math.Max(bitmap.Width, bitmap.Height);
        Bitmap workBitmap;
        if (maxSide > TableMaxImageDimension)
        {
            scale = (double)TableMaxImageDimension / maxSide;
            int newW = (int)(bitmap.Width * scale);
            int newH = (int)(bitmap.Height * scale);
            workBitmap = new Bitmap(bitmap, newW, newH);
        }
        else
        {
            workBitmap = new Bitmap(bitmap);
        }

        try
        {
            using Mat mat = BitmapToMatFast(workBitmap);

            // 并行执行 OCR 推理与 OpenCV 线检测，各持一份 Mat 副本以保证线程安全
            var ocrTask = Task.Run(() =>
            {
                using var clone = mat.Clone();
                return _ocrService.RunRaw(clone);
            });
            var gridTask = Task.Run(() =>
            {
                using var clone = mat.Clone();
                return DetectGridLines(clone);
            });

            PaddleOcrResult ocrResult = ocrTask.GetAwaiter().GetResult();
            GridDetectionResult? grid = gridTask.GetAwaiter().GetResult();

            // 坐标还原到原始尺寸
            var boxes = ocrResult.Regions
                .Where(r => !string.IsNullOrWhiteSpace(r.Text))
                .Select(r =>
                {
                    var rect = r.Rect.BoundingRect();
                    if (scale != 1.0)
                        return new Box(OcrTextPostProcessor.Clean(r.Text),
                            rect.X / scale, rect.Y / scale,
                            rect.Width / scale, rect.Height / scale);
                    return new Box(OcrTextPostProcessor.Clean(r.Text), rect.X, rect.Y, rect.Width, rect.Height);
                })
                .OrderBy(b => b.Y).ToList();

            if (boxes.Count == 0)
                return string.Empty;

            if (grid != null)
            {
                // 线检测坐标还原
                var yBounds = scale != 1.0 ? grid.YBounds.Select(y => (int)(y / scale)).ToList() : grid.YBounds;
                var xBounds = scale != 1.0 ? grid.XBounds.Select(x => (int)(x / scale)).ToList() : grid.XBounds;
                string? html = BuildGridHtml(yBounds, xBounds, grid.MergeRight, grid.MergeDown, grid.Cells, boxes);
                if (html != null) return html;
            }

            return HeuristicHtml(boxes);
        }
        finally
        {
            workBitmap.Dispose();
        }
    }

    // ================= 框线网格路径 =================

    /// <summary>OpenCV 线检测 + 合并分析（不依赖 OCR 结果，可与 OCR 并行）。</summary>
    private static GridDetectionResult? DetectGridLines(Mat mat)
    {
        try
        {
            using var gray = mat.CvtColor(ColorConversionCodes.BGR2GRAY);
            using var inverted = new Mat();
            Cv2.BitwiseNot(gray, inverted);
            using var bw = new Mat();
            Cv2.AdaptiveThreshold(inverted, bw, 255, AdaptiveThresholdTypes.MeanC, ThresholdTypes.Binary, 15, -2);

            int hk = Math.Max(10, mat.Cols / 20), vk = Math.Max(10, mat.Rows / 20);
            using var hKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(hk, 1));
            using var vKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(1, vk));
            using var hLines = new Mat(); using var vLines = new Mat();
            Cv2.MorphologyEx(bw, hLines, MorphTypes.Open, hKernel);
            Cv2.MorphologyEx(bw, vLines, MorphTypes.Open, vKernel);

            var yBounds = ProjectBounds(hLines, horizontal: true, minCoverage: 0.4);
            var xBounds = ProjectBounds(vLines, horizontal: false, minCoverage: 0.4);
            if (Environment.GetEnvironmentVariable("BEEX_TABLE_DEBUG") == "1")
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "beex_table_debug.txt"),
                    "x: " + string.Join(",", xBounds) + "\ny: " + string.Join(",", yBounds));
            }
            if (yBounds.Count < 3 || xBounds.Count < 3 || yBounds.Count > 80 || xBounds.Count > 80)
                return null;

            int rows = yBounds.Count - 1, cols = xBounds.Count - 1;
            var mergeRight = new bool[rows, cols];
            var mergeDown = new bool[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    if (c + 1 < cols)
                        mergeRight[r, c] = !SegmentExists(vLines, xBounds[c + 1], yBounds[r], yBounds[r + 1], vertical: true);
                    if (r + 1 < rows)
                        mergeDown[r, c] = !SegmentExists(hLines, yBounds[r + 1], xBounds[c], xBounds[c + 1], vertical: false);
                }

            var owner = new (int R, int C)[rows, cols];
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) owner[r, c] = (-1, -1);
            var cells = new List<(int R, int C, int RS, int CS)>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    if (owner[r, c].R >= 0) continue;
                    int cs = 1;
                    while (c + cs < cols && Enumerable.Range(r, 1).All(rr => mergeRight[rr, c + cs - 1])) cs++;
                    int rs = 1;
                    while (r + rs < rows && Enumerable.Range(c, cs).All(cc => mergeDown[r + rs - 1, cc])) rs++;
                    for (int rr = r; rr < r + rs; rr++) for (int cc = c; cc < c + cs; cc++) owner[rr, cc] = (r, c);
                    cells.Add((r, c, rs, cs));
                }

            return new GridDetectionResult(yBounds, xBounds, mergeRight, mergeDown, cells);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用 OCR 文本框填充网格单元格，生成 HTML。</summary>
    private static string? BuildGridHtml(List<int> yBounds, List<int> xBounds,
        bool[,] mergeRight, bool[,] mergeDown,
        List<(int R, int C, int RS, int CS)> cells, List<Box> boxes)
    {
        int rows = yBounds.Count - 1, cols = xBounds.Count - 1;
        var owner = new (int R, int C)[rows, cols];
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) owner[r, c] = (-1, -1);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                if (owner[r, c].R >= 0) continue;
                int cs = 1;
                while (c + cs < cols && Enumerable.Range(r, 1).All(rr => mergeRight[rr, c + cs - 1])) cs++;
                int rs = 1;
                while (r + rs < rows && Enumerable.Range(c, cs).All(cc => mergeDown[r + rs - 1, cc])) rs++;
                for (int rr = r; rr < r + rs; rr++) for (int cc = c; cc < c + cs; cc++) owner[rr, cc] = (r, c);
            }

        var content = new Dictionary<(int, int), List<Box>>();
        var outside = new List<Box>();
        foreach (var box in boxes)
        {
            int r = FindBand(yBounds, box.CenterY), c = FindBand(xBounds, box.CenterX);
            if (r < 0 || c < 0) { outside.Add(box); continue; }
            var key = owner[r, c];
            if (!content.TryGetValue(key, out var list)) content[key] = list = [];
            list.Add(box);
        }

        string CellText((int R, int C) key) => content.TryGetValue(key, out var list)
            ? string.Join(" ", list.OrderBy(b => b.Y).ThenBy(b => b.X).Select(b => b.Text))
            : string.Empty;

        var html = new StringBuilder("<table style=\"border-collapse:collapse;margin:4px 0\">");
        foreach (var box in outside.Where(b => b.CenterY < yBounds[0]).OrderBy(b => b.Y))
            html.Append($"<tr><td style=\"padding:4px 6px\" colspan=\"{cols}\">{System.Net.WebUtility.HtmlEncode(box.Text)}</td></tr>");
        for (int r = 0; r < rows; r++)
        {
            html.Append("<tr>");
            foreach (var cell in cells.Where(x => x.R == r).OrderBy(x => x.C))
            {
                html.Append("<td style=\"padding:4px 6px\"");
                if (cell.CS > 1) html.Append($" colspan=\"{cell.CS}\"");
                if (cell.RS > 1) html.Append($" rowspan=\"{cell.RS}\"");
                html.Append('>').Append(System.Net.WebUtility.HtmlEncode(CellText((cell.R, cell.C)))).Append("</td>");
            }
            html.Append("</tr>");
        }
        foreach (var box in outside.Where(b => b.CenterY >= yBounds[^1]).OrderBy(b => b.Y))
            html.Append($"<tr><td style=\"padding:4px 6px\" colspan=\"{cols}\">{System.Net.WebUtility.HtmlEncode(box.Text)}</td></tr>");
        return html.Append("</table>").ToString();
    }

    /// <summary>把线掩码沿一个方向投影，聚类出边界坐标。</summary>
    private static List<int> ProjectBounds(Mat lines, bool horizontal, double minCoverage)
    {
        int n = horizontal ? lines.Rows : lines.Cols;
        int span = horizontal ? lines.Cols : lines.Rows;
        var strengths = new int[n];
        using (var reduced = new Mat())
        {
            Cv2.Reduce(lines, reduced, horizontal ? ReduceDimension.Column : ReduceDimension.Row, ReduceTypes.Sum, MatType.CV_32S);
            for (int i = 0; i < n; i++)
                strengths[i] = reduced.At<int>(horizontal ? i : 0, horizontal ? 0 : i) / 255;
        }

        var bounds = new List<int>();
        int runStart = -1;
        for (int i = 0; i <= n; i++)
        {
            bool on = i < n && strengths[i] >= span * minCoverage;
            if (on && runStart < 0) runStart = i;
            else if (!on && runStart >= 0)
            {
                bounds.Add((runStart + i - 1) / 2);
                runStart = -1;
            }
        }
        // 合并间距 <6px 的重复线
        var merged = new List<int>();
        foreach (var b in bounds)
            if (merged.Count == 0 || b - merged[^1] > 6) merged.Add(b);
            else merged[^1] = (merged[^1] + b) / 2;
        return merged;
    }

    /// <summary>检查两格之间的分隔线段是否真实存在（覆盖率 > 35%）。</summary>
    private static bool SegmentExists(Mat lines, int at, int from, int to, bool vertical)
    {
        int pad = Math.Max(2, (to - from) / 10);
        from += pad; to -= pad;
        if (to <= from) return true;
        int hits = 0, total = 0;
        for (int i = from; i <= to; i++)
        {
            total++;
            for (int d = -2; d <= 2; d++)
            {
                int x = vertical ? at + d : i, y = vertical ? i : at + d;
                if (x < 0 || y < 0 || x >= lines.Cols || y >= lines.Rows) continue;
                if (lines.At<byte>(y, x) > 0) { hits++; break; }
            }
        }
        return total > 0 && hits > total * 0.35;
    }

    private static int FindBand(List<int> bounds, double v)
    {
        for (int i = 0; i < bounds.Count - 1; i++)
            if (v >= bounds[i] && v < bounds[i + 1]) return i;
        return -1;
    }

    /// <summary>LockBits 直接复制像素数据，避免 PNG 编解码开销。</summary>
    private static Mat BitmapToMatFast(Bitmap bitmap)
    {
        var argb = bitmap.PixelFormat == PixelFormat.Format32bppArgb
            ? bitmap
            : ConvertToArgb(bitmap);
        var bounds = new Rectangle(0, 0, argb.Width, argb.Height);
        BitmapData data = argb.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var bgra = Mat.FromPixelData(argb.Height, argb.Width, MatType.CV_8UC4, data.Scan0, data.Stride);
            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            return bgr;
        }
        finally
        {
            argb.UnlockBits(data);
            if (!ReferenceEquals(argb, bitmap))
                argb.Dispose();
        }
    }

    private static Bitmap ConvertToArgb(Bitmap bitmap)
    {
        var converted = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(converted);
        g.Clear(Color.White);
        g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
        return converted;
    }

    // ================= v2 启发式回退（无框线表格） =================

    private static string HeuristicHtml(List<Box> boxes)
    {
        double medianH = Median(boxes.Select(b => b.H));
        double rowThreshold = Math.Max(8.0, medianH * 0.6);
        var rowGroups = new List<List<Box>>();
        foreach (var box in boxes)
        {
            var row = rowGroups.FirstOrDefault(g => Math.Abs(g.Average(i => i.CenterY) - box.CenterY) <= rowThreshold);
            if (row == null) { row = []; rowGroups.Add(row); }
            row.Add(box);
        }
        rowGroups.Sort((a, b) => a.Average(i => i.CenterY).CompareTo(b.Average(i => i.CenterY)));

        double medianW = Median(boxes.Select(b => b.W));
        var narrow = boxes.Where(b => b.W <= Math.Max(medianW * 2.2, 48.0)).OrderBy(b => b.CenterX).ToList();
        if (narrow.Count == 0) narrow = boxes.OrderBy(b => b.CenterX).ToList();
        double gapThreshold = Math.Max(24.0, medianW * 0.75);
        var bandEnds = new List<double>();
        double prev = narrow[0].CenterX;
        foreach (var b in narrow.Skip(1))
        {
            if (b.CenterX - prev > gapThreshold) bandEnds.Add((prev + b.CenterX) / 2);
            prev = b.CenterX;
        }
        int columns = bandEnds.Count + 1;
        int ColumnOf(double x) { for (int i = 0; i < bandEnds.Count; i++) if (x < bandEnds[i]) return i; return columns - 1; }

        var html = new StringBuilder("<table style=\"border-collapse:collapse;margin:4px 0\">");
        foreach (var row in rowGroups)
        {
            var byColumn = new string[columns];
            foreach (var box in row.OrderBy(b => b.X))
            {
                int col = ColumnOf(box.CenterX);
                byColumn[col] = byColumn[col] == null ? box.Text : byColumn[col] + " " + box.Text;
            }
            html.Append("<tr>");
            foreach (var value in byColumn)
                html.Append("<td style=\"padding:4px 6px\">").Append(System.Net.WebUtility.HtmlEncode(value ?? string.Empty)).Append("</td>");
            html.Append("</tr>");
        }
        return html.Append("</table>").ToString();
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(v => v).ToList();
        return ordered.Count == 0 ? 0 : ordered[ordered.Count / 2];
    }

    public void Dispose()
    {
    }
}
