import argparse
import struct

from capstone import Cs, CS_ARCH_X86, CS_MODE_64


def rva_to_file_offset(image: bytes, rva: int) -> int:
    pe_offset = struct.unpack_from("<I", image, 0x3C)[0]
    if image[pe_offset:pe_offset + 4] != b"PE\0\0":
        raise ValueError("not a PE image")
    section_count = struct.unpack_from("<H", image, pe_offset + 6)[0]
    optional_size = struct.unpack_from("<H", image, pe_offset + 20)[0]
    section_offset = pe_offset + 24 + optional_size
    for index in range(section_count):
        offset = section_offset + index * 40
        virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from(
            "<IIII", image, offset + 8
        )
        span = max(virtual_size, raw_size)
        if virtual_address <= rva < virtual_address + span:
            return raw_offset + rva - virtual_address
    raise ValueError(f"RVA 0x{rva:X} is not backed by a section")


def parse_number(text: str) -> int:
    return int(text, 0)


parser = argparse.ArgumentParser(description="Disassemble an x64 PE image at an RVA")
parser.add_argument("image")
parser.add_argument("rva", type=parse_number)
parser.add_argument("length", type=parse_number)
args = parser.parse_args()

with open(args.image, "rb") as stream:
    data = stream.read()

file_offset = rva_to_file_offset(data, args.rva)
decoder = Cs(CS_ARCH_X86, CS_MODE_64)
for instruction in decoder.disasm(data[file_offset:file_offset + args.length], args.rva):
    print(f"GRW+0x{instruction.address:X}  {instruction.bytes.hex().upper():<30} "
          f"{instruction.mnemonic:<8} {instruction.op_str}")
