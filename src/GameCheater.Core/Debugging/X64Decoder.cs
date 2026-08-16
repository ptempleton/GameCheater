namespace GameCheater.Core.Debugging;

/// <summary>
/// A minimal x86-64 instruction *length* decoder, plus the backward search that turns a
/// trapped RIP into the instruction that actually wrote memory.
///
/// Why this exists: a hardware data breakpoint is a trap, not a fault — the CPU reports it
/// *after* the storing instruction retires, so the RIP we read out of the thread context is
/// the address of the NEXT instruction. To report (and NOP) the writer we have to walk
/// backwards, and x86 is variable-length so you cannot simply step back a fixed amount.
/// <see cref="FindWriterEndingAt"/> resolves it by decoding forward from many earlier start
/// points and keeping whichever instruction the most decode chains agree ends exactly at RIP.
///
/// Scope: 64-bit mode only, and only what a length decoder needs (prefixes, REX, VEX/EVEX,
/// the three opcode maps, ModRM/SIB/displacement/immediate sizing). It is deliberately not a
/// disassembler. Known gaps are exotic encodings that do not appear in game write paths —
/// e.g. <c>66 0F 78</c> (EXTRQ, two immediates) is sized as if it had one.
/// </summary>
public static class X64Decoder
{
    /// <summary>Architectural maximum instruction length, prefixes included.</summary>
    public const int MaxInstructionLength = 15;

    [Flags]
    private enum Op : byte
    {
        None = 0,
        ModRm = 1,
        Ib = 2,       // imm8
        Iw = 4,       // imm16
        Iz = 8,       // imm16 when 0x66 is present, otherwise imm32
        Iv = 16,      // imm16 / imm32 / imm64 (REX.W)
        Io = 32,      // moffs — an address-sized immediate (8 bytes, 4 under 0x67)
        Group3 = 64,  // F6/F7: whether there is an immediate depends on ModRM.reg
        Invalid = 128,
    }

    private static readonly Op[] OneByte = BuildOneByteMap();
    private static readonly Op[] ZeroF = BuildZeroFMap();

    private static Op[] BuildOneByteMap()
    {
        var m = new Op[256];

        // 00-3F: eight ALU groups of eight, all encoded the same way. The last two slots of
        // each group are the 16-bit-era PUSH/POP seg and BCD opcodes — invalid in 64-bit mode
        // (the segment-prefix and 0F-escape bytes in those slots never reach this table,
        // because prefixes and escapes are consumed before the lookup).
        for (int op = 0x00; op < 0x40; op += 8)
        {
            m[op + 0] = Op.ModRm;
            m[op + 1] = Op.ModRm;
            m[op + 2] = Op.ModRm;
            m[op + 3] = Op.ModRm;
            m[op + 4] = Op.Ib;
            m[op + 5] = Op.Iz;
            m[op + 6] = Op.Invalid;
            m[op + 7] = Op.Invalid;
        }

        for (int op = 0x40; op <= 0x4F; op++) m[op] = Op.Invalid;   // REX — consumed as a prefix
        for (int op = 0x50; op <= 0x5F; op++) m[op] = Op.None;      // push/pop r64

        m[0x60] = Op.Invalid;
        m[0x61] = Op.Invalid;
        m[0x62] = Op.Invalid;   // EVEX — consumed before the lookup
        m[0x63] = Op.ModRm;     // movsxd
        m[0x64] = Op.Invalid;   // FS prefix
        m[0x65] = Op.Invalid;   // GS prefix
        m[0x66] = Op.Invalid;   // operand-size prefix
        m[0x67] = Op.Invalid;   // address-size prefix
        m[0x68] = Op.Iz;
        m[0x69] = Op.ModRm | Op.Iz;
        m[0x6A] = Op.Ib;
        m[0x6B] = Op.ModRm | Op.Ib;
        for (int op = 0x6C; op <= 0x6F; op++) m[op] = Op.None;      // ins/outs
        for (int op = 0x70; op <= 0x7F; op++) m[op] = Op.Ib;        // jcc rel8

        m[0x80] = Op.ModRm | Op.Ib;
        m[0x81] = Op.ModRm | Op.Iz;
        m[0x82] = Op.Invalid;
        m[0x83] = Op.ModRm | Op.Ib;
        for (int op = 0x84; op <= 0x8F; op++) m[op] = Op.ModRm;     // test/xchg/mov/lea/pop

        for (int op = 0x90; op <= 0x99; op++) m[op] = Op.None;      // xchg/nop/cwde/cdq
        m[0x9A] = Op.Invalid;
        for (int op = 0x9B; op <= 0x9F; op++) m[op] = Op.None;
        for (int op = 0xA0; op <= 0xA3; op++) m[op] = Op.Io;        // mov al/eax, moffs
        for (int op = 0xA4; op <= 0xA7; op++) m[op] = Op.None;      // movs/cmps
        m[0xA8] = Op.Ib;
        m[0xA9] = Op.Iz;
        for (int op = 0xAA; op <= 0xAF; op++) m[op] = Op.None;      // stos/lods/scas
        for (int op = 0xB0; op <= 0xB7; op++) m[op] = Op.Ib;        // mov r8, imm8
        for (int op = 0xB8; op <= 0xBF; op++) m[op] = Op.Iv;        // mov r, imm16/32/64

        m[0xC0] = Op.ModRm | Op.Ib;
        m[0xC1] = Op.ModRm | Op.Ib;
        m[0xC2] = Op.Iw;
        m[0xC3] = Op.None;
        m[0xC4] = Op.Invalid;   // 3-byte VEX — consumed before the lookup
        m[0xC5] = Op.Invalid;   // 2-byte VEX — ditto
        m[0xC6] = Op.ModRm | Op.Ib;
        m[0xC7] = Op.ModRm | Op.Iz;
        m[0xC8] = Op.Iw | Op.Ib; // enter imm16, imm8
        m[0xC9] = Op.None;
        m[0xCA] = Op.Iw;
        m[0xCB] = Op.None;
        m[0xCC] = Op.None;
        m[0xCD] = Op.Ib;
        m[0xCE] = Op.Invalid;
        m[0xCF] = Op.None;

        for (int op = 0xD0; op <= 0xD3; op++) m[op] = Op.ModRm;     // shift by 1/cl
        m[0xD4] = Op.Invalid;
        m[0xD5] = Op.Invalid;
        m[0xD6] = Op.Invalid;
        m[0xD7] = Op.None;
        for (int op = 0xD8; op <= 0xDF; op++) m[op] = Op.ModRm;     // x87

        for (int op = 0xE0; op <= 0xE7; op++) m[op] = Op.Ib;        // loop/jcxz/in/out
        m[0xE8] = Op.Iz;
        m[0xE9] = Op.Iz;
        m[0xEA] = Op.Invalid;
        m[0xEB] = Op.Ib;
        for (int op = 0xEC; op <= 0xEF; op++) m[op] = Op.None;

        m[0xF0] = Op.Invalid;   // lock prefix
        m[0xF1] = Op.None;
        m[0xF2] = Op.Invalid;   // repne prefix
        m[0xF3] = Op.Invalid;   // rep prefix
        m[0xF4] = Op.None;
        m[0xF5] = Op.None;
        m[0xF6] = Op.ModRm | Op.Group3;
        m[0xF7] = Op.ModRm | Op.Group3;
        for (int op = 0xF8; op <= 0xFD; op++) m[op] = Op.None;
        m[0xFE] = Op.ModRm;
        m[0xFF] = Op.ModRm;

        return m;
    }

    private static Op[] BuildZeroFMap()
    {
        var m = new Op[256];
        Array.Fill(m, Op.ModRm);   // the 0F map is overwhelmingly SSE/AVX "op reg, r/m"

        m[0x04] = Op.Invalid;
        for (int op = 0x05; op <= 0x09; op++) m[op] = Op.None;      // syscall/clts/sysret/invd/wbinvd
        m[0x0A] = Op.Invalid;
        m[0x0B] = Op.None;                                          // ud2
        m[0x0C] = Op.Invalid;
        m[0x0E] = Op.None;                                          // femms
        m[0x0F] = Op.ModRm | Op.Ib;                                 // 3DNow!, opcode in a suffix byte

        for (int op = 0x24; op <= 0x27; op++) m[op] = Op.Invalid;
        for (int op = 0x30; op <= 0x37; op++) m[op] = Op.None;      // wrmsr/rdtsc/rdmsr/rdpmc/sysenter…
        m[0x38] = Op.Invalid;                                       // 0F 38 escape — consumed earlier
        m[0x39] = Op.Invalid;
        m[0x3A] = Op.Invalid;                                       // 0F 3A escape — consumed earlier
        for (int op = 0x3B; op <= 0x3F; op++) m[op] = Op.Invalid;

        for (int op = 0x70; op <= 0x73; op++) m[op] = Op.ModRm | Op.Ib;
        m[0x77] = Op.None;                                          // emms
        m[0x7A] = Op.Invalid;
        m[0x7B] = Op.Invalid;
        for (int op = 0x80; op <= 0x8F; op++) m[op] = Op.Iz;        // jcc rel32

        m[0xA0] = Op.None;
        m[0xA1] = Op.None;
        m[0xA2] = Op.None;
        m[0xA4] = Op.ModRm | Op.Ib;                                 // shld r/m, r, imm8
        m[0xA6] = Op.Invalid;
        m[0xA7] = Op.Invalid;
        m[0xA8] = Op.None;
        m[0xA9] = Op.None;
        m[0xAA] = Op.None;                                          // rsm
        m[0xAC] = Op.ModRm | Op.Ib;                                 // shrd r/m, r, imm8
        m[0xBA] = Op.ModRm | Op.Ib;                                 // bt/bts/btr/btc r/m, imm8

        m[0xC2] = Op.ModRm | Op.Ib;                                 // cmpps
        m[0xC4] = Op.ModRm | Op.Ib;                                 // pinsrw
        m[0xC5] = Op.ModRm | Op.Ib;                                 // pextrw
        m[0xC6] = Op.ModRm | Op.Ib;                                 // shufps
        for (int op = 0xC8; op <= 0xCF; op++) m[op] = Op.None;      // bswap

        return m;
    }

    /// <summary>
    /// Decode the instruction starting at <paramref name="index"/> within
    /// <paramref name="code"/> (whose first byte lives at <paramref name="baseAddress"/>).
    /// Returns null when the bytes are not a valid 64-bit instruction, or when the
    /// instruction runs past the end of the buffer.
    /// </summary>
    public static X64Instruction? Decode(ReadOnlySpan<byte> code, int index, ulong baseAddress)
    {
        if (index < 0 || index >= code.Length)
            return null;

        int i = index;
        bool operand16 = false;
        bool address32 = false;
        bool rexW = false;

        // --- legacy prefixes (any order, any number) ---
        while (i < code.Length)
        {
            byte p = code[i];
            if (p is 0xF0 or 0xF2 or 0xF3 or 0x2E or 0x36 or 0x3E or 0x26 or 0x64 or 0x65)
                i++;
            else if (p == 0x66) { operand16 = true; i++; }
            else if (p == 0x67) { address32 = true; i++; }
            else break;

            if (i - index > MaxInstructionLength)
                return null;
        }
        if (i >= code.Length)
            return null;

        // --- VEX / EVEX, which replace REX and select an opcode map directly. In 64-bit
        //     mode C4/C5/62 can only be these (their legacy meanings are invalid here). ---
        var map = OpcodeMap.OneByte;
        byte lead = code[i];
        bool escaped = false;

        if (lead is 0xC5 or 0xC4 or 0x62)
        {
            int prefixLength = lead == 0xC5 ? 2 : lead == 0xC4 ? 3 : 4;
            if (i + prefixLength >= code.Length)
                return null;

            int mm;
            if (lead == 0xC5)
            {
                mm = 1;                                     // 2-byte VEX always implies the 0F map
            }
            else if (lead == 0xC4)
            {
                mm = code[i + 1] & 0x1F;
                rexW = (code[i + 2] & 0x80) != 0;
            }
            else
            {
                mm = code[i + 1] & 0x07;
                rexW = (code[i + 2] & 0x80) != 0;
            }

            map = mm switch
            {
                1 => OpcodeMap.ZeroF,
                2 => OpcodeMap.ZeroF38,
                3 => OpcodeMap.ZeroF3A,
                _ => (OpcodeMap)(-1),                       // reserved map — not a real instruction
            };
            if (map == (OpcodeMap)(-1))
                return null;

            i += prefixLength;
            escaped = true;
        }
        else if (lead is >= 0x40 and <= 0x4F)
        {
            // --- REX (must be the last prefix before the opcode) ---
            rexW = (lead & 0x08) != 0;
            i++;
            if (i >= code.Length)
                return null;
        }

        // --- opcode (for VEX/EVEX the map is already fixed, so no escape byte follows) ---
        byte opcode = code[i++];
        if (!escaped && opcode == 0x0F)
        {
            if (i >= code.Length)
                return null;
            byte second = code[i++];
            if (second is 0x38 or 0x3A)
            {
                if (i >= code.Length)
                    return null;
                map = second == 0x38 ? OpcodeMap.ZeroF38 : OpcodeMap.ZeroF3A;
                opcode = code[i++];
            }
            else
            {
                map = OpcodeMap.ZeroF;
                opcode = second;
            }
        }

        Op flags = map switch
        {
            OpcodeMap.ZeroF => ZeroF[opcode],
            OpcodeMap.ZeroF38 => Op.ModRm,          // the 0F 38 map is uniformly "op reg, r/m"
            OpcodeMap.ZeroF3A => Op.ModRm | Op.Ib,  // …and the 0F 3A map always carries an imm8
            _ => OneByte[opcode],
        };

        if ((flags & Op.Invalid) != 0)
            return null;

        // --- ModRM / SIB / displacement ---
        bool hasModRm = (flags & Op.ModRm) != 0;
        byte modrm = 0;
        int dispOffset = -1;
        int dispLength = 0;
        bool ripRelative = false;

        if (hasModRm)
        {
            if (i >= code.Length)
                return null;
            modrm = code[i++];
            int mod = modrm >> 6;
            int rm = modrm & 7;

            if (mod != 3)
            {
                bool sibBaseIsRbp = false;
                if (rm == 4)
                {
                    if (i >= code.Length)
                        return null;
                    sibBaseIsRbp = (code[i++] & 7) == 5;
                }

                if (mod == 0)
                {
                    if (rm == 5)
                    {
                        dispLength = 4;         // [rip + disp32]
                        ripRelative = true;
                    }
                    else if (rm == 4 && sibBaseIsRbp)
                    {
                        dispLength = 4;         // SIB with no base — disp32 only
                    }
                }
                else
                {
                    dispLength = mod == 1 ? 1 : 4;
                }

                if (dispLength > 0)
                {
                    dispOffset = i - index;
                    i += dispLength;
                }
            }
        }

        // --- immediate ---
        // F6/F7 are the one group whose immediate depends on the ModRM.reg subopcode:
        // only TEST (/0 and /1) carries one.
        if ((flags & Op.Group3) != 0 && ((modrm >> 3) & 7) <= 1)
            flags |= opcode == 0xF6 ? Op.Ib : Op.Iz;

        int immLength = 0;
        if ((flags & Op.Ib) != 0) immLength += 1;
        if ((flags & Op.Iw) != 0) immLength += 2;
        if ((flags & Op.Iz) != 0) immLength += operand16 ? 2 : 4;
        if ((flags & Op.Iv) != 0) immLength += rexW ? 8 : operand16 ? 2 : 4;
        if ((flags & Op.Io) != 0) immLength += address32 ? 4 : 8;

        int immOffset = -1;
        if (immLength > 0)
        {
            immOffset = i - index;
            i += immLength;
        }

        int length = i - index;
        if (length is <= 0 or > MaxInstructionLength || index + length > code.Length)
            return null;

        return new X64Instruction
        {
            Address = baseAddress + (ulong)index,
            Length = length,
            Bytes = code.Slice(index, length).ToArray(),
            Opcode = opcode,
            Map = map,
            HasModRm = hasModRm,
            ModRm = modrm,
            DisplacementOffset = dispOffset,
            DisplacementLength = dispLength,
            ImmediateOffset = immOffset,
            ImmediateLength = immLength,
            IsRipRelative = ripRelative,
        };
    }

    /// <summary>
    /// Find the instruction that ends exactly at <paramref name="endAddress"/> — i.e. the one
    /// that just retired when the CPU reported the trap at that RIP.
    ///
    /// x86 has no way to decode backwards, so this brute-forces it: start decoding from every
    /// byte in the preceding window, follow each decode chain forward, and keep only the chains
    /// that land precisely on <paramref name="endAddress"/>. Chains that started mid-instruction
    /// almost always desynchronise or hit an invalid opcode, and the handful that survive
    /// re-synchronise onto the real instruction stream — so the correct answer is the one the
    /// most chains agree on.
    ///
    /// With <paramref name="preferMemoryWriters"/> (the default) candidates that cannot touch
    /// memory are ranked last, since a write breakpoint can only have been tripped by one that
    /// can. Pass false when using this purely to find instruction boundaries — e.g. walking
    /// back through the context bytes of a signature — where that bias would be wrong.
    /// </summary>
    public static X64Instruction? FindWriterEndingAt(ReadOnlySpan<byte> code, ulong baseAddress,
        ulong endAddress, int maxLookback = 48, bool preferMemoryWriters = true)
    {
        if (endAddress < baseAddress)
            return null;
        int end = (int)(endAddress - baseAddress);
        if (end <= 0 || end > code.Length)
            return null;

        var votes = new Dictionary<int, int>();
        var candidates = new Dictionary<int, X64Instruction>();

        for (int back = 1; back <= maxLookback; back++)
        {
            int start = end - back;
            if (start < 0)
                break;

            int p = start;
            X64Instruction? last = null;
            while (p < end)
            {
                var decoded = Decode(code, p, baseAddress);
                if (decoded is null)
                {
                    last = null;
                    break;
                }
                last = decoded;
                p += decoded.Length;
            }

            if (p != end || last is null)
                continue;

            int key = (int)(last.Address - baseAddress);
            votes[key] = votes.GetValueOrDefault(key) + 1;
            candidates[key] = last;
        }

        if (votes.Count == 0)
            return null;

        // Rank: can-write-memory first (when asked), then most agreement, then closest to the trap.
        int best = votes.Keys
            .OrderByDescending(k => preferMemoryWriters && candidates[k].CanWriteMemory)
            .ThenByDescending(k => votes[k])
            .ThenByDescending(k => k)
            .First();

        return candidates[best];
    }
}
