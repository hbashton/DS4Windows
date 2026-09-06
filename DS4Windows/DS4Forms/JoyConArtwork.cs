using System;
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

    internal readonly record struct JoyConMapTarget(DS4Controls Control, Rect Bounds, Geometry Highlight);

    // Original vector artwork. Shared by the controller list and mapping view;
    // frozen drawings remain crisp at both icon size and desktop scaling.
    public static class JoyConArtwork
    {
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

        // Initialize after the canonical button bounds: paint and hit masks use
        // the same geometry, including square Capture and the sideways rails.
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

        internal static Geometry ButtonShape(string name)
        {
            Rect bounds = name == "C" ? new Rect(264, 190, 12, 12) : Buttons[name];
            Geometry shape = name switch
            {
                "L" or "R" or "ZL" or "ZR" => new RectangleGeometry(bounds, 5, 5),
                "Capture" => new RectangleGeometry(bounds, 2, 2),
                "Minus" or "Plus" => new RectangleGeometry(bounds, 3, 3),
                _ => new EllipseGeometry(bounds),
            };
            shape.Freeze();
            return shape;
        }

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
            void Add(DS4Controls control, Geometry shape)
            {
                var transformed = new GeometryGroup { Transform = transform };
                transformed.Children.Add(shape);
                transformed.Freeze();
                // Preserve the canonical double-precision layout. WPF's path
                // bounds may round to float when it flattens a rotated shape.
                result.Add(new(control, transform.TransformBounds(shape.Bounds), transformed));
            }
            void Button(DS4Controls control, string name) => Add(control, ButtonShape(name));
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
                Button(DS4Controls.Switch2C, "C");
            }
            if (horizontal)
            {
                bool left = view == JoyConView.SidewaysLeft;
                Add(DS4Controls.L1, new RectangleGeometry(RailBounds(left, true), 3, 3));
                Add(DS4Controls.R1, new RectangleGeometry(RailBounds(left, false), 3, 3));
            }
            return result.AsReadOnly();
        }

        private static DrawingImage CreateSingle(JoyConView view, bool diagram)
        {
            bool left = view is JoyConView.UprightLeft or JoyConView.SidewaysLeft;
            var drawing = new DrawingGroup();
            using (DrawingContext dc = drawing.Open())
            {
                var transform = new MatrixTransform(ViewTransform(view));
                dc.PushTransform(transform);
                dc.DrawDrawing(Create(left, !left, diagram, frame: false).Drawing);
                if (IsSideways(view))
                {
                    dc.DrawRoundedRectangle(Gradient(0x41464B, 0x111417), new Pen(Brush(0x697076), .7),
                        new Rect(left ? 206 : 215, 46, 19, 145), 5, 5);
                    dc.DrawRoundedRectangle(Brush(left ? 0x66C9F0u : 0xF57768u), null,
                        new Rect(left ? 208 : 229, 51, 3, 135), 1.5, 1.5);
                    foreach (bool sl in new[] { true, false })
                        DrawKey(dc, new RectangleGeometry(RailBounds(left, sl), 3, 3));
                }
                dc.Pop();
                if (IsSideways(view))
                {
                    foreach (bool sl in new[] { true, false })
                    {
                        Rect bounds = transform.TransformBounds(RailBounds(left, sl));
                        var label = new FormattedText(sl ? "SL" : "SR", CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.WhiteSmoke, 1);
                        dc.DrawText(label, new Point(bounds.X + (bounds.Width - label.Width) / 2,
                            bounds.Y + (bounds.Height - label.Height) / 2));
                    }
                }
            }
            return Finish(drawing, diagram);
        }

        private static DrawingImage Create(bool left, bool right, bool diagram, bool frame = true)
        {
            var drawing = new DrawingGroup();
            var ink = Brush(0xE2E5E8);
            using (DrawingContext dc = drawing.Open())
            {
                void Half(double x, bool isLeft)
                {
                    var accent = Brush(isLeft ? 0x66C9F0u : 0xF57768u);
                    // Keep the face orthographic for accurate mapping; layered
                    // shell contours and material gradients supply depth.
                    var shell = Geometry.Parse("M30,28 L67,28 Q74,28 74,37 L74,196 Q74,205 65,205 " +
                        "L29,205 C9,205 0,192 0,172 L0,61 C0,41 11,28 30,28 Z").Clone();
                    shell.Transform = new MatrixTransform(isLeft
                        ? new Matrix(1, 0, 0, 1, x, 0) : new Matrix(-1, 0, 0, 1, x + 76, 0));
                    // The rear trigger and its housing belong to the complete
                    // silhouette in icons too, not only to the mapping diagram.
                    dc.DrawRoundedRectangle(Gradient(0x363B41, 0x101318), new Pen(Brush(0x161A1F), .8),
                        new Rect(x + 15, 12, 46, 24), 7, 7);
                    dc.PushTransform(new TranslateTransform(1.5, 4));
                    dc.DrawGeometry(Gradient(0x393D42, 0x0B0D10), new Pen(Brush(0x090B0E), 2), shell);
                    dc.Pop();
                    dc.DrawRoundedRectangle(Gradient(isLeft ? 0x89DEF8u : 0xFFA292u,
                            isLeft ? 0x2888B7u : 0xB63F38u), new Pen(Brush(0x1B2026), .8),
                        new Rect(isLeft ? x + 71 : x - 3, 40, 8, 161), 4, 4);
                    var face = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0), EndPoint = new Point(1, .45),
                        GradientStops = new GradientStopCollection
                        {
                            new(ColorOf(0x60656B), 0), new(ColorOf(0x383C41), .12),
                            new(ColorOf(0x2B2E33), .62), new(ColorOf(0x1D2025), 1),
                        },
                    };
                    dc.DrawGeometry(face, new Pen(Gradient(0x93989D, 0x202329), .9), shell);
                    dc.PushTransform(new TranslateTransform(isLeft ? x : x + 76, 0));
                    if (!isLeft) dc.PushTransform(new ScaleTransform(-1, 1));
                    dc.DrawGeometry(null, new Pen(Gradient(0xA0A5AA, 0x30343A), .65),
                        Geometry.Parse("M29,31 C11,32 3,44 3,63 L3,172 C3,192 13,201 29,202"));
                    if (!isLeft) dc.Pop();
                    dc.Pop();
                    DrawKey(dc, ButtonShape(isLeft ? "L" : "R"));
                    DrawKey(dc, ButtonShape(isLeft ? "ZL" : "ZR"));
                    double stickY = isLeft ? 76 : 140;
                    DrawStick(dc, new Point(x + 38, stickY), accent);
                    double faceY = isLeft ? 141 : 84;
                    foreach (string name in isLeft ? new[] { "Up", "Right", "Down", "Left" } : new[] { "X", "A", "B", "Y" })
                        DrawKey(dc, ButtonShape(name));
                    DrawKey(dc, ButtonShape(isLeft ? "Minus" : "Plus"));
                    double smallX = isLeft ? x + 60.5 : x + 15.5;
                    dc.DrawLine(new Pen(ink, 1.2), new Point(smallX - 3.5, 49.5), new Point(smallX + 3.5, 49.5));
                    if (!isLeft) dc.DrawLine(new Pen(ink, 1.2), new Point(smallX, 46), new Point(smallX, 53));
                    DrawKey(dc, ButtonShape(isLeft ? "Capture" : "Home"));
                    if (isLeft)
                        dc.DrawEllipse(null, new Pen(ink, .8), new Point(x + 38, 188), 3.5, 3.5);
                    else
                    {
                        dc.PushTransform(new TranslateTransform(x + 38, 176));
                        dc.DrawGeometry(null, new Pen(ink, .8), Geometry.Parse("M-4,0 L0,-3.5 4,0 M-2.5,-1 L-2.5,3 2.5,3 2.5,-1"));
                        dc.Pop();
                        DrawKey(dc, ButtonShape("C"));
                    }
                    void Label(string text, double cx, double cy, double size = 9)
                    {
                        var formatted = new FormattedText(text, CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, ink, 1);
                        dc.DrawText(formatted, new Point(cx - formatted.Width / 2, cy - formatted.Height / 2));
                    }
                    Label(isLeft ? "L" : "R", x + 38, 30.5, 8);
                    Label(isLeft ? "ZL" : "ZR", x + 38, 13.5, 8);
                    var labels = isLeft ? new[] { "▴", "▸", "▾", "◂" } : new[] { "X", "A", "B", "Y" };
                    Label(labels[0], x + 38, faceY - 19);
                    Label(labels[1], x + 57, faceY);
                    Label(labels[2], x + 38, faceY + 19);
                    Label(labels[3], x + 19, faceY);
                    if (!isLeft) Label("C", x + 38, 196, 8);
                }
                if (left) Half(132, true);
                if (right) Half(232, false);
            }
            return Finish(drawing, diagram, frame);
        }

        private static Color ColorOf(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        private static SolidColorBrush Brush(uint rgb) => new(ColorOf(rgb));
        private static LinearGradientBrush Gradient(uint top, uint bottom) => new(ColorOf(top), ColorOf(bottom), 90);

        private static void DrawKey(DrawingContext dc, Geometry shape)
        {
            dc.PushTransform(new TranslateTransform(0, 1.4));
            dc.DrawGeometry(Brush(0x090B0D), new Pen(Brush(0x101215), 1.2), shape);
            dc.Pop();
            dc.DrawGeometry(Gradient(0x53585E, 0x22252A), new Pen(Gradient(0x7D848B, 0x121519), .7), shape);
        }

        private static void DrawStick(DrawingContext dc, Point center, Brush accent)
        {
            dc.DrawEllipse(Brush(0x121519), new Pen(Brush(0x545B62), .6), center, 21, 21);
            dc.DrawEllipse(Brush(0x101317), new Pen(accent, 2), center, 18.7, 18.7);
            dc.DrawEllipse(Brush(0x080A0D), null, center + new Vector(0, 2), 17.5, 17.5);
            dc.DrawEllipse(Gradient(0x777E85, 0x16191E), new Pen(Brush(0x111419), .8), center, 17, 17);
            var cap = new RadialGradientBrush(ColorOf(0x575D64), ColorOf(0x25292E))
            {
                GradientOrigin = new Point(.3, .22), Center = new Point(.45, .4), RadiusX = .7, RadiusY = .7,
            };
            dc.DrawEllipse(cap, new Pen(Gradient(0x171B20, 0x757C83), .7), center, 12.5, 12.5);
            for (int i = 0; i < 24; i++)
            {
                double angle = i * Math.PI / 12;
                var radial = new Vector(Math.Cos(angle), Math.Sin(angle));
                dc.DrawLine(new Pen(Brush(0x343A40), .55), center + radial * 14.3, center + radial * 15.6);
            }
        }

        private static DrawingImage Finish(DrawingGroup content, bool diagram, bool frame = true)
        {
            DrawingGroup drawing = content;
            if (frame)
            {
                drawing = new DrawingGroup();
                using var dc = drawing.Open();
                if (diagram)
                    // Mapping coordinates stay absolute. Never auto-crop this canvas.
                    dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 440, 220));
                else
                {
                    // Icons have a zero-origin, padded viewport, including all
                    // bevels and shadows. No edge sits on the Image's clip boundary.
                    Rect bounds = content.Bounds;
                    bounds.Inflate(8, 8);
                    dc.DrawRectangle(Brushes.Transparent, null, new Rect(new Point(), bounds.Size));
                    dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
                }
                dc.DrawDrawing(content);
                if (!diagram) dc.Pop();
            }
            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
    }
}
