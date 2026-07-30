namespace BeeX.OCR;

internal readonly record struct OcrImageStats(double AverageLuminance, int LowLuminance, int HighLuminance)
{
    public bool LooksDark => AverageLuminance < 112 || HighLuminance < 150;

    public bool LowContrast => HighLuminance - LowLuminance < 48;
}
