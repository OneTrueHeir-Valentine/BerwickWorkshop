# MIPS assembler selection

The current application includes a small C# assembler for a limited set of ordinary MIPS instructions. It is adequate for the tested heap increment, but not a replacement for a full PlayStation 2 Emotion Engine assembler.

## Candidates

| Toolchain | Strengths | Limitations | Proposed use |
| --- | --- | --- | --- |
| PS2DEV EE GNU binutils | Maintained R5900-targeted assembler, linker and binary tools. Suitable for object files, symbols and repeatable builds. | External toolchain installation; existing Berwick instruction equivalence has not been established yet. | Reference assembler for larger routines |
| armips | Readable MIPS source, file offsets, symbolic labels and direct binary patch workflows. Convenient on Windows. | Emotion Engine opcode coverage is documented as partial; direct disc writes should not be part of Workshop's export pipeline. | Cross-check selected instructions and small prototypes |
| Workshop's `MipsAssembly` | Deterministic, embedded and already regression-tested for a defined opcode subset. | No R5900 multimedia instructions, relocatable objects, macros or full expression handling. | Retain for small, verified instructions |

References:
- https://github.com/ps2dev/ps2toolchain-ee
- https://github.com/ps2dev/ps2sdk/blob/master/Defs.make
- https://github.com/Kingcom/armips/blob/master/Readme.md
- https://github.com/Kingcom/armips/blob/master/Tests/MIPS/PS2%20Opcodes/PS2%20Opcodes.asm

## Decision

Use PS2DEV EE GNU binutils as the reference toolchain for substantial new R5900 routines. Do not introduce it as an application runtime requirement. Assemble patch sources during development and commit the human-readable source and a reproducible byte fixture. Workshop should continue to validate and write the resulting bytes itself.

The source assembler must be explicit about instruction scheduling and register use. For assembly produced with GNU binutils, use `.set noreorder` when an instruction sequence has known delay slots, and inspect the actual emitted bytes rather than assuming an assembler flag guarantees compatibility. Keep relocations and absolute addresses under the patch manager's control. Do not perform unverified in-place instruction expansion.

## Evaluation before migrating a full routine

1. Assemble a small existing hook with the R5900 toolchain. Record the tool version, flags and output format.
2. Compare every generated instruction word and its address with the established original and modified sequences.
3. Check endianness, branch distances, delay slots, clobbered registers and calls between patches.
4. Verify allocation, load segment permissions and execution from any reserved region.
5. Build and export against both supported game editions, then test the changed behavior in PCSX2.

Keep the tested C# assembler for its existing use until a replacement produces identical output. A successful assembly does not by itself validate game behavior or edition compatibility.
