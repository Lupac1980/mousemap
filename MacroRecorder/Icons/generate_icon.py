# Gera um .ICO válido (32x32 e 16x16, 32bpp) sem dependências externas.
# Ícone: círculo "rec" vermelho sobre fundo escuro com borda arredondada.
import struct, math

def render(size):
    px = bytearray()  # BGRA bottom-up (DIB)
    cx = cy = (size - 1) / 2.0
    r_bg = size * 0.48
    r_ring = size * 0.34
    for y in range(size - 1, -1, -1):  # bottom-up
        for x in range(size):
            d = math.hypot(x - cx, y - cy)
            # fundo: quadrado arredondado escuro (#1E1E2E)
            half = size / 2.0
            corner = size * 0.18
            dx = max(abs(x - cx) - (half - corner), 0)
            dy = max(abs(y - cy) - (half - corner), 0)
            inside_bg = math.hypot(dx, dy) <= corner
            if not inside_bg:
                px += bytes((0, 0, 0, 0)); continue
            if d <= r_ring:
                # anel vermelho de "gravação" (#E53935)
                if d >= r_ring - size * 0.12:
                    px += bytes((0x35, 0x39, 0xE5, 255))  # BGRA
                    continue
                # interior do anel: cinza claro
                px += bytes((0xF2, 0xF2, 0xF2, 255))
            else:
                # fora do anel: fundo escuro da janela
                px += bytes((0x2E, 0x1E, 0x1E, 255))
    return bytes(px)

def make_dir_entry(width, height, bmp_size, offset, planes=1, bpp=32):
    w = width if width < 256 else 0
    h = height if height < 256 else 0
    return struct.pack('<BBBBHHII', w, h, 0, 0, planes, bpp, bmp_size, offset)

def build_ico(path, sizes=(32, 16)):
    images = []
    for s in sizes:
        pixels = render(s)
        # AND mask (1bpp) - tudo transparente onde alpha=0; aqui usamos máscara zerada
        and_row_bytes = ((s + 31) // 32) * 4
        and_mask = b'\x00' * (and_row_bytes * s * 2)  # AND + XOR(premultiplied ok p/ 32bpp)
        bih = struct.pack('<IiiHHIIiiII',
                          40, s, s * 2, 1, 32, 0, len(pixels) + len(and_mask),
                          0, 0, 0, 0)
        images.append(bih + pixels + and_mask)

    count = len(images)
    header_size = 6 + count * 16
    offset = header_size
    dir_entries = b''
    data = b''
    for img in images:
        s = sizes[list(images).index(img)]
        dir_entries += make_dir_entry(s, s, len(img), offset)
        data += img
        offset += len(img)

    with open(path, 'wb') as f:
        f.write(struct.pack('<HHH', 0, 1, count) + dir_entries + data)
    print("ICO gerado:", path)

build_ico('/workspace/MacroRecorder/Icons/app.ico')
