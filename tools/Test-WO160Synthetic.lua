-- Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
-- GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
-- content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
-- WO-160 synthetic test (the game-side half), against the real kdcmp.lua under MoonSharp.
--
--   (S) the own-world verdict: silent at the main menu and during a load, spoken in a loaded world
--   (R) the release before a placement: stance + unstance the game's own way, never a corpse, a talker, a dialogue, a cutscene
--   (C) a conversation stands the NPC still on the other machine (host talk -> joiner copy held; joiner talk -> host's NPC frozen)
--   (H) the avatar's herb gathering as a plain clip, never the minigame fragment
--   (M) the mount gate: a horse is polled every 0.25 s for up to 5 s before the avatar's ForceMount
--   (T) talking to a copy: the two resets the game has (no LeftHand/RightHand: "Unsupported element type to reset!")
--
-- Driven by Test-WO160Synthetic.ps1 through the WO-77 MoonSharp driver. Live evidence: docs/WO-160-findings.md.

NOW = 0
os.clock = function() return NOW end
LOG = {}; TIMERS = {}; ENTS = {}; ERRS = {}; TOASTS = {}; CMDS = {}; CCMDS = {}

local function mkstub()
    return setmetatable({}, { __index = function(_, k) return function(...) return nil end end })
end
System = mkstub()
System.LogAlways = function(s) LOG[#LOG + 1] = tostring(s) end
System.GetCVarValue = function() return "0" end
System.GetEntityByName = function(n) return ENTS[n] end
System.GetEntitiesInSphere = function() return {} end
System.GetTerrainElevation = function(p) return 0 end
System.ExecuteCommand = function(s) CMDS[#CMDS + 1] = tostring(s) end
System.AddCCommand = function(name, body, help) CCMDS[name] = { body = tostring(body), help = tostring(help or "") } end
Script = mkstub()
Script.SetTimer = function(ms, f) TIMERS[#TIMERS + 1] = { ms = ms, f = f, at = NOW } end
AI = mkstub(); Sound = mkstub(); Physics = mkstub(); Terrain = mkstub()
AI.EnableUpdateLookTarget = function(id, on) LOOKT = { id = id, on = on } end
UIAction = mkstub()
UIAction.CallFunction = function(panel, inst, fn, text) TOASTS[#TOASTS + 1] = tostring(text) end
Game = mkstub()
Calendar = mkstub()
WORLD = 1000000
Calendar.GetWorldTime = function() return WORLD end
LABELS = {}
XGenAIModule = mkstub()
XGenAIModule.IsPointInAreaWithLabel = function(p, l) return LABELS[l] == true end

STATE = { health = 50, exhaust = 40, hunger = 60 }
player = { id = 1, soul = {
    GetState = function(self, n) return STATE[n] end,
    SetState = function(self, n, v) STATE[n] = v end,
} }
player.GetWorldPos = function() return { x = 0, y = 0, z = 0 } end

local rawpcall = pcall
pcall = function(f, ...)
    local r = { rawpcall(f, ...) }
    if not r[1] then ERRS[#ERRS + 1] = tostring(r[2]) end
    return unpack(r)
end

-- @@KDCMP@@

local RESULTS = {}
local function check(name, ok, detail)
    RESULTS[#RESULTS + 1] = (ok and "PASS  " or "FAIL  ") .. name .. (detail and ("  [" .. tostring(detail) .. "]") or "")
end
local function lastLog(needle, fromLog)
    for i = #LOG, (fromLog or 0) + 1, -1 do if LOG[i]:find(needle, 1, true) then return LOG[i] end end
    return nil
end
local function countEvt(name, argPrefix, fromLog)
    local n = 0
    for i = (fromLog or 0) + 1, #LOG do
        local l = LOG[i]
        if l:find("[KCD2-MP-EVT] v1 ", 1, true) and l:find(" " .. name .. " " .. (argPrefix or ""), 1, true) then n = n + 1 end
    end
    return n
end
local function noErrs(label) check(label .. ": no swallowed Lua errors", #ERRS == 0, ERRS[1]) end
local P = { x = 10, y = 20, z = 30 }

-- (S) the own-world verdict needs a loaded world --------------------------------------------------------------------------------
do
    ERRS = {}
    local savedPlayer = player
    player = nil
    System.GetEntityByName = function(n) return ENTS[n] end
    ENTS = {}
    local t0, mark = #TOASTS, #LOG
    KCD2MP_Wo124SessionMode(true)
    KCD2MP_W140Separate(true, "You loaded your own save.")
    check("S1: a session-mode packet at the main menu, then the agent's verdict: no toast", #TOASTS == t0 and lastLog('WO140-SEPARATE "', mark) == nil)
    check("S2: ... and the hold is logged", lastLog("WO160-SEPARATE held at the menu", mark) ~= nil)
    check("S3: ... nothing is left to draw", KCD2MP.w140.sep == nil)
    Game.IsLoadingEngineSaveGame = function() return true end
    player = savedPlayer
    KCD2MP.w140.sepHeld = 0
    mark = #LOG
    KCD2MP_W140Separate(true, "You loaded your own save.")
    check("S4: during a load (the join's own): no toast either", #TOASTS == t0 and KCD2MP.w140.sep == nil and lastLog("WO160-SEPARATE held at the loading", mark) ~= nil)
    Game.IsLoadingEngineSaveGame = function() return false end
    mark = #LOG
    KCD2MP_W140Separate(true, "You loaded your own save.")
    check("S5: in a loaded world it still fires (the real own-save case)", #TOASTS == t0 + 1 and KCD2MP.w140.sep ~= nil and lastLog('WO140-SEPARATE "You loaded your own save."', mark) ~= nil)
    KCD2MP_W140Separate(false, "")
    check("S6: the agent clears it when the join lands", KCD2MP.w140.sep == nil and lastLog("WO140-SEPARATE off", mark) ~= nil)
    check("S7: where is read without an event", (function() local m = #LOG; KCD2MP_Wo124WhereNow(); return countEvt("wo124_where", "", m) == 0 end)())
    noErrs("S")
end

-- (R) the release before a placement -------------------------------------------------------------------------------------------
do
    ERRS = {}
    local function body(name, dead)
        local e = { id = 90, actor = { IsDead = function() return dead == true end }, human = { IsInDialog = function() return false end } }
        ENTS[name] = e
        KCD2MP.npcPuppets[name] = {}
        return e
    end
    body("ttkc_scribe")
    CMDS = {}
    local mark = #LOG
    check("R1: a placed copy: Unstance then Stance, the game's own reset", KCD2MP_W160Release("ttkc_scribe", "activity-changed") == true
        and #CMDS == 2 and CMDS[1] == "wh_ai_NPCStateResetElement ttkc_scribe Unstance" and CMDS[2] == "wh_ai_NPCStateResetElement ttkc_scribe Stance", CMDS[1])
    check("R2: ... one log line", lastLog("WO160-REL npc=ttkc_scribe why=activity-changed unstance=ok stance=ok", mark) ~= nil)
    body("ttkc_corpse", true)
    CMDS = {}
    check("R3: never a corpse (its lying pose IS its unstance)", KCD2MP_W160Release("ttkc_corpse", "x") == false and #CMDS == 0)
    body("ttkc_talker")
    KCD2MP.w137.talking["ttkc_talker"] = { since = NOW }
    CMDS = {}
    check("R4: nor a copy this player is talking to (the talk's own reset ran)", KCD2MP_W160Release("ttkc_talker", "x") == false and #CMDS == 0)
    KCD2MP.w137.talking["ttkc_talker"] = nil
    local d = body("ttkc_dialogue")
    d.human.IsInDialog = function() return true end
    CMDS = {}
    check("R5: nor a body in a dialogue", KCD2MP_W160Release("ttkc_dialogue", "x") == false and #CMDS == 0)
    KCD2MP.cutsceneActive = true
    CMDS = {}
    check("R6: nor during a cutscene", KCD2MP_W160Release("ttkc_scribe", "x") == false and #CMDS == 0)
    KCD2MP.cutsceneActive = false
    check("R7: a name with no body: nothing", KCD2MP_W160Release("ttkc_nobody", "x") == false)
    KCD2MP_W160SetRelease("off")
    CMDS = {}
    check("R8: mp_ctx_release off: nothing is released", KCD2MP_W160Release("ttkc_scribe", "x") == false and #CMDS == 0)
    KCD2MP_W160SetRelease("on")
    check("R9: the switch is registered", CCMDS["mp_ctx_release"] ~= nil and CCMDS["mp_ctx_release"].body == "KCD2MP_W160SetRelease(%line)")
    for _, n in ipairs({ "ttkc_scribe", "ttkc_corpse", "ttkc_talker", "ttkc_dialogue" }) do ENTS[n] = nil; KCD2MP.npcPuppets[n] = nil end
    noErrs("R")
end

-- (T) talking to a copy ----------------------------------------------------------------------------------------------------------
do
    ERRS = {}
    CMDS = {}
    KCD2MP.w137.talking["ttac_blacksmith"] = { since = NOW }
    local mark = #LOG
    KCD2MP_W157TalkFree("ttac_blacksmith")
    check("T1: only the two resets the game has: Unstance and Stance", #CMDS == 2 and CMDS[1] == "wh_ai_NPCStateResetElement ttac_blacksmith Unstance"
        and CMDS[2] == "wh_ai_NPCStateResetElement ttac_blacksmith Stance", CMDS[1])
    check("T2: the log says exactly what was reset", lastLog("reset=Unstance=ok,Stance=ok placement=held", mark) ~= nil)
    KCD2MP.w137.talking["ttac_blacksmith"] = nil
    noErrs("T")
end

-- (C) conversations ----------------------------------------------------------------------------------------------------------
do
    ERRS = {}
    local w = KCD2MP.w160
    local DIALOG = false
    local npc = { GetName = function() return "ttkc_man_11" end, human = { IsInDialog = function() return DIALOG end } }
    ENTS["ttkc_man_11"] = npc
    KCD2MP.w137.host, KCD2MP.w137.active, KCD2MP.w137.joiner = true, true, false
    w.hostTalks = {}
    local mark = #LOG
    KCD2MP_W160HostTalk(npc, { id = player.id }, "OnTalk")
    check("C1: the host's talk is told to the joiner (w160_conv on)", countEvt("w160_conv", "on ttkc_man_11", mark) == 1 and w.hostTalks["ttkc_man_11"] ~= nil
        and lastLog("WO160-CONV host-talk start npc=ttkc_man_11", mark) ~= nil)
    KCD2MP_W160HostTalk(npc, { id = player.id }, "OnChat")
    check("C2: a second press is not told again", countEvt("w160_conv", "on ttkc_man_11", mark) == 1)
    KCD2MP_W160HostTalk(npc, { id = 99 }, "OnTalk")
    w.hostTalks["ttkc_other"] = nil
    check("C3: only this player's own talk counts", w.hostTalks["ttkc_other"] == nil)
    DIALOG = true
    NOW = NOW + 1; KCD2MP_W160ConvTick()
    check("C4: while the dialogue runs: nothing ends", w.hostTalks["ttkc_man_11"] ~= nil and w.hostTalks["ttkc_man_11"].saw == true)
    DIALOG = false
    NOW = NOW + 1; KCD2MP_W160ConvTick()
    check("C5: 1 s out of the dialogue: still held", w.hostTalks["ttkc_man_11"] ~= nil)
    mark = #LOG
    NOW = NOW + 4; KCD2MP_W160ConvTick()
    check("C6: out for 3 s+: the conversation is over, the joiner is told (w160_conv off)", w.hostTalks["ttkc_man_11"] == nil
        and countEvt("w160_conv", "off ttkc_man_11", mark) == 1 and lastLog("host-talk end npc=ttkc_man_11 why=dialog-ended", mark) ~= nil)
    KCD2MP_W160HostTalk(npc, { id = player.id }, "OnTalk")
    mark = #LOG
    NOW = NOW + 26; KCD2MP_W160ConvTick()
    check("C7: a talk that never reached a dialogue ends in 25 s", w.hostTalks["ttkc_man_11"] == nil and lastLog("why=never-started", mark) ~= nil)
    KCD2MP.w137.host, KCD2MP.w137.active = false, false
    mark = #LOG
    KCD2MP_W160HostTalk(npc, { id = player.id }, "OnTalk")
    check("C8: not a host in a session: nothing is told", w.hostTalks["ttkc_man_11"] == nil and countEvt("w160_conv", "on", mark) == 0)

    -- the joiner: the host talks to its copy
    local p = { nativeOwned = true }
    KCD2MP.npcPuppets["ttkc_man_11"] = p
    npc.GetWorldPos = function() return { x = 5, y = 6, z = 7 } end
    mark = #LOG
    check("C9: the copy is held", KCD2MP_W160Conversation(true, "ttkc_man_11") == true and w.conv["ttkc_man_11"] ~= nil
        and lastLog("WO160-CONV copy-held npc=ttkc_man_11 at 5.00 6.00 7.00", mark) ~= nil)
    check("C10: the native writer is held at once", countEvt("npc_native_hold", "ttkc_man_11 2000", mark) == 1)
    mark = #LOG
    check("C11: the puppet tick skips every writer for it", KCD2MP_W160ConvHeld("ttkc_man_11", p, npc) == true)
    NOW = NOW + 1.2
    KCD2MP_W160ConvHeld("ttkc_man_11", p, npc)
    check("C12: ... and holds the native writer again every second", countEvt("npc_native_hold", "ttkc_man_11 2000", mark) == 1)
    mark = #LOG
    KCD2MP_W160Conversation(false, "ttkc_man_11")
    check("C13: the host's conversation over: the copy is free", w.conv["ttkc_man_11"] == nil and KCD2MP_W160ConvHeld("ttkc_man_11", p, npc) == false
        and lastLog("WO160-CONV copy-released npc=ttkc_man_11", mark) ~= nil)
    KCD2MP_W160Conversation(true, "ttkc_man_11")
    NOW = NOW + 301
    mark = #LOG
    check("C14: no end ever comes: released at convMaxS", KCD2MP_W160ConvHeld("ttkc_man_11", p, npc) == false and w.conv["ttkc_man_11"] == nil
        and lastLog("past 300 s", mark) ~= nil)
    mark = #LOG
    check("C15: a name with no streamed copy: refused, logged", KCD2MP_W160Conversation(true, "ttkc_nobody") == false and lastLog("copy-held refused npc=ttkc_nobody", mark) ~= nil)
    check("C16: a hostile name is refused", KCD2MP_W160Conversation(true, "x; quit") == false)
    KCD2MP_W160Conversation(true, "ttkc_man_11")
    KCD2MP_W160ConvReleaseAll("test")
    check("C17: release-all gives every copy back", next(w.conv) == nil)

    -- the host's NPC while the joiner talks
    KCD2MP.w151 = KCD2MP.w151 or {}
    KCD2MP.w151.holdFreeze = false
    KCD2MP.w137.held = {}
    CMDS = {}
    mark = #LOG
    KCD2MP_W137HostHold(true, "ttkc_man_11", 1, "talk")
    check("C18: the joiner talks: the host's NPC is FROZEN for the conversation", CMDS[1] == "wh_ai_PauseNPC ttkc_man_11" and KCD2MP.w137.held["ttkc_man_11"].paused == true
        and lastLog("WO137-HOLD on npc=ttkc_man_11 peer=1 why=talk exec=ok", mark) ~= nil, CMDS[1])
    CMDS = {}
    KCD2MP_W137HostHold(false, "ttkc_man_11", 1, "talk")
    check("C19: and resumed when the talk ends", CMDS[1] == "wh_ai_ResumeNPC ttkc_man_11")
    CMDS = {}
    mark = #LOG
    KCD2MP_W137HostHold(true, "ttkc_man_11", 1, "w139-stop")
    check("C20: a guard's stop stays block-only, and the log says why", #CMDS == 0 and KCD2MP.w137.held["ttkc_man_11"].blockOnly == true
        and lastLog("exec=block-only(why=w139-stop,hold_freeze=off)", mark) ~= nil)
    KCD2MP_W137HostHold(false, "ttkc_man_11", 1, "w139-stop")
    KCD2MP_W160SetConvHold("off")
    CMDS = {}
    mark = #LOG
    KCD2MP_W137HostHold(true, "ttkc_man_11", 1, "talk")
    check("C21: mp_conv_hold off: block-only again, with that reason", #CMDS == 0 and lastLog("exec=block-only(why=talk,hold_freeze=off,mp_conv_hold=off)", mark) ~= nil)
    KCD2MP_W137HostHold(false, "ttkc_man_11", 1, "talk")
    KCD2MP_W160HostTalk(npc, { id = player.id }, "OnTalk")
    check("C22: ... and the host's talk is not told", w.hostTalks["ttkc_man_11"] == nil)
    KCD2MP_W160SetConvHold("on")
    check("C23: the switch is registered", CCMDS["mp_conv_hold"] ~= nil and CCMDS["mp_conv_hold"].body == "KCD2MP_W160SetConvHold(%line)")
    ENTS["ttkc_man_11"] = nil; KCD2MP.npcPuppets["ttkc_man_11"] = nil
    noErrs("C")
end

-- (H) the herb proxy ---------------------------------------------------------------------------------------------------------------
do
    ERRS = {}
    local clips = {}
    local ent = { StartAnimation = function(self, layer, name, a, blend, speed, loop) clips[#clips + 1] = { name = name, loop = loop } end }
    KCD2MP.ghosts = KCD2MP.ghosts or {}
    KCD2MP.ghosts["7"] = { entity = ent, istate = { animLoopName = "walk" } }
    local g = KCD2MP.ghosts["7"]
    local mark = #LOG
    check("H1: the proxy starts a plain looped clip on the avatar", KCD2MP_W160HerbProxy(7, true) == true and #clips == 1 and clips[1].name == "herbs_picking_area_loop"
        and clips[1].loop == true and g.istate.herbProxy ~= nil and lastLog("WO160-HERB id=7 on clip=herbs_picking_area_loop", mark) ~= nil)
    check("H2: the locomotion update is held off meanwhile (oneShotUntil)", g.istate.oneShotUntil ~= nil and g.istate.oneShotUntil > NOW)
    check("H3: a second 'on' is nothing", KCD2MP_W160HerbProxy(7, true) == true and #clips == 1)
    NOW = NOW + 1
    check("H4: the hold keeps it off and does not restart the clip within 4 s", KCD2MP_W160HerbHold("7", g, g.istate) == true and #clips == 1)
    NOW = NOW + 4
    check("H5: ... restarts it as a keep-alive after 4 s", KCD2MP_W160HerbHold("7", g, g.istate) == true and #clips == 2)
    mark = #LOG
    KCD2MP_W160HerbProxy(7, false)
    check("H6: off: the proxy is gone, the locomotion loop starts again, the DLL is asked for the T-pose pulse", g.istate.herbProxy == nil and g.istate.animLoopName == nil
        and g.istate.oneShotUntil == nil and countEvt("w160_loopstop", "7 herb clip stopped", mark) == 1 and lastLog("WO160-HERB id=7 off", mark) ~= nil)
    check("H7: not held once off", KCD2MP_W160HerbHold("7", g, g.istate) == false)
    check("H8: no avatar: nothing", KCD2MP_W160HerbProxy(99, true) == false)
    KCD2MP_W160HerbProxy(7, true)
    NOW = NOW + 601
    check("H9: no end ever came: released after 600 s", KCD2MP_W160HerbHold("7", g, g.istate) == false and g.istate.herbProxy == nil)
    check("H10: mp_avatar_herbs is ON by default now", KCD2MP.w143.herbs == true)
    KCD2MP.ghosts["7"] = nil
    noErrs("H")
end

-- (M) the mount gate -----------------------------------------------------------------------------------------------------------------
do
    ERRS = {}
    local HID, ACT, HX = false, true, 12
    local horse = { GetWorldPos = function() return { x = HX, y = 0, z = 0 } end, IsHidden = function() return HID end, IsActive = function() return ACT end }
    local ghost = { entity = { human = {}, GetWorldPos = function() return { x = 10, y = 0, z = 0 } end }, istate = {} }
    local again = 0
    local function cb() again = again + 1 end
    local function timers() local n = 0; for _, t in ipairs(TIMERS) do if t.at == NOW then n = n + 1 end end return n end
    local mark = #LOG
    TIMERS = {}
    check("M1: a ready horse: go ahead at once", KCD2MP_W160MountGate(7, ghost, horse, cb) == true and #TIMERS == 0)
    HID = true
    NOW = NOW + 10
    TIMERS = {}
    check("M2: a hidden horse: not yet, polled again in 250 ms", KCD2MP_W160MountGate(7, ghost, horse, cb) == false and #TIMERS == 1 and TIMERS[1].ms == 250)
    NOW = NOW + 1.0
    HID = false
    mark = #LOG
    check("M3: it shows after 1 s: ready, and the wait is logged", KCD2MP_W160MountGate(7, ghost, horse, cb) == true and lastLog("WO160-MOUNT id=7 ready after 1.00 s", mark) ~= nil)
    NOW = NOW + 10
    ACT = false
    TIMERS = {}
    KCD2MP_W160MountGate(7, ghost, horse, cb)
    NOW = NOW + 5.1
    TIMERS = {}
    mark = #LOG
    check("M4: still not ready after 5 s: refused with its reason, retried in 3 s", KCD2MP_W160MountGate(7, ghost, horse, cb) == false and #TIMERS == 1 and TIMERS[1].ms == 3000
        and lastLog("WO160-MOUNT id=7 refused after 5.1 s: horse-inactive", mark) ~= nil)
    ACT = true
    HX = 40
    NOW = NOW + 10
    local ok, why = KCD2MP_W160MountReady(ghost, horse)
    check("M5: a horse 30 m from the rider is not ready (the engine rolls that mount back)", ok == false and why:find("from-the-rider", 1, true) ~= nil, why)
    HX = 12
    ghost.entity.human = nil
    ok, why = KCD2MP_W160MountReady(ghost, horse)
    check("M6: a rider with no body is not ready", ok == false and why == "rider-has-no-body", why)
    ghost.entity.human = {}
    horse.GetWorldPos = function() return { x = 0, y = 0, z = 0 } end
    ok, why = KCD2MP_W160MountReady(ghost, horse)
    check("M7: a horse not streamed in (the origin) is not ready", ok == false and why == "horse-not-streamed-in", why)
    check("M8: the stats count it", KCD2MP.w160.mountStats.ready >= 2 and KCD2MP.w160.mountStats.refused == 1 and KCD2MP.w160.mountStats.waited == 1)
    noErrs("M")
end

-- (O) M3 independent horse ownership, dismount parking, same-soul proxy, and save-safe tack sync ------------------------------------
do
    ERRS = {}
    local BY_ID = {}
    local nextSpawnId = 5000
    local origGetEnt = System.GetEntity
    local origSpawn = System.SpawnEntity
    local origRemove = System.RemoveEntity
    local origSphere = System.GetEntitiesInSphere
    local removedIds = {}

    local function mkHorse(name, eid, x, y, z, legal)
        local invItems = {}
        local eqItems = {}
        local h = {
            id = eid,
            class = "Horse",
            px = x or 10, py = y or 10, pz = z or 0,
            mountIsLegal = legal == true,
            noSave = false,
            invItems = invItems,
            eqItems = eqItems,
            GetName = function() return name end,
            GetWorldPos = function(self) return { x = self.px, y = self.py, z = self.pz } end,
            SetWorldPos = function(self, p) self.px, self.py, self.pz = p.x, p.y, p.z end,
            GetWorldAngles = function() return { x = 0, y = 0, z = 0 } end,
            SetWorldAngles = function() end,
            SetMountIsLegal = function(self, v) self.mountIsLegal = (v == true) end,
            IsMountLegal = function(self) return self.mountIsLegal == true end,
            SetMountableByPlayer = function(self, v) self.mountable = v end,
            Hide = function() end,
            SetFlags = function(self, flag, mode) if mode == 0 or mode == 1 then self.noSave = true end end,
            GetFlags = function(self) return self.noSave and 1 or 0 end,
            soul = { GetId = function() return "userdata: 0500000000000BBB" end },
            inventory = {
                CreateItem = function(_, cls)
                    local handle = "item:" .. cls
                    invItems[handle] = cls
                    return handle
                end,
                FindItem = function(_, cls)
                    for k, v in pairs(invItems) do if v == cls then return k end end
                    return nil
                end,
                GetInventoryTable = function()
                    local t = {}
                    for k in pairs(invItems) do t[#t + 1] = k end
                    return t
                end,
                DeleteItem = function(_, handle)
                    invItems[handle] = nil
                    eqItems[handle] = nil
                    return true
                end,
            },
            actor = {
                EquipInventoryItem = function(_, handle) eqItems[handle] = true; return true end,
                UnequipInventoryItem = function(_, handle) eqItems[handle] = nil; return true end,
            },
        }
        ENTS[name] = h
        BY_ID[eid] = h
        return h
    end

    System.GetEntity = function(id) return BY_ID[id] end
    System.SpawnEntity = function(params)
        nextSpawnId = nextSpawnId + 1
        local h = mkHorse(params.name, nextSpawnId, params.position.x, params.position.y, params.position.z, false)
        return h
    end
    System.RemoveEntity = function(eid)
        local h = BY_ID[eid]
        if h then ENTS[h:GetName()] = nil; BY_ID[eid] = nil end
        removedIds[eid] = true
    end

    -- O1: Joiner reports 65-char bracketed horse name, exempts full name in WO-216, unbinds lh* alias, and ignores unstreamed GetHorseId()==0
    local bracketName = "horse[animalcare/horseCare6_ff15a088-7332-4a17-9536-3e8f6a8898ad]"
    local bracketAlias = KCD2MP_W217Alias(bracketName)
    local hBracket = mkHorse(bracketName, 701, 20, 20, 0, false)
    local bracketHp = 100
    hBracket.soul.GetState = function(_, k) return k == "health" and bracketHp or nil end
    local ownHorseId = hBracket.id
    player.player = { GetHorseId = function() return ownHorseId end }
    KCD2MP.w137.joiner, KCD2MP.w137.host, KCD2MP.w137.active = true, false, true
    KCD2MP.w160.ownHorse = { name = nil, at = -1e9 }
    KCD2MP.npcPuppets[bracketAlias] = { nativeOwned = true }
    W216.exemptAt = {}
    local mark = #LOG
    NOW = NOW + 10
    KCD2MP_W160OwnHorseTick()
    check("O1: joiner reports 65-char bracketed Trosky horse name, exempts full name in WO-216, and unbinds WO-217 lh* alias",
        countEvt("w160_ownhorse", bracketName, mark) == 1
        and countEvt("w216_exempt", bracketName .. " on 120", mark) == 1
        and countEvt("npc_native", bracketAlias .. " off", mark) == 1
        and KCD2MP.npcPuppets[bracketAlias] == nil
        and KCD2MP_W136Ridden(bracketAlias) == true)
    ownHorseId = 0
    BY_ID[701] = nil
    ENTS[bracketName] = nil
    mark = #LOG
    NOW = NOW + 10
    KCD2MP_W160OwnHorseTick()
    check("O1b: unstreamed horse layer (GetHorseId()==0 / GetEntity==nil) does NOT wipe ownHorse or emit w160_ownhorse -",
        countEvt("w160_ownhorse", "-", mark) == 0
        and KCD2MP.w160.ownHorse.name == bracketName)
    BY_ID[701] = hBracket
    ENTS[bracketName] = hBracket
    ownHorseId = hBracket.id
    bracketHp = 0
    mark = #LOG
    NOW = NOW + 10
    KCD2MP_W160OwnHorseTick()
    check("O1c: loaded dead horse (health <= 0) clears ownHorse and emits w160_ownhorse -",
        countEvt("w160_ownhorse", "-", mark) == 1
        and KCD2MP.w160.ownHorse.name == nil)
    check("O1d: snapshot seed (KCD2MP_M3SeedOwnHorse) pre-arms whistle and stream hold before layer streams in",
        KCD2MP_M3SeedOwnHorse("nebakovPruzkum_herynk") == true
        and KCD2MP_W136Ridden("nebakovPruzkum_herynk") == true
        and KCD2MP_M3OnJoinerWhistle() == true)
    KCD2MP_M3SeedOwnHorse("-")

    -- O2/O3/O4/O5: Host owns Pebbles, Joiner 1 owns tsem_horseForSale_2 with saddle + bridle
    KCD2MP.w137.joiner, KCD2MP.w137.host, KCD2MP.w137.active = false, true, true
    local pebbles = mkHorse("tsem_sedivka", 702, 5, 5, 0, false)
    local horseB = mkHorse("tsem_horseForSale_2", 703, 12, 10, 0, false)
    local lent = mkHorse("lent_horse", 704, 15, 10, 0, true)
    ownHorseId = pebbles.id
    System.GetEntitiesInSphere = function() return { pebbles, horseB, lent } end

    local saddle = "bf06d242-4dcb-48e9-bbf1-b6a36f368a57"
    local bridle = "505343b8-b5ff-4f47-94d9-470a1977a421"
    mark = #LOG
    check("O2: host emits crime context and equips tack on Horse B WITHOUT flipping mountIsLegal true for Host",
        KCD2MP_W160PeerHorse(1, "tsem_horseForSale_2", saddle .. "," .. bridle) == true
        and horseB.mountIsLegal == false
        and countEvt("m3_peerhorse_ctx", "tsem_horseForSale_2 1", mark) == 1
        and horseB.eqItems["item:" .. saddle] == true
        and horseB.eqItems["item:" .. bridle] == true)
    mark = #LOG
    KCD2MP_W151OnMountStart(horseB, player)
    KCD2MP.w151.mountingName, KCD2MP.w151.mountingAt = nil, nil
    check("O2b: if Host mounts an originally-illegal peer horse, crime_ignoredHorseTheft_Horse is cleared so theft still applies",
        countEvt("m3_peerhorse_ctx", "tsem_horseForSale_2 0", mark) == 1)
    KCD2MP_M3HostTick()

    -- L12: even if a peer claims an originally-legal world horse (lent_horse), W139HostHorses excludes it
    KCD2MP_W160PeerHorse(9, "lent_horse", "")
    mark = #LOG
    KCD2MP_W139HostHorses()
    check("O3: W139HostHorses excludes peer-owned horses even when originally legal on Host",
        countEvt("w139_horses", "-", mark) == 1
        and countEvt("w139_horses", "lent_horse", mark) == 0)
    KCD2MP_W160PeerHorse(9, "-", "")
    mark = #LOG
    KCD2MP_W139HostHorses()
    check("O3b: W139HostHorses shares unowned lent world horses only (neither host's Pebbles nor joiner's private Horse B)",
        countEvt("w139_horses", "lent_horse", mark) == 1
        and countEvt("w139_horses", "tsem_sedivka", mark) == 0
        and countEvt("w139_horses", "tsem_horseForSale_2", mark) == 0)

    -- Joiner 1 mounts & dismounts Horse B -> stays parked (paused), never wh_ai_ResumeNPC!
    KCD2MP.ghosts["1"] = { entity = { id = 801, GetWorldPos = function() return { x = 12, y = 10, z = 1.5 } end, human = { ForceDismount = function() end, IsMounted = function() return false end } }, istate = { isRiding = true, spawnedAtClock = -100 } }
    KCD2MP.ghostHorseName["1"] = "tsem_horseForSale_2"
    CMDS = {}
    KCD2MP_SpawnHorse("1", 12, 10, 0, 0)
    KCD2MP_W154RideNative("1", true)
    CMDS = {}
    KCD2MP.ghosts["1"].istate.isRiding = false
    KCD2MP_GhostDismount("1", KCD2MP.ghosts["1"], "dismount")
    KCD2MP_RemoveHorse("1")
    local sawResume = false
    local sawPause = false
    for _, c in ipairs(CMDS) do
        if c == "wh_ai_ResumeNPC tsem_horseForSale_2" then sawResume = true end
        if c == "wh_ai_PauseNPC tsem_horseForSale_2" then sawPause = true end
    end
    check("O4: dismounting joiner's Horse B keeps it AI-paused at the dismount spot instead of walking back to Semine",
        sawPause == true and sawResume == false and KCD2MP.w160.parkedPeerHorses["tsem_horseForSale_2"] ~= nil)

    -- O5: Pre-save strips mod-created tack from world horse; "busy" is ignored; 15s safety timeout / SaveDone restores it
    KCD2MP_M3BeforeHostSave()
    check("O5a: before host save, temp tack is stripped from world Horse B so .whs stays clean",
        next(horseB.invItems) == nil and next(horseB.eqItems) == nil)
    KCD2MP_W178SaveDone("busy", "", "quicksave:host:0")
    check("O5b: KCD2MP_W178SaveDone('busy') does NOT prematurely restore tack while another save is writing",
        KCD2MP.w160.saveTackStripped == true and next(horseB.eqItems) == nil)
    NOW = NOW + 16
    KCD2MP_M3HostTick()
    check("O5c: 15 s safety timeout in M3HostTick auto-restores temp tack onto world Horse B",
        KCD2MP.w160.saveTackStripped == false
        and horseB.eqItems["item:" .. saddle] == true and horseB.eqItems["item:" .. bridle] == true)

    -- O6: Same-soul collision (Joiner 2 also owns tsem_sedivka == Host's own horse, and Joiner 3 also owns tsem_horseForSale_2 == Joiner 1's horse!)
    KCD2MP.ghosts["2"] = { entity = { id = 802, GetWorldPos = function() return { x = 8, y = 8, z = 1.5 } end, human = { ForceDismount = function() end, IsMounted = function() return false end } }, istate = { isRiding = true, spawnedAtClock = -100 } }
    KCD2MP.ghostHorseName["2"] = "tsem_sedivka"
    CMDS = {}
    KCD2MP_W160PeerHorse(2, "tsem_sedivka", saddle)
    local spawnedProxy = KCD2MP_SpawnHorse("2", 8, 8, 0, 0)
    check("O6: same-soul collision leaves Host's Pebbles untouched and mounts Joiner 2 on NO_SAVE kcd2mp_peerhorse_2 with tack",
        pebbles.eqItems["item:" .. saddle] == nil
        and KCD2MP.w160.parkedPeerHorses["tsem_sedivka"] == nil
        and spawnedProxy ~= nil and spawnedProxy ~= pebbles
        and spawnedProxy:GetName() == "kcd2mp_peerhorse_2"
        and spawnedProxy.eqItems["item:" .. saddle] == true)
    -- M11: if proxy tack was cleared, M3HostTick re-asserts it
    spawnedProxy.actor:UnequipInventoryItem("item:" .. saddle)
    KCD2MP_M3HostTick()
    check("O6b: M3HostTick re-asserts tack on kcd2mp_peerhorse_2 and dismount parks without destroying it",
        spawnedProxy.eqItems["item:" .. saddle] == true
        and (function() KCD2MP_RemoveHorse("2"); return KCD2MP.w160.peerProxies["2"] ~= nil and removedIds[spawnedProxy.id] == nil end)())

    -- M9: two joiners (1 and 3) owning the same tsem_horseForSale_2 -> Joiner 1 keeps world horse, Joiner 3 gets NO_SAVE kcd2mp_peerhorse_3
    KCD2MP.ghosts["3"] = { entity = { id = 805, GetWorldPos = function() return { x = 14, y = 10, z = 1.5 } end, human = { ForceDismount = function() end, IsMounted = function() return false end } }, istate = { isRiding = true, spawnedAtClock = -100 } }
    KCD2MP.ghostHorseName["3"] = "tsem_horseForSale_2"
    KCD2MP_W160PeerHorse(3, "tsem_horseForSale_2", bridle)
    local proxy3 = KCD2MP_SpawnHorse("3", 14, 10, 0, 0)
    check("O6c: second joiner owning same tsem_horseForSale_2 gets dedicated NO_SAVE kcd2mp_peerhorse_3 without overwriting Joiner 1's tack",
        proxy3 ~= nil and proxy3 ~= horseB and proxy3:GetName() == "kcd2mp_peerhorse_3"
        and proxy3.eqItems["item:" .. bridle] == true
        and horseB.eqItems["item:" .. saddle] == true)
    KCD2MP_RemoveHorse("3")
    KCD2MP_W160PeerHorse(3, "-", "")

    -- O8 (H5): riding an unowned horse uses generic proxy, and premature peerProxy swaps cleanly when world horse arrives
    KCD2MP.ghosts["1"].istate.isRiding = true
    KCD2MP.ghostHorseName["1"] = "unowned_bandit_horse"
    local genericRide = KCD2MP_SpawnHorse("1", 12, 10, 0, 0)
    check("O8a: joiner riding an unowned horse spawns generic kcd2mp_horse_1 instead of personal peerhorse stand-in",
        genericRide ~= nil and genericRide:GetName() == "kcd2mp_horse_1" and KCD2MP.w160.peerProxies["1"] == nil)
    KCD2MP_RemoveHorse("1")
    ENTS["tsem_horseForSale_2"] = nil   -- simulate unstreamed when isRiding arrives before HorseInfoDown
    KCD2MP.ghostHorseName["1"] = ""
    local earlyPeerProxy = KCD2MP_SpawnHorse("1", 12, 10, 0, 0)
    ENTS["tsem_horseForSale_2"] = horseB   -- streams in, then HorseInfoDown arrives
    KCD2MP_SetGhostHorse("1", "tsem_horseForSale_2")
    check("O8b: premature peerProxy swaps cleanly to world Horse B when HorseInfoDown arrives (no duplicate horse left behind)",
        earlyPeerProxy ~= nil and earlyPeerProxy:GetName() == "kcd2mp_peerhorse_1"
        and KCD2MP.horseGhosts["1"] ~= nil and KCD2MP.horseGhosts["1"].isWorldHorse == true
        and removedIds[earlyPeerProxy.id] == true and KCD2MP.w160.peerProxies["1"] == nil)
    KCD2MP.ghosts["1"].istate.isRiding = false
    KCD2MP_RemoveHorse("1")

    -- O7: Disconnect / clear releases world horse + removes peer proxy
    mark = #LOG
    CMDS = {}
    KCD2MP_W160PeerHorse(1, "-", "")
    KCD2MP_W160PeerHorse(2, "-", "")
    local resumedHorseB = false
    for _, c in ipairs(CMDS) do if c == "wh_ai_ResumeNPC tsem_horseForSale_2" then resumedHorseB = true end end
    check("O7: peer disconnect strips tack, keeps original legality false, clears crime context, unparks Horse B, and deletes stand-in proxy",
        next(horseB.invItems) == nil and horseB.mountIsLegal == false
        and countEvt("m3_peerhorse_ctx", "tsem_horseForSale_2 0", mark) == 1
        and resumedHorseB == true
        and removedIds[spawnedProxy.id] == true)

    -- Cleanup stubs
    for _, n in ipairs({ bracketName, "tsem_sedivka", "tsem_horseForSale_2", "lent_horse", "kcd2mp_peerhorse_1", "kcd2mp_peerhorse_2", "kcd2mp_peerhorse_3", "kcd2mp_horse_1" }) do ENTS[n] = nil end
    KCD2MP.ghosts["1"] = nil; KCD2MP.ghosts["2"] = nil; KCD2MP.ghosts["3"] = nil
    KCD2MP.ghostHorseName["1"] = nil; KCD2MP.ghostHorseName["2"] = nil; KCD2MP.ghostHorseName["3"] = nil
    KCD2MP.w137.host, KCD2MP.w137.joiner, KCD2MP.w137.active = false, false, false
    player.player = nil
    System.GetEntity = origGetEnt
    System.SpawnEntity = origSpawn
    System.RemoveEntity = origRemove
    System.GetEntitiesInSphere = origSphere
    noErrs("O")
end

-- (W) M3 far whistle (>150 m deferred fetch, unloaded-layer NO_SAVE stand-in, and joiner 15 s stream release) ------------------------
do
    ERRS = {}
    local BY_ID = {}
    local nextSpawnId = 6000
    local origGetEnt = System.GetEntity
    local origSpawn = System.SpawnEntity
    local origRemove = System.RemoveEntity
    local origPlayerPos = player.GetWorldPos
    local hostPos = { x = 0, y = 0, z = 0 }
    local removedIds = {}

    local function mkHorse(name, eid, x, y, z)
        local invItems, eqItems = {}, {}
        local h = {
            id = eid,
            class = "Horse",
            px = x or 0, py = y or 0, pz = z or 0,
            mountIsLegal = false,
            noSave = false,
            invItems = invItems,
            eqItems = eqItems,
            GetName = function() return name end,
            GetWorldPos = function(self) return { x = self.px, y = self.py, z = self.pz } end,
            SetWorldPos = function(self, p) self.px, self.py, self.pz = p.x, p.y, p.z end,
            GetWorldAngles = function() return { x = 0, y = 0, z = 0 } end,
            SetWorldAngles = function() end,
            SetMountIsLegal = function(self, v) self.mountIsLegal = (v == true) end,
            IsMountLegal = function(self) return self.mountIsLegal == true end,
            SetMountableByPlayer = function() end,
            Hide = function() end,
            SetFlags = function(self) self.noSave = true end,
            GetFlags = function(self) return self.noSave and 1 or 0 end,
            soul = { GetId = function() return "userdata: 0500000000000CCC" end },
            inventory = {
                CreateItem = function(_, cls) local k = "item:" .. cls; invItems[k] = cls; return k end,
                FindItem = function(_, cls) for k, v in pairs(invItems) do if v == cls then return k end end return nil end,
                GetInventoryTable = function() local t = {}; for k in pairs(invItems) do t[#t + 1] = k end return t end,
                DeleteItem = function(_, k) invItems[k] = nil; eqItems[k] = nil; return true end,
            },
            actor = {
                EquipInventoryItem = function(_, k) eqItems[k] = true; return true end,
                UnequipInventoryItem = function(_, k) eqItems[k] = nil; return true end,
            },
        }
        ENTS[name] = h
        BY_ID[eid] = h
        return h
    end

    player.GetWorldPos = function() return hostPos end
    System.GetEntity = function(id) return BY_ID[id] end
    System.SpawnEntity = function(params)
        nextSpawnId = nextSpawnId + 1
        return mkHorse(params.name, nextSpawnId, params.position.x, params.position.y, params.position.z)
    end
    System.RemoveEntity = function(eid)
        local h = BY_ID[eid]
        if h then ENTS[h:GetName()] = nil; BY_ID[eid] = nil end
        removedIds[eid] = true
    end

    -- W1: Joiner whistle & ownership permanently suppress own horse from Host puppet stream (H4)
    local jHorse = mkHorse("nebakovPruzkum_herynk", 901, 50, 50, 0)
    player.player = { GetHorseId = function() return jHorse.id end }
    KCD2MP.w137.joiner, KCD2MP.w137.active = true, true
    KCD2MP.w131.joiner, KCD2MP.w131.guard, KCD2MP.w131.shared, KCD2MP.w131.aliveAt = true, true, true, NOW
    KCD2MP.npcPuppets["nebakovPruzkum_herynk"] = {}
    local mark = #LOG
    check("W1: joiner whistle unbinds puppet and suppresses own horse from host stream",
        KCD2MP_M3OnJoinerWhistle() == true
        and KCD2MP.npcPuppets["nebakovPruzkum_herynk"] == nil
        and KCD2MP_W136Ridden("nebakovPruzkum_herynk") == true
        and countEvt("w136_ride", "nebakovPruzkum_herynk 1", mark) >= 1)
    NOW = NOW + 20
    KCD2MP.w131.aliveAt = NOW
    mark = #LOG
    KCD2MP_W136RideTick()
    check("W1b: after 20 s (> 15 s whistle / > 3 s dismount), joiner's own companion horse stays permanently suppressed from host stream",
        KCD2MP_W136Ridden("nebakovPruzkum_herynk") == true
        and countEvt("w136_ride", "nebakovPruzkum_herynk 0", mark) == 0)
    mark = #LOG
    KCD2MP_M3SeedOwnHorse("-")
    check("W1c: clearing joiner's own horse lifts stream hold cleanly",
        KCD2MP_W136Ridden("nebakovPruzkum_herynk") == false
        and countEvt("w136_ride", "nebakovPruzkum_herynk 0", mark) == 1)
    ENTS["nebakovPruzkum_herynk"] = nil; BY_ID[901] = nil
    KCD2MP.w137.joiner, KCD2MP.w137.active = false, false
    KCD2MP.w131.joiner, KCD2MP.w131.guard, KCD2MP.w131.shared = false, false, false
    player.player = nil

    -- W2: Host > 150 m from Joiner's avatar -> deferred in pendingFetch, fulfilled when Host comes within 150 m
    local caparison = "bf06d242-4dcb-48e9-bbf1-b6a36f368a57"
    local farHorse = mkHorse("tsem_horseForSale_2", 902, 5, 5, 0)
    KCD2MP_W160PeerHorse(3, "tsem_horseForSale_2", caparison)
    KCD2MP.ghosts["3"] = {
        entity = {
            id = 803,
            GetWorldPos = function() return { x = 300, y = 0, z = 10 } end,
            GetWorldAngles = function() return { x = 0, y = 0, z = 0 } end,
        },
        istate = { isRiding = false, spawnedAtClock = -100 },
    }
    hostPos = { x = 0, y = 0, z = 0 }   -- 300 m away (> 150 m physics bubble)
    check("W2a: whistle > 150 m from host records pendingFetch instead of refusing or moving into unstreamed terrain",
        KCD2MP_W160Whistle(3) == true
        and KCD2MP.w160.pendingFetch["3"] ~= nil
        and farHorse.px == 5)
    -- Host walks within 150 m of (307, 0) -> M3HostTick fulfills pendingFetch and parks Horse B there!
    hostPos = { x = 220, y = 0, z = 10 }
    KCD2MP_M3HostTick()
    check("W2b: once within 150 m physics range, M3HostTick places and parks Horse B beside the joiner's whistle spot",
        KCD2MP.w160.pendingFetch["3"] == nil
        and math.abs(farHorse.px - 307) < 0.5
        and KCD2MP.w160.parkedPeerHorses["tsem_horseForSale_2"] ~= nil)

    -- W3: Host <= 150 m from Joiner's avatar, but Horse B's level layer is unstreamed (nil) -> spawns NO_SAVE stand-in 7 m beside avatar!
    hostPos = { x = 0, y = 0, z = 0 }
    KCD2MP.ghosts["4"] = {
        entity = {
            id = 804,
            GetWorldPos = function() return { x = 30, y = 40, z = 5 } end,
            GetWorldAngles = function() return { x = 0, y = 0, z = 0 } end,
        },
        istate = { isRiding = false, spawnedAtClock = -100 },
    }
    KCD2MP_W160PeerHorse(4, "nebakovPruzkum_herynk", caparison)   -- not in ENTS yet (Nebakov layer unstreamed!)
    check("W3a: whistle for unstreamed horse within 150 m spawns NO_SAVE kcd2mp_peerhorse_4 stand-in 7 m beside avatar with tack",
        KCD2MP_W160Whistle(4) == true
        and KCD2MP.w160.peerProxies["4"] ~= nil
        and KCD2MP.w160.peerProxies["4"].entity.noSave == true
        and math.abs(KCD2MP.w160.peerProxies["4"].entity.px - 37) < 0.5
        and KCD2MP.w160.peerProxies["4"].entity.eqItems["item:" .. caparison] == true
        and KCD2MP.w160.pendingFetch["4"] ~= nil)
    local standInId = KCD2MP.w160.peerProxies["4"].entityId
    -- Later, Nebakov layer streams in -> M3HostTick syncs real Herring to stand-in's spot and retires stand-in!
    local realHerring = mkHorse("nebakovPruzkum_herynk", 903, 900, 900, 50)
    KCD2MP_M3HostTick()
    check("W3b: when unstreamed Herring later streams in, M3HostTick syncs Herring to the stand-in spot and retires the stand-in",
        KCD2MP.w160.pendingFetch["4"] == nil
        and math.abs(realHerring.px - 37) < 0.5
        and realHerring.eqItems["item:" .. caparison] == true
        and removedIds[standInId] == true)

    -- Cleanup
    KCD2MP_M3ReleaseAllPeerHorses("test-cleanup")
    for _, n in ipairs({ "tsem_horseForSale_2", "nebakovPruzkum_herynk", "kcd2mp_peerhorse_4" }) do ENTS[n] = nil end
    KCD2MP.ghosts["3"] = nil; KCD2MP.ghosts["4"] = nil
    player.GetWorldPos = origPlayerPos
    System.GetEntity = origGetEnt
    System.SpawnEntity = origSpawn
    System.RemoveEntity = origRemove
    noErrs("W")
end

OUT = table.concat(RESULTS, "\n")
