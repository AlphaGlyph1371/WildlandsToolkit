import argparse
import struct


def parse_number(text: str) -> int:
    return int(text, 0)


def executable_sections(image: bytes):
    pe_offset = struct.unpack_from("<I", image, 0x3C)[0]
    if image[pe_offset:pe_offset + 4] != b"PE\0\0":
        raise ValueError("not a PE image")
    section_count = struct.unpack_from("<H", image, pe_offset + 6)[0]
    optional_size = struct.unpack_from("<H", image, pe_offset + 20)[0]
    section_offset = pe_offset + 24 + optional_size
    for index in range(section_count):
        offset = section_offset + index * 40
        name = image[offset:offset + 8].rstrip(b"\0").decode("ascii", errors="replace")
        virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from(
            "<IIII", image, offset + 8
        )
        characteristics = struct.unpack_from("<I", image, offset + 36)[0]
        if characteristics & 0x20000000:
            yield name, virtual_address, raw_offset, min(virtual_size, raw_size)


parser = argparse.ArgumentParser(description="Find x64 PE rel32 references and byte patterns")
parser.add_argument("image")
parser.add_argument("--target", action="append", type=parse_number, default=[])
parser.add_argument("--pattern", action="append", default=[])
args = parser.parse_args()

with open(args.image, "rb") as stream:
    data = stream.read()

targets = set(args.target)
patterns = [(text, bytes.fromhex(text)) for text in args.pattern]
for section_name, section_rva, raw_offset, size in executable_sections(data):
    section = data[raw_offset:raw_offset + size]
    for opcode in (0xE8, 0xE9):
        start = 0
        while targets:
            index = section.find(bytes([opcode]), start, max(0, len(section) - 4))
            if index < 0:
                break
            displacement = struct.unpack_from("<i", section, index + 1)[0]
            source = section_rva + index
            target = source + 5 + displacement
            if target in targets:
                kind = "call" if opcode == 0xE8 else "jmp"
                print(f"{kind} GRW+0x{source:X} -> GRW+0x{target:X} ({section_name})")
            start = index + 1
    for text, pattern in patterns:
        start = 0
        while True:
            index = section.find(pattern, start)
            if index < 0:
                break
            print(f"bytes {text.upper()} at GRW+0x{section_rva + index:X} ({section_name})")
            start = index + 1
