"""Rendert das Logo mit Edge (headless) in alle Größen und baut daraus logo.ico.

Aufruf:  python branding/render.py
Ergebnis: branding/out/logo-<n>.png, branding/logo.ico, branding/logo-512.png
"""
import os, struct, subprocess, tempfile, pathlib

HERE = pathlib.Path(__file__).resolve().parent
OUT = HERE / "out"
OUT.mkdir(exist_ok=True)
EDGE = next(p for p in [
    os.path.expandvars(r"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
    os.path.expandvars(r"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"),
] if os.path.exists(p))

ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def render(svg: pathlib.Path, size: int, target: pathlib.Path) -> None:
    html = f"""<!doctype html><html><head><style>
      html,body{{margin:0;padding:0;background:transparent;overflow:hidden}}
      img{{display:block;width:{size}px;height:{size}px}}
    </style></head><body><img src="{svg.as_uri()}"></body></html>"""
    with tempfile.TemporaryDirectory() as tmp:
        page = pathlib.Path(tmp) / "p.html"
        page.write_text(html, encoding="utf-8")
        subprocess.run([
            EDGE, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
            "--default-background-color=00000000", f"--user-data-dir={tmp}\\ud",
            f"--window-size={size},{size}", f"--screenshot={target}", page.as_uri(),
        ], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=60)


def png_size(data: bytes) -> tuple[int, int]:
    return struct.unpack(">II", data[16:24])


def build_ico(pngs: list[pathlib.Path], target: pathlib.Path) -> None:
    images = [p.read_bytes() for p in pngs]
    header = struct.pack("<HHH", 0, 1, len(images))
    entries, offset = b"", 6 + 16 * len(images)
    for data in images:
        w, h = png_size(data)
        entries += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    target.write_bytes(header + entries + b"".join(images))


if __name__ == "__main__":
    pngs = []
    for n in ICON_SIZES:
        src = HERE / ("logo-small.svg" if n <= 32 else "logo.svg")
        dst = OUT / f"logo-{n}.png"
        render(src, n, dst)
        w, h = png_size(dst.read_bytes())
        assert (w, h) == (n, n), f"{dst}: {w}x{h}"
        pngs.append(dst)
        print("rendered", dst.name)
    render(HERE / "logo.svg", 512, HERE / "logo-512.png")
    build_ico(pngs, HERE / "logo.ico")
    print("wrote", HERE / "logo.ico")
