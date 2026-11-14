// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KcdMp.Client;

/// <summary>
/// WO-125 Phase 1: the joiner's Henry files, one folder per host world
/// (docs/WO-125-findings.md). Kept under the agent's data folder
/// (KCDMP_DATA_DIR, else %LOCALAPPDATA%\KCDMP) in <c>henry/</c>: never in a
/// playline, never in the repo.
///
/// <code>
///   henry/&lt;worldTag&gt;/snap-&lt;yyyyMMddHHmmssfff&gt;-&lt;md5&gt;-&lt;source&gt;.hblk   a Henry block (WhsSave.SerializeBlock)
///   henry/&lt;worldTag&gt;/world.json                                  last joined + log-only pair details
///   henry/ledger.json                                             saves the mod made or saw land in a playline
///   henry/swept/                                                  files moved out of a playline (newest 8 kept)
/// </code>
///
/// The world tag is <see cref="WhsSave.SeedTag"/> of the playthrough seed: the
/// seed itself is never stored. A snapshot is keyed by the md5 of the host
/// save it pairs with (the save's own footer MD5 -- the one WorldSaved and
/// WorldOffer carry); the name carries it so the index can always be rebuilt
/// from the folder. Writes are .part + rename, then read back (hash + full
/// parse). A snapshot that does not parse is skipped and logged, and the next
/// older one is used.
/// </summary>
public sealed class HenryStore
{
    /// <summary>
    /// Snapshots kept per world. 100 = the host's autosave rotation (100 slots per
    /// playline, wh_sys_PlaylineSavegameCount, observed WO-112), the rotation that
    /// turns over fastest (WO-122's 5-minute schedule): every autosave the host can
    /// still load has its pair. Quick and manual saves rotate slower or not at all;
    /// a host save older than the oldest pair falls back to the newest snapshot.
    /// </summary>
    public const int KeepPerWorld = 100;

    /// <summary>
    /// WO-184: snapshots kept per host save, the newest first. A host save gets later snapshots of its own (the joiner's ESC menu, a
    /// leave: the late rule); a restore takes the newest of a host save (an older one only when the newest is broken), so the rest must
    /// not push other host saves' pairs out: <see cref="KeepPerWorld"/> counts host saves, not files.
    /// </summary>
    public const int KeepPerHostSave = 2;

    /// <summary>A world not joined for this many days is deleted at agent start (logged). The maintainer may change it.</summary>
    public const int StaleDays = 90;

    public const string SourceBring = "bring", SourceFresh = "fresh", SourceSnapshot = "snapshot", SourceJoin = "join", SourceRestore = "restore";

    public sealed record TransferOutEntry(
        uint DropId,
        string Class,
        int Amount,
        float Health,
        DateTime ClaimedUtc,
        long Seq,
        long RemovedSeq = 0,
        DateTime? RemovedUtc = null,
        long SelfPickupSeq = 0,
        DateTime? SelfPickupUtc = null)
    {
        [JsonIgnore]
        public long EffectiveRemovedSeq => RemovedSeq > 0 ? RemovedSeq : Seq;
        [JsonIgnore]
        public DateTime EffectiveRemovedUtc => RemovedUtc ?? ClaimedUtc;

        /// <summary>
        /// True iff this peer-claimed outgoing drop was present in Henry's saved inventory at the moment
        /// the snapshot was captured: either before the drop left Henry's inventory (<c>sq &lt; EffectiveRemovedSeq</c>),
        /// or during an in-flight local self-pickup race before a peer won <c>ItemClaimDown</c>
        /// (<c>SelfPickupSeq &lt;= sq &lt; Seq</c>).
        /// </summary>
        public bool AppliesToSnapshot(long? snapSeq, DateTime snapTakenUtc)
        {
            if (snapSeq is long sq && sq > 0 && EffectiveRemovedSeq > 0)
            {
                return sq < EffectiveRemovedSeq
                    || (SelfPickupSeq > 0 && sq >= SelfPickupSeq && Seq > SelfPickupSeq && sq < Seq);
            }
            return snapTakenUtc < EffectiveRemovedUtc
                || (SelfPickupUtc is DateTime spUtc && snapTakenUtc >= spUtc && ClaimedUtc > spUtc && snapTakenUtc < ClaimedUtc);
        }
    }

    public sealed record SnapshotCapture(
        string Tag,
        long CaptureSeq,
        DateTime CapturedUtc,
        long TokenId = 0);

    private readonly Func<DateTime> _now;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Dictionary<(string Tag, uint DropId), (long RemovedSeq, DateTime RemovedUtc, long SelfPickupSeq, DateTime? SelfPickupUtc)> _pendingOutDrops = new();
    private readonly List<(SnapshotCapture Capture, DateTime CreatedRealtimeUtc)> _activeCaptures = [];
    private long _nextCaptureTokenId;

    public string Root { get; }

    public HenryStore(string root, Func<DateTime>? now = null, Action<string>? log = null)
    {
        Root = root;
        _now = now ?? (() => DateTime.UtcNow);
        _log = log ?? (s => Console.WriteLine(s));
    }

    /// <summary>&lt;data&gt;/henry, where &lt;data&gt; is KCDMP_DATA_DIR or %LOCALAPPDATA%\KCDMP.</summary>
    public static string DefaultRoot()
    {
        string data = Environment.GetEnvironmentVariable("KCDMP_DATA_DIR") is { Length: > 0 } d
            ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KCDMP");
        return Path.Combine(data, "henry");
    }

    /// <summary>KCDMP_HENRY_NOW (an ISO date, tests of the 90-day rule) or the real clock.</summary>
    public static DateTime ClockFromEnv()
    {
        var s = Environment.GetEnvironmentVariable("KCDMP_HENRY_NOW");
        return s is { Length: > 0 } && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.UtcNow;
    }

    private static readonly Regex TagRx = new("^[0-9a-f]{10}$", RegexOptions.CultureInvariant);
    private static readonly Regex SnapRx = new(@"^snap-(\d{17})-([0-9a-f]{32})-([a-z]+)\.hblk$", RegexOptions.CultureInvariant);

    public sealed record Snapshot(string Tag, string File, string Md5, DateTime TakenUtc, string Source, long Bytes)
    {
        public string Short => $"{Md5[..8]}@{TakenUtc:yyyy-MM-dd HH:mm:ss}";
    }

    public sealed record WorldInfo(string Tag, DateTime LastJoinedUtc, int Count, long Bytes, string FirstSource);

    public string WorldDir(string tag) => Path.Combine(Root, tag);

    private static void CheckTag(string tag)
    {
        if (!TagRx.IsMatch(tag)) throw new ArgumentException("not a world tag: " + tag);
    }

    // ------------------------------------------------------------------ read

    /// <summary>Every snapshot of a world, newest first (by the time in the name).</summary>
    public List<Snapshot> Snapshots(string tag)
    {
        CheckTag(tag);
        var dir = WorldDir(tag);
        var o = new List<Snapshot>();
        if (!Directory.Exists(dir)) return o;
        foreach (var f in Directory.EnumerateFiles(dir, "snap-*.hblk"))
        {
            var m = SnapRx.Match(Path.GetFileName(f));
            if (!m.Success) continue;
            if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture,
                                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) continue;
            long len;
            try { len = new FileInfo(f).Length; } catch (IOException) { continue; }
            o.Add(new Snapshot(tag, Path.GetFileName(f), m.Groups[2].Value, t, m.Groups[3].Value, len));
        }
        return o.OrderByDescending(s => s.TakenUtc).ThenByDescending(s => s.File, StringComparer.Ordinal).ToList();
    }

    public bool HasWorld(string tag) => Snapshots(tag).Count > 0;

    /// <summary>Load and fully check one snapshot; null (logged) when it does not parse.</summary>
    public WhsSave.HenryParts? Load(Snapshot s)
    {
        try
        {
            var b = File.ReadAllBytes(Path.Combine(WorldDir(s.Tag), s.File));
            return WhsSave.ParseBlock(b);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _log($"MP-HENRY world {s.Tag}: snapshot {s.Short} is BROKEN ({ex.Message}) -- skipped, trying the next older one");
            return null;
        }
    }

    public sealed record Pick(Snapshot Snapshot, WhsSave.HenryParts Parts, string How);

    /// <summary>
    /// WO-125 Phase 5: the snapshot paired with the newest save of the host's
    /// current branch that has one (<paramref name="branchNewestFirst"/>: md5s),
    /// else the newest snapshot of the world. A broken snapshot is skipped.
    /// Any peer-claimed drops recorded after the chosen snapshot was taken are
    /// deducted automatically so an unpaired disconnect never duplicates items.
    /// </summary>
    public Pick? PickFor(string tag, IReadOnlyList<string> branchNewestFirst)
    {
        var all = Snapshots(tag);
        int pos = 0;
        foreach (var md5 in branchNewestFirst)
        {
            foreach (var s in all.Where(x => x.Md5 == md5))
                if (Load(s) is { } p) return ApplyPendingTransfers(tag, s, p, pos == 0 ? "paired with the host's newest save" : $"paired with the host's branch save #{pos} (newest = 0)");
            pos++;
        }
        foreach (var s in all)
            if (Load(s) is { } p) return ApplyPendingTransfers(tag, s, p, branchNewestFirst.Count == 0 ? "newest (the host sent no branch)" : "newest (no snapshot pairs with any save of the host's branch)");
        return null;
    }

    private string TransfersPath(string tag) => Path.Combine(WorldDir(tag), "transfers.json");

    /// <summary>Peer-claimed outgoing drops recorded for world <paramref name="tag"/>, oldest first.</summary>
    public List<TransferOutEntry> TransfersOut(string tag)
    {
        CheckTag(tag);
        lock (_gate)
        {
            var p = TransfersPath(tag);
            try
            {
                if (!File.Exists(p)) return [];
                var raw = JsonSerializer.Deserialize<List<TransferOutEntry>>(File.ReadAllText(p));
                return raw?.Where(e => e.DropId != 0 && e.Amount > 0 && Guid.TryParse(e.Class, out _)).ToList() ?? [];
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _log($"MP-HENRY world {tag}: transfers.json unreadable ({ex.Message}) -- ignored");
                return [];
            }
        }
    }

    private DateTime NextMonotonicUtcUnlocked(string tag, DateTime candidate, List<TransferOutEntry>? knownTransfers = null)
    {
        if (ReadWorld(tag).LastEventUtc is DateTime lastEvt && candidate <= lastEvt)
            candidate = lastEvt.AddMilliseconds(1);
        if (Snapshots(tag).FirstOrDefault()?.TakenUtc is DateTime newestSnapUtc && candidate <= newestSnapUtc)
            candidate = newestSnapUtc.AddMilliseconds(1);
        foreach (var kv in _pendingOutDrops)
        {
            if (kv.Key.Tag != tag) continue;
            if (candidate <= kv.Value.RemovedUtc) candidate = kv.Value.RemovedUtc.AddMilliseconds(1);
            if (kv.Value.SelfPickupUtc is DateTime spUtc && candidate <= spUtc) candidate = spUtc.AddMilliseconds(1);
        }
        if (knownTransfers is not null)
        {
            foreach (var tr in knownTransfers)
            {
                if (candidate <= tr.EffectiveRemovedUtc) candidate = tr.EffectiveRemovedUtc.AddMilliseconds(1);
                if (tr.SelfPickupUtc is DateTime trSpUtc && candidate <= trSpUtc) candidate = trSpUtc.AddMilliseconds(1);
                if (candidate <= tr.ClaimedUtc) candidate = tr.ClaimedUtc.AddMilliseconds(1);
            }
        }
        return candidate;
    }

    private void PruneExpiredActiveCapturesUnlocked()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        _activeCaptures.RemoveAll(x => x.CreatedRealtimeUtc < cutoff);
    }

    /// <summary>
    /// Record the inventory-removal boundary of an outgoing drop when the item leaves Henry's inventory,
    /// before any peer claim arrives. Snapshots captured after this boundary already exclude <paramref name="dropId"/>.
    /// </summary>
    public long NoteOutgoingDrop(string tag, uint dropId)
    {
        CheckTag(tag);
        if (dropId == 0) return 0;
        lock (_gate)
        {
            Directory.CreateDirectory(WorldDir(tag));
            var removedUtc = NextMonotonicUtcUnlocked(tag, _now());
            long seq = 0;
            UpdateWorld(tag, w =>
            {
                seq = ++w.NextSeq;
                if (w.LastEventUtc is null || removedUtc > w.LastEventUtc.Value) w.LastEventUtc = removedUtc;
            });
            _pendingOutDrops[(tag, dropId)] = (seq, removedUtc, 0, null);
            return seq;
        }
    }

    /// <summary>
    /// Record when the player tentatively picks their own outgoing drop back up locally (before the relay's
    /// ItemClaimDown settles who won), preserving the original <c>RemovedSeq</c> while opening a tentative
    /// self-pickup window <c>[SelfPickupSeq, Seq)</c> in case a peer wins the claim race.
    /// </summary>
    public long NoteSelfPickupAttempt(string tag, uint dropId)
    {
        CheckTag(tag);
        if (dropId == 0) return 0;
        lock (_gate)
        {
            if (!_pendingOutDrops.TryGetValue((tag, dropId), out var pending)) return 0;
            if (pending.SelfPickupSeq > 0) return pending.SelfPickupSeq;
            Directory.CreateDirectory(WorldDir(tag));
            var pickupUtc = NextMonotonicUtcUnlocked(tag, _now());
            long seq = 0;
            UpdateWorld(tag, w =>
            {
                seq = ++w.NextSeq;
                if (w.LastEventUtc is null || pickupUtc > w.LastEventUtc.Value) w.LastEventUtc = pickupUtc;
            });
            _pendingOutDrops[(tag, dropId)] = (pending.RemovedSeq, pending.RemovedUtc, seq, pickupUtc);
            return seq;
        }
    }

    /// <summary>Clear an outgoing drop's early removal boundary once ItemClaimDown confirms the player won their own drop back.</summary>
    public void ClearOutgoingDrop(string tag, uint dropId)
    {
        CheckTag(tag);
        lock (_gate) _pendingOutDrops.Remove((tag, dropId));
    }

    /// <summary>Clear all session-scoped pending outgoing drop boundaries on disconnect/reconnect.</summary>
    public void ClearPendingOutgoingDrops()
    {
        lock (_gate) _pendingOutDrops.Clear();
    }

    /// <summary>
    /// Capture the game-state boundary for a snapshot at the moment Henry's state is sampled in-game,
    /// decoupled from when the .hblk file finishes writing/storing to disk.
    /// </summary>
    public SnapshotCapture CaptureBoundary(string tag)
    {
        CheckTag(tag);
        lock (_gate)
        {
            Directory.CreateDirectory(WorldDir(tag));
            PruneExpiredActiveCapturesUnlocked();
            var capturedUtc = NextMonotonicUtcUnlocked(tag, _now());
            long seq = 0;
            UpdateWorld(tag, w =>
            {
                seq = ++w.NextSeq;
                if (w.LastEventUtc is null || capturedUtc > w.LastEventUtc.Value) w.LastEventUtc = capturedUtc;
            });
            var cap = new SnapshotCapture(tag, seq, capturedUtc, ++_nextCaptureTokenId);
            _activeCaptures.Add((cap, DateTime.UtcNow));
            return cap;
        }
    }

    /// <summary>Release an unstored <see cref="SnapshotCapture"/> when a snapshot attempt aborts without storing.</summary>
    public void ReleaseCapture(SnapshotCapture? capture)
    {
        if (capture is null) return;
        lock (_gate) _activeCaptures.RemoveAll(x => x.Capture.TokenId == capture.TokenId);
    }

    private void WriteTransfersUnlocked(string tag, List<TransferOutEntry> list)
    {
        var p = TransfersPath(tag);
        if (list.Count == 0)
        {
            TryDelete(p);
            return;
        }
        File.WriteAllText(p + ".part", JsonSerializer.Serialize(list));
        File.Move(p + ".part", p, overwrite: true);
    }

    private List<TransferOutEntry> FilterRetainedTransfersUnlocked(string tag, List<TransferOutEntry> list)
    {
        if (list.Count == 0) return list;
        PruneExpiredActiveCapturesUnlocked();
        var snaps = Snapshots(tag);
        var activeForTag = _activeCaptures.Where(x => x.Capture.Tag == tag).Select(x => x.Capture).ToList();
        if (snaps.Count == 0 && activeForTag.Count == 0) return list;

        var w = ReadWorld(tag);
        bool AppliesToAnyRetainedOrActive(TransferOutEntry t)
        {
            foreach (var cap in activeForTag)
                if (t.AppliesToSnapshot(cap.CaptureSeq, cap.CapturedUtc)) return true;
            foreach (var s in snaps)
            {
                long? sq = w.SnapshotSeq.TryGetValue(s.File, out long v) && v > 0 ? v : null;
                if (t.AppliesToSnapshot(sq, s.TakenUtc)) return true;
            }
            return false;
        }

        return list
            .Where(AppliesToAnyRetainedOrActive)
            .OrderBy(t => t.EffectiveRemovedSeq)
            .ThenBy(t => t.Seq)
            .ThenBy(t => t.ClaimedUtc)
            .ToList();
    }

    private void PruneTransfersUnlocked(string tag)
    {
        var list = TransfersOut(tag);
        if (list.Count == 0) return;
        var kept = FilterRetainedTransfersUnlocked(tag, list);
        if (kept.Count != list.Count)
            WriteTransfersUnlocked(tag, kept);
    }

    /// <summary>Record an outgoing item drop claimed by a peer in world <paramref name="tag"/> (atomic .part + rename, zero game hitch).</summary>
    public TransferOutEntry? RecordTransferOut(string tag, uint dropId, string itemClass, int amount, float health)
    {
        CheckTag(tag);
        if (dropId == 0 || amount <= 0 || !Guid.TryParse(itemClass, out var clsGuid)) return null;
        lock (_gate)
        {
            Directory.CreateDirectory(WorldDir(tag));
            var list = TransfersOut(tag);
            var claimedUtc = NextMonotonicUtcUnlocked(tag, _now(), list);
            long claimSeq = 0;
            UpdateWorld(tag, w =>
            {
                claimSeq = ++w.NextSeq;
                if (w.LastEventUtc is null || claimedUtc > w.LastEventUtc.Value) w.LastEventUtc = claimedUtc;
            });
            long removedSeq = claimSeq;
            DateTime removedUtc = claimedUtc;
            long selfPickupSeq = 0;
            DateTime? selfPickupUtc = null;
            if (_pendingOutDrops.Remove((tag, dropId), out var pending))
            {
                removedSeq = pending.RemovedSeq;
                removedUtc = pending.RemovedUtc;
                selfPickupSeq = pending.SelfPickupSeq;
                selfPickupUtc = pending.SelfPickupUtc;
            }
            var entry = new TransferOutEntry(
                dropId, clsGuid.ToString("D"), amount, health,
                claimedUtc, claimSeq, removedSeq, removedUtc, selfPickupSeq, selfPickupUtc);
            list.Add(entry);
            list = FilterRetainedTransfersUnlocked(tag, list);
            WriteTransfersUnlocked(tag, list);
            _log($"MP-HENRY world {tag}: recorded peer-claimed drop {dropId} ({entry.Class[..8]} x{amount}, removed_seq={removedSeq}, self_pickup_seq={selfPickupSeq}, claim_seq={claimSeq})");
            return entry;
        }
    }

    private Pick ApplyPendingTransfers(string tag, Snapshot s, WhsSave.HenryParts p, string how)
    {
        var transfers = TransfersOut(tag);
        if (transfers.Count == 0) return new Pick(s, p, how);
        long? snapSeq = null;
        lock (_gate)
        {
            if (ReadWorld(tag).SnapshotSeq.TryGetValue(s.File, out long sq) && sq > 0) snapSeq = sq;
        }
        var applicable = transfers
            .Where(t => t.AppliesToSnapshot(snapSeq, s.TakenUtc))
            .Select(t => (t.Class, t.Amount))
            .ToList();
        if (applicable.Count == 0) return new Pick(s, p, how);
        int deducted = WhsSave.ApplyTransferredOut(p, applicable);
        if (deducted > 0)
        {
            _log($"MP-HENRY world {tag}: applied {applicable.Count} peer-claimed transfer(s) (-{deducted} item(s)) onto snapshot {s.Short}");
            how += $" (-{deducted} transferred-out item(s))";
        }
        return new Pick(s, p, how);
    }

    // ------------------------------------------------------------------ write

    /// <summary>
    /// Store one snapshot, paired with the host save <paramref name="md5"/>
    /// (32 hex). Atomic (.part + rename), read back by hash and a full parse.
    /// Then the world is pruned to the pairs of its newest <see cref="KeepPerWorld"/>
    /// host saves, at most <see cref="KeepPerHostSave"/> each, oldest first.
    /// </summary>
    public Snapshot Store(string tag, WhsSave.HenryParts parts, string md5, string source, string detail, SnapshotCapture? capture = null)
    {
        CheckTag(tag);
        if (capture is not null && capture.Tag != tag) throw new ArgumentException("capture belongs to a different world tag");
        md5 = md5.ToLowerInvariant();
        if (!Regex.IsMatch(md5, "^[0-9a-f]{32}$")) throw new ArgumentException("md5 must be 32 hex characters");
        if (!Regex.IsMatch(source, "^[a-z]+$")) throw new ArgumentException("bad source");
        lock (_gate)
        {
            var dir = WorldDir(tag);
            Directory.CreateDirectory(dir);
            var data = WhsSave.SerializeBlock(parts);
            var t = capture?.CapturedUtc ?? NextMonotonicUtcUnlocked(tag, _now());
            string name;
            int bump = 0;
            do name = $"snap-{t.AddMilliseconds(bump++):yyyyMMddHHmmssfff}-{md5}-{source}.hblk";
            while (File.Exists(Path.Combine(dir, name)));
            var takenUtc = t.AddMilliseconds(bump - 1);
            string final = Path.Combine(dir, name), part = final + ".part";
            using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(data);
                fs.Flush(true);
            }
            File.Move(part, final);
            var back = File.ReadAllBytes(final);
            if (!SHA256.HashData(back).AsSpan().SequenceEqual(SHA256.HashData(data)))
            {
                TryDelete(final);
                throw new IOException("the snapshot does not read back as written");
            }
            WhsSave.ParseBlock(back);   // throws if it would not load later
            var snap = new Snapshot(tag, name, md5, takenUtc, source, data.Length);
            UpdateWorld(tag, w =>
            {
                w.FirstSource ??= source;
                w.Pairs[name] = detail;
                long capSeq = capture?.CaptureSeq ?? ++w.NextSeq;
                if (capSeq > w.NextSeq) w.NextSeq = capSeq;
                w.SnapshotSeq[name] = capSeq;
                if (w.LastEventUtc is null || takenUtc > w.LastEventUtc.Value) w.LastEventUtc = takenUtc;
            });
            if (capture is not null) _activeCaptures.RemoveAll(x => x.Capture.TokenId == capture.TokenId);
            _log($"MP-HENRY world {tag}: stored {source} snapshot {snap.Short} ({data.Length} B, read back: hash + parse ok){(detail != "" ? " -- " + detail : "")}");
            Prune(tag);
            return snap;
        }
    }

    // ------------------------------------------------------------------ WO-134: chest ledgers

    /// <summary>
    /// WO-134: the joiner's chest ledger paired with one snapshot (what this Henry
    /// took out of which container in that world, as of that snapshot). Stored and
    /// pruned with its snapshot, so a restore brings back the ledger of the same
    /// moment: loss, never duplication.
    /// </summary>
    public string ChestLedgerPath(Snapshot s) => Path.Combine(WorldDir(s.Tag), "chests-" + Path.GetFileNameWithoutExtension(s.File) + ".json");

    public void StoreChestLedger(Snapshot s, string json)
    {
        lock (_gate)
        {
            var p = ChestLedgerPath(s);
            File.WriteAllText(p + ".part", json);
            File.Move(p + ".part", p, overwrite: true);
        }
    }

    /// <summary>The ledger stored with this snapshot, or null (a snapshot from before WO-134: none).</summary>
    public string? LoadChestLedger(Snapshot s)
    {
        try { var p = ChestLedgerPath(s); return File.Exists(p) ? File.ReadAllText(p) : null; }
        catch (IOException) { return null; }
    }

    private void Prune(string tag)
    {
        // newest first: the pairs of the newest KeepPerWorld host saves, at most KeepPerHostSave of each (WO-184)
        var kept = new Dictionary<string, int>(StringComparer.Ordinal);   // md5 -> kept so far; -1 = past the newest KeepPerWorld host saves
        int hostSaves = 0;
        foreach (var s in Snapshots(tag))
        {
            string why;
            if (!kept.TryGetValue(s.Md5, out int n))
            {
                if (hostSaves < KeepPerWorld) { kept[s.Md5] = 1; hostSaves++; continue; }
                kept[s.Md5] = -1;
                why = $"keeping the pairs of the newest {KeepPerWorld} host saves";
            }
            else if (n < 0) why = $"keeping the pairs of the newest {KeepPerWorld} host saves";
            else if (n >= KeepPerHostSave) why = $"keeping the newest {KeepPerHostSave} of host save {s.Md5[..8]}";
            else { kept[s.Md5] = n + 1; continue; }
            TryDelete(Path.Combine(WorldDir(tag), s.File));
            TryDelete(ChestLedgerPath(s));
            UpdateWorld(tag, w =>
            {
                w.Pairs.Remove(s.File);
                w.SnapshotSeq.Remove(s.File);
            });
            _log($"MP-HENRY world {tag}: pruned snapshot {s.Short} ({why})");
        }
        PruneTransfersUnlocked(tag);
    }

    public void MarkJoined(string tag) => UpdateWorld(tag, w => w.LastJoinedUtc = _now());

    // ------------------------------------------------------------------ WO-157: which host's world

    /// <summary>The host whose world this folder holds (null: not known -- stored before WO-157, or no host key came).</summary>
    public uint? WorldHostKey(string tag)
    {
        CheckTag(tag);
        return File.Exists(Path.Combine(WorldDir(tag), "world.json")) ? ReadWorld(tag).HostKey : null;
    }

    /// <summary>The plain seed tag a world folder belongs to (its own name, or the seed tag a host-qualified folder records).</summary>
    public string BaseTagOf(string tag) =>
        TagRx.IsMatch(tag) && File.Exists(Path.Combine(WorldDir(tag), "world.json")) ? ReadWorld(tag).BaseTag ?? tag : tag;

    /// <summary>A join landed: the folder belongs to this host from now on (the first host of a pre-WO-157 folder claims it).</summary>
    public void ClaimHost(string tag, string baseTag, uint hostKey) => UpdateWorld(tag, w =>
    {
        w.HostKey ??= hostKey;
        if (tag != baseTag) w.BaseTag ??= baseTag;
    });

    /// <summary>This install's key (random, never 0), made once; sent by a host so joiners can keep two hosts' worlds apart.</summary>
    public uint InstallKey()
    {
        lock (_gate)
        {
            var p = Path.Combine(Root, "host.key");
            try
            {
                if (File.Exists(p) && uint.TryParse(File.ReadAllText(p).Trim(), System.Globalization.NumberStyles.HexNumber, null, out uint k) && k != 0) return k;
            }
            catch (IOException) { }
            uint key;
            do key = BitConverter.ToUInt32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
            while (key == 0);
            Directory.CreateDirectory(Root);
            File.WriteAllText(p, key.ToString("x8"));
            return key;
        }
    }

    private string HostedPath => Path.Combine(Root, "hosted.json");

    /// <summary>The worlds (plain seed tags) this install hosted: its own saves of such a world are its own playthrough.</summary>
    public HashSet<string> Hosted()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(HostedPath) && JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(HostedPath)) is { } d)
                    return d.Keys.ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { _log($"MP-HENRY hosted.json unreadable ({ex.Message})"); }
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public bool HostedHere(string baseTag) => Hosted().Contains(baseTag);

    /// <summary>This install hosts world <paramref name="baseTag"/> (once per world; the first time is kept).</summary>
    public void MarkHosted(string baseTag)
    {
        if (!TagRx.IsMatch(baseTag)) return;
        lock (_gate)
        {
            Dictionary<string, DateTime> d = new(StringComparer.Ordinal);
            try
            {
                if (File.Exists(HostedPath) && JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(HostedPath)) is { } r) d = r;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            if (d.ContainsKey(baseTag)) return;
            d[baseTag] = _now();
            Directory.CreateDirectory(Root);
            File.WriteAllText(HostedPath + ".part", JsonSerializer.Serialize(d));
            File.Move(HostedPath + ".part", HostedPath, overwrite: true);
            _log($"MP-HENRY world {baseTag}: hosted by this install (its saves of it are this player's own playthrough)");
        }
    }

    // ------------------------------------------------------------------ world.json

    private sealed class WorldFile
    {
        public DateTime LastJoinedUtc { get; set; }
        public DateTime? LastEventUtc { get; set; }
        public string? FirstSource { get; set; }
        public Dictionary<string, string> Pairs { get; set; } = new(StringComparer.Ordinal);
        public long NextSeq { get; set; }
        public Dictionary<string, long> SnapshotSeq { get; set; } = new(StringComparer.Ordinal);
        /// <summary>WO-157: the host whose world this is (null before WO-157).</summary>
        public uint? HostKey { get; set; }
        /// <summary>WO-157: the plain seed tag of a host-qualified folder.</summary>
        public string? BaseTag { get; set; }
    }

    private WorldFile ReadWorld(string tag)
    {
        var p = Path.Combine(WorldDir(tag), "world.json");
        try
        {
            if (File.Exists(p) && JsonSerializer.Deserialize<WorldFile>(File.ReadAllText(p)) is { } w) return w;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { _log($"MP-HENRY world {tag}: world.json unreadable ({ex.Message}) -- rebuilt from the snapshot names"); }
        var snaps = Snapshots(tag);
        return new WorldFile { LastJoinedUtc = snaps.Count > 0 ? snaps[0].TakenUtc : _now(), FirstSource = snaps.LastOrDefault()?.Source };
    }

    private void UpdateWorld(string tag, Action<WorldFile> f)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(WorldDir(tag));
            var w = ReadWorld(tag);
            f(w);
            var p = Path.Combine(WorldDir(tag), "world.json");
            File.WriteAllText(p + ".part", JsonSerializer.Serialize(w));
            File.Move(p + ".part", p, overwrite: true);
        }
    }

    public List<WorldInfo> Worlds()
    {
        var o = new List<WorldInfo>();
        if (!Directory.Exists(Root)) return o;
        foreach (var d in Directory.EnumerateDirectories(Root))
        {
            string tag = Path.GetFileName(d);
            if (!TagRx.IsMatch(tag)) continue;
            var snaps = Snapshots(tag);
            var w = ReadWorld(tag);
            o.Add(new WorldInfo(tag, w.LastJoinedUtc, snaps.Count, snaps.Sum(s => s.Bytes), w.FirstSource ?? "-"));
        }
        return o.OrderByDescending(x => x.LastJoinedUtc).ToList();
    }

    /// <summary>Delete one world's Henry files (mp_henry_files delete, mp_henry_reset, the 90-day rule). Logged by the caller's reason.</summary>
    public bool DeleteWorld(string tag, string why)
    {
        CheckTag(tag);
        var dir = WorldDir(tag);
        if (!Directory.Exists(dir)) return false;
        int n = Snapshots(tag).Count;
        Directory.Delete(dir, recursive: true);
        _log($"MP-HENRY world {tag}: DELETED ({n} snapshot(s)) -- {why}");
        return true;
    }

    /// <summary>The 90-day rule: every world not joined for <see cref="StaleDays"/> days goes. Returns the tags deleted.</summary>
    public List<string> CleanupStale()
    {
        var gone = new List<string>();
        foreach (var w in Worlds())
        {
            double days = (_now() - w.LastJoinedUtc).TotalDays;
            if (days > StaleDays && DeleteWorld(w.Tag, $"not joined for {days:F0} days (the {StaleDays}-day rule)")) gone.Add(w.Tag);
        }
        return gone;
    }

    // ------------------------------------------------------------------ the sweep ledger

    /// <summary>
    /// A save the mod made (a snapshot's QuickSave, before the engine named it)
    /// or saw land in a playline while this game was in a host's world.
    /// </summary>
    public sealed class LedgerEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public int Playline { get; set; }
        public DateTime SinceUtc { get; set; }
        public string? File { get; set; }
        public string? Md5 { get; set; }
        public string WorldTag { get; set; } = "";
        public string Kind { get; set; } = "snapshot";     // snapshot | leak
        public string State { get; set; } = "pending";     // pending | written | done
    }

    private string LedgerPath => Path.Combine(Root, "ledger.json");

    public List<LedgerEntry> Ledger()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(LedgerPath) && JsonSerializer.Deserialize<List<LedgerEntry>>(File.ReadAllText(LedgerPath)) is { } l) return l;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { _log($"MP-HENRY ledger unreadable ({ex.Message}) -- starting a new one"); }
            return [];
        }
    }

    private void WriteLedger(List<LedgerEntry> l)
    {
        Directory.CreateDirectory(Root);
        // done entries a week old are forgotten
        l = l.Where(e => e.State != "done" || (_now() - e.SinceUtc).TotalDays < 7).ToList();
        File.WriteAllText(LedgerPath + ".part", JsonSerializer.Serialize(l));
        File.Move(LedgerPath + ".part", LedgerPath, overwrite: true);
    }

    public LedgerEntry LedgerAdd(LedgerEntry e)
    {
        lock (_gate) { var l = Ledger(); l.Add(e); WriteLedger(l); }
        return e;
    }

    public void LedgerSet(string id, Action<LedgerEntry> f)
    {
        lock (_gate)
        {
            var l = Ledger();
            foreach (var e in l.Where(x => x.Id == id)) f(e);
            WriteLedger(l);
        }
    }

    /// <summary>
    /// Move a file out of a playline into henry/swept (never deleted outright:
    /// the newest 8 swept files are kept). Returns true when it is gone from
    /// the playline.
    /// </summary>
    public bool MoveOut(string path, string why)
    {
        string swept = Path.Combine(Root, "swept");
        try
        {
            Directory.CreateDirectory(swept);
            string pl = Path.GetFileName(Path.GetDirectoryName(path)) ?? "playline?";
            string dest = Path.Combine(swept, $"{_now():yyyyMMddHHmmssfff}-{pl}-{Path.GetFileName(path)}");
            File.Move(path, dest);
            _log($"MP-HENRY moved {GameBridge.SaveDisplay(path)} out of the playline -> <data>/henry/swept ({why})");
            foreach (var old in new DirectoryInfo(swept).GetFiles().OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(8))
                TryDelete(old.FullName);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"MP-HENRY could not move {GameBridge.SaveDisplay(path)} out ({why}): {ex.Message}");
            return false;
        }
    }

    private static void TryDelete(string p) { try { File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
