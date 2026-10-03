using KeraLua;

/// <summary>
/// Small push/PCall helper for the bootstrap-defined globals (__openlot_*). Every call fetches
/// the function by name instead of holding a LuaFunction reference: NLua LuaFunction wrappers pin
/// Lua registry references and are easy to leak, and the bootstrap globals are invisible to
/// creator code anyway (sandbox envs only see the default-deny whitelist). The stack top is
/// restored in a finally so a failing call leaves nothing behind.
/// </summary>
internal static class LuaCall
{
	internal static bool CallBool(KeraLua.Lua state, string functionName)
	{
		int top = state.GetTop();
		try
		{
			state.GetGlobal(functionName);
			RequireOk(state, state.PCall(0, 1, 0), functionName);
			return state.ToBoolean(-1);
		}
		finally
		{
			state.SetTop(top);
		}
	}

	internal static void CallVoid(KeraLua.Lua state, string functionName)
	{
		int top = state.GetTop();
		try
		{
			state.GetGlobal(functionName);
			RequireOk(state, state.PCall(0, 0, 0), functionName);
		}
		finally
		{
			state.SetTop(top);
		}
	}

	internal static void CallVoidInt(KeraLua.Lua state, string functionName, int argument)
	{
		int top = state.GetTop();
		try
		{
			state.GetGlobal(functionName);
			state.PushInteger(argument);
			RequireOk(state, state.PCall(1, 0, 0), functionName);
		}
		finally
		{
			state.SetTop(top);
		}
	}

	internal static void CallVoidIntString(KeraLua.Lua state, string functionName, int intArgument, string stringArgument)
	{
		int top = state.GetTop();
		try
		{
			state.GetGlobal(functionName);
			state.PushInteger(intArgument);
			state.PushString(stringArgument ?? "");
			RequireOk(state, state.PCall(2, 0, 0), functionName);
		}
		finally
		{
			state.SetTop(top);
		}
	}

	internal static double CallNumber(KeraLua.Lua state, string functionName)
	{
		int top = state.GetTop();
		try
		{
			state.GetGlobal(functionName);
			RequireOk(state, state.PCall(0, 1, 0), functionName);
			return state.ToNumber(-1);
		}
		finally
		{
			state.SetTop(top);
		}
	}

	private static void RequireOk(KeraLua.Lua state, LuaStatus status, string functionName)
	{
		if (status == LuaStatus.OK) return;
		string message = state.ToString(-1, false);
		state.SetTop(state.GetTop() - 1);
		throw new System.InvalidOperationException("Lua " + functionName + " failed: " + message);
	}
}

/// <summary>
/// The Lua-side half of Milestone 3.2's scripting layer: sandbox hardening, the per-entity net.*
/// tables, the validation pre-pass and the watchdog hook. One chunk on purpose — privileged
/// references (rawset, debug.sethook, load, ...) must be captured as upvalues before the globals
/// are stripped, and upvalues do not cross chunks.
///
/// Layering (clinerules 1.1): everything here is sugar over exactly two bound LotLuaApi methods,
/// NetRegister and NetInvoke. No Lua code in here touches Godot.
///
/// Sandbox rules enforced (design doc v4 §1/§6 + step-2 amendments):
///   - Envs are default-deny: only the whitelist table is visible, `_G` points at the env itself.
///   - getmetatable() is neutralised on the env metatable, every library proxy, the net tables
///     and the string metatable (__metatable = false), so the shared tables cannot be reached.
///   - rawset is not in the whitelist (it would write real keys into the shared proxies).
///   - setmetatable rejects any metatable containing __gc (Lua disables hooks in finalizers).
///   - string.rep is capped and the string metatable's __index routes to the capped proxy, so
///     ("x"):rep(...) cannot bypass it.
///   - pcall/xpcall are wrapped: once the watchdog trips, they re-throw instead of swallowing.
/// </summary>
public static class LuaBootstrap
{
	public const string Chunk = """
-- ===== OpenLot net.* bootstrap + sandbox (Milestone 3.2, step 3) =====
-- Privileged references, captured before anything is replaced or stripped.
local rawget, rawset_priv, rawequal, rawlen = rawget, rawset, rawequal, rawlen
local setmetatable, getmetatable = setmetatable, getmetatable
local raw_pcall, raw_xpcall = pcall, xpcall
local unpack = table.unpack
local next, type, tostring, tonumber, select, error = next, type, tostring, tonumber, select, error
local math_type = math.type
local string_lib, table_lib, math_lib, utf8_lib = string, table, math, utf8
local debug_sethook = debug.sethook
local load_chunk = load

local MAX_REP_BYTES = 1048576
local MAX_DEPTH = 8
local WATCHDOG_INTERVAL = 10000

-- Shared state declared BEFORE any function that touches it: a local declared later in the chunk
-- would leave earlier function bodies compiled against a global of the same name (a silent,
-- very confusing divergence).
local currentSender = nil
local currentFromPlayer = false
local netRegistry = {}
local disabledEntities = {}
local entityScripts = {}

-- --- watchdog state (armed/cleared from C#; the hook itself lives here) ---
local tripped = false
local armed = false
local used = 0
local unitBudget = 0
local frameUsed = 0
-- Unlimited until the owner calls __openlot_beginFrame: a 0 default would make the very first
-- hook fire a false "frame execution limit" trip.
local frameBudget = math.huge

local function trip(message)
    tripped = true
    armed = true
    debug_sethook(function() error(message, 2) end, "", 1)
    error(message, 2)
end

local function watchdogHook()
    used = used + WATCHDOG_INTERVAL
    if used > unitBudget then
        trip("script execution limit exceeded")
    end
    frameUsed = frameUsed + WATCHDOG_INTERVAL
    if frameUsed > frameBudget then
        trip("frame execution limit exceeded")
    end
end

function __openlot_armWatchdog(budget)
    used = 0
    unitBudget = budget
    armed = true
    debug_sethook(watchdogHook, "", WATCHDOG_INTERVAL)
end

function __openlot_beginFrame(budget)
    frameUsed = 0
    frameBudget = budget
end

function __openlot_takeTripped()
    local was = tripped
    tripped = false
    return was
end

function __openlot_resetDispatch()
    currentSender = nil
    currentFromPlayer = false
    armed = false
    debug_sethook()
end

function __openlot_isArmed() return armed end
function __openlot_unitBudget() return unitBudget end
-- Instructions consumed by the current/last execution unit, rounded to the hook interval. Used by
-- the self-tests to CALIBRATE heavy scripts by measurement instead of guessing iteration counts.
function __openlot_unitUsed() return used end
function __openlot_disableNet(handle) disabledEntities[handle] = true end
-- One cleanup path for a destroyed entity (F2 / entity-destroy hook): drop its declarations, its
-- circuit-breaker record and its script name so nothing survives the node.
function __openlot_forgetEntity(handle)
    netRegistry[handle] = nil
    disabledEntities[handle] = nil
    entityScripts[handle] = nil
end
function __openlot_setEntityScript(handle, scriptName) entityScripts[handle] = scriptName end
-- Item 6: a (re)load is a clean slate. Clearing the registries up front stops a re-run from seeing
-- a previous load's declarations, disabled flags or script names (the C# side clears its mirror).
-- Cleared IN PLACE (not reassigned) so the upvalue identities makeNet's proxies captured stay
-- valid; a reload recreates the per-entity envs anyway, so their declarations start fresh.
function __openlot_resetRegistries()
    for key in next, netRegistry do netRegistry[key] = nil end
    for key in next, disabledEntities do disabledEntities[key] = nil end
    for key in next, entityScripts do entityScripts[key] = nil end
end

-- --- validation pre-pass (v4 A6): runs in the routing closure BEFORE NetInvoke, iterating with
-- next() so table identity is exact (Lua-side tables-as-keys), never with pairs().
local function validateValue(value, depth, seen)
    local kind = type(value)
    if kind == "number" then
        if value ~= value then return nil, "net: cannot send NaN" end
        return true
    end
    if kind == "boolean" or kind == "string" or kind == "nil" then
        return true
    end
    if kind ~= "table" then
        return nil, "net: cannot send " .. kind
    end
    -- getmetatable returns false for every sealed table (envs, libraries, strings), so only a
    -- real, creator-set metatable is a rejection here.
    if type(getmetatable(value)) == "table" then
        return nil, "net: cannot send table with metatable"
    end
    if depth > MAX_DEPTH then
        return nil, "net: table exceeds depth limit (" .. tostring(MAX_DEPTH) .. ")"
    end
    if seen[value] then
        return nil, "net: cannot send cyclic table"
    end
    seen[value] = true
    local key, nested = next(value)
    while key ~= nil do
        local keyKind = type(key)
        if keyKind == "string" then
            -- accepted
        elseif keyKind == "number" then
            if math_type(key) ~= "integer" then
                return nil, "net: table keys must be strings or integers"
            end
        else
            return nil, "net: table keys must be strings or integers"
        end
        local ok, err = validateValue(nested, depth + 1, seen)
        if ok == nil then
            seen[value] = nil
            return nil, err
        end
        key, nested = next(value, key)
    end
    seen[value] = nil -- shared (non-cyclic) references are allowed: they serialize by value twice
    return true
end

function __openlot_validate(packed, count)
    local seen = {}
    for index = 1, count do
        local ok, err = validateValue(packed[index], 1, seen)
        if ok == nil then return nil, err end
    end
    return true
end

-- --- per-entity net tables (design doc decision 1: makeNet(handle) remembers its owner) ---
local function makeNamespace(handle, direction)
    local registry = netRegistry[handle]
    if registry == nil then
        registry = {}
        netRegistry[handle] = registry
    end
    -- Shared per (handle, direction): every script on the entity declares INTO the same table, so
    -- handlers from two scripts on one entity coexist (only a same-NAME collision overwrites, and
    -- the router's redeclaration warning names both files — see net-api.md §2). Creating a fresh
    -- table here made the last script's env wipe out every earlier script's handlers (item 6).
    local declared = registry[direction]
    if declared == nil then
        declared = {}
        registry[direction] = declared
    end

    local proxy = setmetatable({}, {
        __index = function(_, name)
            -- ALWAYS a routing closure: the raw body stays in `declared`, reachable only by the
            -- dispatcher below, so net.server bodies run only where the router decides they may.
            return function(...)
                if disabledEntities[handle] then return end
                local count = select("#", ...)
                local packed = { n = count, ... }
                local ok, err = __openlot_validate(packed, count)
                if ok == nil then error(err, 2) end
                NetInvoke(handle, direction, name, packed)
            end
        end,
        __newindex = function(_, name, fn)
            if type(fn) ~= "function" then
                error("net." .. direction .. "." .. tostring(name) .. ": only functions can be declared", 2)
            end
            declared[name] = fn
            NetRegister(handle, direction, name, entityScripts[handle] or "unknown script")
        end,
        __call = function()
            error("net." .. direction .. " is a namespace; call a function on it", 2)
        end,
        __metatable = false,
    })
    return proxy
end

function makeNet(handle)
    local net = setmetatable({}, {
        __index = function(_, key)
            if key == "sender" then return currentSender end
            if key == "fromPlayer" then return currentFromPlayer end
            return nil
        end,
        __newindex = function()
            error("the net table is read-only", 2)
        end,
        __metatable = false,
    })
    rawset_priv(net, "server", makeNamespace(handle, "server"))
    rawset_priv(net, "client", makeNamespace(handle, "client"))
    return net
end

function __openlot_dispatch(handle, direction, name, sender, fromPlayer, args)
    local registry = netRegistry[handle]
    if registry == nil or disabledEntities[handle] then return false end
    local declared = registry[direction]
    if declared == nil then return false end
    local fn = declared[name]
    if fn == nil then return false end

    local previousSender, previousFromPlayer = currentSender, currentFromPlayer
    currentSender, currentFromPlayer = sender, fromPlayer
    local ok, err = raw_pcall(fn, unpack(args, 1, args.n))
    currentSender, currentFromPlayer = previousSender, previousFromPlayer
    if not ok then error(err, 0) end
    return true
end

-- --- environment loading (per-entity envs; ScriptRuntime owns WHO loads what) ---
function __openlot_newEnv(handle)
    local env = setmetatable({}, { __index = __openlot_sandbox, __metatable = false })
    rawset_priv(env, "_G", env) -- per-env alias: _G.x = 1 writes to THIS script's env, not the shared table
    rawset_priv(env, "net", makeNet(handle))
    rawset_priv(env, "this", setmetatable({}, {
        __index = { Handle = handle },
        __newindex = function() error("this is read-only", 2) end,
        __metatable = false,
    }))
    return env
end

function __openlot_load(chunk, chunkName, env)
    local fn, err = load_chunk(chunk, chunkName, "t", env)
    if fn == nil then error(err, 2) end
    return fn
end

-- --- sandbox ---
local function safe_pcall(f, ...)
    local results = { n = select("#", ...) + 1, raw_pcall(f, ...) }
    if tripped then error("script execution limit exceeded", 2) end
    return unpack(results, 1, results.n)
end

local function safe_xpcall(f, handler, ...)
    local results = { n = select("#", ...) + 1, raw_xpcall(f, handler, ...) }
    if tripped then error("script execution limit exceeded", 2) end
    return unpack(results, 1, results.n)
end

local function safe_setmetatable(target, mt)
    if type(mt) == "table" and rawget(mt, "__gc") ~= nil then
        error("setmetatable: __gc is not allowed in sandboxed scripts", 2)
    end
    return setmetatable(target, mt)
end

local function readonlyLib(name, source, overrides)
    local proxy = setmetatable({}, {
        __index = function(_, key)
            if overrides ~= nil then
                local custom = overrides[key]
                if custom ~= nil then return custom end
            end
            return source[key]
        end,
        __newindex = function()
            error("attempt to modify read-only library '" .. name .. "'", 2)
        end,
        __metatable = false,
    })
    return proxy
end

local stringProxy = readonlyLib("string", string_lib, {
    rep = function(s, n, sep)
        if type(s) == "string" and type(n) == "number" then
            local unit = #s + (sep ~= nil and #sep or 0)
            if unit * n > MAX_REP_BYTES then
                error("string.rep result too large (max " .. tostring(MAX_REP_BYTES) .. " bytes)", 2)
            end
        end
        return string_lib.rep(s, n, sep)
    end,
})

-- Sealing the string metatable closes getmetatable("").__index, and pointing __index at the
-- capped proxy means method syntax ("x"):rep(...) cannot bypass the wrapper either.
local stringMeta = getmetatable("")
if type(stringMeta) == "table" then
    stringMeta.__index = stringProxy
    stringMeta.__metatable = false
end

-- Cross-entity calls (step 5): the same envelope/queue/router path as net.*, just with someone
-- else's handle as the target. Packed + validated here (never through C# params marshaling, which
-- mangles nil holes), then handed to the bound methods by name.
local function crossEntityCall(boundMethod, handle, name, ...)
    if type(handle) ~= "number" then
        error("Lot cross-entity calls: handle must be a number", 2)
    end
    if type(name) ~= "string" then
        error("Lot cross-entity calls: function name must be a string", 2)
    end
    local count = select("#", ...)
    local packed = { n = count, ... }
    local ok, err = __openlot_validate(packed, count)
    if ok == nil then error(err, 2) end
    boundMethod(handle, name, packed)
end

local sandbox = {
    math = readonlyLib("math", math_lib),
    string = stringProxy,
    table = readonlyLib("table", table_lib),
    utf8 = readonlyLib("utf8", utf8_lib),
    pairs = pairs, ipairs = ipairs, next = next,
    type = type, tostring = tostring, tonumber = tonumber, select = select,
    error = error, pcall = safe_pcall, xpcall = safe_xpcall,
    rawget = rawget, rawequal = rawequal, rawlen = rawlen,
    setmetatable = safe_setmetatable, getmetatable = getmetatable,
    unpack = unpack,
    -- Overrides shadow the raw (LuaTable-taking) bound methods, so creators only ever see the
    -- vararg form with the validation pre-pass applied.
    Lot = readonlyLib("Lot", Lot, {
        CallServer = function(handle, name, ...) return crossEntityCall(CallServer, handle, name, ...) end,
        CallClient = function(handle, name, ...) return crossEntityCall(CallClient, handle, name, ...) end,
    }),
    log = function(message) return Lot.Log(tostring(message)) end,
    -- Convenience wrappers matching the project's default script template; they only call Lot.*.
    cube = function(x, y, z) return Lot.SpawnCube(x or 0, y or 0, z or 0) end,
    sphere = function(x, y, z) return Lot.SpawnSphere(x or 0, y or 0, z or 0) end,
    cylinder = function(x, y, z) return Lot.SpawnCylinder(x or 0, y or 0, z or 0) end,
    capsule = function(x, y, z) return Lot.SpawnCapsule(x or 0, y or 0, z or 0) end,
}
__openlot_sandbox = sandbox

-- --- strip everything a creator must not reach (scratch chunks in the global env included) ---
io = nil
os = nil
require = nil
dofile = nil
loadfile = nil
package = nil
debug = nil
load = nil
collectgarbage = nil
coroutine = nil
rawset = nil
print = nil
luanet = nil
""";

	/// <summary>
	/// Runs the bootstrap on a state whose `Lot` table already exists (LuaManager registers the
	/// API surface first). Idempotent per state: each state gets exactly one bootstrap.
	/// </summary>
	public static void Apply(NLua.Lua state)
	{
		if (state == null) throw new System.ArgumentNullException(nameof(state));
		state.DoString(Chunk, "openlot_bootstrap_v2");
	}
}
