using GameCheater.Core.Cheats;
using GameCheater.Core.Definitions;

namespace GameCheater.Demo;

/// <summary>Process-free lifecycle tests for transactional composite toggles.</summary>
public static class CompositeCheatSelfTest
{
    public static int Run()
    {
        Console.WriteLine("\ncomposite cheats — transactional member toggles\n");
        int failed = 0;

        failed += RunCase("enables and disables every member", () =>
        {
            var (master, first, second) = Make();
            master.Enable();
            Require(master.Enabled && first.Enabled && second.Enabled);
            master.Disable();
            Require(!master.Enabled && !first.Enabled && !second.Enabled);
        });

        failed += RunCase("preserves a member that was already enabled", () =>
        {
            var (master, first, second) = Make();
            first.Enable();
            master.Enable();
            master.Disable();
            Require(!master.Enabled && first.Enabled && !second.Enabled);
        });

        failed += RunCase("rolls back earlier members when a later member fails", () =>
        {
            var first = new TestCheat() { Name = "First" };
            var failing = new TestCheat(failEnable: true) { Name = "Failing" };
            var master = new CompositeCheat(new[] { first, failing }) { Name = "Master" };
            var trainer = new Trainer("Test", "Test");
            trainer.Add(first);
            trainer.Add(failing);
            trainer.Add(master);

            try { master.Enable(); }
            catch (InvalidOperationException) { }
            Require(!master.Enabled && !first.Enabled && !failing.Enabled);
        });

        failed += RunCase("loads a JSON composite declared before its members", () =>
        {
            const string json = """
                {
                  "game": "Test",
                  "process": "Test",
                  "cheats": [
                    {
                      "name": "Master",
                      "type": "composite",
                      "hideMembers": true,
                      "members": ["First", "Second"]
                    },
                    {
                      "name": "First",
                      "type": "freeze",
                      "valueType": "int",
                      "value": "0",
                      "resolve": { "kind": "static", "moduleOffset": "0x10" }
                    },
                    {
                      "name": "Second",
                      "type": "freeze",
                      "valueType": "int",
                      "value": "0",
                      "resolve": { "kind": "static", "moduleOffset": "0x20" }
                    }
                  ]
                }
                """;

            var trainer = TrainerDefinitionLoader.ToTrainer(
                TrainerDefinitionLoader.Parse(json), out var skipped);
            Require(skipped.Count == 0);
            Require(trainer.Cheats[0] is CompositeCheat { Members.Count: 2, HideMembers: true });
        });

        Console.WriteLine(failed == 0 ? "All composite cases passed." : $"{failed} composite case(s) FAILED.");
        return failed;
    }

    private static (CompositeCheat Master, TestCheat First, TestCheat Second) Make()
    {
        var first = new TestCheat() { Name = "First" };
        var second = new TestCheat() { Name = "Second" };
        var master = new CompositeCheat(new[] { first, second }) { Name = "Master" };
        var trainer = new Trainer("Test", "Test");
        trainer.Add(first);
        trainer.Add(second);
        trainer.Add(master);
        return (master, first, second);
    }

    private static int RunCase(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"  ok   {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
            return 1;
        }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Unexpected enabled state.");
    }

    private sealed class TestCheat(bool failEnable = false) : Cheat
    {
        protected override void OnEnable()
        {
            if (failEnable) throw new InvalidOperationException("expected failure");
        }

        protected override void OnDisable() { }
    }
}
