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
from collections import deque
import argparse
from typing import Callable

from PIL import Image, ImageChops, ImageDraw, ImageFilter


CANVAS_WIDTH = 440
CANVAS_HEIGHT = 220
SUPERSAMPLE = 4
ACCENT = (47, 128, 237, 154)
MAPPING_FILL_ALPHA = 88
MAPPING_EDGE_ALPHA = 224
MAPPING_PRIMARY_CONTROL_FILL_ALPHA = 124


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


def sculpt_stick_directions(surface: Image.Image, center_x: float,
                            center_y: float,
                            polished: bool = False) -> list[Image.Image]:
    """Divide the exact thumb cap into four full directional surfaces.

    A thin decorative arc looked like a row of dots once a large source
    raster was reduced to the 440 px controller canvas. Each direction now
    paints one complete, solid quarter of the cap and follows its actual outer
    curve. WPF independently removes the center from the pointer target so
    L3/R3 remain easy to click without making the visible highlight tiny.
    """

    bounds = surface.getbbox()
    if bounds is None:
        return [Image.new("L", surface.size, 0) for _ in range(4)]
    if not polished:
        return split_stick_ring(surface, center_x, center_y)

    # Author the four wedges above final resolution. Every wedge shares the
    # same exact center point, while multiplication by the painted thumb-cap
    # supplies its curved outside edge. This avoids the one-pixel, off-center
    # apex and stair-stepped diagonals produced by classifying final pixels.
    scale = SUPERSAMPLE
    large_size = (surface.width * scale, surface.height * scale)
    large_surface = surface.resize(large_size, Image.Resampling.LANCZOS)
    left, top, right, bottom = bounds
    cx = center_x * scale
    cy = center_y * scale
    l = left * scale
    t = top * scale
    r = right * scale
    b = bottom * scale
    polygons = (
        ((cx, cy), (l, t), (r, t)),
        ((cx, cy), (r, t), (r, b)),
        ((cx, cy), (r, b), (l, b)),
        ((cx, cy), (l, b), (l, t)),
    )
    sectors: list[Image.Image] = []
    for points in polygons:
        clip = Image.new("L", large_size, 0)
        ImageDraw.Draw(clip).polygon(points, fill=255)
        clipped = ImageChops.multiply(large_surface, clip).resize(
            surface.size, Image.Resampling.LANCZOS)
        sectors.append(ImageChops.multiply(clipped, surface))

    # Lanczos gives the diagonals a clean edge but also produces a one-pixel
    # shared fringe. Award every final pixel to its strongest wedge. The four
    # wedges still meet at the same authored center, but their pointer targets
    # cannot overlap or flicker between neighbouring directions.
    sector_pixels = [sector.load() for sector in sectors]
    for y in range(surface.height):
        for x in range(surface.width):
            values = [pixels[x, y] for pixels in sector_pixels]
            winner = max(range(len(values)), key=values.__getitem__)
            if values[winner] == 0:
                continue
            for index, pixels in enumerate(sector_pixels):
                if index != winner:
                    pixels[x, y] = 0
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


@dataclass(frozen=True)
class SourceSurface:
    """Select one painted control surface from the source raster.

    The box prevents a neighbouring control from participating, while the
    luminance/chroma selector follows the actual antialiased pixels instead
    of replacing the artwork with a rounded rectangle. The selected connected
    component is hole-filled so labels and glyphs remain part of the button.
    """

    box: tuple[int, int, int, int]
    seed: tuple[int, int]
    minimum_luma: int = 0
    maximum_luma: int = 255
    minimum_chroma: int = 0
    maximum_chroma: int = 255
    clip: tuple[tuple[float, float], ...] | None = None
    connected: bool = True


def dark_surface(box: tuple[int, int, int, int], seed: tuple[int, int],
                 maximum_luma: int,
                 clip: list[tuple[float, float]] | None = None) -> SourceSurface:
    return SourceSurface(box, seed, maximum_luma=maximum_luma,
                         clip=tuple(clip) if clip else None)


def _nearest_selected(mask: Image.Image, point: tuple[int, int]) -> tuple[int, int] | None:
    pixels = mask.load()
    width, height = mask.size
    px = min(max(point[0], 0), width - 1)
    py = min(max(point[1], 0), height - 1)
    if pixels[px, py]:
        return px, py
    for radius in range(1, max(width, height)):
        for y in range(max(0, py - radius), min(height, py + radius + 1)):
            for x in (px - radius, px + radius):
                if 0 <= x < width and pixels[x, y]:
                    return x, y
        for x in range(max(0, px - radius), min(width, px + radius + 1)):
            for y in (py - radius, py + radius):
                if 0 <= y < height and pixels[x, y]:
                    return x, y
    return None


def _connected_component(mask: Image.Image,
                         seed: tuple[int, int]) -> Image.Image:
    """Return the 8-connected component containing seed and fill its holes."""

    width, height = mask.size
    source = mask.load()
    start = _nearest_selected(mask, seed)
    if start is None:
        return Image.new("L", mask.size, 0)

    selected = bytearray(width * height)
    queue: deque[tuple[int, int]] = deque([start])
    selected[start[1] * width + start[0]] = 1
    while queue:
        x, y = queue.popleft()
        for next_y in range(max(0, y - 1), min(height, y + 2)):
            for next_x in range(max(0, x - 1), min(width, x + 2)):
                index = next_y * width + next_x
                if not selected[index] and source[next_x, next_y]:
                    selected[index] = 1
                    queue.append((next_x, next_y))

    component = Image.frombytes("L", (width, height),
                                bytes(255 if value else 0
                                      for value in selected))
    # Closing bridges antialiased label strokes without expanding beyond the
    # painted outside edge. Hole filling then restores the complete cap.
    component = component.filter(ImageFilter.MaxFilter(3)).filter(
        ImageFilter.MinFilter(3))

    outside = bytearray(width * height)
    outside_queue: deque[tuple[int, int]] = deque()
    component_pixels = component.load()
    for x in range(width):
        for y in (0, height - 1):
            index = y * width + x
            if not component_pixels[x, y] and not outside[index]:
                outside[index] = 1
                outside_queue.append((x, y))
    for y in range(height):
        for x in (0, width - 1):
            index = y * width + x
            if not component_pixels[x, y] and not outside[index]:
                outside[index] = 1
                outside_queue.append((x, y))
    while outside_queue:
        x, y = outside_queue.popleft()
        for next_y in range(max(0, y - 1), min(height, y + 2)):
            for next_x in range(max(0, x - 1), min(width, x + 2)):
                index = next_y * width + next_x
                if (not outside[index] and
                        not component_pixels[next_x, next_y]):
                    outside[index] = 1
                    outside_queue.append((next_x, next_y))

    filled = Image.new("L", (width, height), 0)
    filled.putdata([255 if component_pixels[index % width,
                    index // width] or not outside[index] else 0
                    for index in range(width * height)])
    return filled


def source_surface_mask(source: Image.Image,
                        surface: SourceSurface) -> Image.Image:
    left, top, right, bottom = surface.box
    crop = source.crop((left, top, right, bottom)).convert("RGBA")
    selected = Image.new("L", crop.size, 0)
    output = selected.load()
    for y in range(crop.height):
        for x in range(crop.width):
            red, green, blue, alpha = crop.getpixel((x, y))
            luma = (red * 299 + green * 587 + blue * 114) // 1000
            chroma = max(red, green, blue) - min(red, green, blue)
            if (alpha >= 16 and surface.minimum_luma <= luma <=
                    surface.maximum_luma and surface.minimum_chroma <=
                    chroma <= surface.maximum_chroma):
                output[x, y] = 255

    local_seed = (surface.seed[0] - left, surface.seed[1] - top)
    if surface.connected:
        selected = _connected_component(selected, local_seed)
    result = Image.new("L", source.size, 0)
    result.paste(selected, (left, top))
    if surface.clip:
        clip = Image.new("L", source.size, 0)
        ImageDraw.Draw(clip).polygon(surface.clip, fill=255)
        result = ImageChops.multiply(result, clip)
    return result


def ellipse(box: tuple[float, float, float, float]) -> Callable[[ImageDraw.ImageDraw], None]:
    return lambda draw: draw.ellipse(box, fill=255)


def rounded(box: tuple[float, float, float, float], radius: float) -> Callable[[ImageDraw.ImageDraw], None]:
    return lambda draw: draw.rounded_rectangle(box, radius=radius, fill=255)


def polygon(points: list[tuple[float, float]]) -> Callable[[ImageDraw.ImageDraw], None]:
    return lambda draw: draw.polygon(points, fill=255)


def scale_shape(
    shape: Callable[[ImageDraw.ImageDraw], None] | SourceSurface,
    artwork: Artwork, source: Image.Image
) -> Callable[[ImageDraw.ImageDraw], None]:
    """Draw source-pixel geometry into a supersampled 440 px canvas."""

    def draw_scaled(target: ImageDraw.ImageDraw) -> None:
        # Geometry is authored in source pixels. Draw it there at a high
        # resolution, then scale it using the same transform as the base art.
        if isinstance(shape, SourceSurface):
            exact = source_surface_mask(source, shape)
            source_mask = exact.resize(
                (artwork.source_width * SUPERSAMPLE,
                 artwork.source_height * SUPERSAMPLE),
                Image.Resampling.LANCZOS)
        else:
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

        if not isinstance(shape, SourceSurface):
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


def separate_trigger_pairs(masks: list[Image.Image]) -> None:
    """Make each visible shoulder/trigger pixel belong to one control only.

    L1/R1 are the front surfaces in every front-facing controller render.
    They therefore own every shared pixel; L2/R2 retain only the rounded rear
    surface that is actually visible above them. A midpoint split clipped the
    shoulder itself and left triangular corners on the rear trigger.
    """

    for shoulder_index, trigger_index in ((4, 6), (5, 7)):
        if trigger_index >= len(masks):
            continue
        overlap = ImageChops.multiply(masks[shoulder_index],
                                      masks[trigger_index])
        overlap_bounds = overlap.getbbox()
        if overlap_bounds is None:
            continue
        trigger_pixels = masks[trigger_index].load()
        for y in range(overlap_bounds[1], overlap_bounds[3]):
            for x in range(overlap_bounds[0], overlap_bounds[2]):
                if overlap.getpixel((x, y)):
                    trigger_pixels[x, y] = 0


def render_atlas(
    resources: Path,
    artwork: Artwork,
    source_shapes: list[Callable[[ImageDraw.ImageDraw], None]],
) -> None:
    source = Image.open(resources / artwork.source).convert("RGBA")
    source_alpha = source.getchannel("A")
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

    masks: list[Image.Image] = []
    for source_shape in source_shapes:
        large_mask = Image.new(
            "L", (CANVAS_WIDTH * SUPERSAMPLE, CANVAS_HEIGHT * SUPERSAMPLE), 0
        )
        scale_shape(source_shape, artwork, source)(ImageDraw.Draw(large_mask))
        mask = large_mask.resize(
            (CANVAS_WIDTH, CANVAS_HEIGHT), Image.Resampling.LANCZOS
        )
        mask = ImageChops.multiply(mask, artwork_clip)
        masks.append(mask)

    # Shoulder and trigger faces overlap in the source crops on several
    # controller renders. A physical pixel belongs to only one control: split
    # any shared fringe at its vertical midpoint so adjacent hover targets and
    # highlights can never paint over one another.
    separate_trigger_pairs(masks)

    # On this raster the black LT/RT-to-bumper seam is less than one rendered
    # pixel tall. The generic front-surface ownership rule rounds that whole
    # antialiased row into LB/RB and visibly clips the trigger bottoms. Give
    # exactly one rendered row back to LT/RT, then remove the identical pixels
    # from the bumper masks so hover and click ownership remains exclusive.
    for shoulder_index, trigger_index in ((4, 6), (5, 7)):
        shifted = Image.new("L", masks[trigger_index].size, 0)
        shifted.paste(masks[trigger_index], (0, 1))
        masks[trigger_index] = ImageChops.lighter(
            masks[trigger_index], shifted)
        masks[shoulder_index] = ImageChops.subtract(
            masks[shoulder_index], masks[trigger_index])

    frames: list[Image.Image] = []
    for mask in masks:
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


def render_stick_atlas(resources: Path, source_atlas: str,
                       target_atlas: str, polished: bool = False) -> None:
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

        sector_masks = sculpt_stick_directions(
            surface, center_x, center_y, polished=polished)

        # Paint the complete cap for L3/R3, but keep the click target limited
        # to its center in WPF. Direction frames remain four disjoint sectors
        # around that center so they never steal the press target.
        for mask in (surface, *sector_masks):
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
        # Shoulder/trigger faces and stick presses must read as the complete
        # physical control rather than a floating outline. Preserve the
        # subtler treatment for face buttons and directional wedges.
        is_stick_press = ("Stick_Highlights" in source_atlas and
                          frame_index in (0, 5))
        fill_alpha = (MAPPING_PRIMARY_CONTROL_FILL_ALPHA
                      if frame_index in (4, 5, 6, 7) or is_stick_press
                      else MAPPING_FILL_ALPHA)
        fill = normalized.point(
            lambda value: round(value * fill_alpha / 255))
        edge = inner_edge.point(
            lambda value: round(value * MAPPING_EDGE_ALPHA / 255))
        alpha = ImageChops.lighter(fill, edge)
        styled = Image.new("RGBA", (CANVAS_WIDTH, CANVAS_HEIGHT),
                           ACCENT[:3] + (0,))
        styled.putalpha(alpha)
        output.alpha_composite(styled, (0, frame_index * CANVAS_HEIGHT))

    output.save(resources / target_atlas, optimize=True)
    print(f"generated {target_atlas}: {output.width} x {output.height}")


def render_preview(resources: Path, artwork: Artwork, atlas_name: str,
                   target: Path, labels: list[str]) -> None:
    """Render a contact sheet used to visually audit every generated mask."""

    source = Image.open(resources / artwork.source).convert("RGBA")
    rendered = source.resize((artwork.rendered_width, artwork.rendered_height),
                             Image.Resampling.LANCZOS)
    base = Image.new("RGBA", (CANVAS_WIDTH, CANVAS_HEIGHT),
                     (7, 17, 29, 255))
    base.alpha_composite(rendered,
                         ((CANVAS_WIDTH - artwork.rendered_width) // 2,
                          (CANVAS_HEIGHT - artwork.rendered_height) // 2))
    atlas = Image.open(resources / atlas_name).convert("RGBA")
    frame_count = atlas.height // CANVAS_HEIGHT
    columns = 3
    label_height = 24
    rows = (frame_count + columns - 1) // columns
    sheet = Image.new("RGBA", (CANVAS_WIDTH * columns,
                               (CANVAS_HEIGHT + label_height) * rows),
                      (7, 17, 29, 255))
    for index in range(frame_count):
        preview = base.copy()
        frame = atlas.crop((0, index * CANVAS_HEIGHT, CANVAS_WIDTH,
                            (index + 1) * CANVAS_HEIGHT))
        preview.alpha_composite(frame)
        column = index % columns
        row = index // columns
        left = column * CANVAS_WIDTH
        top = row * (CANVAS_HEIGHT + label_height)
        sheet.alpha_composite(preview, (left, top))
        label = labels[index] if index < len(labels) else f"Frame {index}"
        ImageDraw.Draw(sheet).text((left + 8, top + CANVAS_HEIGHT + 4),
                                   f"{index}: {label}", fill=(235, 242, 255, 255))
    target.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(target, optimize=True)


CONTROL_LABELS = [
    "Cross / B", "Circle / A", "Square / Y", "Triangle / X",
    "L1", "R1", "L2", "R2", "Share / Minus", "Options / Plus",
    "PS / Home", "Mute", "Left stick", "Right stick", "D-pad up",
    "D-pad right", "D-pad down", "D-pad left", "Touch left",
    "Touch multi", "Touch right", "Touch upper", "Fn left", "Fn right",
    "Back paddle left", "Back paddle right", "Capture",
]

STICK_LABELS = [
    "L3", "Left up", "Left right", "Left down", "Left left",
    "R3", "Right up", "Right right", "Right down", "Right left",
]


def render_xbox_preview(resources: Path, target: Path) -> None:
    source = Image.open(resources / "360 map.png").convert("RGBA")
    rendered_height = round(source.height * 630 / source.width)
    rendered = source.resize((630, rendered_height), Image.Resampling.LANCZOS)
    base = Image.new("RGBA", (630, 247), (7, 17, 29, 255))
    base.alpha_composite(rendered, (0, (247 - rendered_height) // 2))
    atlas = Image.open(resources / "Xbox360-Action_Highlights.png").convert("RGBA")
    labels = CONTROL_LABELS[:11] + STICK_LABELS + CONTROL_LABELS[14:18]
    columns = 2
    label_height = 24
    rows = (len(labels) + columns - 1) // columns
    sheet = Image.new("RGBA", (630 * columns, (247 + label_height) * rows),
                      (7, 17, 29, 255))
    for index, label in enumerate(labels):
        preview = base.copy()
        preview.alpha_composite(atlas.crop((0, index * 247, 630,
                                            (index + 1) * 247)))
        left = (index % columns) * 630
        top = (index // columns) * (247 + label_height)
        sheet.alpha_composite(preview, (left, top))
        ImageDraw.Draw(sheet).text((left + 8, top + 251),
                                   f"{index}: {label}",
                                   fill=(235, 242, 255, 255))
    target.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(target, optimize=True)


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


def render_dualsense_edge_mapping_lightbar(resources: Path) -> None:
    """Extract the complete Edge light pipe around all three touchpad sides."""

    artwork = Artwork("DualSense Edge Controller.png",
                      "DualSenseEdge-Mapping-Lightbar.png", 1558, 1009)
    source = Image.open(resources / artwork.source).convert("RGBA")
    mask = Image.new("L", source.size, 0)
    output = mask.load()
    for y in range(source.height):
        for x in range(source.width):
            red, green, blue, alpha = source.getpixel((x, y))
            # The light pipe is the only saturated blue painted in this
            # front render. Preserve its antialiased outside edge exactly.
            if (alpha >= 8 and blue >= 70 and blue > red * 1.45 and
                    blue > green * 1.12):
                output[x, y] = alpha

    rendered = mask.resize((artwork.rendered_width,
                            artwork.rendered_height),
                           Image.Resampling.LANCZOS)
    canvas = Image.new("L", (CANVAS_WIDTH, CANVAS_HEIGHT), 0)
    canvas.paste(rendered,
                 ((CANVAS_WIDTH - artwork.rendered_width) // 2,
                  (CANVAS_HEIGHT - artwork.rendered_height) // 2))
    result = Image.new("RGBA", canvas.size, (255, 255, 255, 0))
    result.putalpha(canvas)
    target_name = artwork.atlas
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
    source = Image.open(resources / "360 map.png").convert("RGBA")
    source_alpha = source.getchannel("A")
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

    def exact(surface: SourceSurface) -> Image.Image:
        source_mask = source_surface_mask(source, surface)
        rendered = source_mask.resize((width, rendered_height),
                                      Image.Resampling.LANCZOS)
        canvas = Image.new("L", (width, height), 0)
        canvas.paste(rendered, (0, round(offset_y)))
        return canvas

    masks = [
        exact(SourceSurface((885, 175, 980, 260), (934, 218),
                            minimum_chroma=80, connected=False)),
        exact(SourceSurface((970, 112, 1068, 202), (1018, 156),
                            minimum_chroma=80, connected=False)),
        exact(SourceSurface((818, 113, 913, 204), (865, 158),
                            minimum_chroma=80, connected=False)),
        exact(SourceSurface((894, 54, 990, 144), (942, 99),
                            minimum_chroma=80, connected=False)),
        # Each bumper is drawn as two white faces separated by its black
        # contour. Union both connected faces so hover paints the complete LB
        # or RB control instead of only its upper lip. Keeping the components
        # separate during extraction prevents the grey rear shell from being
        # pulled into the mask.
        ImageChops.lighter(
            exact(SourceSurface((0, 60, 215, 155), (100, 100),
                                minimum_luma=220, maximum_luma=255)),
            exact(SourceSurface((0, 60, 215, 155), (100, 135),
                                minimum_luma=220, maximum_luma=255))),
        ImageChops.lighter(
            exact(SourceSurface((1108, 60, 1323, 155), (1220, 100),
                                minimum_luma=220, maximum_luma=255)),
            exact(SourceSurface((1108, 60, 1323, 155), (1220, 135),
                                minimum_luma=220, maximum_luma=255))),
        exact(SourceSurface((100, 0, 185, 96), (145, 40),
                            minimum_luma=205, maximum_luma=255)),
        exact(SourceSurface((1138, 0, 1225, 96), (1180, 40),
                            minimum_luma=205, maximum_luma=255)),
        exact(SourceSurface((528, 130, 600, 194), (562, 162),
                            minimum_luma=220, maximum_luma=255)),
        exact(SourceSurface((727, 130, 800, 194), (763, 162),
                            minimum_luma=220, maximum_luma=255)),
        make_mask("ellipse", (607, 113, 718, 207)),
    ]

    def stick_masks(surface: Image.Image) -> list[Image.Image]:
        bounds = surface.getbbox()
        assert bounds is not None
        left, top, right, bottom = bounds
        center_x = (left + right) / 2.0
        center_y = (top + bottom) / 2.0
        return [surface, *sculpt_stick_directions(surface, center_x, center_y)]

    # Match the movable thumb-cap, not the stationary socket around it.
    # L3/R3 then occupy the small center while directions divide its rim.
    masks.extend(stick_masks(exact(SourceSurface(
        (326, 132, 448, 248), (386, 190),
        minimum_luma=125, maximum_luma=195))))
    masks.extend(stick_masks(exact(SourceSurface(
        (731, 257, 856, 385), (793, 322),
        minimum_luma=125, maximum_luma=195))))
    dpad = exact(SourceSurface((430, 232, 620, 391), (522, 310),
                               minimum_luma=115, maximum_luma=200))
    dpad_bounds = dpad.getbbox()
    assert dpad_bounds is not None
    dpad_center_x = (dpad_bounds[0] + dpad_bounds[2]) / 2.0
    dpad_center_y = (dpad_bounds[1] + dpad_bounds[3]) / 2.0
    masks.extend(split_stick_ring(dpad, dpad_center_x, dpad_center_y))

    separate_trigger_pairs(masks)

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
        # Select the actual painted caps. These masks retain the source
        # raster's curved outline and never spill into its pale bezel.
        dark_surface((292, 132, 324, 164), (307, 148), 180),  # Cross
        dark_surface((319, 110, 351, 142), (335, 126), 180),  # Circle
        dark_surface((264, 110, 296, 142), (280, 126), 180),  # Square
        dark_surface((292, 89, 324, 121), (307, 105), 180),   # Triangle
        SourceSurface((48, 41, 105, 69), (64, 55),
                      minimum_luma=42, maximum_luma=170),      # L1
        SourceSurface((279, 41, 336, 69), (320, 55),
                      minimum_luma=42, maximum_luma=170),      # R1
        SourceSurface((57, 17, 99, 50), (68, 34),
                      minimum_luma=34, maximum_luma=155),      # L2
        SourceSurface((285, 17, 327, 50), (317, 34),
                      minimum_luma=34, maximum_luma=155),      # R2
        dark_surface((106, 77, 125, 108), (115, 92), 185),    # Share
        dark_surface((258, 77, 277, 108), (267, 92), 185),    # Options
        dark_surface((178, 155, 206, 180), (192, 167), 205),  # PS
        ellipse((0, 0, 0, 0)),          # No mute button
        ellipse((114, 163, 152, 201)),                         # Left cap
        ellipse((234, 163, 272, 201)),                         # Right cap
        dark_surface((61, 96, 93, 132), (76, 112), 175),      # D-pad up
        dark_surface((82, 109, 116, 144), (99, 126), 175),    # right
        dark_surface((61, 128, 93, 164), (76, 146), 175),     # down
        dark_surface((41, 109, 76, 144), (58, 126), 175),     # left
        # Touch gestures share one bounded touch surface. Upper touch lives
        # inside the exact black pad (the lightbar remains independent).
        dark_surface((128, 76, 257, 137), (160, 111), 90,
                     [(128, 94), (181, 94), (181, 137), (128, 137)]),
        dark_surface((128, 76, 257, 137), (192, 111), 90,
                     [(180, 94), (205, 94), (205, 137), (180, 137)]),
        dark_surface((128, 76, 257, 137), (225, 111), 90,
                     [(204, 94), (257, 94), (257, 137), (204, 137)]),
        dark_surface((128, 76, 257, 137), (192, 84), 90,
                     [(128, 76), (257, 76), (257, 96), (128, 96)]),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),          # No capture button
    ]


def dualsense_edge_shapes():
    return [
        # Seed outside each white glyph so connected-component tracing owns
        # only the physical black cap. Hole filling retains the glyph while
        # the highlight stops at the button's real antialiased boundary.
        dark_surface((1140, 412, 1268, 538), (1204, 430), 135),
        dark_surface((1240, 309, 1368, 437), (1304, 330), 135),
        dark_surface((1040, 309, 1168, 437), (1104, 330), 135),
        dark_surface((1140, 206, 1268, 334), (1204, 225), 135),
        # Edge shoulders use their complete shaded control surfaces, matching
        # the polished DualSense masks instead of selecting only a dark strip.
        SourceSurface((248, 88, 458, 199), (300, 170),
                      minimum_luma=18, maximum_luma=125),
        SourceSurface((1100, 88, 1310, 199), (1255, 170),
                      minimum_luma=18, maximum_luma=125),
        SourceSurface((250, 18, 450, 112), (300, 50),
                      minimum_luma=18, maximum_luma=125),
        SourceSurface((1108, 18, 1308, 112), (1255, 50),
                      minimum_luma=18, maximum_luma=125),
        dark_surface((425, 188, 500, 282), (461, 234), 80),
        dark_surface((1058, 188, 1132, 282), (1096, 234), 80),
        SourceSurface((715, 500, 835, 615), (780, 560),
                      minimum_luma=55, maximum_luma=205,
                      connected=False),
        SourceSurface((730, 625, 830, 672), (780, 646),
                      minimum_luma=35, maximum_luma=155),
        ellipse((505, 511, 609, 615)),
        ellipse((948, 511, 1052, 615)),
        dark_surface((295, 242, 414, 363), (355, 298), 100),
        dark_surface((360, 304, 490, 424), (425, 365), 100),
        dark_surface((295, 374, 414, 493), (355, 433), 100),
        dark_surface((222, 304, 348, 424), (285, 365), 100),
        dark_surface((496, 156, 1062, 426), (600, 300), 80,
                     [(496, 220), (700, 220), (700, 426), (496, 426)]),
        dark_surface((496, 156, 1062, 426), (780, 300), 80,
                     [(699, 220), (859, 220), (859, 426), (699, 426)]),
        dark_surface((496, 156, 1062, 426), (960, 300), 80,
                     [(858, 220), (1062, 220), (1062, 426), (858, 426)]),
        dark_surface((496, 156, 1062, 426), (780, 185), 80,
                     [(496, 156), (1062, 156), (1062, 226), (496, 226)]),
        polygon([(522, 730), (604, 729), (600, 765), (584, 780),
                 (537, 777), (524, 760)]),
        polygon([(954, 729), (1036, 730), (1034, 760), (1020, 777),
                 (973, 780), (958, 765)]),
        ellipse((0, 0, 0, 0)),          # Rear paddles are not visible in this front raster
        ellipse((0, 0, 0, 0)),
        ellipse((0, 0, 0, 0)),          # No capture button
    ]


def switch2_pro_shapes():
    return [
        ellipse((1065, 366, 1143, 444)),  # B / Cross
        ellipse((1156, 286, 1234, 364)),  # A / Circle
        ellipse((971, 284, 1049, 362)),   # Y / Square
        ellipse((1065, 203, 1143, 281)),  # X / Triangle
        # The bumper and trigger overlap in this front render. Constrain each
        # bumper to its visible front cap and let the source component retain
        # the cap's real curved outline. This prevents L/R from swallowing ZL/
        # ZR through the antialiased seam or leaking into the controller shell.
        SourceSurface((274, 72, 512, 200), (390, 125),
                      minimum_luma=40, maximum_luma=205,
                      clip=((277, 139), (314, 122), (365, 107), (430, 94),
                            (466, 94), (490, 108), (501, 122), (501, 136),
                            (485, 148), (435, 156), (374, 169), (325, 184),
                            (283, 200), (274, 186)), connected=False),
        SourceSurface((1024, 72, 1264, 200), (1146, 125),
                      minimum_luma=40, maximum_luma=205,
                      clip=((1259, 139), (1222, 122), (1171, 107),
                            (1106, 94), (1070, 94), (1046, 108),
                            (1035, 122), (1035, 136), (1051, 148),
                            (1101, 156), (1162, 169), (1211, 184),
                            (1253, 200), (1262, 186)), connected=False),
        SourceSurface((286, 16, 490, 132), (390, 60),
                      minimum_luma=24, maximum_luma=220),
        SourceSurface((1048, 16, 1252, 132), (1146, 60),
                      minimum_luma=24, maximum_luma=220),
        ellipse((585, 206, 640, 261)),
        ellipse((886, 206, 941, 261)),
        ellipse((812, 296, 875, 358)),
        ellipse((0, 0, 0, 0)),          # No mute button
        ellipse((360, 280, 460, 380)),
        ellipse((890, 458, 990, 558)),
        SourceSurface((460, 400, 690, 620), (575, 455),
                      minimum_luma=45, maximum_luma=135,
                      clip=((460, 400), (690, 400), (610, 520), (540, 520))),
        SourceSurface((460, 400, 690, 620), (630, 510),
                      minimum_luma=45, maximum_luma=135,
                      clip=((575, 465), (690, 400), (690, 620), (575, 540))),
        SourceSurface((460, 400, 690, 620), (575, 575),
                      minimum_luma=45, maximum_luma=135,
                      clip=((540, 500), (610, 500), (690, 620), (460, 620))),
        SourceSurface((460, 400, 690, 620), (510, 510),
                      minimum_luma=45, maximum_luma=135,
                      clip=((460, 400), (575, 465), (575, 540), (460, 620))),
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
    parser = argparse.ArgumentParser()
    parser.add_argument("--preview-dir", type=Path,
                        help="Write visual QC contact sheets to this directory")
    args = parser.parse_args()
    resources = Path(__file__).resolve().parents[1] / "DS4Windows" / "Resources"
    jobs = [
        (Artwork("DualShock 4 Controller.png", "DualShock4-Config_Highlights.png", 384, 247), dualshock4_shapes()),
        (Artwork("DualSense Edge Controller.png", "DualSenseEdge-Config_Highlights.png", 1558, 1009), dualsense_edge_shapes()),
        (Artwork("Switch 2 Pro Controller.png", "Switch2Pro-Config_Highlights.png", 1536, 1024), switch2_pro_shapes()),
    ]
    for artwork, shapes in jobs:
        render_atlas(resources, artwork, shapes)

    for source_atlas, target_atlas, polished in (
        ("DualSense-Config_Highlights.png", "DualSense-Stick_Highlights.png", True),
        ("DualShock4-Config_Highlights.png", "DualShock4-Stick_Highlights.png", True),
        ("DualSenseEdge-Config_Highlights.png", "DualSenseEdge-Stick_Highlights.png", True),
        ("Switch2Pro-Config_Highlights.png", "Switch2Pro-Stick_Highlights.png", True),
    ):
        render_stick_atlas(resources, source_atlas, target_atlas,
                           polished=polished)

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
    render_dualsense_edge_mapping_lightbar(resources)

    render_xbox_action_atlas(resources)

    if args.preview_dir:
        dualsense_preview = Artwork(
            "DualSense Config.png", "DualSense-Config_Highlights.png",
            880, 440)
        render_preview(resources, dualsense_preview,
                       dualsense_preview.atlas,
                       args.preview_dir / "DualSense-Config_Highlights.png",
                       CONTROL_LABELS)
        render_preview(resources, dualsense_preview,
                       "DualSense-Stick_Highlights.png",
                       args.preview_dir / "DualSense-Stick_Highlights.png",
                       STICK_LABELS)
        for artwork, _ in jobs:
            render_preview(resources, artwork, artwork.atlas,
                           args.preview_dir / f"{Path(artwork.atlas).stem}.png",
                           CONTROL_LABELS)
            mapping_name = artwork.atlas.replace("-Config_", "-Mapping_")
            render_preview(resources, artwork, mapping_name,
                           args.preview_dir / f"{Path(mapping_name).stem}.png",
                           CONTROL_LABELS)
            stick_name = artwork.atlas.replace("Config_Highlights",
                                               "Stick_Highlights")
            render_preview(resources, artwork, stick_name,
                           args.preview_dir / f"{Path(stick_name).stem}.png",
                           STICK_LABELS)
        render_xbox_preview(resources,
                            args.preview_dir / "Xbox360-Action_Highlights.png")


if __name__ == "__main__":
    main()
