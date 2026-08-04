using System.Windows;
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
        public void StickDirectionsAreExclusiveAndStayInsideTheCap()
        {
            foreach (string atlas in StickAtlases)
            {
                AssertStickSet(atlas, 0);
                AssertStickSet(atlas, 5);
            }
        }

        [TestMethod]
        public void StickDirectionsFillTheCurvedCapInsteadOfRenderingAsDots()
        {
            foreach (string atlas in StickAtlases)
            {
                foreach (int firstFrame in new[] { 0, 5 })
                {
                    int surfacePixels = CountOpaquePixels(
                        RasterHighlightAtlas.Frame(atlas, firstFrame));
                    int directionPixels = Enumerable.Range(firstFrame + 1, 4)
                        .Sum(frame => CountOpaquePixels(
                            RasterHighlightAtlas.Frame(atlas, frame)));

                    Assert.IsTrue(directionPixels >= surfacePixels * 0.90,
                        $"{atlas} directional surfaces cover only " +
                        $"{directionPixels}/{surfacePixels} pixels of the cap.");
                }
            }
        }

        [TestMethod]
        public void StickPressHitAreaIsCenteredAndSmallerThanItsVisualCap()
        {
            foreach (string atlas in StickAtlases)
            {
                foreach (int frame in new[] { 0, 5 })
                {
                    Geometry surface = RasterHighlightAtlas.Mask(atlas, frame);
                    Geometry hit = RasterHighlightAtlas.CenterPressMask(surface);
                    Assert.IsFalse(hit.Bounds.IsEmpty);
                    Assert.IsTrue(hit.Bounds.Width < surface.Bounds.Width * 0.5);
                    Assert.IsTrue(hit.Bounds.Height < surface.Bounds.Height * 0.5);
                    Assert.AreEqual(surface.Bounds.Left + surface.Bounds.Width / 2.0,
                        hit.Bounds.Left + hit.Bounds.Width / 2.0, 1.0);
                    Assert.AreEqual(surface.Bounds.Top + surface.Bounds.Height / 2.0,
                        hit.Bounds.Top + hit.Bounds.Height / 2.0, 1.0);
                }
            }
        }

        [TestMethod]
        public void StickDirectionHitAreasReserveTheCenteredPressLane()
        {
            foreach (string atlas in StickAtlases)
            {
                foreach (int surfaceFrame in new[] { 0, 5 })
                {
                    Geometry surface = RasterHighlightAtlas.Mask(atlas,
                        surfaceFrame);
                    Geometry center = RasterHighlightAtlas.CenterPressMask(
                        surface);
                    foreach (int frame in Enumerable.Range(surfaceFrame + 1,
                        4))
                    {
                        Geometry hit = RasterHighlightAtlas
                            .DirectionalHitMask(
                                RasterHighlightAtlas.Mask(atlas, frame),
                                surface);
                        Point capCenter = new Point(
                            surface.Bounds.Left + surface.Bounds.Width / 2.0,
                            surface.Bounds.Top + surface.Bounds.Height / 2.0);
                        Assert.IsFalse(hit.FillContains(capCenter),
                            $"{atlas} direction frame {frame} steals the " +
                            "centered L3/R3 pointer lane.");
                    }
                }
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
        public void MappingHighlightsKeepTextureVisibleInsideACrispBoundedEdge()
        {
            foreach (string atlas in new[]
            {
                "DualShock4-Mapping_Highlights.png",
                "DualSenseEdge-Mapping_Highlights.png",
                "Switch2Pro-Mapping_Highlights.png",
            })
            {
                byte[] alpha = AlphaPixels(
                    RasterHighlightAtlas.Frame(atlas, 7));
                Assert.IsTrue(alpha.Any(value => value >= 180),
                    $"{atlas} has no crisp inner edge.");
                Assert.IsTrue(alpha.Any(value => value >= 32 && value <= 120),
                    $"{atlas} has no translucent interior.");
            }
        }

        [TestMethod]
        public void ShoulderAndTriggerHighlightsNeverOverlap()
        {
            foreach ((string atlas, int width, int height) in new[]
            {
                ("DualShock4-Config_Highlights.png", 440, 220),
                ("DualSenseEdge-Config_Highlights.png", 440, 220),
                ("Switch2Pro-Config_Highlights.png", 440, 220),
                ("Xbox360-Action_Highlights.png", 630, 247),
            })
            {
                foreach ((int shoulder, int trigger) in new[]
                {
                    (4, 6),
                    (5, 7),
                })
                {
                    byte[] shoulderAlpha = AlphaPixels(
                        RasterHighlightAtlas.Frame(atlas, shoulder, width,
                            height));
                    byte[] triggerAlpha = AlphaPixels(
                        RasterHighlightAtlas.Frame(atlas, trigger, width,
                            height));
                    bool overlaps = shoulderAlpha.Zip(triggerAlpha,
                        (first, second) => first >=
                            RasterHighlightAtlas.HitAlphaThreshold &&
                            second >= RasterHighlightAtlas.HitAlphaThreshold)
                        .Any(value => value);

                    Assert.IsFalse(overlaps,
                        $"{atlas} frames {shoulder}/{trigger} overlap.");
                }
            }
        }

        [TestMethod]
        public void SwitchAndXboxShouldersStayBoundedToTheirPaintedCaps()
        {
            AssertPairedControlBounds("Switch2Pro-Config_Highlights.png",
                440, 220, 4, 5, 40, 52, 18, 28);
            AssertPairedControlBounds("Switch2Pro-Config_Highlights.png",
                440, 220, 6, 7, 33, 45, 12, 22);
            AssertPairedControlBounds("Xbox360-Action_Highlights.png",
                630, 247, 4, 5, 78, 84, 30, 38);
            AssertPairedControlBounds("Xbox360-Action_Highlights.png",
                630, 247, 6, 7, 26, 38, 32, 44);
        }

        [TestMethod]
        public void EveryNonDualSenseHighlightStaysOnControllerArtwork()
        {
            AssertAtlasStaysOnArtwork("DualShock 4 Controller.png", 384, 247,
                "DualShock4-Config_Highlights.png", 440, 220);
            AssertAtlasStaysOnArtwork("DualSense Edge Controller.png", 1558,
                1009, "DualSenseEdge-Config_Highlights.png", 440, 220);
            AssertAtlasStaysOnArtwork("Switch 2 Pro Controller.png", 1536,
                1024, "Switch2Pro-Config_Highlights.png", 440, 220);
            AssertAtlasStaysOnArtwork("360 map.png", 1323, 439,
                "Xbox360-Action_Highlights.png", 630, 247);
        }

        private static void AssertPairedControlBounds(string atlas, int width,
            int height, int leftFrame, int rightFrame, double minimumWidth,
            double maximumWidth, double minimumHeight, double maximumHeight)
        {
            BitmapSource leftImage = RasterHighlightAtlas.Frame(atlas,
                leftFrame, width, height);
            BitmapSource rightImage = RasterHighlightAtlas.Frame(atlas,
                rightFrame, width, height);
            Rect left = RasterHighlightAtlas.Mask(atlas, leftFrame, width,
                height).Bounds;
            Rect right = RasterHighlightAtlas.Mask(atlas, rightFrame, width,
                height).Bounds;

            foreach (Rect bounds in new[] { left, right })
            {
                Assert.IsTrue(bounds.Width >= minimumWidth &&
                    bounds.Width <= maximumWidth &&
                    bounds.Height >= minimumHeight &&
                    bounds.Height <= maximumHeight,
                    $"{atlas} control bounds {bounds.Width:F1} x " +
                    $"{bounds.Height:F1} leave the painted cap.");
            }

            int leftPixels = CountOpaquePixels(leftImage);
            int rightPixels = CountOpaquePixels(rightImage);
            double ratio = leftPixels / (double)Math.Max(1, rightPixels);
            Assert.IsTrue(ratio >= 0.75 && ratio <= 1.25,
                $"{atlas} paired control areas are asymmetric ({ratio:F2}).");
        }

        [TestMethod]
        public void DualSenseEdgeFaceHighlightsCoverButtonsNotTheirGlyphs()
        {
            const string atlas = "DualSenseEdge-Config_Highlights.png";
            for (int frame = 0; frame < 4; frame++)
            {
                BitmapSource highlight = RasterHighlightAtlas.Frame(atlas,
                    frame);
                Geometry mask = RasterHighlightAtlas.Mask(atlas, frame);
                Assert.IsTrue(CountOpaquePixels(highlight) >= 320,
                    $"Edge face frame {frame} covers only a glyph fragment.");
                Assert.IsTrue(mask.Bounds.Width >= 18 &&
                    mask.Bounds.Height >= 18 &&
                    mask.Bounds.Width <= 31 &&
                    mask.Bounds.Height <= 31,
                    $"Edge face frame {frame} has implausible cap bounds " +
                    $"{mask.Bounds.Width:F1} x {mask.Bounds.Height:F1}.");
            }
        }

        [TestMethod]
        public void DualSenseEdgeLightbarWrapsAroundTheTouchpad()
        {
            Geometry lightbar = RasterHighlightAtlas.Mask(
                "DualSenseEdge-Mapping-Lightbar.png", 0);
            Assert.IsTrue(lightbar.Bounds.Width >= 130,
                $"Edge lightbar width is only {lightbar.Bounds.Width}.");
            Assert.IsTrue(lightbar.Bounds.Height >= 50,
                $"Edge lightbar height is only {lightbar.Bounds.Height}.");
        }

        [TestMethod]
        public void DualShock4UpperTouchIsInsideTheTouchpadNotTheLightbar()
        {
            Rect upperTouch = RasterHighlightAtlas.Mask(
                "DualShock4-Config_Highlights.png", 21).Bounds;
            Assert.IsTrue(upperTouch.Top >= 67,
                $"Upper touch starts above the touchpad at {upperTouch.Top}.");
            Assert.IsTrue(upperTouch.Bottom <= 87,
                $"Upper touch leaves the touchpad at {upperTouch.Bottom}.");
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

        private static void AssertStickSet(string atlas, int firstFrame)
        {
            BitmapSource surfaceImage = RasterHighlightAtlas.Frame(atlas,
                firstFrame);
            byte[] surface = AlphaPixels(surfaceImage);
            int minX = surfaceImage.PixelWidth;
            int minY = surfaceImage.PixelHeight;
            int maxX = -1;
            int maxY = -1;
            for (int index = 0; index < surface.Length; index++)
            {
                if (surface[index] < 16)
                {
                    continue;
                }

                int x = index % surfaceImage.PixelWidth;
                int y = index / surfaceImage.PixelWidth;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            double centerX = (minX + maxX + 1) / 2.0;
            double centerY = (minY + maxY + 1) / 2.0;
            byte[][] frames = Enumerable.Range(firstFrame + 1, 4)
                .Select(frame => AlphaPixels(
                    RasterHighlightAtlas.Frame(atlas, frame)))
                .ToArray();
            foreach (byte[] direction in frames)
            {
                Assert.IsFalse(direction.Where((alpha, index) => alpha >= 16 &&
                    surface[index] < 16).Any(),
                    $"{atlas} has a stick direction outside its cap.");
            }
            for (int left = 0; left < frames.Length; left++)
            {
                for (int right = left + 1; right < frames.Length; right++)
                {
                    bool overlaps = frames[left].Select((alpha, index) =>
                    {
                        if (alpha < 16 || frames[right][index] < 16)
                        {
                            return false;
                        }

                        int x = index % surfaceImage.PixelWidth;
                        int y = index / surfaceImage.PixelWidth;
                        double dx = x + 0.5 - centerX;
                        double dy = y + 0.5 - centerY;
                        return dx * dx + dy * dy > 4.0;
                    }).Any(value => value);
                    Assert.IsFalse(overlaps,
                        $"{atlas} direction frames {firstFrame + left + 1} and " +
                        $"{firstFrame + right + 1} overlap outside the shared " +
                        "center apex.");
                }
            }
        }

        private static int CountOpaquePixels(BitmapSource source) =>
            AlphaPixels(source).Count(alpha => alpha >= 16);

        private static void AssertAtlasStaysOnArtwork(string artworkName,
            int artworkWidth, int artworkHeight, string atlasName,
            int frameWidth, int frameHeight)
        {
            BitmapSource artwork = LoadResource(artworkName);
            byte[] artworkAlpha = AlphaPixels(artwork);
            BitmapSource atlas = LoadResource(atlasName);
            int frameCount = atlas.PixelHeight / frameHeight;
            double scale = Math.Min((double)frameWidth / artworkWidth,
                (double)frameHeight / artworkHeight);
            double offsetX = (frameWidth - artworkWidth * scale) / 2.0;
            double offsetY = (frameHeight - artworkHeight * scale) / 2.0;

            for (int frame = 0; frame < frameCount; frame++)
            {
                byte[] highlightAlpha = AlphaPixels(RasterHighlightAtlas.Frame(
                    atlasName, frame, frameWidth, frameHeight));
                for (int y = 0; y < frameHeight; y++)
                {
                    for (int x = 0; x < frameWidth; x++)
                    {
                        if (highlightAlpha[y * frameWidth + x] <
                            RasterHighlightAtlas.HitAlphaThreshold)
                        {
                            continue;
                        }

                        int sourceX = Math.Clamp((int)((x + 0.5 - offsetX) /
                            scale), 0, artworkWidth - 1);
                        int sourceY = Math.Clamp((int)((y + 0.5 - offsetY) /
                            scale), 0, artworkHeight - 1);
                        bool touchesArtwork = false;
                        for (int sampleY = Math.Max(0, sourceY - 1);
                            sampleY <= Math.Min(artworkHeight - 1, sourceY + 1) &&
                            !touchesArtwork; sampleY++)
                        {
                            for (int sampleX = Math.Max(0, sourceX - 1);
                                sampleX <= Math.Min(artworkWidth - 1, sourceX + 1);
                                sampleX++)
                            {
                                if (artworkAlpha[sampleY * artworkWidth +
                                    sampleX] >= 16)
                                {
                                    touchesArtwork = true;
                                    break;
                                }
                            }
                        }

                        Assert.IsTrue(touchesArtwork,
                            $"{atlasName} frame {frame} paints transparent " +
                            $"space at ({x}, {y}).");
                    }
                }
            }
        }

        private static BitmapSource LoadResource(string resourceName)
        {
            var uri = new Uri(
                $"/DS4Windows;component/Resources/{resourceName}",
                UriKind.Relative);
            System.Windows.Resources.StreamResourceInfo resource =
                Application.GetResourceStream(uri);
            Assert.IsNotNull(resource?.Stream,
                $"Resource {resourceName} was not found.");
            using (resource.Stream)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = resource.Stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }

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
