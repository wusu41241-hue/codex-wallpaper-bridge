"""Build the Windows icon family from the checked-in transparent master."""
import argparse
from pathlib import Path

from PIL import Image, ImageOps


ROOT = Path(__file__).resolve().parent
SIZES = [(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 128, 256)]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=ROOT / "app-icon-source.png")
    parser.add_argument("--output-dir", type=Path, default=ROOT)
    args = parser.parse_args()
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    with Image.open(args.source) as source:
        master = ImageOps.exif_transpose(source).convert("RGBA")
    if master.width != master.height:
        raise ValueError("The icon master must be square; do not crop its silhouette.")
    alpha = master.getchannel("A")
    if alpha.getextrema()[0] != 0 or alpha.getextrema()[1] != 255:
        raise ValueError("The icon needs transparent corners and an opaque interior.")
    icon = master.resize((1024, 1024), Image.Resampling.LANCZOS)
    icon.save(output / "app-icon.png", format="PNG")
    icon.save(output / "app.ico", format="ICO", sizes=SIZES)
    icon.resize((32, 32), Image.Resampling.LANCZOS).save(output / "app-icon-32.png")
    with Image.open(output / "app.ico") as saved:
        if saved.ico.sizes() != set(SIZES):
            raise ValueError("The ICO does not contain all required Windows sizes.")
        for size in SIZES:
            saved.ico.getimage(size).load()
    print(f"Built {output / 'app.ico'}: {len(SIZES)} sizes, transparent corners preserved.")


if __name__ == "__main__":
    main()
