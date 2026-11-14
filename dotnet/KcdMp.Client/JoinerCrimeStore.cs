// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Text.Json;
using System.Text.RegularExpressions;
using KcdMp.Wire;

namespace KcdMp.Client;

/// <summary>
/// M4: per-world persistence store for open joiner crimes on the host, keyed by normalized player name
/// under <c>%LocalAppData%\KCDMP\crimes\&lt;worldKey&gt;\</c> (<c>latest.json</c> + <c>&lt;hostSaveMd5&gt;.json</c>).
/// Monotonic <see cref="Environment.TickCount64"/> timers are stored as remaining durations relative to
/// snapshot time so restored records across process restarts preserve known, calmed, fresh, and resisted
/// windows accurately alongside <c>AtWorldS</c> multi-day expiration.
/// </summary>
public sealed class JoinerCrimeStore
{
    public const int KeepPairs = 100;
    public const int MaxPlayers = 64;
    public const int MaxCrimesPerPlayer = 200;

    private readonly string _root;
    private readonly object _gate = new();
    private static readonly Regex TagRx = new("^[0-9a-f]{6,16}$", RegexOptions.Compiled);
    private static readonly Regex Md5Rx = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex Md5FileRx = new("^[0-9a-f]{32}\\.json$", RegexOptions.Compiled);

    public sealed class SavedCrime
    {
        public uint Id { get; set; }
        public string Kind { get; set; } = "";
        public string Settlement { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public string Victim { get; set; } = "";
        public int Witnesses { get; set; }
        public int Guards { get; set; }
        public bool Known { get; set; }
        public bool Calmed { get; set; }
        public bool Resisted { get; set; }
        public double AtWorldS { get; set; }
        public long RemainingReportDelayMs { get; set; }
        public long RemainingFreshMs { get; set; }
        public long RemainingEscalationMs { get; set; }

        public static bool IsValid(SavedCrime? s) =>
            s is not null
            && Wo139Text.IsCrime(s.Kind)
            && s.Witnesses > 0
            && s.Guards >= 0
            && (s.Settlement.Length == 0 || Wo139Text.IsSettlement(s.Settlement))
            && (s.Victim.Length == 0 || Wo139Text.IsName(s.Victim))
            && float.IsFinite(s.X) && float.IsFinite(s.Y) && float.IsFinite(s.Z)
            && Math.Abs(s.X) < 100_000f && Math.Abs(s.Y) < 100_000f && Math.Abs(s.Z) < 100_000f
            && double.IsFinite(s.AtWorldS) && s.AtWorldS >= 0;

        public static SavedCrime FromRuntime(JoinerCrimeRecord.Crime c, long nowMs)
        {
            long elapsedSinceAt = Math.Max(0L, nowMs - c.AtMs);
            bool effectiveKnown = c.Known || c.Guards > 0 || elapsedSinceAt >= JoinerCrimeRecord.ReportDelayMs;
            long effectiveKnownAtMs = c.Known
                ? c.KnownAtMs
                : c.Guards > 0 ? c.AtMs : c.AtMs + JoinerCrimeRecord.ReportDelayMs;

            long remReport = effectiveKnown
                ? 0L
                : Math.Clamp(JoinerCrimeRecord.ReportDelayMs - elapsedSinceAt, 1L, JoinerCrimeRecord.ReportDelayMs);
            long remFresh = effectiveKnown && !c.Calmed && Wo139Rules.IsViolent(c.Kind)
                ? Math.Clamp(JoinerCrimeRecord.FreshMs - Math.Max(0L, nowMs - effectiveKnownAtMs), 0L, JoinerCrimeRecord.FreshMs)
                : 0L;
            long remEsc = c.Resisted && !c.Calmed
                ? Math.Clamp(JoinerCrimeRecord.EscalationMs - Math.Max(0L, nowMs - c.ResistedAtMs), 0L, JoinerCrimeRecord.EscalationMs)
                : 0L;

            return new SavedCrime
            {
                Id = c.Id,
                Kind = c.Kind,
                Settlement = c.Settlement ?? "",
                X = c.X,
                Y = c.Y,
                Z = c.Z,
                Victim = c.Victim ?? "",
                Witnesses = c.Witnesses,
                Guards = c.Guards,
                Known = effectiveKnown,
                Calmed = c.Calmed,
                Resisted = c.Resisted && !c.Calmed,
                AtWorldS = c.AtWorldS,
                RemainingReportDelayMs = remReport,
                RemainingFreshMs = remFresh,
                RemainingEscalationMs = remEsc,
            };
        }

        public JoinerCrimeRecord.Crime? ToRuntime(long nowMs, double currentWorldS = 0)
        {
            if (!IsValid(this)) return null;
            if (currentWorldS > 0 && AtWorldS > 0 && currentWorldS - AtWorldS > Wo139Rules.RowOf(Kind).ExpirationDays * 86400.0)
                return null;

            bool known = Known || Guards > 0 || RemainingReportDelayMs <= 0;
            long atMs = known
                ? nowMs - JoinerCrimeRecord.ReportDelayMs
                : nowMs - (JoinerCrimeRecord.ReportDelayMs - Math.Clamp(RemainingReportDelayMs, 1L, JoinerCrimeRecord.ReportDelayMs));

            long knownAtMs;
            if (!known)
                knownAtMs = 0L;
            else if (!Calmed && Wo139Rules.IsViolent(Kind) && RemainingFreshMs > 0)
                knownAtMs = nowMs - (JoinerCrimeRecord.FreshMs - Math.Clamp(RemainingFreshMs, 1L, JoinerCrimeRecord.FreshMs));
            else
                knownAtMs = nowMs - JoinerCrimeRecord.FreshMs - 1L;

            bool resisted = Resisted && !Calmed;
            long resistedAtMs;
            if (!resisted)
                resistedAtMs = 0L;
            else if (RemainingEscalationMs > 0)
                resistedAtMs = nowMs - (JoinerCrimeRecord.EscalationMs - Math.Clamp(RemainingEscalationMs, 1L, JoinerCrimeRecord.EscalationMs));
            else
                resistedAtMs = nowMs - JoinerCrimeRecord.EscalationMs - 1L;

            return new JoinerCrimeRecord.Crime
            {
                Id = Id,
                Kind = Kind,
                Settlement = Settlement,
                X = X,
                Y = Y,
                Z = Z,
                Victim = Victim,
                Witnesses = Witnesses,
                Guards = Guards,
                Known = known,
                Calmed = Calmed,
                Resisted = resisted,
                AtWorldS = AtWorldS,
                AtMs = atMs,
                KnownAtMs = knownAtMs,
                ResistedAtMs = resistedAtMs,
            };
        }
    }

    public sealed class WorldCrimeLedger
    {
        public Dictionary<string, List<SavedCrime>> Players { get; set; } = new(StringComparer.Ordinal);

        public void SetPlayerRecord(string? playerName, JoinerCrimeRecord? rec, long nowMs)
        {
            if (NormalizePlayerKey(playerName) is not string key) return;
            var snap = rec?.Open;
            if (snap is null || snap.Count == 0)
            {
                Players.Remove(key);
                return;
            }
            var list = new List<SavedCrime>(Math.Min(snap.Count, MaxCrimesPerPlayer));
            foreach (var c in snap.TakeLast(MaxCrimesPerPlayer))
            {
                var sc = SavedCrime.FromRuntime(c, nowMs);
                if (SavedCrime.IsValid(sc)) list.Add(sc);
            }
            if (list.Count == 0)
            {
                Players.Remove(key);
                return;
            }
            Players[key] = list;
            if (Players.Count > MaxPlayers)
            {
                foreach (var excess in Players.Keys.OrderBy(x => x, StringComparer.Ordinal).Take(Players.Count - MaxPlayers).ToList())
                    Players.Remove(excess);
            }
        }

        public bool TryBuildRecord(string? playerName, long nowMs, double currentWorldS, out JoinerCrimeRecord rec)
        {
            rec = new JoinerCrimeRecord();
            if (NormalizePlayerKey(playerName) is not string key || !Players.TryGetValue(key, out var list) || list.Count == 0)
                return false;

            var open = new List<JoinerCrimeRecord.Crime>(list.Count);
            foreach (var s in list)
                if (s.ToRuntime(nowMs, currentWorldS) is { } c)
                    open.Add(c);
            rec.Restore(open);
            return rec.Count > 0;
        }

        public string ToJson() => JsonSerializer.Serialize(this);

        public static WorldCrimeLedger FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new WorldCrimeLedger();
            try
            {
                var raw = JsonSerializer.Deserialize<WorldCrimeLedger>(json);
                var clean = new WorldCrimeLedger();
                if (raw?.Players is null) return clean;
                foreach (var (rawKey, rawList) in raw.Players)
                {
                    if (NormalizePlayerKey(rawKey) is not string key || rawList is null) continue;
                    var valid = rawList.Where(SavedCrime.IsValid).TakeLast(MaxCrimesPerPlayer).ToList();
                    if (valid.Count > 0 && clean.Players.Count < MaxPlayers)
                        clean.Players[key] = valid;
                }
                return clean;
            }
            catch (JsonException) { return new WorldCrimeLedger(); }
        }

        public WorldCrimeLedger Clone() => FromJson(ToJson());
    }

    public JoinerCrimeStore(string root) { _root = root; }

    public static string DefaultRoot() => Path.Combine(Path.GetDirectoryName(HenryStore.DefaultRoot())!, "crimes");

    public static string? NormalizePlayerKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string trimmed = name.Trim().ToLowerInvariant();
        return trimmed.Length is > 0 and <= 64 ? trimmed : null;
    }

    private string Dir(string worldKey)
    {
        if (!TagRx.IsMatch(worldKey)) throw new ArgumentException("not a world key: " + worldKey, nameof(worldKey));
        return Path.Combine(_root, worldKey);
    }

    public WorldCrimeLedger Latest(string worldKey)
    {
        lock (_gate)
        {
            var p = Path.Combine(Dir(worldKey), "latest.json");
            return WorldCrimeLedger.FromJson(File.Exists(p) ? File.ReadAllText(p) : null);
        }
    }

    public DateTime? LatestLastWriteTimeUtc(string worldKey)
    {
        lock (_gate)
        {
            var p = Path.Combine(Dir(worldKey), "latest.json");
            return File.Exists(p) ? File.GetLastWriteTimeUtc(p) : null;
        }
    }

    public void SaveLatest(string worldKey, WorldCrimeLedger ledger) =>
        Write(Path.Combine(Dir(worldKey), "latest.json"), ledger.ToJson());

    public void Pair(string worldKey, string hostSaveMd5, WorldCrimeLedger ledger)
    {
        string md5 = (hostSaveMd5 ?? "").ToLowerInvariant();
        if (!Md5Rx.IsMatch(md5)) throw new ArgumentException("invalid host save md5", nameof(hostSaveMd5));
        string dir = Dir(worldKey);
        Write(Path.Combine(dir, $"{md5}.json"), ledger.ToJson());
        lock (_gate)
        {
            var old = Directory.EnumerateFiles(dir, "*.json")
                               .Where(f => Md5FileRx.IsMatch(Path.GetFileName(f)))
                               .Select(f => new FileInfo(f))
                               .OrderByDescending(f => f.LastWriteTimeUtc)
                               .Skip(KeepPairs)
                               .ToList();
            foreach (var f in old)
                try { f.Delete(); } catch (IOException) { }
        }
    }

    public WorldCrimeLedger? Paired(string worldKey, string hostSaveMd5)
    {
        string md5 = (hostSaveMd5 ?? "").ToLowerInvariant();
        if (!Md5Rx.IsMatch(md5)) return null;
        lock (_gate)
        {
            var p = Path.Combine(Dir(worldKey), $"{md5}.json");
            return File.Exists(p) ? WorldCrimeLedger.FromJson(File.ReadAllText(p)) : null;
        }
    }

    private void Write(string path, string json)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".part", json);
            File.Move(path + ".part", path, overwrite: true);
        }
    }
}
