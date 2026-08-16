using GameCheater.Core.Debugging;

namespace GameCheater.Demo;

/// <summary>
/// Self-test for <see cref="X64Decoder"/>. The decoder is the one piece of "find what writes"
/// that can be checked without a live game, and it is also the piece most likely to be subtly
/// wrong — a one-byte length error means NOPing into the middle of the next instruction, which
/// crashes the game. So the encodings below are hand-verified reference cases covering every
/// sizing rule the decoder implements: prefixes, REX.W, the three opcode maps, SIB/RIP
/// addressing, the F6/F7 subopcode-dependent immediate, and VEX.
///
/// Run with <c>GameCheater.Cli --selftest</c>. Works on any OS (no process attach).
/// </summary>
public static class DecoderSelfTest
{
    private record Case(string Bytes, int Length, string What);

    private static readonly Case[] Lengths =
    {
        // --- bread and butter ---
        new("90", 1, "nop"),
        new("C3", 1, "ret"),
        new("50", 1, "push rax"),
        new("EB 10", 2, "jmp rel8"),
        new("E8 00 00 00 00", 5, "call rel32"),
        new("0F 84 00 00 00 00", 6, "je rel32"),
        new("48 83 EC 28", 4, "sub rsp, 0x28  (REX + imm8)"),
        new("81 EC 00 01 00 00", 6, "sub esp, imm32"),

        // --- ModRM / SIB / displacement forms ---
        new("48 89 5C 24 08", 5, "mov [rsp+8], rbx  (SIB + disp8)"),
        new("48 8B 05 12 34 56 78", 7, "mov rax, [rip+disp32]"),
        new("48 8D 05 00 00 00 00", 7, "lea rax, [rip+0]"),
        new("8B 84 88 00 01 00 00", 7, "mov eax, [rax+rcx*4+disp32]"),
        new("41 8B 04 24", 4, "mov eax, [r12]  (SIB, no disp)"),
        new("48 89 04 25 00 10 00 00", 8, "mov [abs32], rax  (SIB, no base)"),
        new("FF 15 12 34 56 78", 6, "call [rip+disp32]"),
        new("FF 24 25 00 00 00 00", 7, "jmp [abs32]"),

        // --- immediates sized by prefix ---
        new("C7 43 10 00 00 80 3F", 7, "mov dword [rbx+0x10], 1.0f"),
        new("48 C7 C0 01 00 00 00", 7, "mov rax, 1  (REX.W, Iz stays 4 bytes)"),
        new("48 B8 88 77 66 55 44 33 22 11", 10, "movabs rax, imm64  (Iv + REX.W)"),
        new("B8 78 56 34 12", 5, "mov eax, imm32"),
        new("66 B8 34 12", 4, "mov ax, imm16  (Iv under 0x66)"),
        new("68 78 56 34 12", 5, "push imm32"),
        new("6A 00", 2, "push imm8"),
        new("69 C0 78 56 34 12", 6, "imul eax, eax, imm32"),
        new("6B C0 05", 3, "imul eax, eax, imm8"),
        new("80 3D 12 34 56 78 00", 7, "cmp byte [rip+disp32], 0"),
        new("C8 10 00 00", 4, "enter imm16, imm8  (two immediates)"),
        new("48 A1 00 11 22 33 44 55 66 77", 10, "movabs rax, [moffs64]"),

        // --- F6/F7: immediate depends on the ModRM.reg subopcode ---
        new("F7 D0", 2, "not eax  (/2 — no immediate)"),
        new("F7 C0 01 00 00 00", 6, "test eax, imm32  (/0 — Iz)"),
        new("F6 C1 01", 3, "test cl, imm8  (/0 — Ib)"),

        // --- SSE / x87 stores, i.e. what actually trips a float write breakpoint ---
        new("F3 0F 11 43 20", 5, "movss [rbx+0x20], xmm0"),
        new("F2 0F 11 45 F8", 5, "movsd [rbp-8], xmm0"),
        new("F3 0F 10 05 11 22 33 44", 8, "movss xmm0, [rip+disp32]"),
        new("0F 11 45 F0", 4, "movups [rbp-0x10], xmm0"),
        new("66 0F 7E C0", 4, "movd eax, xmm0"),
        new("0F B6 C0", 3, "movzx eax, al"),
        new("D9 45 F4", 3, "fld dword [rbp-0xC]"),
        new("DD 5D F8", 3, "fstp qword [rbp-8]"),

        // --- three-byte maps ---
        new("0F 38 00 C0", 4, "pshufb  (0F 38 map, no immediate)"),
        new("0F 3A 0B C0 01", 5, "roundsd  (0F 3A map, always imm8)"),
        new("66 0F 3A 0B C0 01", 6, "roundsd with 0x66"),

        // --- VEX ---
        new("C5 FA 11 43 20", 5, "vmovss [rbx+0x20], xmm0  (2-byte VEX)"),
        new("C4 E1 7A 10 43 20", 6, "vmovss xmm0, [rbx+0x20]  (3-byte VEX)"),

        // --- string ops and multi-byte nops ---
        new("F3 A4", 2, "rep movsb"),
        new("F3 48 AB", 3, "rep stosq"),
        new("9C", 1, "pushfq"),
        new("0F 1F 44 00 00", 5, "nop dword [rax+rax]"),
        new("66 0F 1F 84 00 00 00 00 00", 9, "nop word [rax+rax]  (9-byte nop)"),
    };

    public static int Run()
    {
        int failed = 0;

        Console.WriteLine("x86-64 length decoder — reference encodings\n");
        foreach (var c in Lengths)
        {
            var bytes = Parse(c.Bytes);
            var decoded = X64Decoder.Decode(bytes, 0, 0x140000000);
            int actual = decoded?.Length ?? -1;
            bool ok = actual == c.Length;
            if (!ok) failed++;
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {c.Bytes,-30} want {c.Length,2}  got {actual,2}   {c.What}");
        }

        Console.WriteLine("\nbackward resolution — which instruction ends at the trapped RIP\n");
        failed += RunBackward();

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"All {Lengths.Length} length cases + backward cases passed."
            : $"{failed} case(s) FAILED.");
        return failed;
    }

    /// <summary>
    /// The real job: given a stream of code and the RIP the CPU reported, recover the
    /// instruction that just retired. Each case is "leading code" + the writer + a trailing
    /// instruction; the trap RIP sits immediately after the writer.
    /// </summary>
    private static int RunBackward()
    {
        (string Lead, string Writer, string Trail, string What)[] cases =
        {
            ("48 8B 41 08 48 85 C0 74 0C", "F3 0F 11 43 20", "48 8B 5C 24 30",
                "movss [rbx+0x20], xmm0"),
            ("55 48 89 E5 48 83 EC 20", "89 46 10", "5D C3",
                "mov [rsi+0x10], eax"),
            ("0F 28 C1 0F 57 D2 48 8D 4C 24 40", "F2 0F 11 4B 18", "E8 00 00 00 00",
                "movsd [rbx+0x18], xmm1"),
            ("48 83 EC 28 48 8B 05 11 22 33 44", "C7 40 08 00 00 80 3F", "48 83 C4 28",
                "mov dword [rax+8], 1.0f"),
            ("31 C0 48 8D 3D 00 00 00 00 B9 10 00 00 00", "F3 AB", "5F C3",
                "rep stosd (an inlined memset)"),
        };

        int failed = 0;
        const ulong baseAddress = 0x140001000;

        foreach (var (lead, writer, trail, what) in cases)
        {
            var code = Parse($"{lead} {writer} {trail}");
            int leadLength = Parse(lead).Length;
            int writerLength = Parse(writer).Length;
            ulong rip = baseAddress + (ulong)(leadLength + writerLength);

            var found = X64Decoder.FindWriterEndingAt(code, baseAddress, rip);
            ulong wantAddress = baseAddress + (ulong)leadLength;
            bool ok = found is not null
                      && found.Address == wantAddress
                      && found.Length == writerLength;

            if (!ok) failed++;
            string got = found is null ? "(none)" : $"0x{found.Address:X} len {found.Length}";
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} want 0x{wantAddress:X} len {writerLength,2}  got {got,-22} {what}");
            if (found is not null)
                Console.WriteLine($"       aob: {found.ToWildcardPattern()}   writes-memory: {found.CanWriteMemory}");
        }

        return failed;
    }

    private static byte[] Parse(string hex)
    {
        var tokens = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            bytes[i] = Convert.ToByte(tokens[i], 16);
        return bytes;
    }
}
