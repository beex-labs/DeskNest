using System.IO;

namespace BeeX.OCR;

internal static class PaddleModelStore
{
    // Both detection and recognition use mobile (for printed screenshot text the accuracy is nearly identical to server, but 10x+ faster;
    // a large screenshot drops from 10s+ to 1-2s; server_rec is stronger on handwriting/artistic fonts but that is not needed for screenshots)
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
        // Release layout: models\ in the same directory as the exe
        string releaseRoot = Path.Combine(AppContext.BaseDirectory, "models");
        if (Directory.Exists(releaseRoot))
        {
            return releaseRoot;
        }

        // Dev layout: search upward from the bin directory for models-src\ under the project root
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
