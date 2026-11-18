"""Remove the .debug_* sections from a static ELF64 little-endian executable.

Zig 0.17 has no working `objcopy --strip-debug` for RISC-V, and its linker turns
`-Wl,--strip-debug` into a full strip that also drops .symtab. godot-sandbox finds a
guest's public functions (gdscript.elf's `compile`) through .symtab, so it must stay.

Only sections that are not loaded (no SHF_ALLOC) and are named .debug_* are removed.
Every byte the program headers load is copied unchanged; .symtab/.strtab/.shstrtab and
the other kept sections are re-laid after it, and section indices are renumbered.
The result is checked: same loaded bytes, same symbols (name, value, size, binding).

usage: python -I strip-debug.py <input> <output> [required-symbol ...]
"""
import struct
import sys

SHF_ALLOC = 0x2
SHT_SYMTAB = 2
SHT_NOBITS = 8
PT_LOAD = 1
SHN_LORESERVE = 0xFF00
SHDR = '<IIQQQQIIQQ'
SYM = '<IBBHQQ'


def parse(data):
    if data[:4] != b'\x7fELF' or data[4] != 2 or data[5] != 1:
        raise SystemExit('not an ELF64 little-endian file')
    phoff, shoff = struct.unpack_from('<QQ', data, 0x20)
    phentsize, phnum, shentsize, shnum, shstrndx = struct.unpack_from('<HHHHH', data, 0x36)
    sections = [list(struct.unpack_from(SHDR, data, shoff + i * shentsize)) for i in range(shnum)]
    names_at = sections[shstrndx][4]

    def name(sh):
        end = data.index(b'\0', names_at + sh[0])
        return data[names_at + sh[0]:end].decode()

    segments = [struct.unpack_from('<IIQQQQQQ', data, phoff + i * phentsize) for i in range(phnum)]
    return sections, [name(s) for s in sections], segments, shstrndx


def symbols(data, sections, names):
    out = []
    for sh in sections:
        if sh[1] != SHT_SYMTAB:
            continue
        strtab = sections[sh[6]]
        for i in range(sh[5] // 24):
            st_name, info, other, shndx, value, size = struct.unpack_from(SYM, data, sh[4] + i * 24)
            end = data.index(b'\0', strtab[4] + st_name)
            out.append((data[strtab[4] + st_name:end], info, other, value, size))
    return out


def main(source, target, required=()):
    data = open(source, 'rb').read()
    sections, names, segments, shstrndx = parse(data)
    drop = {i for i, (sh, n) in enumerate(zip(sections, names)) if n.startswith('.debug') and not sh[2] & SHF_ALLOC}
    if not any(sh[1] == SHT_SYMTAB for sh in sections):
        raise SystemExit('input has no .symtab; nothing a sandbox can call would remain')
    nonalloc = [i for i in range(1, len(sections)) if not sections[i][2] & SHF_ALLOC]
    loaded_end = max((p[2] + p[5] for p in segments if p[0] == PT_LOAD), default=0)  # p_offset + p_filesz
    head_end = min(sections[i][4] for i in nonalloc)
    if loaded_end > head_end:
        raise SystemExit('a loaded segment overlaps non-allocated sections; refusing to rewrite')
    # A non-loaded segment may describe a non-allocated section (PT_RISCV_ATTRIBUTES ->
    # .riscv.attributes). It must point at exactly one kept section and moves with it.
    described = {}
    for n, p in enumerate(segments):
        if p[0] != PT_LOAD and p[5] and p[2] >= head_end:
            match = [i for i in nonalloc if sections[i][4] == p[2] and sections[i][5] == p[5] and i not in drop]
            if len(match) != 1:
                raise SystemExit('a segment describes data that would be removed; refusing to rewrite')
            described[n] = match[0]

    out = bytearray(data[:head_end])
    index = {}
    kept = []
    for i, sh in enumerate(sections):
        if i in drop:
            continue
        index[i] = len(kept)
        kept.append(list(sh))
    for new, sh in enumerate(kept):
        old = [i for i, n in index.items() if n == new][0]
        if old == 0 or sh[2] & SHF_ALLOC:
            continue
        align = max(sh[8], 1)
        out += b'\0' * (-len(out) % align)
        body = b'' if sh[1] == SHT_NOBITS else data[sh[4]:sh[4] + sh[5]]
        sh[4] = len(out)
        out += body
    for sh in kept:
        sh[6] = index.get(sh[6], 0) if sh[6] else 0  # sh_link
        if sh[1] == SHT_SYMTAB:
            for i in range(sh[5] // 24):
                at = sh[4] + i * 24
                shndx = struct.unpack_from('<H', out, at + 6)[0]
                if 0 < shndx < SHN_LORESERVE:
                    struct.pack_into('<H', out, at + 6, index.get(shndx, 0))
    phoff, = struct.unpack_from('<Q', out, 0x20)
    phentsize, = struct.unpack_from('<H', out, 0x36)
    for n, old in described.items():
        struct.pack_into('<Q', out, phoff + n * phentsize + 8, kept[index[old]][4])
    out += b'\0' * (-len(out) % 8)
    shoff = len(out)
    for sh in kept:
        out += struct.pack(SHDR, *sh)
    struct.pack_into('<Q', out, 0x28, shoff)
    struct.pack_into('<HH', out, 0x3C, len(kept), index[shstrndx])

    # Check before writing: the loaded image and the symbols are unchanged. The ELF header
    # and the program header table lie inside the first PT_LOAD; the only bytes allowed to
    # differ there are e_shoff, e_shnum/e_shstrndx and the p_offset of a moved segment.
    new_sections, new_names, new_segments, _ = parse(bytes(out))
    allowed = set(range(0x28, 0x30)) | set(range(0x3C, 0x40))
    for n in described:
        allowed |= set(range(phoff + n * phentsize + 8, phoff + n * phentsize + 16))
    changed = [i for i in range(loaded_end) if out[i] != data[i] and i not in allowed]
    if changed:
        raise SystemExit(f'loaded image changed at {len(changed)} bytes (first {changed[0]:#x}); not writing')
    if [p for n, p in enumerate(new_segments) if n not in described] != [p for n, p in enumerate(segments) if n not in described]:
        raise SystemExit('a loaded segment header changed; not writing')
    for n in described:
        p, q = new_segments[n], segments[n]
        if p[:2] + p[3:] != q[:2] + q[3:] or out[p[2]:p[2] + p[5]] != data[q[2]:q[2] + q[5]]:
            raise SystemExit('a moved segment changed; not writing')
    before = [(n, i, o, v, s) for n, i, o, v, s in symbols(data, sections, names)]
    after = [(n, i, o, v, s) for n, i, o, v, s in symbols(bytes(out), new_sections, new_names)]
    if before != after:
        raise SystemExit('symbol table changed; not writing')
    if any(n.startswith('.debug') for n in new_names):
        raise SystemExit('debug sections remain')
    exported = {n for n, info, *_ in after if info >> 4 == 1 and info & 15 == 2}  # GLOBAL FUNC
    missing = [r for r in required if r.encode() not in exported]
    if missing:
        raise SystemExit(f'required guest functions missing from .symtab: {missing}')
    open(target, 'wb').write(out)
    print(f'STRIP_DEBUG removed={len(drop)} sections bytes {len(data)} -> {len(out)} symbols={len(after)}')


if __name__ == '__main__':
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2], sys.argv[3:])
