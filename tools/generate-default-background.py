"""Generate the project's original abstract default wallpaper. Requires Pillow."""
from pathlib import Path
from math import exp, hypot
from PIL import Image, ImageDraw


def main():
    width, height = 1600, 900
    image = Image.new("RGB", (width, height))
    pixels = image.load()
    for y in range(height):
        for x in range(width):
            u, v = x / width, y / height
            teal = exp(-((u - 0.79) ** 2 / 0.12 + (v - 0.24) ** 2 / 0.17))
            violet = exp(-((u - 0.63) ** 2 / 0.23 + (v - 0.84) ** 2 / 0.12))
            pixels[x, y] = (int(13 + 28 * violet + 6 * teal), int(19 + 61 * teal + 9 * violet), int(35 + 55 * teal + 51 * violet))
    overlay = Image.new("RGBA", image.size)
    draw = ImageDraw.Draw(overlay)
    draw.ellipse((800, -250, 1760, 710), fill=(104, 221, 214, 8), outline=(174, 251, 237, 31), width=2)
    draw.ellipse((960, -80, 1590, 550), outline=(174, 251, 237, 23), width=1)
    draw.polygon(((820, 900), (1600, 80), (1600, 900)), fill=(125, 137, 253, 16))
    draw.line(((0, 805), (1600, 95)), fill=(171, 223, 245, 14), width=2)
    image = Image.alpha_composite(image.convert("RGBA"), overlay).convert("RGB")
    output = Path(__file__).resolve().parents[1] / "windows" / "assets" / "default-background.png"
    output.parent.mkdir(parents=True, exist_ok=True)
    image.save(output, optimize=True)
    print(output)


if __name__ == "__main__":
    main()
