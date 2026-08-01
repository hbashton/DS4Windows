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

from PIL import Image, ImageChops, ImageDraw, ImageFilter


CANVAS_WIDTH = 440
CANVAS_HEIGHT = 220
SUPERSAMPLE = 4
ACCENT = (47, 128, 237, 178)
MAPPING_FILL_ALPHA = 88
MAPPING_EDGE_ALPHA = 224


def split_stick_ring(ring: Image.Image, center_x: float,
                     center_y: float) -> list[Image.Image]:
    """Assign every ring pixel to exactly one cardinal direction."""

    sectors = [Image.new("L", ring.size, 0) for _ in range(4)]
    source = ring.load()
    targets = [sector.load() for sector in sectors]
    for y in range(ring.height):
        for x in range(ring.width):
            alpha = source[x, y]
            if alpha == 0:
                continue
            dx = x + 0.5 - center_x
            dy = y + 0.5 - center_y
            if abs(dx) > abs(dy):
                direction = 1 if dx > 0 else 3
            else:
                direction = 2 if dy > 0 else 0
            targets[direction][x, y] = alpha
    return sectors


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
    source_alpha = Image.open(resources / artwork.source).convert("RGBA").getchannel("A")
    rendered_alpha = source_alpha.resize(
        (artwork.rendered_width, artwork.rendered_height),
        Image.Resampling.LANCZOS,
    )
    artwork_mask = Image.new("L", (CANVAS_WIDTH, CANVAS_HEIGHT), 0)
    artwork_mask.paste(
        rendered_alpha,
        ((CANVAS_WIDTH - artwork.rendered_width) // 2,
         (CANVAS_HEIGHT - artwork.rendered_height) // 2),
    )
    # Never let interpolation spill a hit target beyond the rendered pad.
    # The authored geometry identifies the individual control; this final
    # clip supplies the exact antialiased outside silhouette of the raster.
    artwork_clip = artwork_mask.point(lambda value: 255 if value >= 64 else 0)

    frames: list[Image.Image] = []
    for source_shape in source_shapes:
        large_mask = Image.new(
            "L", (CANVAS_WIDTH * SUPERSAMPLE, CANVAS_HEIGHT * SUPERSAMPLE), 0
        )
        scale_shape(source_shape, artwork)(ImageDraw.Draw(large_mask))
        mask = large_mask.resize(
            (CANVAS_WIDTH, CANVAS_HEIGHT), Image.Resampling.LANCZOS
        )
        mask = ImageChops.multiply(mask, artwork_clip)
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


def render_stick_atlas(resources: Path, source_atlas: str, target_atlas: str) -> None:
    """Derive pixel-exact stick press/direction masks from the painted sticks.

    Frames 12 and 13 in every controller atlas trace the visible left and
    right stick surfaces.  Intersecting sectors with those masks retains the
    raster's real rim instead of approximating it with a floating circle.
    """

    atlas = Image.open(resources / source_atlas).convert("RGBA")
    output: list[Image.Image] = []
    for frame_index in (12, 13):
        frame = atlas.crop((0, frame_index * CANVAS_HEIGHT,
                            CANVAS_WIDTH, (frame_index + 1) * CANVAS_HEIGHT))
        surface = frame.getchannel("A").point(
            lambda value: min(255, round(value * 255 / ACCENT[3]))
        )
        bounds = surface.getbbox()
        if bounds is None:
            output.extend(Image.new("RGBA", (CANVAS_WIDTH, CANVAS_HEIGHT),
                                    (0, 0, 0, 0)) for _ in range(5))
            continue

        left, top, right, bottom = bounds
        width = right - left
        height = bottom - top
        center_x = left + width / 2.0
        center_y = top + height / 2.0

        center = Image.new("L", surface.size, 0)
        center_draw = ImageDraw.Draw(center)
        center_draw.ellipse((
            center_x - width * 0.22,
            center_y - height * 0.22,
            center_x + width * 0.22,
            center_y + height * 0.22,
        ), fill=255)
        press_mask = ImageChops.multiply(surface, center)

        ring = ImageChops.subtract(surface, press_mask)
        sector_masks = split_stick_ring(ring, center_x, center_y)

        for mask in (press_mask, *sector_masks):
            result = Image.new("RGBA", (CANVAS_WIDTH, CANVAS_HEIGHT), ACCENT)
            result.putalpha(mask.point(lambda value: value * ACCENT[3] // 255))
            output.append(result)

    stick_atlas = Image.new(
        "RGBA", (CANVAS_WIDTH, CANVAS_HEIGHT * len(output)), (0, 0, 0, 0)
    )
    for index, frame in enumerate(output):
        stick_atlas.alpha_composite(frame, (0, index * CANVAS_HEIGHT))

    stick_atlas.save(resources / target_atlas, optimize=True)
    print(f"generated {target_atlas}: {stick_atlas.width} x {stick_atlas.height}")


def render_mapping_atlas(resources: Path, source_atlas: str,
                         target_atlas: str) -> None:
    """Create a bounded inner-glow treatment for the button-mapping view.

    The action picker intentionally retains its existing solid overlays. The
    profile map needs the underlying label and button texture to remain
    visible, so it uses a translucent fill plus a crisp edge entirely inside
    the exact source mask. No blur is allowed outside the control surface.
    """

    source = Image.open(resources / source_atlas).convert("RGBA")
    frame_count = source.height // CANVAS_HEIGHT
    output = Image.new("RGBA", source.size, (0, 0, 0, 0))
    for frame_index in range(frame_count):
        frame = source.crop((0, frame_index * CANVAS_HEIGHT,
                             CANVAS_WIDTH,
                             (frame_index + 1) * CANVAS_HEIGHT))
        source_alpha = frame.getchannel("A")
        normalized = source_alpha.point(
            lambda value: min(255, round(value * 255 / ACCENT[3])))
        eroded = normalized.filter(ImageFilter.MinFilter(5))
        inner_edge = ImageChops.subtract(normalized, eroded)
        fill = normalized.point(
            lambda value: round(value * MAPPING_FILL_ALPHA / 255))
        edge = inner_edge.point(
            lambda value: round(value * MAPPING_EDGE_ALPHA / 255))
        alpha = ImageChops.lighter(fill, edge)
        styled = Image.new("RGBA", (CANVAS_WIDTH, CANVAS_HEIGHT),
                           ACCENT[:3] + (0,))
        styled.putalpha(alpha)
        output.alpha_composite(styled, (0, frame_index * CANVAS_HEIGHT))

    output.save(resources / target_atlas, optimize=True)
    print(f"generated {target_atlas}: {output.width} x {output.height}")


def render_dualshock4_mapping_lightbar(resources: Path) -> None:
    """Trace only the blue light pipe visible in the DS4 front raster."""

    source = Image.open(resources / "DualShock 4 Controller.png").convert("RGBA")
    left, top, right, bottom = (128, 47, 256, 61)
    crop = source.crop((left, top, right, bottom))
    pixels = crop.load()
    mask = Image.new("L", crop.size, 0)
    target = mask.load()
    for y in range(crop.height):
        for x in range(crop.width):
            red, green, blue, alpha = pixels[x, y]
            if alpha > 0 and blue >= 110 and blue > red * 1.35 and blue > green * 1.08:
                target[x, y] = alpha

    mask = mask.filter(ImageFilter.MaxFilter(3)).filter(ImageFilter.MinFilter(3))
    result = Image.new("RGBA", crop.size, (255, 255, 255, 0))
    result.putalpha(mask)
    target_name = "DualShock4-Mapping-Lightbar.png"
    result.save(resources / target_name, optimize=True)
    print(f"generated {target_name}: {result.width} x {result.height}")


def render_xbox_action_atlas(resources: Path) -> None:
    """Render the Xbox action picker against its native 630 x 247 canvas."""

    width = 630
    height = 247
    frame_height = height
    source_width = 1323
    source_height = 439
    scale = width / source_width
    offset_y = (height - source_height * scale) / 2.0
    source_alpha = Image.open(resources / "360 map.png").convert("RGBA").getchannel("A")
    rendered_height = round(source_height * scale)
    rendered_alpha = source_alpha.resize(
        (width, rendered_height), Image.Resampling.LANCZOS)
    artwork_mask = Image.new("L", (width, height), 0)
    artwork_mask.paste(rendered_alpha, (0, round(offset_y)))
    artwork_clip = artwork_mask.point(lambda value: 255 if value >= 64 else 0)

    def point(x: float, y: float) -> tuple[int, int]:
        return (round(x * scale * SUPERSAMPLE),
                round((offset_y + y * scale) * SUPERSAMPLE))

    def make_mask(kind: str, data, radius: float = 0) -> Image.Image:
        mask = Image.new("L", (width * SUPERSAMPLE,
                               height * SUPERSAMPLE), 0)
        draw = ImageDraw.Draw(mask)
        if kind in ("ellipse", "rounded"):
            x1, y1 = point(data[0], data[1])
            x2, y2 = point(data[2], data[3])
            if kind == "ellipse":
                draw.ellipse((x1, y1, x2, y2), fill=255)
            else:
                draw.rounded_rectangle((x1, y1, x2, y2),
                    radius=round(radius * scale * SUPERSAMPLE), fill=255)
        else:
            draw.polygon([point(x, y) for x, y in data], fill=255)
        return mask.resize((width, height), Image.Resampling.LANCZOS)

    masks = [
        make_mask("ellipse", (895, 186, 970, 251)),   # A
        make_mask("ellipse", (980, 124, 1057, 190)),  # B
        make_mask("ellipse", (829, 126, 902, 193)),   # X
        make_mask("ellipse", (904, 65, 979, 133)),    # Y
        make_mask("polygon", [(28, 106), (42, 78), (115, 51),
                               (181, 74), (201, 107), (197, 129),
                               (51, 150), (31, 132)]),
        make_mask("polygon", [(1122, 107), (1142, 74), (1208, 51),
                               (1281, 78), (1295, 106), (1292, 132),
                               (1272, 150), (1126, 129)]),
        make_mask("polygon", [(108, 82), (119, 8), (135, 0),
                               (164, 1), (174, 11), (176, 84)]),
        make_mask("polygon", [(1147, 84), (1149, 11), (1159, 1),
                               (1188, 0), (1204, 8), (1215, 82)]),
        make_mask("ellipse", (538, 142, 589, 182)),
        make_mask("ellipse", (738, 142, 790, 182)),
        make_mask("ellipse", (607, 113, 718, 207)),
    ]

    def stick_masks(surface: Image.Image) -> list[Image.Image]:
        bounds = surface.getbbox()
        assert bounds is not None
        left, top, right, bottom = bounds
        center_x = (left + right) / 2.0
        center_y = (top + bottom) / 2.0
        center = Image.new("L", surface.size, 0)
        ImageDraw.Draw(center).ellipse((
            center_x - (right - left) * 0.22,
            center_y - (bottom - top) * 0.22,
            center_x + (right - left) * 0.22,
            center_y + (bottom - top) * 0.22,
        ), fill=255)
        press = ImageChops.multiply(surface, center)
        ring = ImageChops.subtract(surface, press)
        return [press, *split_stick_ring(ring, center_x, center_y)]

    # Match the movable thumb-cap, not the stationary socket around it.
    # L3/R3 then occupy the small center while directions divide its rim.
    masks.extend(stick_masks(make_mask("ellipse", (341, 143, 433, 237))))
    masks.extend(stick_masks(make_mask("ellipse", (746, 271, 840, 371))))
    masks.extend([
        make_mask("polygon", [(500, 250), (548, 250), (551, 291),
                               (541, 301), (507, 301), (497, 291)]),
        make_mask("polygon", [(548, 280), (599, 280), (607, 292),
                               (607, 326), (596, 337), (548, 337)]),
        make_mask("polygon", [(500, 326), (548, 326), (551, 367),
                               (541, 378), (507, 378), (497, 367)]),
        make_mask("polygon", [(452, 280), (500, 280), (500, 337),
                               (452, 337), (441, 326), (441, 292)]),
    ])

    frames: list[Image.Image] = []
    for mask in masks:
        mask = ImageChops.multiply(mask, artwork_clip)
        frame = Image.new("RGBA", (width, height), ACCENT)
        frame.putalpha(mask.point(lambda value: value * ACCENT[3] // 255))
        frames.append(frame)
    atlas = Image.new("RGBA", (width, frame_height * len(frames)),
                      (0, 0, 0, 0))
    for index, frame in enumerate(frames):
        atlas.alpha_composite(frame, (0, index * frame_height))
    target = resources / "Xbox360-Action_Highlights.png"
    atlas.save(target, optimize=True)
    print(f"generated {target.name}: {atlas.width} x {atlas.height}")


def dualshock4_shapes():
    return [
        # Trace the dark, movable cap of each control rather than the pale
        # bezel around it. This is the same visual contract as the polished
        # DualSense atlas: the button itself glows, never its surroundings.
        ellipse((295, 135, 322, 162)),  # Cross
        ellipse((322, 113, 349, 140)),  # Circle
        ellipse((267, 113, 294, 140)),  # Square
        ellipse((295, 92, 322, 119)),   # Triangle
        rounded((51, 50, 102, 76), 10),
        rounded((282, 50, 333, 76), 10),
        polygon([(54, 48), (60, 40), (64, 33), (67, 28),
                 (72, 25), (88, 25), (93, 28), (96, 33),
                 (97, 40), (99, 48)]),
        polygon([(285, 48), (287, 40), (288, 33), (291, 28),
                 (296, 25), (312, 25), (317, 28), (320, 33),
                 (324, 40), (330, 48)]),
        rounded((109, 81, 122, 104), 6),
        rounded((261, 81, 274, 104), 6),
        ellipse((182, 159, 202, 176)),
        ellipse((0, 0, 0, 0)),          # No mute button
        # The stick map is the top cap only. The stick-atlas pass divides
        # this exact cap into a small L3/R3 center and four disjoint sectors.
        ellipse((112, 162, 152, 201)),
        ellipse((234, 162, 274, 201)),
        polygon([(65, 100), (88, 100), (89, 104), (89, 126),
                 (85, 129), (68, 129), (64, 126), (64, 104)]),
        polygon([(84, 114), (88, 113), (109, 113), (112, 117),
                 (112, 136), (108, 140), (88, 140), (84, 137)]),
        polygon([(65, 132), (88, 132), (89, 136), (89, 157),
                 (85, 160), (68, 160), (64, 157), (64, 136)]),
        polygon([(45, 114), (49, 113), (69, 113), (73, 117),
                 (73, 136), (69, 140), (49, 140), (45, 137)]),
        # Touch gestures share one bounded touch surface. Upper touch lives
        # inside the pad (the old atlas accidentally painted the lightbar).
        polygon([(130, 95), (180, 95), (180, 135), (138, 135),
                 (130, 127)]),
        rounded((180, 95, 205, 135), 2),
        polygon([(205, 95), (255, 95), (255, 127), (247, 135),
                 (205, 135)]),
        polygon([(132, 79), (253, 79), (255, 81), (255, 95),
                 (130, 95), (130, 81)]),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),          # No capture button
    ]


def dualsense_edge_shapes():
    return [
        ellipse((1159, 426, 1247, 514)),
        ellipse((1258, 325, 1346, 413)),
        ellipse((1058, 325, 1146, 413)),
        ellipse((1159, 223, 1247, 311)),
        polygon([(255, 101), (284, 91), (390, 92), (426, 106),
                 (449, 132), (449, 189), (440, 192), (258, 192)]),
        polygon([(1109, 101), (1138, 91), (1244, 92), (1280, 106),
                 (1303, 132), (1303, 189), (1294, 192), (1112, 192)]),
        polygon([(257, 97), (261, 45), (279, 25), (368, 25),
                 (405, 40), (430, 68), (440, 97)]),
        polygon([(1118, 97), (1128, 68), (1153, 40), (1190, 25),
                 (1279, 25), (1297, 45), (1301, 97)]),
        rounded((440, 204, 477, 263), 16),
        rounded((1081, 204, 1118, 263), 16),
        polygon([(731, 512), (760, 523), (756, 568), (738, 568), (766, 583), (803, 574), (819, 584), (776, 604), (731, 583)]),
        rounded((744, 636, 814, 659), 10),
        ellipse((510, 506, 620, 616)),
        ellipse((939, 506, 1049, 616)),
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
        ellipse((585, 206, 640, 261)),
        ellipse((886, 206, 941, 261)),
        ellipse((812, 296, 875, 358)),
        ellipse((0, 0, 0, 0)),          # No mute button
        ellipse((350, 270, 470, 390)),
        ellipse((872, 438, 1012, 578)),
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
        rounded((654, 300, 706, 352), 7),  # Capture
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

    for source_atlas, target_atlas in (
        ("DualSense-Config_Highlights.png", "DualSense-Stick_Highlights.png"),
        ("DualShock4-Config_Highlights.png", "DualShock4-Stick_Highlights.png"),
        ("DualSenseEdge-Config_Highlights.png", "DualSenseEdge-Stick_Highlights.png"),
        ("Switch2Pro-Config_Highlights.png", "Switch2Pro-Stick_Highlights.png"),
    ):
        render_stick_atlas(resources, source_atlas, target_atlas)

    for source_atlas, target_atlas in (
        ("DualShock4-Config_Highlights.png", "DualShock4-Mapping_Highlights.png"),
        ("DualShock4-Stick_Highlights.png", "DualShock4-Mapping-Stick_Highlights.png"),
        ("DualSenseEdge-Config_Highlights.png", "DualSenseEdge-Mapping_Highlights.png"),
        ("DualSenseEdge-Stick_Highlights.png", "DualSenseEdge-Mapping-Stick_Highlights.png"),
        ("Switch2Pro-Config_Highlights.png", "Switch2Pro-Mapping_Highlights.png"),
        ("Switch2Pro-Stick_Highlights.png", "Switch2Pro-Mapping-Stick_Highlights.png"),
    ):
        render_mapping_atlas(resources, source_atlas, target_atlas)

    render_dualshock4_mapping_lightbar(resources)

    render_xbox_action_atlas(resources)


if __name__ == "__main__":
    main()
