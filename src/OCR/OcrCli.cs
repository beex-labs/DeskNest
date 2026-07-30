using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BeeX.OCR;

internal static class OcrCli
{
    private const uint AttachParentProcess = 0xffffffff;

    public static bool TryRun(string[] args)
    {
        if (args.Length == 0 || !args.Any(arg => arg.Equals("--ocr-file", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--ocr-load-engine", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--list-ocr-languages", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--clean-text", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--translate-text", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--formula-file", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--serve", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--ocr-pos", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        AttachConsoleForOutput();

        try
        {
            if (args.Any(arg => arg.Equals("--serve", StringComparison.OrdinalIgnoreCase)))
            {
                RunServeLoop(ReadOption(args, "--serve-role"));
                return true;
            }

            using TextWriter? outputFile = CreateOutputFile(args);
            TextWriter output = outputFile ?? Console.Out;

            string? cleanText = ReadOption(args, "--clean-text");
            if (cleanText != null)
            {
                output.WriteLine(OcrTextPostProcessor.Clean(cleanText));
                return true;
            }

            string? translateText = ReadOption(args, "--translate-text");
            if (translateText != null)
            {
                string targetLanguage = ReadOption(args, "--translate-to") ?? "en";
                var translationService = new TranslationService();
                output.WriteLine(translationService.TranslateAsync(translateText, targetLanguage).GetAwaiter().GetResult());
                return true;
            }

            string? formulaPath = ReadOption(args, "--formula-file");
            if (formulaPath != null)
            {
                if (!File.Exists(formulaPath))
                {
                    Console.Error.WriteLine("Formula image file was not found.");
                    return true;
                }

                using Bitmap formulaBitmap = LoadBitmap(formulaPath);
                using var formulaService = new FormulaRecognitionService();
                Stopwatch formulaWatch = Stopwatch.StartNew();
                string latex = formulaService.Recognize(formulaBitmap);
                formulaWatch.Stop();
                output.WriteLine("ElapsedMs=" + formulaWatch.ElapsedMilliseconds);
                output.WriteLine(latex);
                return true;
            }

            if (args.Any(arg => arg.Equals("--list-ocr-languages", StringComparison.OrdinalIgnoreCase)))
            {
                ListLanguages(output);
                return true;
            }

            if (args.Any(arg => arg.Equals("--ocr-load-engine", StringComparison.OrdinalIgnoreCase)))
            {
                string? loadLanguageTag = ReadOption(args, "--ocr-lang");
                var loadService = new OcrService();
                Stopwatch stopwatch = Stopwatch.StartNew();
                loadService.LoadEngineAsync(loadLanguageTag).GetAwaiter().GetResult();
                stopwatch.Stop();
                output.WriteLine("EngineLoadMs=" + stopwatch.ElapsedMilliseconds);
                return true;
            }

            if (args.Any(arg => arg.Equals("--ocr-pos", StringComparison.OrdinalIgnoreCase)))
            {
                string? posPath = ReadOption(args, "--ocr-file");
                if (string.IsNullOrWhiteSpace(posPath) || !File.Exists(posPath))
                {
                    Console.Error.WriteLine("OCR image file was not found.");
                    return true;
                }

                using Bitmap posBitmap = LoadBitmap(posPath);
                using var posService = new OcrService();
                var posBlocks = posService.RecognizeWithPositions(posBitmap);
                var posLines = new List<Dictionary<string, object>>();
                foreach (var (x, y, w, h, text) in posBlocks)
                {
                    posLines.Add(new Dictionary<string, object> { ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h, ["text"] = text });
                }
                var posResult = new Dictionary<string, object> { ["regions"] = posLines };
                output.WriteLine(JsonSerializer.Serialize(posResult, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                return true;
            }

            string? path = ReadOption(args, "--ocr-file");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Console.Error.WriteLine("OCR image file was not found.");
                return true;
            }

            string? languageTag = ReadOption(args, "--ocr-lang");
            using Bitmap bitmap = LoadBitmap(path);
            var service = new OcrService();
            if (args.Any(arg => arg.Equals("--ocr-prewarm", StringComparison.OrdinalIgnoreCase)))
            {
                service.WarmUpAsync(languageTag).GetAwaiter().GetResult();
            }

            int repeat = int.TryParse(ReadOption(args, "--ocr-repeat"), out int parsedRepeat)
                ? Math.Clamp(parsedRepeat, 1, 20)
                : 1;

            OcrRecognitionReport? report = null;
            var elapsed = new List<long>();
            for (int i = 0; i < repeat; i++)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                report = service.RecognizeDetailedAsync(bitmap, languageTag).GetAwaiter().GetResult();
                stopwatch.Stop();
                elapsed.Add(stopwatch.ElapsedMilliseconds);
            }

            if (report != null)
            {
                output.WriteLine("ElapsedMs=" + elapsed[^1]);
                if (elapsed.Count > 1)
                {
                    output.WriteLine("ElapsedRuns=" + string.Join(",", elapsed));
                }

                output.WriteLine("Candidate=" + report.CandidateName);
                output.WriteLine(report.Text);

                if (args.Any(arg => arg.Equals("--ocr-debug", StringComparison.OrdinalIgnoreCase)))
                {
                    output.WriteLine();
                    output.WriteLine("Candidates:");
                    foreach (OcrCandidateInfo candidate in report.Candidates)
                    {
                        output.WriteLine("[" + candidate.CandidateName + "] Score=" + candidate.Score);
                        output.WriteLine(candidate.Text);
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return true;
        }
    }

    /// <summary>
    /// 侧车常驻模式：模型只加载一次，通过 stdin/stdout 行协议服务 DeskNest。
    /// 请求：OCR\t图片路径\t结果文件 或 FORMULA\t图片路径\t结果文件 或 EXIT；
    /// 响应：OK\t耗时ms 或 ERR\t错误信息。启动完成后输出 READY。
    /// role 决定预加载哪个引擎：ocr（默认，MKL 侧车）或 formula（openblas 侧车）。
    /// </summary>
    private static void RunServeLoop(string? role)
    {
        using var ocrService = new OcrService();
        using var formulaService = new FormulaRecognitionService();
        using var tableService = new TableRecognitionService(ocrService);
        // 与 DeskNest 约定双向 UTF-8，避免中文路径在默认代码页下乱码
        using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };

        if (string.Equals(role, "formula", StringComparison.OrdinalIgnoreCase))
        {
            formulaService.WarmUp();
        }
        else
        {
            // WarmUpAsync 会真实跑一张小图，把 Paddle 首次推理的图优化也在 READY 前做完，
            // 用户首次识别不再多等 1~2s
            ocrService.WarmUpAsync(null).GetAwaiter().GetResult();
        }

        output.WriteLine("READY");

        string? line;
        while ((line = input.ReadLine()) != null)
        {
            string[] parts = line.Split('\t');
            string command = parts[0].Trim().ToUpperInvariant();
            if (command.Length == 0)
            {
                continue;
            }

            if (command == "EXIT")
            {
                break;
            }

            try
            {
                if (parts.Length < 3 || (command != "OCR" && command != "FORMULA" && command != "TABLE" && command != "OCRPOS"))
                {
                    output.WriteLine("ERR\t未知指令。");
                    continue;
                }

                string imagePath = parts[1];
                string resultPath = parts[2];
                if (!File.Exists(imagePath))
                {
                    output.WriteLine("ERR\t图片文件不存在。");
                    continue;
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                string text;
                using (Bitmap bitmap = LoadBitmap(imagePath))
                {
                    if (command == "OCRPOS")
                    {
                        var blocks = ocrService.RecognizeWithPositions(bitmap);
                        var lines = new List<Dictionary<string, object>>();
                        foreach (var (bx, by, bw, bh, btext) in blocks)
                        {
                            lines.Add(new Dictionary<string, object> { ["x"] = bx, ["y"] = by, ["w"] = bw, ["h"] = bh, ["text"] = btext });
                        }
                        text = JsonSerializer.Serialize(
                            new Dictionary<string, object> { ["regions"] = lines },
                            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                    }
                    else
                    {
                        text = command switch
                        {
                            "FORMULA" => formulaService.Recognize(bitmap),
                            "TABLE" => tableService.RecognizeHtml(bitmap),
                            _ => ocrService.RecognizeAsync(bitmap, null).GetAwaiter().GetResult()
                        };
                    }
                }

                stopwatch.Stop();
                string? directory = Path.GetDirectoryName(resultPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(resultPath, text);
                output.WriteLine("OK\t" + stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                output.WriteLine("ERR\t" + ex.Message.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '));
            }
        }
    }

    private static void ListLanguages(TextWriter output)
    {
        var service = new OcrService();
        foreach (OcrLanguageOption language in service.GetAvailableLanguages())
        {
            output.WriteLine((language.LanguageTag ?? "auto") + "|" + language.DisplayName);
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static TextWriter? CreateOutputFile(string[] args)
    {
        string? outputPath = ReadOption(args, "--ocr-out");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return null;
        }

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new StreamWriter(outputPath, append: false);
    }

    private static Bitmap LoadBitmap(string path)
    {
        using var source = new Bitmap(path);
        return new Bitmap(source);
    }

    private static void AttachConsoleForOutput()
    {
        AttachConsole(AttachParentProcess);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
