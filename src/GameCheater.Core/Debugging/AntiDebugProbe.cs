using System.Diagnostics;
using GameCheater.Core.Memory;
using GameCheater.Core.Native;

namespace GameCheater.Core.Debugging;

/// <summary>What a single anti-debug survival experiment learned.</summary>
public sealed record AntiDebugProbeResult
{
    public ulong? PebBase { get; init; }
    public PebDebugFlags? BeforeAttach { get; init; }
    public PebDebugFlags? AfterAttach { get; init; }
    public bool ClearedPeb { get; init; }
    public bool Survived { get; init; }
    public double SecondsElapsed { get; init; }
    public int DebugEvents { get; init; }
    public PebDebugFlags? Final { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// A plain-language read of what the numbers imply — the whole point of the experiment is
    /// to answer "will PEB-clearing be enough, or do we need to hook the kernel query?".
    /// </summary>
    public string Diagnosis
    {
        get
        {
            if (Error is not null)
                return $"couldn't run: {Error}";
            if (Survived)
                return ClearedPeb
                    ? "SURVIVED with PEB flags cleared — the check was user-mode (PEB/IsDebuggerPresent), " +
                      "and clearing beats it. Wire the clearer into WriteWatch and proceed."
                    : "SURVIVED even without clearing — this process wasn't the anti-debug tripwire " +
                      "after all; re-check the earlier death.";
            bool attachSetFlags = BeforeAttach is { LooksDebugged: false } && AfterAttach is { LooksDebugged: true };
            if (ClearedPeb && attachSetFlags)
                return "DIED despite the PEB flags being set by attach and then cleared before the game " +
                       "resumed. The check is almost certainly kernel-side (ProcessDebugPort / " +
                       "DebugObjectHandle), which PEB editing cannot mask — the only user-mode fix left " +
                       "is hooking NtQueryInformationProcess inside the target (bigger, riskier).";
            if (ClearedPeb)
                return "DIED with PEB cleared, but attach didn't visibly set the PEB flags either — the " +
                       "mechanism isn't the PEB. Likely a kernel-side query or a self-debugging guard.";
            return "DIED (PEB not cleared this run).";
        }
    }
}

/// <summary>
/// A deliberately minimal, blocking debugger that attaches, optionally scrubs the PEB debug
/// flags, sets NO breakpoint, and simply times how long the target stays alive. It exists to
/// answer one question for the price of one game restart: is a game's anti-debug check
/// user-mode (beatable by <see cref="AntiDebug"/>) or kernel-side (not)?
///
/// Everything runs on the caller's thread, because <c>DebugActiveProcess</c> and
/// <c>WaitForDebugEvent</c> must share a thread. The PEB is cleared while the target is still
/// frozen at the initial attach break — the earliest possible moment, before any of its code
/// runs again — which makes a subsequent death strong evidence that the PEB was never the tell.
/// </summary>
public static class AntiDebugProbe
{
    public static AntiDebugProbeResult Run(ProcessMemory memory, int seconds, bool clearPeb,
        Action<string>? log = null)
    {
        uint pid = (uint)memory.Process.Id;
        var before = AntiDebug.Read(memory);
        log?.Invoke($"PEB base: {(memory.GetPebBaseAddress() is { } p ? $"0x{p:X}" : "(unknown)")}");
        log?.Invoke($"before attach:  {Describe(before)}");

        if (!Win32.DebugActiveProcess(pid))
        {
            return new AntiDebugProbeResult
            {
                Error = $"DebugActiveProcess failed (err {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})",
                BeforeAttach = before,
            };
        }
        Win32.DebugSetProcessKillOnExit(false);

        PebDebugFlags? afterAttach = null;
        bool cleared = false;
        bool sawInitialBreak = false;
        int events = 0;
        bool exited = false;
        var sw = Stopwatch.StartNew();

        try
        {
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                if (Win32.WaitForDebugEvent(out var e, 50))
                {
                    events++;

                    // The first stop is the synthetic attach break — the target is frozen here,
                    // so read what attach did to the PEB and scrub it before anything resumes.
                    if (!sawInitialBreak && e.DebugEventCode == Win32.EXCEPTION_DEBUG_EVENT
                        && e.ExceptionCode == Win32.EXCEPTION_BREAKPOINT)
                    {
                        sawInitialBreak = true;
                        afterAttach = AntiDebug.Read(memory);
                        log?.Invoke($"after attach:   {Describe(afterAttach)}");
                        if (clearPeb)
                        {
                            cleared = AntiDebug.Clear(memory);
                            log?.Invoke($"cleared PEB:    {cleared} → {Describe(AntiDebug.Read(memory))}");
                        }
                    }

                    if (e.DebugEventCode == Win32.EXIT_PROCESS_DEBUG_EVENT)
                    {
                        exited = true;
                        Win32.ContinueDebugEvent(e.ProcessId, e.ThreadId, Win32.DBG_CONTINUE);
                        break;
                    }

                    Win32.ContinueDebugEvent(e.ProcessId, e.ThreadId, Win32.DBG_CONTINUE);
                }

                // Re-scrub between events — the game re-reads the flags on its own loop.
                if (clearPeb && sawInitialBreak)
                    cleared |= AntiDebug.Clear(memory);

                if (memory.Process.HasExited)
                {
                    exited = true;
                    break;
                }
            }
        }
        finally
        {
            if (!exited && !memory.Process.HasExited)
                Win32.DebugActiveProcessStop(pid);
        }

        return new AntiDebugProbeResult
        {
            PebBase = memory.GetPebBaseAddress(),
            BeforeAttach = before,
            AfterAttach = afterAttach,
            ClearedPeb = cleared,
            Survived = !exited && !memory.Process.HasExited,
            SecondsElapsed = sw.Elapsed.TotalSeconds,
            DebugEvents = events,
            Final = exited ? null : AntiDebug.Read(memory),
        };
    }

    private static string Describe(PebDebugFlags? flags) => flags?.ToString() ?? "(PEB unreadable)";
}
