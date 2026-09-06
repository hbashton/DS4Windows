using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DS4Windows.InputDevices;

namespace DS4WinWPF.DS4Forms
{
    // Original vector artwork. Shared by the controller list and mapping view;
    // frozen drawings remain crisp at both icon size and desktop scaling.
    public static class JoyConArtwork
    {
        public static DrawingImage Left { get; } = Create(true, false, false);
        public static DrawingImage Right { get; } = Create(false, true, false);
        public static DrawingImage Pair { get; } = Create(true, true, false);
        internal static DrawingImage Diagram { get; } = Create(true, true, true);

        internal static DrawingImage ForDevice(InputDeviceType type) => type switch
        {
            InputDeviceType.JoyConL or InputDeviceType.Switch2JoyConLeft => Left,
            InputDeviceType.JoyConR or InputDeviceType.Switch2JoyConRight => Right,
            InputDeviceType.JoyConGrip or InputDeviceType.Switch2JoyConJoined => Pair,
            _ => null,
        };

        // Coordinates are shared with the editor's hit targets, not estimated
        // independently from a raster image. This is the upright front view.
        internal static readonly Rect LeftStick = new Rect(151, 57, 38, 38);
        internal static readonly Rect RightStick = new Rect(251, 121, 38, 38);
        internal static IReadOnlyDictionary<string, Rect> Buttons { get; } =
            new Dictionary<string, Rect>
            {
                ["L"] = new Rect(136, 24, 68, 13),
                ["ZL"] = new Rect(149, 7, 42, 13),
                ["R"] = new Rect(236, 24, 68, 13),
                ["ZR"] = new Rect(249, 7, 42, 13),
                ["Minus"] = new Rect(186, 43, 13, 13),
                ["Plus"] = new Rect(241, 43, 13, 13),
                ["Up"] = new Rect(162, 114, 16, 16),
                ["Right"] = new Rect(181, 133, 16, 16),
                ["Down"] = new Rect(162, 152, 16, 16),
                ["Left"] = new Rect(143, 133, 16, 16),
                ["X"] = new Rect(262, 57, 16, 16),
                ["A"] = new Rect(281, 76, 16, 16),
                ["B"] = new Rect(262, 95, 16, 16),
                ["Y"] = new Rect(243, 76, 16, 16),
                ["Capture"] = new Rect(163, 181, 14, 14),
                ["Home"] = new Rect(263, 169, 14, 14),
            };

        private static DrawingImage Create(bool left, bool right, bool diagram)
        {
            var drawing = new DrawingGroup();
            var shell = new SolidColorBrush(Color.FromRgb(45, 49, 56));
            var edge = new Pen(new SolidColorBrush(Color.FromRgb(133, 145, 158)), 1.2);
            var key = new SolidColorBrush(Color.FromRgb(24, 28, 34));
            var ink = new SolidColorBrush(Color.FromRgb(235, 240, 247));
            using (DrawingContext dc = drawing.Open())
            {
                if (diagram) dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 440, 220));
                void Half(double x, bool isLeft)
                {
                    var accent = new SolidColorBrush(isLeft
                        ? Color.FromRgb(84, 187, 222) : Color.FromRgb(240, 137, 105));
                    dc.DrawRoundedRectangle(shell, edge, new Rect(x, 28, 76, 181), 26, 26);
                    dc.DrawRoundedRectangle(accent, null, new Rect(isLeft ? x + 69 : x + 3, 49, 4, 133), 2, 2);
                    dc.DrawRoundedRectangle(key, edge, new Rect(x + 4, 24, 68, 13), 6, 6);
                    if (diagram) dc.DrawRoundedRectangle(key, edge, new Rect(x + 17, 7, 42, 13), 5, 5);
                    double stickY = isLeft ? 76 : 140;
                    dc.DrawEllipse(accent, null, new Point(x + 38, stickY), 20, 20);
                    dc.DrawEllipse(key, edge, new Point(x + 38, stickY), 17, 17);
                    dc.DrawEllipse(null, new Pen(shell, 2), new Point(x + 38, stickY), 12, 12);
                    double faceY = isLeft ? 141 : 84;
                    foreach (var offset in new[] { new Vector(0, -19), new Vector(19, 0), new Vector(0, 19), new Vector(-19, 0) })
                        dc.DrawEllipse(key, edge, new Point(x + 38 + offset.X, faceY + offset.Y), 8, 8);
                    double smallX = isLeft ? x + 60.5 : x + 15.5;
                    dc.DrawLine(new Pen(ink, 1.7), new Point(smallX - 4, 49.5), new Point(smallX + 4, 49.5));
                    if (!isLeft) dc.DrawLine(new Pen(ink, 1.7), new Point(smallX, 45.5), new Point(smallX, 53.5));
                    dc.DrawRoundedRectangle(key, edge, new Rect(x + 31, isLeft ? 181 : 169, 14, 14), isLeft ? 2 : 7, isLeft ? 2 : 7);
                    if (!isLeft) dc.DrawEllipse(key, edge, new Point(x + 38, 196), 6, 6);
                    if (!diagram) return;
                    void Label(string text, double cx, double cy, double size = 9)
                    {
                        var formatted = new FormattedText(text, CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, ink, 1);
                        dc.DrawText(formatted, new Point(cx - formatted.Width / 2, cy - formatted.Height / 2));
                    }
                    Label(isLeft ? "L" : "R", x + 38, 30.5, 8);
                    Label(isLeft ? "ZL" : "ZR", x + 38, 13.5, 8);
                    var labels = isLeft ? new[] { "↑", "→", "↓", "←" } : new[] { "X", "A", "B", "Y" };
                    Label(labels[0], x + 38, faceY - 19);
                    Label(labels[1], x + 57, faceY);
                    Label(labels[2], x + 38, faceY + 19);
                    Label(labels[3], x + 19, faceY);
                    if (!isLeft) Label("C", x + 38, 196, 8);
                }
                if (left) Half(132, true);
                if (right) Half(232, false);
            }
            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
    }
}
