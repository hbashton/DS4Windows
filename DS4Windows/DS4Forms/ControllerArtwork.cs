using System;
using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DS4WinWPF.DS4Forms
{
    internal static class ControllerArtwork
    {
        private static readonly ConcurrentDictionary<string, ImageSource> Resources = new();

        internal static ImageSource LoadResource(string fileName) =>
            fileName == null ? null : Resources.GetOrAdd(fileName, Create);

        private static ImageSource Create(string fileName)
        {
            // Without the application pack scheme, a getter invoked directly
            // (outside XAML's base-URI context) resolves this as C:\DS4Windows;...
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(
                $"pack://application:,,,/DS4Windows;component/Resources/{fileName}",
                UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
    }
}
