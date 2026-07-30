using System.IO;

namespace BeeX.OCR;

internal static class PaddleModelStore
{
    // 检测/识别均用 mobile（截屏印刷体精度与 server 几无差距，识别速度快 10 倍以上，
    // 大截图从 10s+ 降到 1-2s；server_rec 在手写/艺术字上更强但截屏场景用不到）
    private const string DetectionModelName = "PP-OCRv5_mobile_det";
    private const string RecognitionModelName = "PP-OCRv5_mobile_rec";
    private const string FormulaModelName = "PP-FormulaNet_plus-S";
    private const string TableModelName = "SLANet_plus";

    public static PaddleModelPaths GetOcrModelPaths()
    {
        string root = GetModelsRoot();
        return new PaddleModelPaths(
            RequireModelDirectory(root, DetectionModelName),
            RequireModelDirectory(root, RecognitionModelName));
    }

    public static string GetFormulaModelDirectory()
    {
        return RequireModelDirectory(GetModelsRoot(), FormulaModelName);
    }

    public static string GetTableModelDirectory()
    {
        return RequireModelDirectory(GetModelsRoot(), TableModelName);
    }

    private static string GetModelsRoot()
    {
        // 发布布局：exe 同目录 models\
        string releaseRoot = Path.Combine(AppContext.BaseDirectory, "models");
        if (Directory.Exists(releaseRoot))
        {
            return releaseRoot;
        }

        // 开发布局：从 bin 目录向上找项目根下的 models-src\
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 6 && current != null; depth++)
        {
            string devRoot = Path.Combine(current.FullName, "models-src");
            if (Directory.Exists(devRoot))
            {
                return devRoot;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "未找到 PaddleOCR 模型目录。发布版需要 exe 同目录下的 models 文件夹；" +
            "开发环境请先运行 scripts\\download-models.ps1 下载模型到 models-src。");
    }

    private static string RequireModelDirectory(string root, string modelName)
    {
        string directory = Path.Combine(root, modelName);
        if (!Directory.Exists(directory) ||
            (!File.Exists(Path.Combine(directory, "inference.pdiparams")) &&
             !File.Exists(Path.Combine(directory, "inference.json"))))
        {
            throw new InvalidOperationException("PaddleOCR 模型缺失或不完整：" + directory);
        }

        return directory;
    }
}
