-- Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
-- GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
-- content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
-- WO-217 synthetic test: C (loot_live) and D (item_flags) against the real kdcmp.lua under MoonSharp (WO-195's loot stubs, with the
-- engine's move: an item added to one inventory leaves the one it was in). Driven by Test-WO217Synthetic.ps1.
--   J  the joiner: the live list opens the screen on the host's items by id (moves only, no deletes in the copy); a take goes into the
--      escrow and enters the pack only on the grant; a refusal deletes it and says "Already taken"; no answer: cancelled, a late grant
--      ignored; a put is not swept out by an older list
--   H  the host: the live list carries item ids, owners and stolen marks; a take is granted once (out of the body into the escrow), the
--      second asker refused; the ack deletes the escrow's copy, a cancel puts it back into the body; the host's own take is logged
--   T  the twin (loot_live off): nothing of this runs
-- What this proves: the Lua half (the ledger's rules). Live evidence: docs/WO-217-findings.md.

NOW = 0
os.clock = function() return NOW end
LOG = {}; TIMERS = {}; ENTS = {}; ERRS = {}; TOASTS = {}; CMDS = {}; CCMDS = {}; LOCKS = {}

local function mkstub()
    return setmetatable({}, { __index = function(_, k) return function(...) return nil end end })
end
local function d2(a, b) return (a.x - b.x) ^ 2 + (a.y - b.y) ^ 2 + (a.z - b.z) ^ 2 end
System = mkstub()
System.LogAlways = function(s) LOG[#LOG + 1] = tostring(s) end
System.GetCVarValue = function() return "0" end
System.GetCVar = function() return "0" end
System.GetEntityByName = function(n) return ENTS[n] end
System.GetEntitiesInSphere = function(p, r)
    local o = {}
    for _, e in pairs(ENTS) do
        if e.GetWorldPos and d2(e:GetWorldPos(), p) <= r * r then o[#o + 1] = e end
    end
    table.sort(o, function(a, b) return a.id < b.id end)
    return o
end
System.ExecuteCommand = function(s) CMDS[#CMDS + 1] = tostring(s) end
System.RemoveEntity = function(eid) for n, e in pairs(ENTS) do if e.id == eid then ENTS[n] = nil end end end
System.AddCCommand = function(name, body, help) CCMDS[name] = true end
Script = mkstub()
Script.SetTimer = function(ms, f) TIMERS[#TIMERS + 1] = { ms = ms, f = f, at = NOW } end
Game = mkstub(); AI = mkstub(); Sound = mkstub(); Physics = mkstub(); Terrain = mkstub()
Game.AddSaveLock = function(name) LOCKS[name] = true; return true end
Game.RemoveSaveLock = function(name) LOCKS[name] = nil; return true end
UIAction = mkstub()
UIAction.CallFunction = function(panel, inst, fn, text) TOASTS[#TOASTS + 1] = tostring(text) end
WORLD_T = 1000
Calendar = { GetWorldTime = function() return WORLD_T end, SetWorldTime = function(t) WORLD_T = t end,
             GetWorldTimeRatio = function() return 15 end, SetWorldTimeRatio = function() end }

-- ---- items and inventories: the engine's move (AddItem takes the item out of the inventory that held it) ----
ITEMS = {}          -- wuid -> { class, amount, health, owner, stolen }
NEXTWUID = 100
INVS = {}
local function newItem(cls, hp, amt)
    NEXTWUID = NEXTWUID + 1
    local w = "wuid" .. NEXTWUID
    ITEMS[w] = { class = cls, amount = amt or 1, health = hp or 1, id = w }
    return w
end
ItemManager = mkstub()
ItemManager.GetItem = function(w) return ITEMS[w] end
ItemManager.GetItemOwner = function(w) return ITEMS[w] and ITEMS[w].owner or nil end
ItemManager.SetItemOwner = function(w, o, stolen) if ITEMS[w] then ITEMS[w].owner = o; ITEMS[w].stolen = stolen end end
MERGE_STACKS = {}   -- optional: class GUID -> true to emulate engine stack merging on AddItem
local function mkInventory()
    local inv = { list = {} }
    INVS[#INVS + 1] = inv
    inv.GetInventoryTable = function(self) local t = {}; for i, w in ipairs(self.list) do t[i] = w end; return t end
    inv.CreateItem = function(self, cls, hp, amt) local w = newItem(cls, hp, amt); self.list[#self.list + 1] = w; return true end
    inv.AddItem = function(self, w)
        if not ITEMS[w] then error("no such item") end
        for _, other in ipairs(INVS) do
            for i, x in ipairs(other.list) do if x == w then table.remove(other.list, i); break end end
        end
        if MERGE_STACKS[ITEMS[w].class] then
            for _, x in ipairs(self.list) do
                if x ~= w and ITEMS[x] and ITEMS[x].class == ITEMS[w].class and math.abs((ITEMS[x].health or 1) - (ITEMS[w].health or 1)) <= 0.01 then
                    ITEMS[x].amount = ITEMS[x].amount + ITEMS[w].amount
                    ITEMS[w] = nil
                    return
                end
            end
        end
        self.list[#self.list + 1] = w
    end
    inv.HasItem = function(self, w) for _, x in ipairs(self.list) do if x == w then return true end end; return false end
    inv.DeleteItem = function(self, w, n)
        for i, x in ipairs(self.list) do
            if x == w then
                if n and n > 0 and ITEMS[w] and ITEMS[w].amount > n then
                    ITEMS[w].amount = ITEMS[w].amount - n
                    return true
                end
                table.remove(self.list, i); ITEMS[w] = nil; return true
            end
        end
        return false
    end
    inv.RemoveItem = function(self, w) for i, x in ipairs(self.list) do if x == w then table.remove(self.list, i); return w end end end
    inv.GetCountOfClass = function(self, cls) local n = 0; for _, w in ipairs(self.list) do if ITEMS[w] and ITEMS[w].class == cls then n = n + ITEMS[w].amount end end; return n end
    inv.GetId = function() return "inv" end
    return inv
end
NEXTID = 5000

player = { id = 1, class = "Player", inventory = mkInventory(), this = { id = "wuid_player" } }
player.GetName = function() return "Dude" end
player.GetWorldPos = function() return { x = 100, y = 100, z = 10 } end
player.GetWorldAngles = function() return { x = 0, y = 0, z = 0 } end
player.actor = { GetHealth = function() return 100 end, IsDead = function() return false end,
                 UnequipInventoryItem = function(_, w) UNEQUIPPED = (UNEQUIPPED or 0) + 1 end }
player.human = { IsInDialog = function() return false end }
ENTS["Dude"] = player

System.SpawnEntity = function(t)
    NEXTID = NEXTID + 1
    local e = { class = t.class, id = NEXTID, px = t.position.x, py = t.position.y, pz = t.position.z, inventory = mkInventory(), hidden = false }
    e.GetName = function() return t.name end
    e.GetWorldPos = function(self) return { x = self.px, y = self.py, z = self.pz } end
    e.Hide = function(self, v) self.hidden = v == 1 end
    e.IsHidden = function(self) return self.hidden end
    ENTS[t.name] = e
    return e
end

local rawpcall = pcall
pcall = function(f, ...)
    local r = { rawpcall(f, ...) }
    if not r[1] then ERRS[#ERRS + 1] = tostring(r[2]) end
    return table.unpack(r)
end

local function mkBody(name, x, y, z)
    NEXTID = NEXTID + 1
    local e = { class = "NPC", id = NEXTID, px = x, py = y, pz = z, dead = true, inventory = mkInventory(), equipped = {}, this = { id = "wuid_" .. name } }
    e.GetName = function() return name end
    e.GetWorldPos = function(self) return { x = self.px, y = self.py, z = self.pz } end
    e.actor = { IsDead = function() return e.dead end, IsUnconscious = function() return false end,
                EquipInventoryItem = function(_, w) e.equipped[#e.equipped + 1] = w end }
    e.human = { IsInDialog = function() return false end }
    e.Hide = function() end
    e.IsHidden = function() return false end
    ENTS[name] = e
    return e
end
XGenAIModule = mkstub()
XGenAIModule.GetEntityByWUID = function(w) for _, e in pairs(ENTS) do if e.this and e.this.id == w then return e end end end
LOOTS = {}
BasicAIActions = {}
function BasicAIActions:OnLoot(user, slot) LOOTS[#LOOTS + 1] = self:GetName() end
function BasicAIActions:OnPickpocketing(user, slot) LOOTS[#LOOTS + 1] = "pp:" .. self:GetName() end

-- @@KDCMP@@

-- Part 2: scenarios.
local RESULTS = {}
local function check(name, ok, detail)
    RESULTS[#RESULTS + 1] = (ok and "PASS  " or "FAIL  ") .. name .. (detail and ("  [" .. tostring(detail) .. "]") or "")
end
local function events(kind)
    local o = {}
    for _, l in ipairs(LOG) do
        local a = string.match(l, "%[KCD2%-MP%-EVT%] v1 %d+ " .. kind .. " (.*)$")
        if a then o[#o + 1] = a end
    end
    return o
end
local function ledger(ev)
    local o = {}
    for _, l in ipairs(LOG) do if string.find(l, "WO217-LEDGER", 1, true) and string.find(l, " ev=" .. ev .. " ", 1, true) then o[#o + 1] = l end end
    return o
end
local SWORD = "b0fc8e19-af72-4771-9517-caec4f568920"
local APPLE = "2264f217-590e-4c0f-a4c6-f50c6532b9f6"
local COAT = "a856e87a-8065-4338-919d-0aff7a63341d"
local ONION = "4a6fa310-067a-404d-9813-bd1761d1c70d"
local EGG = "7ae2e77b-bdae-46cb-b6ac-f532cf225748"

local function world()
    for n in pairs(ENTS) do ENTS[n] = nil end
    ENTS["Dude"] = player
    INVS = {}
    player.inventory = mkInventory()
    KCD2MP.ghosts = {}; KCD2MP.npcPuppets = {}
    local w = KCD2MP.w134
    w.pendingOpen = {}; w.sessions = {}; w.partial = {}; w.stash = {}; w.itemReq = {}; w.hostTaken = {}; w.hostVerify = {}
    w.bodySig = {}; w.chestSess = {}
    w.bodies, w.items, w.chests = true, true, true
    W217.L.sess = {}; W217.L.hostLive = {}; W217.L.hostEsc = {}; W217.L.partial = {}; W217.L.fellBack = {}; W217.L.active = 0
    LOG = {}; TOASTS = {}; LOOTS = {}; TIMERS = {}
end
local function asJoiner()
    KCD2MP.hitSensorOn = false
    KCD2MP.wo102.authorityHost = true
    KCD2MP_W131Tick(true, true)
    KCD2MP_W134Tick(true, false, true, 1)
    TIMERS = {}
end
local function asHost(peers)
    KCD2MP.hitSensorOn = true
    KCD2MP.w131.joiner = false
    KCD2MP_W134Tick(false, true, true, peers or 1)
    TIMERS = {}
end
local function count(inv, cls) return inv:GetCountOfClass(cls) end
local function moveTo(from, to, cls)
    for _, w in ipairs(from.list) do if ITEMS[w].class == cls then to:AddItem(w); return w end end
end
local function key(w) return (string.gsub(tostring(w), "[^%w]", "_")) end
local function esc() return System.GetEntityByName("kcd2mp_w217_escrow") end

-- ================= J: the joiner =================
world(); asJoiner()
local b = mkBody("bandit_1", 101, 100, 10)
b.inventory:CreateItem(SWORD, 1, 1)
b.inventory:CreateItem(COAT, 0.5, 1)                         -- the copy's own coat: the host's body no longer has it
BasicAIActions.OnLoot(b, player, 1)
check("J0 the loot is a request to the host (WO-134's open)", #events("w134_open") == 1 and #LOOTS == 0, #LOOTS)
KCD2MP_W217BodyList("bandit_1", 1, 1, 1, { { id = "h1", cls = SWORD, amt = 1, hp = 1, worn = 0, stolen = 0, owner = "-" },
                                           { id = "h2", cls = APPLE, amt = 3, hp = 1, worn = 0, stolen = 0, owner = "-" } })
check("J1 the live list opens the game's loot screen on the host's items", LOOTS[1] == "bandit_1", LOOTS[1])
check("J1 the copy holds the host's items: the sword kept, the apples made (through the escrow), the coat out",
    count(b.inventory, SWORD) == 1 and count(b.inventory, APPLE) == 3 and count(b.inventory, COAT) == 0,
    string.format("%d %d %d", count(b.inventory, SWORD), count(b.inventory, APPLE), count(b.inventory, COAT)))
check("J1 nothing was created in the copy itself (made in the escrow, moved in)", esc() ~= nil and esc():IsHidden() and #esc().inventory.list == 0)
check("J1 the agent is told a session lives (its poll)", events("w217_lootlive")[1] == "1", events("w217_lootlive")[1])
-- a take: the screen moves the sword into the pack
local sw = moveTo(b.inventory, player.inventory, SWORD)
LOG = {}
KCD2MP_W217LootPoll()
local rq = events("w217_treq")[1]
local tok = rq and string.match(rq, "^(%S+)")
check("J2 the take is a request with the host's item id", rq ~= nil and rq:find(" bandit_1 h1 " .. SWORD .. " 1", 1, true) ~= nil, rq)
check("J2 the sword is in the escrow, not the pack, until the grant", count(player.inventory, SWORD) == 0 and esc().inventory:HasItem(sw))
check("J2 ledger: request", #ledger("request") == 1)
LOG = {}
KCD2MP_W217TakeResult(tok, "grant", "bandit_1", "h1", "-", 0)
check("J3 granted: the sword enters the pack (the same item)", player.inventory:HasItem(sw) and not esc().inventory:HasItem(sw))
check("J3 the host is told it was applied; ledger: applied", (events("w217_tack")[1] or ""):find(" applied", 1, true) ~= nil and #ledger("applied") == 1)
-- a refused take
local ap = moveTo(b.inventory, player.inventory, APPLE)
LOG = {}
KCD2MP_W217LootPoll()
tok = string.match(events("w217_treq")[1] or "", "^(%S+)")
KCD2MP_W217TakeResult(tok, "refuse", "bandit_1", "h2", "gone", 0)
check("J4 refused: nothing in the pack, the escrow's item deleted, 'Already taken' shown",
    count(player.inventory, APPLE) == 0 and not esc().inventory:HasItem(ap) and ITEMS[ap] == nil and TOASTS[#TOASTS] == "Already taken", TOASTS[#TOASTS])
check("J4 ledger: refused", #ledger("refused") == 1)
-- no answer: cancelled; a late grant changes nothing
local s1 = W217.L.sess["bandit_1"]
b.inventory:CreateItem(EGG, 1, 1)
NOW = NOW + 0.1
KCD2MP_W217BodyList("bandit_1", 2, 1, 1, { { id = "h3", cls = EGG, amt = 1, hp = 1, worn = 0, stolen = 0, owner = "-" } })
local eg = moveTo(b.inventory, player.inventory, EGG)
LOG = {}
KCD2MP_W217LootPoll()
tok = string.match(events("w217_treq")[1] or "", "^(%S+)")
NOW = NOW + 6
KCD2MP_W217LootPoll()
check("J5 no answer in 5 s: cancelled -- the escrow's item deleted, the host told to put it back", ITEMS[eg] == nil and (events("w217_tack")[1] or ""):find(" cancel", 1, true) ~= nil and #ledger("cancel") == 1)
KCD2MP_W217TakeResult(tok, "grant", "bandit_1", "h3", "-", 0)
check("J5 a late grant applies nothing", count(player.inventory, EGG) == 0 and #ledger("late") == 1)
-- a put: an item of the pack into the copy goes to the host (WO-134's put) and an older list does not sweep it out
player.inventory:CreateItem(ONION, 1, 1)
KCD2MP_W217LootPoll()                                         -- the pack's snapshot
local on = moveTo(player.inventory, b.inventory, ONION)
LOG = {}
KCD2MP_W217LootPoll()
check("J6 the put goes to the host", (events("w134_put")[1] or ""):find(" bandit_1 " .. ONION .. " 1", 1, true) ~= nil, events("w134_put")[1])
KCD2MP_W217BodyList("bandit_1", 3, 1, 1, {})
check("J6 a list without it (it predates the put) leaves it in the copy", b.inventory:HasItem(on))
check("J no Lua errors (joiner)", #ERRS == 0, ERRS[1])

-- ================= H: the host =================
world(); asHost(2)
local smith = mkBody("ttac_blacksmith", 140, 100, 10); smith.dead = false
local hb = mkBody("bandit_2", 101, 100, 10)
hb.inventory:CreateItem(SWORD, 1, 1)
hb.inventory:CreateItem(APPLE, 1, 2)
local hsw, hap = hb.inventory.list[1], hb.inventory.list[2]
ItemManager.SetItemOwner(hap, "wuid_ttac_blacksmith", true)    -- stolen goods (the bench stages them from a house)
LOG = {}
KCD2MP_W217HostOpen(1, "bandit_2")
local bl = events("w217_blist")[1] or ""
check("H1 the live list goes to the joiner with item ids", bl:find("^1 bandit_2 1 1 1 ") ~= nil and bl:find(key(hsw) .. ":" .. SWORD, 1, true) ~= nil, bl)
check("H1 ... the stolen apples carry their owner and the mark (item_flags)", bl:find(key(hap) .. ":" .. APPLE .. ":2:1.0000:0:1:ttac_blacksmith", 1, true) ~= nil, bl)
LOG = {}
KCD2MP_W217HostTakeReq(1, "77", "bandit_2", key(hsw), SWORD, 1)
check("H2 granted once: out of the body into the escrow", (events("w217_tres")[1] or "") == "1 77 grant bandit_2 " .. key(hsw) .. " - 0" and not hb.inventory:HasItem(hsw) and esc().inventory:HasItem(hsw), events("w217_tres")[1])
check("H2 ledger: request, grant, removed", #ledger("request") == 1 and #ledger("grant") == 1 and #ledger("removed") == 1)
LOG = {}
KCD2MP_W217HostTakeReq(2, "78", "bandit_2", key(hsw), SWORD, 1)
check("H3 the second asker is refused (gone)", (events("w217_tres")[1] or "") == "2 78 refuse bandit_2 " .. key(hsw) .. " gone 0", events("w217_tres")[1])
KCD2MP_W217HostAck(1, "77", "applied")
check("H4 the joiner applied it: the escrow's copy deleted", ITEMS[hsw] == nil)
LOG = {}
KCD2MP_W217HostTakeReq(1, "79", "bandit_2", key(hap), APPLE, 2)
check("H5 a stolen item's grant carries its owner and mark", (events("w217_tres")[1] or "") == "1 79 grant bandit_2 " .. key(hap) .. " ttac_blacksmith 1", events("w217_tres")[1])
KCD2MP_W217HostAck(1, "79", "cancel")
check("H6 a cancel puts the granted item back into the body", hb.inventory:HasItem(hap) and #ledger("cancelled") == 1)
-- the host's own take, logged through the same ledger
player.inventory:AddItem(hap)
LOG = {}
KCD2MP_W217LootPoll()
check("H7 the host's own take: request, grant, removed, applied (peer=host)", #ledger("applied") == 1 and table.concat(LOG, "\n"):find("peer=host", 1, true) ~= nil)
check("H no Lua errors (host)", #ERRS == 0, ERRS[1])

-- ================= B3: a long name's alias =================
world()
local HORSE = "horse[animalcare/horseCare6_ff15a088-7332-4a17-9536-3e8f6a8898ad]"
local hz = mkBody(HORSE, 120, 100, 10); hz.class = "Horse"; hz.dead = false
System.GetEntitiesByClass = function(cls) local o = {}; for _, e in pairs(ENTS) do if e.class == cls then o[#o + 1] = e end end; return o end
check("B3 the mod's alias is the DLL's (lh838eb35a)", KCD2MP_W217Alias(HORSE) == "lh838eb35a", KCD2MP_W217Alias(HORSE))
check("B3 the alias finds the long-named body", System.GetEntityByName("lh838eb35a") == hz)
CMDS = {}
System.ExecuteCommand("wh_ai_PauseNPC lh838eb35a")
check("B3 a console command with the alias runs on the real name", CMDS[#CMDS] == "wh_ai_PauseNPC " .. HORSE, CMDS[#CMDS])
KCD2MP_W217Set("long_names", "off")
W217.alias.map = {}; W217.alias.at = -1e9
check("B3 long_names off: an alias is no name", System.GetEntityByName("lhe580b514") == nil)
KCD2MP_W217Set("long_names", "on")

-- ================= T: the twin =================
world(); asHost(1)
KCD2MP_W217Set("loot_live", "off")
local tb = mkBody("bandit_3", 101, 100, 10)
tb.inventory:CreateItem(SWORD, 1, 1)
LOG = {}
check("T1 loot_live off: the host makes no live session", KCD2MP_W217HostOpen(1, "bandit_3") == false and #events("w217_blist") == 0)
KCD2MP_W217Set("loot_live", "on")

-- ================= M2.1: coordinate-tagged world chest addressing =================
world()
local function mkWorldChest(slotKey, rawName, cls, x, y, z)
    NEXTID = NEXTID + 1
    local e = { class = cls or "Stash", id = NEXTID, px = x, py = y, pz = z, inventory = mkInventory(), hidden = false }
    e.GetName = function() return rawName end
    e.GetWorldPos = function(self) return { x = self.px, y = self.py, z = self.pz } end
    ENTS[slotKey] = e
    return e
end
local stA = mkWorldChest("stash19_near", "Stash19", "Stash", 2176.15, -1450.25, 31.75)
local stB = mkWorldChest("stash19_far", "Stash19", "Stash", 3107.15, -1450.25, 31.75)
local stOther = mkWorldChest("chest_adjacent", "stash[Chest/chest3_c1d92081]", "Stash", 2176.60, -1450.25, 31.75)
local ckA = KCD2MP_W217ChestKey(stA)
local ckB = KCD2MP_W217ChestKey(stB)
local ckOther = KCD2MP_W217ChestKey(stOther)
check("M2.1 duplicate-named chests at different spots have distinct wire-safe keys", ckA ~= nil and ckB ~= nil and ckA ~= ckB and string.match(ckA, "^[A-Za-z0-9_]+$") ~= nil and #ckA <= 80, ckA)
check("M2.1 sphere lookup resolves each Stash19 to its own coordinates, never the 931 m namesake", KCD2MP_W217ResolveChestKey(ckA) == stA and KCD2MP_W217ResolveChestKey(ckB) == stB)
check("M2.1 adjacent chest with different name hash never cross-matches", KCD2MP_W217ResolveChestKey(ckOther) == stOther)
check("M2.1 System.GetEntityByName resolves wc_ chest keys while NPC names stay unchanged", System.GetEntityByName(ckA) == stA and not KCD2MP_W217IsChestKey("bandit_1"))

-- ================= M2.2: personal stash exclusion, shared chest live open, and lazy host init =================
CHEST_OPENS = {}
Stash = {
    Open = function(self, user) self.bOpened = 1; CHEST_OPENS[#CHEST_OPENS + 1] = self:GetName() .. "@" .. self.px end,
    OnUsed = function(self, user, slot)
        if self.bLocked == true then return false end
        return self:Open(user)
    end,
    GetInventoryToOpen = function(self) return self.openInvId or self.inventory:GetId() end,
}
CartStash = {
    OnUsed = function(self, user, slot) self.bOpened = 1; CHEST_OPENS[#CHEST_OPENS + 1] = "cart:" .. self:GetName() end,
}

world(); asJoiner()
CHEST_OPENS = {}
local function attachStash(e, tbl)
    setmetatable(e, { __index = tbl or Stash })
    return e
end
-- Personal stashes (3 rules) + escrow/grave must NEVER be treated as shared chests
local pByName = attachStash(mkWorldChest("pm1", "stash[playerMasterChest_inn1]", "Stash", 101, 100, 10))
local pByMaster = attachStash(mkWorldChest("pm2", "stash[Chest/bedside_1]", "Stash", 101.5, 100, 10))
pByMaster.stash = { GetMasterInventory = function() return "userdata: 0200000000009999" end }
local pByRedirect = attachStash(mkWorldChest("pm3", "stash[Chest/bedside_2]", "Stash", 102, 100, 10))
pByRedirect.openInvId = "master_inv_wuid"
local sharedNullMaster = attachStash(mkWorldChest("sh_null", "Stash19", "Stash", 101, 101, 10))
sharedNullMaster.stash = { GetMasterInventory = function() return "userdata: 0000000000000000" end }
local farStash19 = attachStash(mkWorldChest("sh_far", "Stash19", "Stash", 900, 101, 10))
farStash19.inventory:CreateItem(COAT, 1, 1)

check("M2.2 personal stashes (name / GetMasterInventory / GetInventoryToOpen) are excluded; null master WUID stays shared",
    KCD2MP_W217IsPersonalStash(pByName) == true
    and KCD2MP_W217IsPersonalStash(pByMaster) == true
    and KCD2MP_W217IsPersonalStash(pByRedirect) == true
    and KCD2MP_W217IsSharedChest(pByName) == false
    and KCD2MP_W217IsSharedChest(pByMaster) == false
    and KCD2MP_W217IsSharedChest(pByRedirect) == false
    and KCD2MP_W217IsSharedChest(sharedNullMaster) == true)

-- Opening a personal stash on Joiner opens locally without asking Host
LOG = {}
pByName:OnUsed(player, 1)
check("M2.2 personal stash opens locally and sends no w134_open", #events("w134_open") == 0 and #CHEST_OPENS == 1)

-- Locked shared chest does not send w134_open until unlocked
sharedNullMaster.bLocked = true
LOG = {}; CHEST_OPENS = {}
sharedNullMaster:OnUsed(player, 1)
check("M2.2 locked world chest does not request open", #events("w134_open") == 0 and #CHEST_OPENS == 0)

-- Unlocked shared Stash19 intercepts Open -> w134_open <wc_key>, applies Host list, opens screen without touching far Stash19
sharedNullMaster.bLocked = false
sharedNullMaster.inventory:CreateItem(EGG, 1, 1)   -- Joiner's stale local egg
local shKey = KCD2MP_W217ChestKey(sharedNullMaster)
LOG = {}; CHEST_OPENS = {}
sharedNullMaster:OnUsed(player, 1)
check("M2.2 unlocked world chest asks Host using coordinate-tagged key before opening UI", events("w134_open")[1] == shKey and #CHEST_OPENS == 0, events("w134_open")[1])
KCD2MP_W217BodyList(shKey, 1, 1, 1, { { id = "hc1", cls = SWORD, amt = 1, hp = 0.9, worn = 0, stolen = 0, owner = "-" } })
check("M2.2 Host's chest list syncs near Stash19, opens UI, and leaves far Stash19 untouched",
    #CHEST_OPENS == 1 and count(sharedNullMaster.inventory, SWORD) == 1 and count(sharedNullMaster.inventory, EGG) == 0
    and count(farStash19.inventory, COAT) == 1)

-- CartStash without :Open intercepts via OnUsed
local cart = attachStash(mkWorldChest("cart1", "CartStash[wagon_1]", "CartStash", 102, 101, 10), CartStash)
local cartKey = KCD2MP_W217ChestKey(cart)
LOG = {}; CHEST_OPENS = {}
cart:OnUsed(player, 1)
check("M2.2 CartStash intercepts OnUsed and opens once Host chest list arrives", events("w134_open")[1] == cartKey and #CHEST_OPENS == 0)
KCD2MP_W217BodyList(cartKey, 1, 1, 1, { { id = "hc2", cls = APPLE, amt = 2, hp = 1, worn = 0, stolen = 0, owner = "-" } })
check("M2.2 CartStash synced and opened", CHEST_OPENS[1] == "cart:CartStash[wagon_1]" and count(cart.inventory, APPLE) == 2)

-- Lazy loot generation + ReportOpened on Host
world(); asHost(1)
local lazyReported = 0
local lazyAssigned = 0
local hostLazy = attachStash(mkWorldChest("hlazy", "stash[Chest/untouched_1]", "Stash", 200, 200, 10))
hostLazy.stash = {
    opened = false,
    IsOpened = function(self) return self.opened end,
    ReportOpened = function(self) self.opened = true; lazyReported = lazyReported + 1 end,
}
hostLazy.AssignInventory = function(self)
    lazyAssigned = lazyAssigned + 1
    self.inventory:CreateItem(SWORD, 0.75, 1)
end
local hlKey = KCD2MP_W217ChestKey(hostLazy)
LOG = {}
KCD2MP_W134HostOpen(1, 0, hlKey)
local hlBlist = events("w217_blist")[1] or ""
check("M2.2 Host initializes untouched chest via AssignInventory and marks ReportOpened before sending w217_blist",
    lazyAssigned == 1 and lazyReported >= 1 and count(hostLazy.inventory, SWORD) == 1 and hlBlist:find(SWORD, 1, true) ~= nil
    and #events("w134_bstate") == 0, hlBlist)
-- Emptying the chest and opening again must NOT regenerate loot
local hswId = key(hostLazy.inventory.list[1])
KCD2MP_W217HostTakeReq(1, "901", hlKey, hswId, SWORD, 1)
KCD2MP_W217HostAck(1, "901", "applied")
hostLazy.__w217ChestInit = nil   -- simulate reload where stash:IsOpened() is true in .whs
KCD2MP_W134HostOpen(1, 0, hlKey)
check("M2.2 an already-opened empty chest stays empty on re-open (never refilled)", lazyAssigned == 1 and count(hostLazy.inventory, SWORD) == 0)

-- ================= M2.3: partial stack takes, stack merges, live stack updates, and escrow puts =================
local ARROW = "11111111-2222-3333-4444-555555555555"
MERGE_STACKS = { [ARROW] = true, [APPLE] = true }

-- (a) Joiner partial stack take (5 of 20 arrows) into empty pack, then merged partial take (4 of 15) into existing 5-arrow stack
world(); asJoiner()
local sc = attachStash(mkWorldChest("stack_chest", "Stash45", "Stash", 101, 100, 10))
local scKey = KCD2MP_W217ChestKey(sc)
sc:OnUsed(player, 1)
KCD2MP_W217BodyList(scKey, 1, 1, 1, { { id = "ha1", cls = ARROW, amt = 20, hp = 1, worn = 0, stolen = 0, owner = "-" } })
check("M2.3a chest opened with 20 arrows", count(sc.inventory, ARROW) == 20)

-- Simulate UI transferring 5 of 20 arrows from sc into player.inventory
local scArrowW = sc.inventory.list[1]
ITEMS[scArrowW].amount = 15
player.inventory:CreateItem(ARROW, 1, 5)
LOG = {}
KCD2MP_W217LootPoll()
local prq = events("w217_treq")[1] or ""
local ptok = string.match(prq, "^(%S+)")
check("M2.3a partial take of 5/20 arrows requests amt=5 with hostId ha1 and escrows the 5 arrows",
    prq:find(" " .. scKey .. " ha1 " .. ARROW .. " 5", 1, true) ~= nil
    and count(player.inventory, ARROW) == 0 and count(esc().inventory, ARROW) == 5 and count(sc.inventory, ARROW) == 15, prq)
-- Stale in-flight w217_blist with amt=20 before Host processes tok must NOT snap Joiner's chest back to 20
KCD2MP_W217BodyList(scKey, 2, 1, 1, { { id = "ha1", cls = ARROW, amt = 20, hp = 1, worn = 0, stolen = 0, owner = "-" } })
check("M2.3a stale pre-take list does not revert Joiner's 15 remaining arrows to 20", count(sc.inventory, ARROW) == 15)
-- Host grants + pushes rev 3 (amt=15)
KCD2MP_W217TakeResult(ptok, "grant", scKey, "ha1", "-", 0)
KCD2MP_W217BodyList(scKey, 3, 1, 1, { { id = "ha1", cls = ARROW, amt = 15, hp = 1, worn = 0, stolen = 0, owner = "-" } })
check("M2.3a granted partial take gives Henry 5 arrows and keeps 15 in chest",
    count(player.inventory, ARROW) == 5 and count(esc().inventory, ARROW) == 0 and count(sc.inventory, ARROW) == 15)

-- Second partial take: Henry ALREADY has 5 arrows; UI merges 4 more onto Henry's existing stack (5 -> 9), chest 15 -> 11
local pArrowW = player.inventory.list[1]
ITEMS[sc.inventory.list[1]].amount = 11
ITEMS[pArrowW].amount = 9
LOG = {}
KCD2MP_W217LootPoll()
prq = events("w217_treq")[1] or ""
ptok = string.match(prq, "^(%S+)")
check("M2.3b merged partial take splits 4 into escrow while Henry keeps his prior 5",
    prq:find(" ha1 " .. ARROW .. " 4", 1, true) ~= nil
    and count(player.inventory, ARROW) == 5 and count(esc().inventory, ARROW) == 4 and count(sc.inventory, ARROW) == 11, prq)
KCD2MP_W217TakeResult(ptok, "grant", scKey, "ha1", "-", 0)
KCD2MP_W217BodyList(scKey, 4, 1, 1, { { id = "ha1", cls = ARROW, amt = 11, hp = 1, worn = 0, stolen = 0, owner = "-" } })
check("M2.3b granted merged partial take merges 4 back onto Henry's 5 -> 9 arrows",
    count(player.inventory, ARROW) == 9 and count(esc().inventory, ARROW) == 0 and count(sc.inventory, ARROW) == 11)

-- Refused partial take reconciles Joiner's displayed stack back to Host's count
ITEMS[sc.inventory.list[1]].amount = 6
ITEMS[player.inventory.list[1]].amount = 14
LOG = {}
KCD2MP_W217LootPoll()
ptok = string.match(events("w217_treq")[1] or "", "^(%S+)")
KCD2MP_W217BodyList(scKey, 5, 1, 1, { { id = "ha1", cls = ARROW, amt = 3, hp = 1, worn = 0, stolen = 0, owner = "-" } })
KCD2MP_W217TakeResult(ptok, "refuse", scKey, "ha1", "gone", 0)
check("M2.3c refused partial take leaves Henry at 9 and reconciles chest stack to Host's 3",
    count(player.inventory, ARROW) == 9 and count(esc().inventory, ARROW) == 0 and count(sc.inventory, ARROW) == 3)

-- Live Joiner -> Host put: (1) partial put of 2 apples into chest with 0 apples, (2) put of 3 more onto existing 2-apple stack
player.inventory:CreateItem(APPLE, 1, 10)
KCD2MP_W217LootPoll()   -- snapshot Henry's 10 apples
ITEMS[player.inventory.list[#player.inventory.list]].amount = 8
sc.inventory:CreateItem(APPLE, 1, 2)
LOG = {}
KCD2MP_W217LootPoll()
local putEvt1 = events("w134_put")[1] or ""
check("M2.3d splitting 2 apples from Henry's 10-apple stack into empty chest slot sends w134_put amt=2",
    putEvt1:find(" " .. scKey .. " " .. APPLE .. " 2 ", 1, true) ~= nil, putEvt1)
KCD2MP_W217BodyList(scKey, 6, 1, 1, { { id = "ha1", cls = ARROW, amt = 3, hp = 1, worn = 0, stolen = 0, owner = "-" },
                                       { id = "hap1", cls = APPLE, amt = 2, hp = 1, worn = 0, stolen = 0, owner = "-" } })
check("M2.3d Host echo binds hap1 to Joiner's 2 apples cleanly", count(sc.inventory, APPLE) == 2)

-- Now merge 3 more apples from Henry's pack onto the existing 2-apple stack in chest (2 -> 5)
ITEMS[player.inventory.list[#player.inventory.list]].amount = 5
for _, w in ipairs(sc.inventory.list) do if ITEMS[w] and ITEMS[w].class == APPLE then ITEMS[w].amount = 5 end end
LOG = {}
KCD2MP_W217LootPoll()
local putEvt2 = events("w134_put")[1] or ""
check("M2.3e putting 3 more apples onto existing 2-apple chest stack sends w134_put amt=3",
    putEvt2:find(" " .. scKey .. " " .. APPLE .. " 3 ", 1, true) ~= nil, putEvt2)
-- Stale rev before Host processed the +3 put keeps chest at 5 (never snaps back to 2)
KCD2MP_W217BodyList(scKey, 7, 1, 1, { { id = "ha1", cls = ARROW, amt = 3, hp = 1, worn = 0, stolen = 0, owner = "-" },
                                       { id = "hap1", cls = APPLE, amt = 2, hp = 1, worn = 0, stolen = 0, owner = "-" } })
check("M2.3e stale pre-put list does not revert merged 5-apple stack to 2", count(sc.inventory, APPLE) == 5)
KCD2MP_W217BodyList(scKey, 8, 1, 1, { { id = "ha1", cls = ARROW, amt = 3, hp = 1, worn = 0, stolen = 0, owner = "-" },
                                       { id = "hap1", cls = APPLE, amt = 5, hp = 1, worn = 0, stolen = 0, owner = "-" } })
check("M2.3e Host echo of 5-apple stack settles cleanly", count(sc.inventory, APPLE) == 5)

-- (f) Host side: partial stack deduction via escrow AND live Joiner put while Host ALSO has UI screen open!
world(); asHost(1)
local hsc = attachStash(mkWorldChest("host_stack_chest", "Stash45", "Stash", 101, 100, 10))
hsc.inventory:CreateItem(ARROW, 1, 20)
local hscKey = KCD2MP_W217ChestKey(hsc)
local hArrId = key(hsc.inventory.list[1])
KCD2MP_W217HostOpen(1, hscKey)
-- Host ALSO opens its inventory UI on this chest right now!
KCD2MP_W201InvIntent(hscKey, "ours")
KCD2MP_W201InvScreen("in", "element")
LOG = {}
KCD2MP_W217HostTakeReq(1, "501", hscKey, hArrId, ARROW, 5)
local htres = events("w217_tres")[1] or ""
local hblist = events("w217_blist")[1] or ""
check("M2.3f Host deducts 5/20 arrows via escrow (15 remain in chest, 5 in escrow) and pushes updated w217_blist immediately",
    htres:find("^1 501 grant ") ~= nil and count(hsc.inventory, ARROW) == 15 and count(esc().inventory, ARROW) == 5
    and hblist:find(ARROW .. ":15:", 1, true) ~= nil, hblist)
KCD2MP_W217HostAck(1, "501", "applied")
check("M2.3f Ack deletes escrowed 5 arrows on Host", count(esc().inventory, ARROW) == 0 and count(hsc.inventory, ARROW) == 15)

-- Joiner puts 1 apple into empty slot of chest WHILE Host's UI screen is STILL open!
LOG = {}
KCD2MP_W134HostPut(1, "502", hscKey, APPLE, 1, 1.0)
hblist = events("w217_blist")[1] or ""
check("M2.3g Joiner put while Host UI is open is NOT blocked by WO-201 defer: created via escrow and broadcast immediately",
    count(hsc.inventory, APPLE) == 1 and count(esc().inventory, APPLE) == 0 and hblist:find(APPLE .. ":1:", 1, true) ~= nil, hblist)
-- Joiner puts 3 more apples onto that 1-apple stack while Host UI is still open -> merges to 4 and broadcasts immediately!
LOG = {}
KCD2MP_W134HostPut(1, "503", hscKey, APPLE, 3, 1.0)
hblist = events("w217_blist")[1] or ""
check("M2.3g Joiner stack-merge put while Host UI is open updates Host chest to 4 apples and broadcasts w217_blist immediately",
    count(hsc.inventory, APPLE) == 4 and hblist:find(APPLE .. ":4:", 1, true) ~= nil, hblist)
KCD2MP_W201InvScreen("out", "element")

-- ================= R1: overlapping stack transfers never merge in escrow or duplicate items =================
-- (a) Joiner takes 5 arrows and then 4 arrows from a 20-arrow stack BEFORE replies arrive (with MERGE_STACKS on)
world(); asJoiner()
W217.L.hostQueue = {}; W217.L.splitRemap = {}
local ovChest = attachStash(mkWorldChest("ov_joiner_chest", "Stash45", "Stash", 101, 100, 10))
local ovKey = KCD2MP_W217ChestKey(ovChest)
ovChest:OnUsed(player, 1)
KCD2MP_W217BodyList(ovKey, 1, 1, 1, { { id = "hoa1", cls = ARROW, amt = 20, hp = 1, worn = 0, stolen = 0, owner = "-" } })

-- First partial take: 5 of 20 arrows
ITEMS[ovChest.inventory.list[1]].amount = 15
player.inventory:CreateItem(ARROW, 1, 5)
LOG = {}
KCD2MP_W217LootPoll()
local ovReq1 = events("w217_treq")[1] or ""
local ovTok1 = string.match(ovReq1, "^(%S+)")
check("R1a first take (5/20) emits w217_treq amt=5 and holds 5 in escrow",
    ovTok1 ~= nil and ovReq1:find(" hoa1 " .. ARROW .. " 5", 1, true) ~= nil and count(player.inventory, ARROW) == 0 and count(ovChest.inventory, ARROW) == 15, ovReq1)

-- Second partial take BEFORE first reply arrives: 4 more arrows (15 -> 11)
ITEMS[ovChest.inventory.list[1]].amount = 11
player.inventory:CreateItem(ARROW, 1, 4)
LOG = {}
KCD2MP_W217LootPoll()
check("R1a overlapping second take (4/15) stages in isolated escrow queue without merging onto tok1 or sending early treq",
    #events("w217_treq") == 0 and count(player.inventory, ARROW) == 0 and count(esc().inventory, ARROW) == 5 and count(ovChest.inventory, ARROW) == 11)

-- Stale BodyList (amt=20) while tok1 is pending and take #2 is queued keeps chest at 11
KCD2MP_W217BodyList(ovKey, 2, 1, 1, { { id = "hoa1", cls = ARROW, amt = 20, hp = 1, worn = 0, stolen = 0, owner = "-" } })
check("R1a stale BodyList (20) accounts for both pending (5) and queued (4) takes -> chest stays at 11", count(ovChest.inventory, ARROW) == 11)

-- First grant arrives: Joiner pack gets ONLY 5 arrows (NOT 9!), and second take promotes and emits w217_treq amt=4
LOG = {}
KCD2MP_W217TakeResult(ovTok1, "grant", ovKey, "hoa1", "-", 0)
local ovReq2 = events("w217_treq")[1] or ""
local ovTok2 = string.match(ovReq2, "^(%S+)")
check("R1a first grant gives Henry exactly 5 arrows (not 9) and promotes queued 4-arrow take",
    count(player.inventory, ARROW) == 5 and ovTok2 ~= nil and ovReq2:find(" " .. ARROW .. " 4", 1, true) ~= nil,
    string.format("pack=%d req2=%s", count(player.inventory, ARROW), ovReq2))

-- Second grant arrives: Henry pack now has 9 arrows and chest has 11
KCD2MP_W217TakeResult(ovTok2, "grant", ovKey, "hoa1", "-", 0)
KCD2MP_W217BodyList(ovKey, 3, 1, 1, { { id = "hoa1", cls = ARROW, amt = 11, hp = 1, worn = 0, stolen = 0, owner = "-" } })
check("R1a second grant gives Henry 9 total arrows and chest keeps 11",
    count(player.inventory, ARROW) == 9 and count(ovChest.inventory, ARROW) == 11 and count(esc().inventory, ARROW) == 0)

-- (b) Host receives two overlapping partial take requests (5 and 4 from 20-stack) before acks -> both cancelled -> chest is 20 (NOT 35!)
world(); asHost(2)
W217.L.hostQueue = {}; W217.L.splitRemap = {}
local hovChest = attachStash(mkWorldChest("ov_host_chest", "Stash45", "Stash", 101, 100, 10))
hovChest.inventory:CreateItem(ARROW, 1, 20)
local hovKey = KCD2MP_W217ChestKey(hovChest)
local hovArrId = key(hovChest.inventory.list[1])
KCD2MP_W217HostOpen(1, hovKey)
KCD2MP_W217HostOpen(2, hovKey)
KCD2MP_W217HostTakeReq(1, "601", hovKey, hovArrId, ARROW, 5)
KCD2MP_W217HostTakeReq(2, "602", hovKey, hovArrId, ARROW, 4)
KCD2MP_W217HostAck(1, "601", "cancel")
KCD2MP_W217HostAck(2, "602", "cancel")
check("R1b two overlapping partial takes on Host both cancelled -> chest stack is 20 (never 35)",
    count(hovChest.inventory, ARROW) == 20 and count(esc().inventory, ARROW) == 0, count(hovChest.inventory, ARROW))

-- Also verify cancelling queued request first (604 then 603) -> chest stack is 20
local hovArrId2 = key(hovChest.inventory.list[1])
KCD2MP_W217HostTakeReq(1, "603", hovKey, hovArrId2, ARROW, 5)
KCD2MP_W217HostTakeReq(2, "604", hovKey, hovArrId2, ARROW, 4)
KCD2MP_W217HostAck(2, "604", "cancel")
KCD2MP_W217HostAck(1, "603", "cancel")
check("R1b cancelling queued request before active request also restores chest stack to 20",
    count(hovChest.inventory, ARROW) == 20 and count(esc().inventory, ARROW) == 0, count(hovChest.inventory, ARROW))

-- (c) First overlapping partial take granted+applied (5), second cancelled (4) -> chest has 15
local hovArrId3 = key(hovChest.inventory.list[1])
LOG = {}
KCD2MP_W217HostTakeReq(1, "605", hovKey, hovArrId3, ARROW, 5)
KCD2MP_W217HostTakeReq(2, "606", hovKey, hovArrId3, ARROW, 4)
KCD2MP_W217HostAck(1, "605", "applied")
check("R1c applying first take (5) promotes queued second take (4) cleanly",
    (events("w217_tres")[2] or ""):find("^2 606 grant ") ~= nil and count(hovChest.inventory, ARROW) == 11, events("w217_tres")[2])
KCD2MP_W217HostAck(2, "606", "cancel")
check("R1c cancelling second take restores its 4 arrows -> chest has 15",
    count(hovChest.inventory, ARROW) == 15 and count(esc().inventory, ARROW) == 0, count(hovChest.inventory, ARROW))

MERGE_STACKS = {}

-- ================= R3: unloaded host chest is never reported as empty =================
-- Host side: chest entity is absent (unstreamed on Host)
world(); asHost(1)
W217.L.hostQueue = {}; W217.L.splitRemap = {}
local missingKey = KCD2MP_W217EncodeChestKey("Stash", "Stash_far_castle", 2500.00, -1800.00, 45.00)
LOG = {}
KCD2MP_W134HostOpen(1, 0, missingKey)
check("R3a unloaded chest on Host creates no hostLive session, emits no empty w217_blist, and emits flags=2 open refusal",
    W217.L.hostLive[missingKey] == nil and #events("w217_blist") == 0
    and (events("w134_bstate")[1] or "") == ("1 0 " .. missingKey .. " open 2 1 1 -"),
    events("w134_bstate")[1])

-- Joiner side: local chest has 1 sword + 3 apples; Host replies flags=2
world(); asJoiner()
CHEST_OPENS = {}; TOASTS = {}
local farChestJ = attachStash(mkWorldChest("far_castle_j", "Stash_far_castle", "Stash", 101.00, 100.00, 10.00))
farChestJ.inventory:CreateItem(SWORD, 0.9, 1)
farChestJ.inventory:CreateItem(APPLE, 1.0, 3)
local farKeyJ = KCD2MP_W217ChestKey(farChestJ)
farChestJ:OnUsed(player, 1)
check("R3b Joiner chest open awaits Host response", KCD2MP.w134.pendingOpen[farKeyJ] ~= nil and #CHEST_OPENS == 0)
KCD2MP_W134BodyState(farKeyJ, "open", 2, 1, 1, {})
check("R3b flags=2 refusal leaves Joiner chest contents untouched, opens no empty screen, creates no live session, and toasts player",
    KCD2MP.w134.pendingOpen[farKeyJ] == nil and W217.L.sess[farKeyJ] == nil and #CHEST_OPENS == 0
    and count(farChestJ.inventory, SWORD) == 1 and count(farChestJ.inventory, APPLE) == 3
    and (TOASTS[#TOASTS] or ""):find("Move closer to your host", 1, true) ~= nil, TOASTS[#TOASTS])

-- Mid-session unstreaming defense: Joiner had chest open, puts 1 coat, Host chest unloads before HostPut -> rolled back to Henry's pack
farChestJ:OnUsed(player, 1)
KCD2MP_W217BodyList(farKeyJ, 1, 1, 1, { { id = "fc1", cls = SWORD, amt = 1, hp = 0.9, worn = 0, stolen = 0, owner = "-" } })
player.inventory:CreateItem(COAT, 0.8, 1)
KCD2MP_W217LootPoll()
moveTo(player.inventory, farChestJ.inventory, COAT)
KCD2MP_W217LootPoll()
check("R3c Joiner put moved coat into chest awaiting Host confirmation", count(player.inventory, COAT) == 0 and count(farChestJ.inventory, COAT) == 1)
KCD2MP_W134BodyState(farKeyJ, "update", 2, 1, 1, {})
check("R3c mid-session flags=2 rolls unconfirmed put back into Henry's pack and closes live session",
    count(player.inventory, COAT) == 1 and count(farChestJ.inventory, COAT) == 0 and W217.L.sess[farKeyJ] == nil)

check("no Lua errors", #ERRS == 0, ERRS[1])
local pass, fail = 0, 0
for _, r in ipairs(RESULTS) do if r:sub(1, 4) == "PASS" then pass = pass + 1 else fail = fail + 1 end end
OUT = table.concat(RESULTS, "\n") .. string.format("\n%d passed, %d failed", pass, fail)
