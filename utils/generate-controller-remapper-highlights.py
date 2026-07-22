"""Generate exact-size raster highlight atlases for the profile remapper.

The source controller renders are intentionally kept separate from the hover
art.  Every atlas frame is a 440 x 220 transparent overlay whose mask follows
one visible control surface in the corresponding source raster.  Keeping the
generation data in source control makes future artwork changes reproducible
instead of leaving hand-positioned WPF rectangles as the only specification.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from PIL import Image, ImageDraw


CANVAS_WIDTH = 440
CANVAS_HEIGHT = 220
SUPERSAMPLE = 4
ACCENT = (47, 128, 237, 178)


@dataclass(frozen=True)
class Artwork:
    source: str
    atlas: str
    source_width: int
    source_height: int

    @property
    def scale(self) -> float:
        return min(
            CANVAS_WIDTH / self.source_width,
            CANVAS_HEIGHT / self.source_height,
        )

    @property
    def rendered_width(self) -> int:
        return round(self.source_width * self.scale)

    @property
    def rendered_height(self) -> int:
        return round(self.source_height * self.scale)


def ellipse(box: tuple[float, float, float, float]) -> Callable[[ImageDraw.ImageDraw], None]:
    return lambda draw: draw.ellipse(box, fill=255)


def rounded(box: tuple[float, float, float, float], radius: float) -> Callable[[ImageDraw.ImageDraw], None]:
    return lambda draw: draw.rounded_rectangle(box, radius=radius, fill=255)


def polygon(points: list[tuple[float, float]]) -> Callable[[ImageDraw.ImageDraw], None]:
    return lambda draw: draw.polygon(points, fill=255)


def scale_shape(
    shape: Callable[[ImageDraw.ImageDraw], None], artwork: Artwork
) -> Callable[[ImageDraw.ImageDraw], None]:
    """Draw source-pixel geometry into a supersampled 440 px canvas."""

    def draw_scaled(target: ImageDraw.ImageDraw) -> None:
        # Geometry is authored in source pixels. Draw it there at a high
        # resolution, then scale it using the same transform as the base art.
        source_mask = Image.new(
            "L",
            (artwork.source_width * SUPERSAMPLE,
             artwork.source_height * SUPERSAMPLE),
            0,
        )

        class ScaledDraw:
            def __init__(self, image: Image.Image) -> None:
                self._image = image
                self._draw = ImageDraw.Draw(image)

            @staticmethod
            def _box(box: tuple[float, float, float, float]):
                return tuple(round(value * SUPERSAMPLE) for value in box)

            @staticmethod
            def _points(points: list[tuple[float, float]]):
                return [
                    (round(x * SUPERSAMPLE), round(y * SUPERSAMPLE))
                    for x, y in points
                ]

            def ellipse(self, box, fill=255):
                self._draw.ellipse(self._box(box), fill=fill)

            def rounded_rectangle(self, box, radius=0, fill=255):
                self._draw.rounded_rectangle(
                    self._box(box), radius=round(radius * SUPERSAMPLE), fill=fill
                )

            def polygon(self, points, fill=255):
                self._draw.polygon(self._points(points), fill=fill)

            def rectangle(self, box, fill=255):
                self._draw.rectangle(self._box(box), fill=fill)

        shape(ScaledDraw(source_mask))
        rendered_mask = source_mask.resize(
            (artwork.rendered_width * SUPERSAMPLE,
             artwork.rendered_height * SUPERSAMPLE),
            Image.Resampling.LANCZOS,
        )
        left = ((CANVAS_WIDTH - artwork.rendered_width) // 2) * SUPERSAMPLE
        top = ((CANVAS_HEIGHT - artwork.rendered_height) // 2) * SUPERSAMPLE
        target._image.paste(rendered_mask, (left, top))

    return draw_scaled


def render_atlas(
    resources: Path,
    artwork: Artwork,
    source_shapes: list[Callable[[ImageDraw.ImageDraw], None]],
) -> None:
    frames: list[Image.Image] = []
    for source_shape in source_shapes:
        large_mask = Image.new(
            "L", (CANVAS_WIDTH * SUPERSAMPLE, CANVAS_HEIGHT * SUPERSAMPLE), 0
        )
        scale_shape(source_shape, artwork)(ImageDraw.Draw(large_mask))
        mask = large_mask.resize(
            (CANVAS_WIDTH, CANVAS_HEIGHT), Image.Resampling.LANCZOS
        )
        frame = Image.new("RGBA", (CANVAS_WIDTH, CANVAS_HEIGHT), ACCENT)
        frame.putalpha(mask.point(lambda value: value * ACCENT[3] // 255))
        frames.append(frame)

    atlas = Image.new(
        "RGBA", (CANVAS_WIDTH, CANVAS_HEIGHT * len(frames)), (0, 0, 0, 0)
    )
    for index, frame in enumerate(frames):
        atlas.alpha_composite(frame, (0, index * CANVAS_HEIGHT))

    atlas.save(resources / artwork.atlas, optimize=True)
    print(f"generated {artwork.atlas}: {atlas.width} x {atlas.height}")


def dualshock4_shapes():
    return [
        ellipse((294, 134, 323, 163)),  # Cross
        ellipse((321, 112, 350, 141)),  # Circle
        ellipse((266, 112, 295, 141)),  # Square
        ellipse((294, 91, 323, 120)),   # Triangle
        rounded((49, 49, 104, 78), 12),
        rounded((280, 49, 335, 78), 12),
        polygon([(49, 49), (52, 28), (62, 20), (91, 20), (102, 28), (104, 49)]),
        polygon([(280, 49), (282, 28), (293, 20), (322, 20), (333, 28), (335, 49)]),
        rounded((108, 80, 123, 105), 7),
        rounded((260, 80, 275, 105), 7),
        ellipse((179, 156, 204, 179)),
        ellipse((0, 0, 0, 0)),          # No mute button
        ellipse((106, 156, 157, 204)),
        ellipse((228, 156, 279, 204)),
        rounded((63, 99, 90, 129), 8),
        rounded((82, 112, 112, 141), 8),
        rounded((63, 131, 90, 161), 8),
        rounded((43, 112, 73, 141), 8),
        polygon([(130, 79), (180, 79), (180, 135), (138, 135), (130, 127)]),
        rounded((180, 79, 205, 135), 2),
        polygon([(205, 79), (255, 79), (255, 127), (247, 135), (205, 135)]),
        rounded((130, 62, 255, 79), 8),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),          # No capture button
    ]


def dualsense_edge_shapes():
    return [
        ellipse((1154, 421, 1252, 519)),
        ellipse((1252, 319, 1352, 418)),
        ellipse((1052, 319, 1152, 418)),
        ellipse((1154, 218, 1252, 316)),
        polygon([(246, 99), (278, 82), (390, 84), (433, 102), (458, 129), (458, 190), (447, 198), (249, 198)]),
        polygon([(1100, 99), (1132, 82), (1244, 84), (1287, 102), (1312, 129), (1312, 190), (1301, 198), (1103, 198)]),
        polygon([(251, 99), (255, 42), (274, 20), (370, 20), (410, 35), (437, 65), (447, 99)]),
        polygon([(1111, 99), (1121, 65), (1148, 35), (1188, 20), (1284, 20), (1303, 42), (1307, 99)]),
        rounded((435, 201, 481, 266), 18),
        rounded((1077, 201, 1123, 266), 18),
        polygon([(731, 512), (760, 523), (756, 568), (738, 568), (766, 583), (803, 574), (819, 584), (776, 604), (731, 583)]),
        rounded((744, 636, 814, 659), 10),
        ellipse((470, 464, 659, 653)),
        ellipse((899, 464, 1088, 653)),
        polygon([(313, 259), (354, 253), (396, 259), (398, 311), (370, 352), (340, 352), (311, 311)]),
        polygon([(373, 328), (411, 311), (454, 325), (476, 352), (475, 389), (453, 412), (411, 409), (374, 389)]),
        polygon([(313, 389), (354, 385), (396, 389), (399, 438), (375, 480), (335, 480), (311, 438)]),
        polygon([(235, 326), (278, 311), (316, 328), (336, 353), (335, 389), (313, 410), (271, 412), (237, 389)]),
        polygon([(504, 226), (700, 226), (700, 414), (592, 414),
                 (548, 395), (516, 345)]),
        polygon([(700, 226), (858, 226), (858, 414), (700, 414)]),
        polygon([(858, 226), (1054, 226), (1042, 344), (1010, 396),
                 (965, 414), (858, 414)]),
        polygon([(498, 158), (1060, 158), (1054, 226), (504, 226)]),
        polygon([(522, 730), (604, 729), (600, 765), (584, 780), (537, 777), (524, 760)]),
        polygon([(954, 729), (1036, 730), (1034, 760), (1020, 777), (973, 780), (958, 765)]),
        ellipse((0, 0, 0, 0)),          # Rear paddles are not visible in this front raster
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),          # No capture button
    ]


def switch2_pro_shapes():
    return [
        ellipse((1052, 366, 1130, 452)),  # B / Cross
        ellipse((1147, 275, 1229, 361)),  # A / Circle
        ellipse((956, 275, 1036, 361)),   # Y / Square
        ellipse((1051, 187, 1129, 274)),  # X / Triangle
        polygon([(280, 91), (299, 77), (470, 77), (497, 94), (504, 143), (494, 191), (279, 191)]),
        polygon([(1032, 91), (1051, 77), (1222, 77), (1249, 94), (1256, 143), (1246, 191), (1031, 191)]),
        polygon([(290, 84), (302, 37), (330, 21), (426, 21), (460, 38), (474, 84)]),
        polygon([(1062, 84), (1076, 38), (1110, 21), (1206, 21), (1234, 37), (1246, 84)]),
        ellipse((578, 199, 646, 270)),
        ellipse((879, 199, 947, 270)),
        ellipse((807, 287, 878, 361)),
        ellipse((0, 0, 0, 0)),          # No mute button
        ellipse((326, 235, 493, 412)),
        ellipse((846, 414, 1038, 606)),
        polygon([(538, 411), (610, 411), (610, 477), (592, 492),
                 (556, 492), (538, 477)]),
        polygon([(600, 465), (666, 465), (681, 483), (681, 522),
                 (666, 540), (600, 540)]),
        polygon([(538, 520), (610, 520), (610, 592), (594, 610),
                 (554, 610), (538, 592)]),
        polygon([(468, 483), (483, 465), (548, 465), (548, 540),
                 (483, 540), (468, 522)]),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        polygon([(430, 694), (520, 694), (520, 756), (500, 780), (451, 774), (432, 746)]),
        polygon([(1000, 694), (1090, 694), (1088, 746), (1069, 774), (1020, 780), (1000, 756)]),
        rounded((648, 288, 713, 355), 8),  # Capture
    ]


def main() -> None:
    resources = Path(__file__).resolve().parents[1] / "DS4Windows" / "Resources"
    jobs = [
        (Artwork("DualShock 4 Controller.png", "DualShock4-Config_Highlights.png", 384, 247), dualshock4_shapes()),
        (Artwork("DualSense Edge Controller.png", "DualSenseEdge-Config_Highlights.png", 1558, 1009), dualsense_edge_shapes()),
        (Artwork("Switch 2 Pro Controller.png", "Switch2Pro-Config_Highlights.png", 1536, 1024), switch2_pro_shapes()),
    ]
    for artwork, shapes in jobs:
        render_atlas(resources, artwork, shapes)


if __name__ == "__main__":
    main()
