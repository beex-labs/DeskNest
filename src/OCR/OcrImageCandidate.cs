using System.Drawing;

namespace BeeX.OCR;

internal sealed class OcrImageCandidate : IDisposable
{
    public OcrImageCandidate(string name, Bitmap bitmap)
    {
        Name = name;
        Bitmap = bitmap;
    }

    public string Name { get; }

    public Bitmap Bitmap { get; }

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}
