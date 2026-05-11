--============================================================================
-- StellarForceAdapt CE Bridge
-- Load after NidasBot's "Stellar Blade" table [Player Pointers] is enabled.
-- Reads resolved player stats and writes binary state to a temp file.
--
-- Usage in Cheat Engine (CE 7.5+):
--   1. Open SB-Win64-Shipping.exe
--   2. Load NidasBot CT → enable [Player Pointers]
--   3. Ctrl+Alt+L → paste this script → Execute
--   4. Keep CE running alongside StellarForceAdapt.exe
--============================================================================

local INTERVAL_MS = 5

-- Use Public user directory — writable, no Unicode issues on Chinese Windows
local dir = "C:\\Users\\Public\\StellarForceAdapt"
local STATE_FILE = dir .. "\\ce_state.bin"

os.execute('mkdir "' .. dir .. '" 2>nul')

--============================================================================
-- Resolve stats base via NidasBot pointer chain
-- NBSB01_ptr → player actor → [0, 0, 28, C8] → stats struct
--============================================================================
local function resolveStatsBase()
  local addr = getAddressSafe("NBSB01_ptr")
  if addr == nil or addr == 0 then return nil end
  local ptr = readQword(addr)
  if ptr == nil or ptr == 0 then return nil end

  ptr = readQword(ptr + 0)
  if ptr == nil or ptr == 0 then return nil end
  ptr = readQword(ptr + 0)
  if ptr == nil or ptr == 0 then return nil end
  ptr = readQword(ptr + 0x28)
  if ptr == nil or ptr == 0 then return nil end
  return readQword(ptr + 0xC8)
end

--============================================================================
-- Build binary state packet
-- Layout (little-endian):
--   0x00  Health         float
--   0x04  MaxHealth      float
--   0x08  BetaEnergy     float
--   0x0C  MaxBetaEnergy  float
--   0x10  BurstEnergy    float
--   0x14  MaxBurstEnergy float
--   0x18  TachyEnergy    float
--   0x1C  MaxTachyEnergy float
--   0x20  Stamp          int32 (incremented each write, for freshness)
--============================================================================
local function packFloat(buf, off, val)
  -- CE Lua 5.3: string.pack with native endianness
  local s = string.pack("f", val or 0.0)
  for i = 1, 4 do
    buf[off + i - 1] = string.byte(s, i)
  end
end

local function packInt32(buf, off, val)
  local s = string.pack("I4", val or 0)
  for i = 1, 4 do
    buf[off + i - 1] = string.byte(s, i)
  end
end

local stamp = 0

local function writeState()
  local stats = resolveStatsBase()
  if stats == nil then return end

  local buf = {}
  packFloat(buf, 0,  readFloat(stats + 0x11C))  -- Health
  packFloat(buf, 4,  readFloat(stats + 0x120))  -- MaxHealth
  packFloat(buf, 8,  readFloat(stats + 0x150))  -- Beta
  packFloat(buf, 12, readFloat(stats + 0x154))  -- MaxBeta
  packFloat(buf, 16, readFloat(stats + 0x160))  -- Burst
  packFloat(buf, 20, readFloat(stats + 0x164))  -- MaxBurst
  packFloat(buf, 24, readFloat(stats + 0x170))  -- Tachy
  packFloat(buf, 28, readFloat(stats + 0x174))  -- MaxTachy

  stamp = (stamp + 1) % 0x7FFFFFFF
  packInt32(buf, 32, stamp)

  -- Write atomically: temp file → rename
  local tmpFile = STATE_FILE .. ".tmp"
  local f = io.open(tmpFile, "wb")
  if f == nil then return end
  f:write(string.char(table.unpack(buf)))
  f:close()
  os.rename(tmpFile, STATE_FILE)
end

--============================================================================
-- Main timer loop
--============================================================================
local timer = createTimer(nil, false)
timer.Interval = INTERVAL_MS
timer.OnTimer = function(t)
  local ok, err = pcall(writeState)
  if not ok then
    print("[SFA Bridge] Error: " .. tostring(err))
  end
end
timer.Enabled = true

local stats = resolveStatsBase()
if stats then
  print("[SFA Bridge] Stats base resolved: 0x" .. string.format("%X", stats))
else
  print("[SFA Bridge] WARNING: stats base not yet resolved — waiting for NBSB01_ptr hook...")
end
print("[SFA Bridge] Writing to: " .. STATE_FILE .. " every " .. INTERVAL_MS .. "ms")
