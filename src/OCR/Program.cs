namespace BeeX.OCR;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (OcrCli.TryRun(args))
        {
            return;
        }

        var app = new System.Windows.Application();
        app.Run(new MainWindow());
    }
}
