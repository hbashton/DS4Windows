using System.Windows;
using System.Windows.Media;

namespace DS4WinWPF.DS4Forms
{
    // Original, code-native illustrations: scalable, theme-independent, and
    // shared once per process. The adjacent text supplies accessible meaning.
    public static class Switch2FeatureArtwork
    {
        public static DrawingImage DeskMouse { get; } = Create(0);
        public static DrawingImage Aim { get; } = Create(1);
        public static DrawingImage Pair { get; } = Create(2);
        public static DrawingImage Rumble { get; } = Create(3);
        public static DrawingImage Layers { get; } = Create(4);
        public static DrawingImage Sticks { get; } = Create(5);
        public static DrawingImage Layout { get; } = Create(6);
        public static DrawingImage Calibration { get; } = Create(7);
        public static DrawingImage Headset { get; } = Create(8);

        private static SolidColorBrush Brush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private static DrawingImage Create(int kind)
        {
            var cyan = Brush("#70DBEC");
            var coral = Brush("#FF998A");
            var ink = Brush("#E1ECF5");
            var dim = Brush("#526B80");
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                dc.DrawRoundedRectangle(Brush("#101F2E"), null, new Rect(0, 0, 160, 96), 10, 10);
                void Line(Brush b, double x, double y, double x2, double y2) =>
                    dc.DrawLine(new Pen(b, 2), new Point(x, y), new Point(x2, y2));
                void Path(Brush fill, Brush stroke, string data) =>
                    dc.DrawGeometry(fill, stroke == null ? null : new Pen(stroke, 2), Geometry.Parse(data));
                void Arrow(Brush b, double x, double y, double x2, double y2)
                {
                    Line(b, x, y, x2, y2);
                    var v = new Vector(x - x2, y - y2); v.Normalize();
                    var n = new Vector(-v.Y, v.X);
                    dc.DrawLine(new Pen(b, 2), new Point(x2, y2), new Point(x2, y2) + v * 6 + n * 4);
                    dc.DrawLine(new Pen(b, 2), new Point(x2, y2), new Point(x2, y2) + v * 6 - n * 4);
                }
                void Target(double x, double y)
                {
                    dc.DrawEllipse(null, new Pen(cyan, 2), new Point(x, y), 14, 14);
                    Line(cyan, x - 21, y, x - 8, y); Line(cyan, x + 8, y, x + 21, y);
                    Line(cyan, x, y - 21, x, y - 8); Line(cyan, x, y + 8, x, y + 21);
                    dc.DrawEllipse(ink, null, new Point(x, y), 2, 2);
                }
                void Pad()
                {
                    Path(Brush("#233C51"), ink, "M 46,37 Q 40,35 36,43 L 27,69 Q 25,80 35,79 L 51,66 L 81,66 L 95,79 Q 105,82 103,70 L 95,43 Q 93,37 87,37 Z");
                    dc.DrawEllipse(null, new Pen(cyan, 2), new Point(51, 49), 7, 7);
                    dc.DrawEllipse(null, new Pen(dim, 2), new Point(73, 61), 5, 5);
                    Line(ink, 44,64,54,64); Line(ink,49,59,49,69);
                    foreach (var p in new[] { new Point(87,45), new Point(92,50), new Point(87,55), new Point(82,50) })
                        dc.DrawEllipse(coral, null, p, 2, 2);
                }
                switch (kind)
                {
                    case 0:
                        Path(Brush("#172C3E"), dim, "M 19,67 L 112,67 L 136,84 L 5,84 Z");
                        dc.DrawRoundedRectangle(Brush("#264557"), new Pen(cyan, 2), new Rect(47,36,27,37), 9, 9);
                        dc.DrawEllipse(null, new Pen(ink, 2), new Point(60,47), 5, 5);
                        Line(coral, 54,73,65,73);
                        Arrow(cyan, 39,55,19,55); Arrow(cyan, 81,55,101,55);
                        Path(ink, null, "M 121,22 L 121,49 L 128,42 L 135,54 L 141,51 L 134,40 L 145,40 Z");
                        break;
                    case 1:
                        Pad(); Target(124,30);
                        Path(null, coral, "M 22,36 Q 31,14 60,19");
                        Arrow(coral, 49,17,61,19);
                        break;
                    case 2:
                        dc.DrawImage(JoyConArtwork.Left, new Rect(19,25,26,57));
                        dc.DrawImage(JoyConArtwork.Right, new Rect(115,25,26,57));
                        Target(80,30);
                        Arrow(cyan, 47,65,65,49); Arrow(coral, 113,65,95,49);
                        break;
                    case 3:
                        Pad();
                        Path(null, cyan, "M 21,41 Q 10,56 21,73 M 13,35 Q -1,56 13,79");
                        Path(null, coral, "M 111,41 Q 122,56 111,73 M 120,35 Q 136,56 120,79");
                        Line(cyan, 43,27,43,19); Line(coral, 87,27,87,19);
                        break;
                    case 4:
                        dc.DrawRoundedRectangle(Brush("#253D51"), new Pen(dim,2), new Rect(27,23,58,41), 6,6);
                        dc.DrawRoundedRectangle(Brush("#203245"), new Pen(cyan,2), new Rect(46,36,58,41), 6,6);
                        dc.DrawEllipse(coral, null, new Point(75,56), 9,9);
                        Arrow(cyan, 112,58,137,58);
                        Line(ink, 121,24,137,24); Line(ink, 121,31,132,31);
                        break;
                    case 5:
                        dc.DrawEllipse(null, new Pen(dim,2), new Point(48,66), 23,10);
                        Line(ink, 48,40,48,65);
                        dc.DrawEllipse(Brush("#29495F"), new Pen(cyan,2), new Point(48,34), 17,10);
                        Arrow(cyan, 73,34,94,34);
                        Path(ink, null, "M 110,23 L 110,48 L 117,41 L 124,51 L 129,48 L 122,38 L 133,38 Z");
                        Arrow(coral, 118,64,118,83); Arrow(coral, 118,81,118,61);
                        break;
                    case 6:
                        Pad();
                        dc.DrawRoundedRectangle(null, new Pen(cyan,2), new Rect(116,28,26,15), 3,3);
                        dc.DrawRectangle(cyan,null,new Rect(120,32,15,7));
                        Line(ink, 144,32,144,39);
                        break;
                    case 8:
                        Path(null, ink, "M 35,60 L 35,42 C 35,12 97,12 97,42 L 97,60");
                        dc.DrawRoundedRectangle(Brush("#233C51"), new Pen(cyan, 2), new Rect(27,43,18,28), 6,6);
                        dc.DrawRoundedRectangle(Brush("#233C51"), new Pen(coral, 2), new Rect(87,43,18,28), 6,6);
                        Path(null, coral, "M 97,70 Q 96,81 75,81");
                        dc.DrawRoundedRectangle(ink, null, new Rect(64,77,15,7), 3,3);
                        Line(cyan, 126,76,126,39);
                        dc.DrawRoundedRectangle(Brush("#233C51"), new Pen(ink, 2), new Rect(119,23,14,20), 4,4);
                        Line(ink, 123,20,129,20);
                        break;
                    default:
                        dc.DrawEllipse(null,new Pen(dim,2),new Point(52,51),27,27);
                        dc.DrawEllipse(null,new Pen(cyan,2),new Point(52,51),12,12);
                        Line(ink,18,51,86,51); Line(ink,52,17,52,85);
                        Path(null,coral,"M 103,51 L 114,63 L 142,32");
                        break;
                }
            }
            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
    }
}
