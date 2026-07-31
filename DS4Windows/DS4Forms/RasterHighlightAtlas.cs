using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DS4WinWPF.DS4Forms
{
    /// <summary>
    /// Loads controller highlight frames and turns their alpha channel into
    /// the hit-test geometry. Painting and pointer activation therefore use
    /// the same raster silhouette instead of two independently maintained
    /// approximations.
    /// </summary>
    internal static class RasterHighlightAtlas
    {
        internal const int FrameWidth = 440;
        internal const int FrameHeight = 220;

        private static readonly object cacheLock = new object();
        private static readonly Dictionary<string, BitmapSource> atlasCache =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<(string Resource, int Frame, int Width, int Height), BitmapSource>
            frameCache = new Dictionary<(string Resource, int Frame, int Width, int Height), BitmapSource>();
        private static readonly Dictionary<(string Resource, int Frame, int Width, int Height), Geometry>
            maskCache = new Dictionary<(string Resource, int Frame, int Width, int Height), Geometry>();

        internal static BitmapSource Frame(string resourceName, int frameIndex,
            int frameWidth = FrameWidth, int frameHeight = FrameHeight)
        {
            lock (cacheLock)
            {
                var key = (resourceName, frameIndex, frameWidth, frameHeight);
                if (frameCache.TryGetValue(key, out BitmapSource cached))
                {
                    return cached;
                }

                BitmapSource atlas = LoadAtlas(resourceName);
                int top = checked(frameIndex * frameHeight);
                if (frameIndex < 0 || atlas.PixelWidth < frameWidth ||
                    top + frameHeight > atlas.PixelHeight)
                {
                    throw new ArgumentOutOfRangeException(nameof(frameIndex),
                        $"Frame {frameIndex} is outside {resourceName}.");
                }

                var frame = new CroppedBitmap(atlas,
                    new Int32Rect(0, top, frameWidth, frameHeight));
                frame.Freeze();
                frameCache[key] = frame;
                return frame;
            }
        }

        internal static Geometry Mask(string resourceName, int frameIndex,
            int frameWidth = FrameWidth, int frameHeight = FrameHeight)
        {
            lock (cacheLock)
            {
                var key = (resourceName, frameIndex, frameWidth, frameHeight);
                if (maskCache.TryGetValue(key, out Geometry cached))
                {
                    return cached;
                }

                Geometry geometry = BuildAlphaMask(Frame(resourceName, frameIndex,
                    frameWidth, frameHeight));
                maskCache[key] = geometry;
                return geometry;
            }
        }

        private static BitmapSource LoadAtlas(string resourceName)
        {
            if (atlasCache.TryGetValue(resourceName, out BitmapSource cached))
            {
                return cached;
            }

            var resourceUri = new Uri(
                $"/DS4Windows;component/Resources/{resourceName}",
                UriKind.Relative);
            System.Windows.Resources.StreamResourceInfo resource =
                Application.GetResourceStream(resourceUri);
            if (resource?.Stream == null)
            {
                throw new InvalidOperationException(
                    $"Controller highlight atlas '{resourceName}' could not be loaded.");
            }

            BitmapSource source;
            using (resource.Stream)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = resource.Stream;
                image.EndInit();
                image.Freeze();
                source = image;
            }

            atlasCache[resourceName] = source;
            return source;
        }

        private static Geometry BuildAlphaMask(BitmapSource source)
        {
            BitmapSource pixels = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32,
                    null, 0);
            int stride = pixels.PixelWidth * 4;
            byte[] buffer = new byte[stride * pixels.PixelHeight];
            pixels.CopyPixels(buffer, stride, 0);

            var geometry = new StreamGeometry
            {
                FillRule = FillRule.Nonzero,
            };
            using (StreamGeometryContext context = geometry.Open())
            {
                for (int y = 0; y < pixels.PixelHeight; y++)
                {
                    int row = y * stride;
                    int x = 0;
                    while (x < pixels.PixelWidth)
                    {
                        while (x < pixels.PixelWidth &&
                            buffer[row + (x * 4) + 3] < 16)
                        {
                            x++;
                        }

                        int start = x;
                        while (x < pixels.PixelWidth &&
                            buffer[row + (x * 4) + 3] >= 16)
                        {
                            x++;
                        }

                        if (start < x)
                        {
                            context.BeginFigure(new Point(start, y), true, true);
                            context.LineTo(new Point(x, y), false, false);
                            context.LineTo(new Point(x, y + 1), false, false);
                            context.LineTo(new Point(start, y + 1), false, false);
                        }
                    }
                }
            }

            geometry.Freeze();
            return geometry;
        }
    }
}
