"""Generates the in-house Formula 1 card thumbnails (no external artwork, no dependencies beyond the standard library).

Run: python assets/formula1/generate.py  ->  writes the four 256x256 PNGs next to this file.
The images are simple geometric drawings made for TSQ Bot (AGPL-3.0-only repository licence); they contain no Formula 1
logos, trademarks or promotional art.
"""
import math
import os
import struct
import zlib

SIZE = 256
SCALE = 4  # supersampling for smooth edges
N = SIZE * SCALE


def canvas(color):
    return [[color for _ in range(N)] for _ in range(N)]


def blend(dst, src, alpha):
    return tuple(int(d + (s - d) * alpha) for d, s in zip(dst, src))


def fill(img, inside, color, alpha=1.0):
    for y in range(N):
        row = img[y]
        for x in range(N):
            if inside(x / SCALE, y / SCALE):
                row[x] = blend(row[x], color, alpha) if alpha < 1 else color


def rect(x0, y0, x1, y1, r=0):
    def inside(x, y):
        if not (x0 <= x <= x1 and y0 <= y <= y1):
            return False
        cx = min(max(x, x0 + r), x1 - r)
        cy = min(max(y, y0 + r), y1 - r)
        return (x - cx) ** 2 + (y - cy) ** 2 <= r * r
    return inside


def circle(cx, cy, r):
    return lambda x, y: (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def polygon(points):
    def inside(x, y):
        c = False
        j = len(points) - 1
        for i in range(len(points)):
            xi, yi = points[i]
            xj, yj = points[j]
            if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
                c = not c
            j = i
        return c
    return inside


def save(img, name):
    rows = []
    for y in range(SIZE):
        raw = bytearray([0])
        for x in range(SIZE):
            acc = [0, 0, 0]
            for dy in range(SCALE):
                line = img[y * SCALE + dy]
                for dx in range(SCALE):
                    p = line[x * SCALE + dx]
                    acc[0] += p[0]
                    acc[1] += p[1]
                    acc[2] += p[2]
            n = SCALE * SCALE
            raw += bytes((acc[0] // n, acc[1] // n, acc[2] // n))
        rows.append(bytes(raw))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(b"".join(rows), 9)) + chunk(b"IEND", b"")
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), name), "wb") as f:
        f.write(png)


BG = (21, 21, 30)


def start_lights():
    img = canvas(BG)
    fill(img, rect(18, 70, 238, 186, 18), (8, 8, 12))           # gantry
    for i in range(5):
        cx = 50 + i * 39
        fill(img, circle(cx, 128, 20), (0, 90, 30), 0.6)         # glow
        fill(img, circle(cx, 128, 15), (40, 220, 90))            # green light
        fill(img, circle(cx - 5, 122, 4), (190, 255, 210))       # highlight
    fill(img, rect(122, 186, 134, 240), (8, 8, 12))              # mount
    save(img, "start-lights.png")


def safety_car():
    img = canvas((250, 190, 20))
    body = polygon([(24, 170), (40, 140), (92, 128), (120, 100), (176, 100), (206, 128), (234, 140), (238, 170)])
    fill(img, body, (30, 30, 36))
    fill(img, polygon([(126, 108), (172, 108), (194, 128), (106, 128)]), (120, 170, 210))   # windows
    fill(img, rect(122, 86, 178, 98, 4), (255, 120, 0))                                     # light bar
    fill(img, rect(134, 86, 146, 98, 2), (255, 220, 120))
    fill(img, rect(154, 86, 166, 98, 2), (255, 220, 120))
    for cx in (74, 190):
        fill(img, circle(cx, 172, 22), (12, 12, 14))
        fill(img, circle(cx, 172, 10), (160, 160, 170))
    fill(img, rect(30, 150, 232, 156), (250, 190, 20))                                      # side stripe
    save(img, "safety-car.png")


def red_flag():
    img = canvas(BG)
    fill(img, rect(58, 40, 68, 232, 4), (200, 200, 210))                                     # pole
    pts_top = [(68 + t, 52 + 10 * math.sin(t / 26.0)) for t in range(0, 141, 4)]
    pts_bot = [(68 + t, 150 + 10 * math.sin(t / 26.0)) for t in range(140, -1, -4)]
    fill(img, polygon(pts_top + pts_bot), (225, 6, 0))                                       # flag
    fill(img, polygon([(68 + t, 52 + 10 * math.sin(t / 26.0) + 18) for t in range(0, 141, 4)] +
                      [(68 + t, 64 + 10 * math.sin(t / 26.0) + 18) for t in range(140, -1, -4)]), (255, 70, 60), 0.5)
    save(img, "red-flag.png")


def weekend_schedule():
    img = canvas(BG)
    fill(img, rect(36, 48, 220, 220, 16), (245, 245, 248))                                   # calendar sheet
    fill(img, rect(36, 48, 220, 92, 16), (225, 6, 0))
    fill(img, rect(36, 76, 220, 92), (225, 6, 0))                                            # header
    for cx in (80, 176):
        fill(img, rect(cx - 6, 34, cx + 6, 62, 5), (60, 60, 70))                              # rings
    colors = [(200, 200, 208)] * 3 + [(225, 6, 0)] * 3                                        # plain days, race-weekend days
    for i in range(6):
        col, row = i % 3, i // 3
        x0, y0 = 56 + col * 52, 108 + row * 52
        fill(img, rect(x0, y0, x0 + 40, y0 + 40, 6), colors[i])
    for r in range(4):                                                                        # small chequered corner
        for c in range(4):
            if (r + c) % 2 == 0:
                fill(img, rect(176 + c * 8, 180 + r * 8, 184 + c * 8, 188 + r * 8), (21, 21, 30))
    save(img, "weekend-schedule.png")


if __name__ == "__main__":
    start_lights()
    safety_car()
    red_flag()
    weekend_schedule()
