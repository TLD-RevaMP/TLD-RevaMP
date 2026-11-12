using System.Collections.Generic;
using System.Text;

namespace TLDRevamp.Net
{
    /// TLD Loader mods (BepInEx/TLDLoaderMods, run by ModHost) must match at join. Their items reach other players as
    /// ordinary game items, which works only when every player has the same mods: a joiner without one, or with another
    /// version, gets other items (or none) for what the host spawns, and mods that change the game's behaviour act on one
    /// side only. Compared by mod ID, version and the SHA-256 of the mod's DLL. Nothing is downloaded: a TLD Loader mod is
    /// not an approved file, so the joiner is told what to install or remove. No game or Unity types, so
    /// tools/unit/LoaderModCheckTest.cs runs it offline.
    public static class LoaderModCheck
    {
        public struct Entry
        {
            public string Id, Name, Version, Hash;
            public Entry(string id, string name, string version, string hash) { Id = id ?? ""; Name = name ?? ""; Version = version ?? ""; Hash = hash ?? ""; }
            internal string Shown => (Name.Length > 0 ? Name : Id) + (Version.Length > 0 ? " " + Version : "");
        }

        public const int MaxEntries = 1024;

        /// The joiner's list: the last field of Hello (after the world check). A joiner built before this check sends none.
        public static void Write(NetWriter w, IList<Entry> mods)
        {
            int n = mods == null ? 0 : System.Math.Min(mods.Count, MaxEntries);
            w.VarU32((uint)n);
            for (int i = 0; i < n; i++) { w.Str(mods[i].Id); w.Str(mods[i].Name); w.Str(mods[i].Version); w.Str(mods[i].Hash); }
        }

        /// Empty when the joiner sent no list (built before this check: it can only join a host without TLD Loader mods);
        /// null when the list can't be read.
        public static List<Entry> Read(NetReader r)
        {
            var list = new List<Entry>();
            if (r.Bad || r.Remaining <= 0) return r.Bad ? null : list;
            uint n = r.VarU32();
            if (r.Bad || n > MaxEntries) return null;
            for (uint i = 0; i < n; i++)
            {
                string id = r.Str(), name = r.Str(), version = r.Str(), hash = r.Str();
                if (r.Bad || id == null) return null;
                list.Add(new Entry(id, name, version, hash));
            }
            return list;
        }

        /// What the joiner has to change, in the host's order, then what it has extra. Empty: the same mods.
        public static List<string> Differences(IList<Entry> host, IList<Entry> joiner)
        {
            var diff = new List<string>();
            var theirs = new Dictionary<string, Entry>();
            foreach (var m in joiner) theirs[m.Id] = m;
            var seen = new HashSet<string>();
            foreach (var m in host)
            {
                if (!seen.Add(m.Id)) continue;
                if (!theirs.TryGetValue(m.Id, out var t)) { diff.Add("install " + m.Shown); continue; }
                theirs.Remove(m.Id);
                if (t.Version != m.Version) diff.Add((m.Name.Length > 0 ? m.Name : m.Id) + ": the host has " + Ver(m.Version) + ", you " + Ver(t.Version));
                else if (t.Hash != m.Hash) diff.Add(m.Shown + ": your DLL differs from the host's");
            }
            foreach (var m in joiner)
                if (theirs.Remove(m.Id)) diff.Add("remove " + m.Shown + " (the host doesn't have it)");
            return diff;
        }

        /// The refusal the joiner reads, or null when the mods match.
        public static string Refusal(List<string> diff)
        {
            if (diff == null) return "The host couldn't read your list of TLD Loader mods. Both need the same build of TLD Revamp.";
            if (diff.Count == 0) return null;
            var sb = new StringBuilder("Your TLD Loader mods (BepInEx/TLDLoaderMods) differ from the host's: ");
            sb.Append(string.Join("; ", diff.Count > 6 ? diff.GetRange(0, 6) : diff));
            if (diff.Count > 6) sb.Append("; and ").Append(diff.Count - 6).Append(" more");
            sb.Append(". Both need the same ones.");
            return sb.ToString();
        }

        private static string Ver(string v) => v.Length > 0 ? v : "no version";
    }
}
