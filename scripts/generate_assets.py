"""Generate icons for the Windows client, the installer and the Home Assistant brand images.

Run from the repository root:  python scripts/generate_assets.py   (requires Pillow)
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "client" / "installer" / "assets"
BRAND = ROOT / "custom_components" / "pdc_homeconnect" / "brand"

TOP = (59, 130, 246)
BOTTOM = (29, 78, 216)
SCREEN = (30, 64, 175)
STATUS = {
    "connected": (34, 197, 94),
    "disconnected": (156, 163, 175),
    "pairing": (245, 158, 11),
}


def gradient(size: int, top: tuple[int, int, int], bottom: tuple[int, int, int]) -> Image.Image:
    image = Image.new("RGB", (size, size))
    draw = ImageDraw.Draw(image)
    for y in range(size):
        t = y / (size - 1)
        color = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        draw.line([(0, y), (size, y)], fill=color)
    return image


def app_icon(size: int = 1024, grayscale: bool = False) -> Image.Image:
    """Blue rounded square with a white house that contains a screen."""
    s = size / 1024
    top, bottom = (TOP, BOTTOM) if not grayscale else ((150, 155, 165), (110, 115, 125))
    background = gradient(size, top, bottom).convert("RGBA")
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=int(230 * s), fill=255)
    icon = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    icon.paste(background, (0, 0), mask)

    draw = ImageDraw.Draw(icon)
    house = [(512, 190), (840, 470), (748, 470), (748, 820), (276, 820), (276, 470), (184, 470)]
    draw.polygon([(x * s, y * s) for x, y in house], fill=(255, 255, 255, 255))
    screen = SCREEN if not grayscale else (100, 105, 115)
    draw.rounded_rectangle([370 * s, 500 * s, 654 * s, 690 * s], radius=int(26 * s), fill=screen)
    draw.rectangle([490 * s, 690 * s, 534 * s, 740 * s], fill=screen)
    draw.rounded_rectangle([420 * s, 732 * s, 604 * s, 768 * s], radius=int(12 * s), fill=screen)
    # Small signal arcs on the screen.
    for radius, width in ((70, 18), (38, 18)):
        draw.arc(
            [(512 - radius) * s, (640 - radius) * s, (512 + radius) * s, (640 + radius) * s],
            start=215, end=325, fill=(255, 255, 255, 255), width=max(1, int(width * s)),
        )
    draw.ellipse([500 * s, 628 * s, 524 * s, 652 * s], fill=(255, 255, 255, 255))
    return icon


def tray_icon(status: str, size: int = 256) -> Image.Image:
    icon = app_icon(size, grayscale=status == "disconnected")
    draw = ImageDraw.Draw(icon)
    radius = size * 0.2
    cx, cy = size - radius - 2, size - radius - 2
    draw.ellipse([cx - radius - size * 0.04, cy - radius - size * 0.04, cx + radius + size * 0.04, cy + radius + size * 0.04], fill=(255, 255, 255, 255))
    draw.ellipse([cx - radius, cy - radius, cx + radius, cy + radius], fill=STATUS[status] + (255,))
    return icon


def save_ico(image: Image.Image, path: Path, sizes: list[int]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, format="ICO", sizes=[(s, s) for s in sizes])


def logo(height: int, dark: bool) -> Image.Image:
    icon = app_icon(1024).resize((height, height), Image.LANCZOS)
    font = ImageFont.truetype("/usr/share/fonts/opentype/inter/Inter-SemiBold.otf", int(height * 0.42))
    text = "PDC HomeConnect"
    bbox = font.getbbox(text)
    width = height + int(height * 0.25) + (bbox[2] - bbox[0]) + int(height * 0.1)
    image = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    image.paste(icon, (0, 0), icon)
    draw = ImageDraw.Draw(image)
    color = (255, 255, 255, 255) if dark else (17, 24, 39, 255)
    draw.text((height + int(height * 0.25), (height - (bbox[3] + bbox[1])) // 2), text, font=font, fill=color)
    return image


def main() -> None:
    big = app_icon(1024)
    save_ico(big, ASSETS / "app.ico", [16, 20, 24, 32, 40, 48, 64, 128, 256])
    big.resize((256, 256), Image.LANCZOS).save(ASSETS / "app-256.png")
    for status in STATUS:
        save_ico(tray_icon(status), ASSETS / f"tray-{status}.ico", [16, 20, 24, 32, 40, 48, 64])

    BRAND.mkdir(parents=True, exist_ok=True)
    big.resize((256, 256), Image.LANCZOS).save(BRAND / "icon.png")
    big.resize((512, 512), Image.LANCZOS).save(BRAND / "icon@2x.png")
    logo(128, dark=False).save(BRAND / "logo.png")
    logo(256, dark=False).save(BRAND / "logo@2x.png")
    logo(128, dark=True).save(BRAND / "dark_logo.png")
    logo(256, dark=True).save(BRAND / "dark_logo@2x.png")
    print("Generated icons and brand images.")


if __name__ == "__main__":
    main()
