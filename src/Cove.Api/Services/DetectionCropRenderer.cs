using Cove.Core.Entities;
using Cove.Api.Imaging;

namespace Cove.Api.Services;

public static class DetectionCropRenderer
{
    public const double DefaultContext = 1.8;

    public static async Task<MemoryStream?> RenderAsync(
        Detection detection,
        Stream sourceStream,
        int? maxDimension = null,
        double? context = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var image = await ImageCodec.Default.LoadAsync(sourceStream, cancellationToken: cancellationToken);
            image.AutoOrient();

            var cropRectangle = BuildRectangle(image.Width, image.Height, detection, context);
            if (cropRectangle is null)
                return null;

            image.Crop(cropRectangle.Value);
            var maximum = Math.Clamp(maxDimension.GetValueOrDefault(640), 64, 2048);
            if (Math.Max(image.Width, image.Height) > maximum)
            {
                image.Resize(ImageGeometry.FitWithin(new PixelSize(image.Width, image.Height), new PixelSize(maximum, maximum)));
            }

            var output = new MemoryStream();
            await image.SaveAsync(output, ImageOutput.Jpeg(88), cancellationToken);
            output.Position = 0;
            return output;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static PixelRect? BuildRectangle(int imageWidth, int imageHeight, Detection detection, double? context)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || detection.W <= 0 || detection.H <= 0)
            return null;

        var normalized = detection.X >= 0
            && detection.Y >= 0
            && detection.X <= 1.000001f
            && detection.Y <= 1.000001f
            && detection.W <= 1.000001f
            && detection.H <= 1.000001f;
        var x = (double)detection.X;
        var y = (double)detection.Y;
        var width = (double)detection.W;
        var height = (double)detection.H;
        if (normalized)
        {
            x *= imageWidth;
            width *= imageWidth;
            y *= imageHeight;
            height *= imageHeight;
        }
        else if (detection.FrameWidth > 0 && detection.FrameHeight > 0)
        {
            x = x / detection.FrameWidth * imageWidth;
            width = width / detection.FrameWidth * imageWidth;
            y = y / detection.FrameHeight * imageHeight;
            height = height / detection.FrameHeight * imageHeight;
        }

        var left = Clamp((int)Math.Floor(x), 0, imageWidth - 1);
        var top = Clamp((int)Math.Floor(y), 0, imageHeight - 1);
        var right = Clamp((int)Math.Ceiling(x + width), left + 1, imageWidth);
        var bottom = Clamp((int)Math.Ceiling(y + height), top + 1, imageHeight);
        var boxWidth = Math.Max(1, right - left);
        var boxHeight = Math.Max(1, bottom - top);
        var contextScale = Math.Clamp(context ?? DefaultContext, 1.0, 4.0);
        var side = Math.Clamp((int)Math.Ceiling(Math.Max(boxWidth, boxHeight) * contextScale), 1, Math.Min(imageWidth, imageHeight));
        var headroomFactor = Math.Clamp((contextScale - 1.0) / (DefaultContext - 1.0), 0.0, 1.0);
        var centerX = left + boxWidth / 2.0;
        var centerY = top + boxHeight / 2.0 - boxHeight * 0.1 * headroomFactor;
        var cropLeft = Clamp((int)Math.Round(centerX - side / 2.0), 0, Math.Max(0, imageWidth - side));
        var cropTop = Clamp((int)Math.Round(centerY - side / 2.0), 0, Math.Max(0, imageHeight - side));
        return new PixelRect(cropLeft, cropTop, side, side);
    }

    private static int Clamp(int value, int min, int max)
        => value < min ? min : value > max ? max : value;
}
