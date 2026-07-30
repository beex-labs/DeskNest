using System.Drawing;
using System.Windows.Forms;

namespace BeeX.OCR;

internal static class ScreenCaptureService
{
    public static Rectangle VirtualScreenBounds => SystemInformation.VirtualScreen;

    public static Bitmap CaptureRegion(Rectangle screenRegion)
    {
        if (screenRegion.Width < 2 || screenRegion.Height < 2)
        {
            throw new InvalidOperationException("框选区域太小，请重新选择。");
        }

        var bitmap = new Bitmap(screenRegion.Width, screenRegion.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(screenRegion.Left, screenRegion.Top, 0, 0, screenRegion.Size, CopyPixelOperation.SourceCopy);

        return bitmap;
    }

    public static Bitmap CaptureVirtualScreen()
    {
        Rectangle bounds = VirtualScreenBounds;
        var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        return bitmap;
    }

    public static Bitmap Crop(Bitmap source, Rectangle region)
    {
        Rectangle sourceBounds = new(0, 0, source.Width, source.Height);
        Rectangle clipped = Rectangle.Intersect(sourceBounds, region);

        if (clipped.Width < 2 || clipped.Height < 2)
        {
            throw new InvalidOperationException("框选区域太小，请重新选择。");
        }

        return source.Clone(clipped, source.PixelFormat);
    }
}
