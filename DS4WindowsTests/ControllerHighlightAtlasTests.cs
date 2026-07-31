using System.Windows.Media;
using System.Windows.Media.Imaging;
using DS4WinWPF.DS4Forms;

namespace DS4WindowsTests
{
    [TestClass]
    public class ControllerHighlightAtlasTests
    {
        private static readonly string[] StickAtlases =
        {
            "DualSense-Stick_Highlights.png",
            "DualShock4-Stick_Highlights.png",
            "DualSenseEdge-Stick_Highlights.png",
            "Switch2Pro-Stick_Highlights.png",
        };

        [TestMethod]
        public void EveryStickControlHasAVisiblePixelExactHitMask()
        {
            foreach (string atlas in StickAtlases)
            {
                for (int frame = 0; frame < 10; frame++)
                {
                    BitmapSource image = RasterHighlightAtlas.Frame(atlas, frame);
                    Geometry mask = RasterHighlightAtlas.Mask(atlas, frame);

                    Assert.IsFalse(mask.Bounds.IsEmpty,
                        $"{atlas} frame {frame} has no hit geometry.");
                    Assert.IsTrue(CountOpaquePixels(image) > 0,
                        $"{atlas} frame {frame} has no visible highlight.");
                }
            }
        }

        [TestMethod]
        public void StickPressAndDirectionPixelsNeverOverlap()
        {
            foreach (string atlas in StickAtlases)
            {
                AssertDisjointSet(atlas, 0);
                AssertDisjointSet(atlas, 5);
            }
        }

        [TestMethod]
        public void XboxActionAtlasCoversEverySelectableControl()
        {
            const string atlas = "Xbox360-Action_Highlights.png";
            for (int frame = 0; frame < 25; frame++)
            {
                BitmapSource image = RasterHighlightAtlas.Frame(atlas, frame,
                    630, 247);
                Geometry mask = RasterHighlightAtlas.Mask(atlas, frame,
                    630, 247);

                Assert.IsFalse(mask.Bounds.IsEmpty,
                    $"Xbox action frame {frame} has no hit geometry.");
                Assert.IsTrue(CountOpaquePixels(image) > 0,
                    $"Xbox action frame {frame} has no visible highlight.");
            }
        }

        [TestMethod]
        public void EveryControllerSurfaceUsedByTheRemapperHasAHighlight()
        {
            var usedFrames = new Dictionary<string, int[]>
            {
                ["DualSense-Config_Highlights.png"] =
                    Enumerable.Range(0, 12).Concat(Enumerable.Range(14, 4))
                        .ToArray(),
                ["DualShock4-Config_Highlights.png"] =
                    Enumerable.Range(0, 11).Concat(Enumerable.Range(14, 8))
                        .ToArray(),
                ["DualSenseEdge-Config_Highlights.png"] =
                    Enumerable.Range(0, 12).Concat(Enumerable.Range(14, 10))
                        .ToArray(),
                ["Switch2Pro-Config_Highlights.png"] =
                    Enumerable.Range(0, 11).Concat(Enumerable.Range(14, 4))
                        .Concat(new[] { 24, 25, 26 }).ToArray(),
            };

            foreach ((string atlas, int[] frames) in usedFrames)
            {
                foreach (int frame in frames)
                {
                    Assert.IsTrue(CountOpaquePixels(
                        RasterHighlightAtlas.Frame(atlas, frame)) > 0,
                        $"{atlas} frame {frame} is empty.");
                    Assert.IsFalse(RasterHighlightAtlas.Mask(atlas, frame)
                        .Bounds.IsEmpty, $"{atlas} frame {frame} has no hit mask.");
                }
            }
        }

        [TestMethod]
        public void ConcurrentHoverReadsReuseFrozenImmutableFrames()
        {
            const string atlas = "DualSense-Stick_Highlights.png";
            BitmapSource expectedFrame = RasterHighlightAtlas.Frame(atlas, 0);
            Geometry expectedMask = RasterHighlightAtlas.Mask(atlas, 0);
            var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();

            Parallel.For(0, 250, index =>
            {
                BitmapSource frame = RasterHighlightAtlas.Frame(atlas,
                    index % 10);
                Geometry mask = RasterHighlightAtlas.Mask(atlas, index % 10);
                if (!frame.IsFrozen || !mask.IsFrozen || mask.Bounds.IsEmpty)
                {
                    failures.Enqueue($"Invalid frame {index % 10}");
                }
            });

            Assert.AreEqual(0, failures.Count,
                string.Join(", ", failures));
            Assert.AreSame(expectedFrame,
                RasterHighlightAtlas.Frame(atlas, 0));
            Assert.AreSame(expectedMask,
                RasterHighlightAtlas.Mask(atlas, 0));
        }

        private static void AssertDisjointSet(string atlas, int firstFrame)
        {
            byte[][] frames = Enumerable.Range(firstFrame, 5)
                .Select(frame => AlphaPixels(
                    RasterHighlightAtlas.Frame(atlas, frame)))
                .ToArray();
            for (int left = 0; left < frames.Length; left++)
            {
                for (int right = left + 1; right < frames.Length; right++)
                {
                    bool overlaps = frames[left].Zip(frames[right],
                        (a, b) => a >= 16 && b >= 16).Any(value => value);
                    Assert.IsFalse(overlaps,
                        $"{atlas} frames {firstFrame + left} and " +
                        $"{firstFrame + right} overlap.");
                }
            }
        }

        private static int CountOpaquePixels(BitmapSource source) =>
            AlphaPixels(source).Count(alpha => alpha >= 16);

        private static byte[] AlphaPixels(BitmapSource source)
        {
            BitmapSource pixels = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32,
                    null, 0);
            int stride = pixels.PixelWidth * 4;
            byte[] buffer = new byte[stride * pixels.PixelHeight];
            pixels.CopyPixels(buffer, stride, 0);
            byte[] alpha = new byte[pixels.PixelWidth * pixels.PixelHeight];
            for (int index = 0; index < alpha.Length; index++)
            {
                alpha[index] = buffer[(index * 4) + 3];
            }

            return alpha;
        }
    }
}
