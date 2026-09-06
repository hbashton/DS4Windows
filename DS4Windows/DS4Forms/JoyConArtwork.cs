using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DS4Windows.InputDevices;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WinWPF.DS4Forms
{
    internal enum JoyConView { Pair, UprightLeft, UprightRight, SidewaysLeft, SidewaysRight }

    internal readonly record struct JoyConMapTarget(DS4Controls Control, Rect Bounds);

    // Original vector artwork. Shared by the controller list and mapping view;
    // frozen drawings remain crisp at both icon size and desktop scaling.
    public static class JoyConArtwork
    {
        public static DrawingImage Left { get; } = Create(true, false, false);
        public static DrawingImage Right { get; } = Create(false, true, false);
        public static DrawingImage Pair { get; } = Create(true, true, false);
        internal static DrawingImage Diagram { get; } = Create(true, true, true);
        public static DrawingImage SidewaysLeft { get; } = CreateSingle(JoyConView.SidewaysLeft, false);
        public static DrawingImage SidewaysRight { get; } = CreateSingle(JoyConView.SidewaysRight, false);
        public static DrawingImage UprightLeftDiagram { get; } = CreateSingle(JoyConView.UprightLeft, true);
        public static DrawingImage UprightRightDiagram { get; } = CreateSingle(JoyConView.UprightRight, true);
        public static DrawingImage SidewaysLeftDiagram { get; } = CreateSingle(JoyConView.SidewaysLeft, true);
        public static DrawingImage SidewaysRightDiagram { get; } = CreateSingle(JoyConView.SidewaysRight, true);

        internal static JoyConView ResolveView(InputDeviceType type, Switch2JoyConHoldMode hold) => type switch
        {
            InputDeviceType.Switch2JoyConLeft or InputDeviceType.JoyConL =>
                hold == Switch2JoyConHoldMode.Horizontal ? JoyConView.SidewaysLeft : JoyConView.UprightLeft,
            InputDeviceType.Switch2JoyConRight or InputDeviceType.JoyConR =>
                hold == Switch2JoyConHoldMode.Horizontal ? JoyConView.SidewaysRight : JoyConView.UprightRight,
            _ => JoyConView.Pair,
        };

        internal static DrawingImage ForView(JoyConView view) => view switch
        {
            JoyConView.UprightLeft => UprightLeftDiagram,
            JoyConView.UprightRight => UprightRightDiagram,
            JoyConView.SidewaysLeft => SidewaysLeftDiagram,
            JoyConView.SidewaysRight => SidewaysRightDiagram,
            _ => Diagram,
        };

        internal static DrawingImage ForDevice(InputDeviceType type, Switch2JoyConHoldMode hold)
        {
            // A joined pair never becomes a sideways single, even if its profile
            // retains a standalone fallback from an earlier session.
            if (hold == Switch2JoyConHoldMode.Horizontal)
            {
                if (type == InputDeviceType.Switch2JoyConLeft) return SidewaysLeft;
                if (type == InputDeviceType.Switch2JoyConRight) return SidewaysRight;
            }
            return ForDevice(type);
        }

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

        internal static Matrix ViewTransform(JoyConView view) => view switch
        {
            JoyConView.UprightLeft => new Matrix(1, 0, 0, 1, 50, 0),
            JoyConView.UprightRight => new Matrix(1, 0, 0, 1, -50, 0),
            // Opposite rotations put each half's stick on the left and rail up.
            JoyConView.SidewaysLeft => new Matrix(0, -1.4, 1.4, 0, 68.8, 348),
            JoyConView.SidewaysRight => new Matrix(0, 1.4, -1.4, 0, 371.2, -268),
            _ => Matrix.Identity,
        };

        internal static bool IsSideways(JoyConView view) =>
            view is JoyConView.SidewaysLeft or JoyConView.SidewaysRight;

        private static Rect RailBounds(bool left, bool sl) =>
            new Rect(left ? 212 : 216, left == sl ? 65 : 146, 12, 23);

        internal static Rect StickBounds(JoyConView view, bool left) =>
            new MatrixTransform(ViewTransform(view)).TransformBounds(left ? LeftStick : RightStick);

        internal static IReadOnlyList<JoyConMapTarget> Targets(JoyConView view, Switch2FaceButtonLayout layout)
        {
            var result = new List<JoyConMapTarget>();
            var transform = new MatrixTransform(ViewTransform(view));
            bool horizontal = IsSideways(view);
            void Add(DS4Controls control, Rect bounds) => result.Add(new(control, transform.TransformBounds(bounds)));
            void Button(DS4Controls control, string name) => Add(control, Buttons[name]);
            DS4Controls Face(bool west = false, bool north = false, bool south = false, bool east = false)
            {
                Switch2FaceButtonLayoutProjection.TryProject(layout, west, north, south, east,
                    out bool square, out bool triangle, out bool cross, out _);
                return square ? DS4Controls.Square : triangle ? DS4Controls.Triangle :
                    cross ? DS4Controls.Cross : DS4Controls.Circle;
            }
            if (view is JoyConView.Pair or JoyConView.UprightLeft or JoyConView.SidewaysLeft)
            {
                Button(horizontal ? DS4Controls.Options : DS4Controls.Share, "Minus");
                Button(horizontal ? DS4Controls.PS : DS4Controls.Capture, "Capture");
                Button(horizontal ? DS4Controls.Switch2JoyConLeftPaddle1 : DS4Controls.L1, "L");
                Button(horizontal ? DS4Controls.Switch2JoyConLeftPaddle2 : DS4Controls.L2, "ZL");
                // These follow MapMiniLeftButtons' pinned raw-button identities.
                Button(horizontal ? Face(north: true) : DS4Controls.DpadUp, "Up");
                Button(horizontal ? Face(south: true) : DS4Controls.DpadRight, "Right");
                Button(horizontal ? Face(west: true) : DS4Controls.DpadDown, "Down");
                Button(horizontal ? Face(east: true) : DS4Controls.DpadLeft, "Left");
            }
            if (view is JoyConView.Pair or JoyConView.UprightRight or JoyConView.SidewaysRight)
            {
                Button(DS4Controls.Options, "Plus");
                Button(DS4Controls.PS, "Home");
                Button(horizontal ? DS4Controls.Switch2JoyConRightPaddle1 : DS4Controls.R1, "R");
                Button(horizontal ? DS4Controls.Switch2JoyConRightPaddle2 : DS4Controls.R2, "ZR");
                Button(Face(north: true), "X"); Button(Face(east: true), "A");
                Button(Face(south: true), "B"); Button(Face(west: true), "Y");
                Add(DS4Controls.Switch2C, new Rect(264, 190, 12, 12));
            }
            if (horizontal)
            {
                bool left = view == JoyConView.SidewaysLeft;
                Add(DS4Controls.L1, RailBounds(left, true));
                Add(DS4Controls.R1, RailBounds(left, false));
            }
            return result.AsReadOnly();
        }

        private static DrawingImage CreateSingle(JoyConView view, bool diagram)
        {
            bool left = view is JoyConView.UprightLeft or JoyConView.SidewaysLeft;
            var drawing = new DrawingGroup();
            using (DrawingContext dc = drawing.Open())
            {
                if (diagram) dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 440, 220));
                var transform = new MatrixTransform(ViewTransform(view));
                dc.PushTransform(transform);
                dc.DrawDrawing(Create(left, !left, diagram, frame: false).Drawing);
                dc.Pop();
                if (IsSideways(view))
                {
                    foreach (bool sl in new[] { true, false })
                    {
                        Rect bounds = transform.TransformBounds(RailBounds(left, sl));
                        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(45, 49, 56)),
                            new Pen(Brushes.SlateGray, 1), bounds, 4, 4);
                        if (!diagram) continue;
                        var label = new FormattedText(sl ? "SL" : "SR", CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.WhiteSmoke, 1);
                        dc.DrawText(label, new Point(bounds.X + (bounds.Width - label.Width) / 2,
                            bounds.Y + (bounds.Height - label.Height) / 2));
                    }
                }
            }
            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        private static DrawingImage Create(bool left, bool right, bool diagram, bool frame = true)
        {
            var drawing = new DrawingGroup();
            var shell = new SolidColorBrush(Color.FromRgb(45, 49, 56));
            var edge = new Pen(new SolidColorBrush(Color.FromRgb(133, 145, 158)), 1.2);
            var key = new SolidColorBrush(Color.FromRgb(24, 28, 34));
            var ink = new SolidColorBrush(Color.FromRgb(235, 240, 247));
            using (DrawingContext dc = drawing.Open())
            {
                if (diagram && frame) dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 440, 220));
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
