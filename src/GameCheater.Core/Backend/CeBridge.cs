namespace GameCheater.Core.Backend;

/// <summary>
/// The bridge between our app and Cheat Engine. We can't drive CE's GUI directly, but CE has
/// a Lua engine and an autorun folder. We drop <see cref="Script"/> into CE's autorun; it
/// starts a tiny file-based command loop that our <see cref="CeBackend"/> talks to — loading
/// a table, attaching to the game, and toggling memory records (the Lua/AA cheats CE runs
/// natively). File IPC (not sockets) keeps it dependency-free and easy to debug.
///
/// CE API note: this targets Cheat Engine 7.x Lua (loadTable / openProcess / getAddressList /
/// memoryrecord.Active / createTimer). CE's API drifts between versions, so this bridge is a
/// first cut to validate against a real CE install on Windows.
/// </summary>
public static class CeBridge
{
    public const string BridgeFileName = "gamecheater_bridge.lua";

    // Shared IPC folder (must match CeBackend.BridgeDir): %AppData%\GameCheater\ce-bridge
    // Files: command.txt (we write), response.txt (CE writes), records.txt (CE writes), status.txt
    public const string CommandFile = "command.txt";
    public const string ResponseFile = "response.txt";
    public const string RecordsFile = "records.txt";
    public const string StatusFile = "status.txt";

    public static readonly string Script = """
        -- GameCheater <-> Cheat Engine bridge (autorun). File-based command loop.
        -- Commands (one line in command.txt): LOAD <path> | ATTACH <process> | LIST | ENABLE <i> | DISABLE <i>
        local dir = os.getenv("APPDATA") .. "\\GameCheater\\ce-bridge\\"
        local cmdPath  = dir .. "command.txt"
        local respPath = dir .. "response.txt"
        local recPath  = dir .. "records.txt"
        local statPath = dir .. "status.txt"

        local function writeFile(path, text)
          local f = io.open(path, "w")
          if f then f:write(text or "") f:close() end
        end
        local function readFile(path)
          local f = io.open(path, "r")
          if not f then return nil end
          local c = f:read("*a") f:close() return c
        end

        local function doList()
          local al = getAddressList()
          local lines = {}
          for i = 0, al.Count - 1 do
            local mr = al.getMemoryRecord(i)
            local desc = mr.Description or ""
            lines[#lines + 1] = string.format("%d|%s|%s", i, desc, tostring(mr.Active))
          end
          writeFile(recPath, table.concat(lines, "\n"))
        end

        local function setActive(idx, on)
          local mr = getAddressList().getMemoryRecord(idx)
          if not mr then return false end
          mr.Active = on
          return true
        end

        local function handle(cmd)
          local op, arg = cmd:match("^(%S+)%s*(.*)$")
          if op == "LOAD" then
            loadTable(arg, false)
            writeFile(respPath, "ok load")
          elseif op == "ATTACH" then
            local ok = openProcess(arg)
            writeFile(respPath, ok and "ok attach" or "err attach")
          elseif op == "LIST" then
            doList()
            writeFile(respPath, "ok list")
          elseif op == "ENABLE" then
            writeFile(respPath, setActive(tonumber(arg), true) and "ok enable" or "err norecord")
          elseif op == "DISABLE" then
            writeFile(respPath, setActive(tonumber(arg), false) and "ok disable" or "err norecord")
          elseif op == "PING" then
            writeFile(respPath, "ok pong")
          else
            writeFile(respPath, "err unknown")
          end
        end

        local timer = createTimer(nil)
        timer.Interval = 250
        timer.OnTimer = function(t)
          local cmd = readFile(cmdPath)
          if cmd and #cmd > 0 then
            writeFile(cmdPath, "")               -- consume the command
            local ok, err = pcall(handle, cmd)
            if not ok then writeFile(respPath, "err " .. tostring(err)) end
          end
        end

        writeFile(statPath, "ready")
        """;
}
