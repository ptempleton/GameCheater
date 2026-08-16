using GameCheater.Core.Memory;

namespace GameCheater.Core.Debugging;

/// <summary>
/// One decoded x86-64 instruction. This is the output of a *length* decoder, not a
/// disassembler: we never need the mnemonic, only (a) how many bytes the instruction
/// occupies — so "find what writes" can NOP exactly the right run — and (b) which of
/// those bytes are displacements/immediates, so a generated AOB signature can wildcard
/// the parts that move between game builds.
/// </summary>
public sealed class X64Instruction
{
    /// <summary>Absolute address of the first byte (the first prefix, if any).</summary>
    public ulong Address { get; init; }

    /// <summary>Total encoded length in bytes (1–15).</summary>
    public int Length { get; init; }

    /// <summary>The instruction's raw bytes, exactly <see cref="Length"/> long.</summary>
    public byte[] Bytes { get; init; } = Array.Empty<byte>();

    /// <summary>Address of the following instruction — where RIP points after this one retires.</summary>
    public ulong EndAddress => Address + (ulong)Length;

    /// <summary>The primary opcode byte (after any escape/VEX map selection).</summary>
    public byte Opcode { get; init; }

    /// <summary>Which opcode map <see cref="Opcode"/> came from (1 byte, 0F, 0F 38, 0F 3A).</summary>
    public OpcodeMap Map { get; init; }

    public bool HasModRm { get; init; }
    public byte ModRm { get; init; }

    /// <summary>Offset of the displacement within <see cref="Bytes"/>, or -1 when there is none.</summary>
    public int DisplacementOffset { get; init; } = -1;
    public int DisplacementLength { get; init; }

    /// <summary>Offset of the immediate within <see cref="Bytes"/>, or -1 when there is none.</summary>
    public int ImmediateOffset { get; init; } = -1;
    public int ImmediateLength { get; init; }

    /// <summary>True for <c>[rip+disp32]</c> addressing — the displacement is build-specific.</summary>
    public bool IsRipRelative { get; init; }

    /// <summary>
    /// True when the ModRM byte selects a memory operand (mod != 3) rather than a register.
    /// A hardware *write* breakpoint can only be tripped by an instruction that touches
    /// memory, so this is the main sanity check on a backward-resolved writer.
    /// </summary>
    public bool HasMemoryOperand => HasModRm && (ModRm >> 6) != 3;

    /// <summary>
    /// True when the instruction can write memory without naming it in a ModRM byte:
    /// the string stores (<c>movs</c>/<c>stos</c> — what <c>memcpy</c>/<c>memset</c> compile
    /// to), plus stack pushes and calls. Game values are often copied by an inlined
    /// <c>rep movs</c>, so these must not be ruled out when picking the writer.
    /// </summary>
    public bool HasImplicitMemoryWrite => Map == OpcodeMap.OneByte && Opcode switch
    {
        >= 0x50 and <= 0x57 => true,               // push r64
        0xA4 or 0xA5 => true,                      // movs
        0xAA or 0xAB => true,                      // stos
        0x6C or 0x6D => true,                      // ins
        0x9C => true,                              // pushfq
        0xC8 => true,                              // enter
        0xE8 => true,                              // call rel32 (pushes a return address)
        _ => false,
    };

    /// <summary>True if this instruction could plausibly be the one that tripped a write breakpoint.</summary>
    public bool CanWriteMemory => HasMemoryOperand || HasImplicitMemoryWrite;

    /// <summary>Hex bytes, e.g. <c>F3 0F 11 43 20</c>.</summary>
    public string ToPattern() => Signature.ToPattern(Bytes);

    /// <summary>
    /// The same bytes with address-sized fields blanked to <c>??</c>.
    ///
    /// Only fields of <paramref name="minFieldBytes"/> or more are wildcarded, and the default
    /// of 4 is the useful line: a disp32/imm32/imm64 can hold a code or data address, which the
    /// loader rewrites when the module lands at a different base, so matching on it breaks after
    /// a relaunch. A disp8 or imm8 is a struct offset or a small constant — stable across
    /// launches, and exactly the detail that keeps a signature unique. Pass 1 to blank both.
    /// </summary>
    public string ToWildcardPattern(int minFieldBytes = 4)
    {
        var tokens = new string[Length];
        for (int i = 0; i < Length; i++)
            tokens[i] = Bytes[i].ToString("X2");

        ApplyWildcards(tokens, 0, minFieldBytes);
        return string.Join(' ', tokens);
    }

    /// <summary>
    /// Blank this instruction's address-sized fields within a larger token window, where the
    /// instruction starts at <paramref name="baseOffset"/>. Used to wildcard every instruction
    /// in a generated signature, not just the one being patched.
    /// </summary>
    internal void ApplyWildcards(string[] tokens, int baseOffset, int minFieldBytes)
    {
        Blank(DisplacementOffset, DisplacementLength);
        Blank(ImmediateOffset, ImmediateLength);

        void Blank(int offset, int length)
        {
            if (offset < 0 || length < minFieldBytes)
                return;
            for (int i = 0; i < length; i++)
            {
                int at = baseOffset + offset + i;
                if (at >= 0 && at < tokens.Length)
                    tokens[at] = "??";
            }
        }
    }

    public override string ToString() => $"0x{Address:X} ({Length} bytes) {ToPattern()}";
}

/// <summary>Which x86-64 opcode map an instruction's primary opcode was read from.</summary>
public enum OpcodeMap
{
    OneByte,
    ZeroF,
    ZeroF38,
    ZeroF3A,
}
