"""Find and validate x64 references to unlock-related strings in GRW.exe.

This is deliberately read-only. It maps file offsets through the PE section
table, scans executable sections for RIP-relative displacements, and asks
Capstone to reject byte coincidences that are not real instructions.
"""

from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass

import numpy as np
from capstone import CS_ARCH_X86, CS_MODE_64, Cs
from capstone.x86 import X86_OP_MEM, X86_REG_RIP


@dataclass(frozen=True)
class Section:
    name: str
    virtual_address: int
    virtual_size: int
    raw_pointer: int
    raw_size: int
    characteristics: int

    @property
    def executable(self) -> bool:
        return bool(self.characteristics & 0x20000000)


def read_pe(data: bytes) -> tuple[int, list[Section]]:
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    count = struct.unpack_from("<H", data, pe + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    optional = pe + 24
    if struct.unpack_from("<H", data, optional)[0] != 0x20B:
        raise ValueError("GRW.exe is expected to be a PE32+ image")
    image_base = struct.unpack_from("<Q", data, optional + 24)[0]
    headers = optional + optional_size
    sections: list[Section] = []
    for index in range(count):
        offset = headers + index * 40
        name = data[offset : offset + 8].split(b"\0", 1)[0].decode(errors="replace")
        virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from(
            "<IIII", data, offset + 8
        )
        characteristics = struct.unpack_from("<I", data, offset + 36)[0]
        sections.append(
            Section(
                name,
                virtual_address,
                virtual_size,
                raw_pointer,
                raw_size,
                characteristics,
            )
        )
    return image_base, sections


def file_offset_to_rva(sections: list[Section], file_offset: int) -> int:
    for section in sections:
        if section.raw_pointer <= file_offset < section.raw_pointer + section.raw_size:
            return section.virtual_address + file_offset - section.raw_pointer
    raise ValueError(f"file offset 0x{file_offset:X} is not in a PE section")


def candidate_displacements(
    data: bytes, section: Section, target_rva: int, chunk_size: int = 4_000_000
):
    raw = np.frombuffer(
        data, dtype=np.uint8, count=section.raw_size, offset=section.raw_pointer
    )
    for start in range(0, max(0, section.raw_size - 3), chunk_size):
        end = min(section.raw_size - 3, start + chunk_size)
        values = (
            raw[start:end].astype(np.uint32)
            | raw[start + 1 : end + 1].astype(np.uint32) << 8
            | raw[start + 2 : end + 2].astype(np.uint32) << 16
            | raw[start + 3 : end + 3].astype(np.uint32) << 24
        )
        positions = np.arange(start, end, dtype=np.int64)
        wanted = (
            target_rva - (section.virtual_address + positions + 4)
        ).astype(np.uint32)
        for relative in np.nonzero(values == wanted)[0]:
            position = start + int(relative)
            yield (
                section.raw_pointer + position,
                section.virtual_address + position,
            )


def validate_reference(
    data: bytes, file_offset: int, rva: int, image_base: int, target_va: int
):
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    decoder.detail = True
    results: set[tuple[int, str, str]] = set()
    for back in range(4, 16):
        start = file_offset - back
        start_va = image_base + rva - back
        for instruction in decoder.disasm(data[start : file_offset + 16], start_va):
            if not (
                instruction.address
                <= image_base + rva
                < instruction.address + instruction.size
            ):
                continue
            for operand in instruction.operands:
                if operand.type != X86_OP_MEM or operand.mem.base != X86_REG_RIP:
                    continue
                resolved = instruction.address + instruction.size + operand.mem.disp
                if resolved == target_va:
                    results.add(
                        (instruction.address, instruction.mnemonic, instruction.op_str)
                    )
    return sorted(results)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("executable")
    parser.add_argument(
        "--context",
        type=int,
        default=0,
        help="disassemble this many instructions starting at each validated reference",
    )
    parser.add_argument(
        "strings",
        nargs="*",
        default=[
            "Unlockables.csv",
            "Locked Initially by Database",
            "AllUnlocked",
            "UnlockableID",
            "UnlockableState",
            "UnlockableCategory",
            "UnlockableWeaponCategory",
            "IsUnlockablesSaveLoading",
        ],
    )
    args = parser.parse_args()

    with open(args.executable, "rb") as stream:
        data = stream.read()
    image_base, sections = read_pe(data)
    executable_sections = [section for section in sections if section.executable]
    print(
        "executable sections:",
        ", ".join(
            f"{section.name} ({section.raw_size / 1024 / 1024:.1f} MiB)"
            for section in executable_sections
        ),
    )

    for text in args.strings:
        file_offset = data.find(text.encode())
        if file_offset < 0:
            print(f"\n{text}: not found")
            continue
        target_rva = file_offset_to_rva(sections, file_offset)
        target_va = image_base + target_rva
        print(
            f"\n{text}: file 0x{file_offset:X}, RVA 0x{target_rva:X}, VA 0x{target_va:X}"
        )
        candidates = 0
        validated = 0
        for section in executable_sections:
            for reference_offset, reference_rva in candidate_displacements(
                data, section, target_rva
            ):
                candidates += 1
                instructions = validate_reference(
                    data,
                    reference_offset,
                    reference_rva,
                    image_base,
                    target_va,
                )
                # Starting one byte into a REX-prefixed instruction can also decode
                # as a superficially valid 32-bit instruction. Keep only the earliest
                # address for an otherwise identical resolved reference.
                if instructions:
                    earliest = min(item[0] for item in instructions)
                    instructions = [item for item in instructions if item[0] == earliest]
                for address, mnemonic, operands in instructions:
                    validated += 1
                    print(
                        f"  {section.name} 0x{address:X}: {mnemonic} {operands}"
                    )
                    if args.context > 0:
                        instruction_offset = (
                            section.raw_pointer
                            + address
                            - image_base
                            - section.virtual_address
                        )
                        decoder = Cs(CS_ARCH_X86, CS_MODE_64)
                        for nearby in list(
                            decoder.disasm(
                                data[instruction_offset : instruction_offset + 512],
                                address,
                            )
                        )[: args.context]:
                            print(
                                f"      0x{nearby.address:X}: "
                                f"{nearby.mnemonic:<8} {nearby.op_str}"
                            )
        print(f"  candidates {candidates}, validated instructions {validated}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
