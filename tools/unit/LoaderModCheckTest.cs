using System; using System.Collections.Generic; using TLDRevamp.Net;
static class P {
  static int fails = 0;
  static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }
  static LoaderModCheck.Entry E(string id, string ver, string hash, string name = null) => new LoaderModCheck.Entry(id, name ?? id + " mod", ver, hash);
  static NetReader R(NetWriter w) { var r = new NetReader(); r.Load(w.Len); Buffer.BlockCopy(w.Buf, 0, r.Buf, 0, w.Len); return r; }
  static string Refuse(List<LoaderModCheck.Entry> host, List<LoaderModCheck.Entry> joiner) => LoaderModCheck.Refusal(LoaderModCheck.Differences(host, joiner));
  static int Main() {
    var host = new List<LoaderModCheck.Entry> { E("Nitrous", "1.2", "aa"), E("Wristwatch", "1.0", "bb") };
    // the wire: what the joiner writes is what the host reads, after other Hello fields
    var w = new NetWriter(); w.Str("before"); LoaderModCheck.Write(w, host);
    var r = R(w); r.Str(); var back = LoaderModCheck.Read(r);
    Check("round trip", back != null && back.Count == 2 && back[0].Id == "Nitrous" && back[0].Name == "Nitrous mod" && back[0].Version == "1.2" && back[0].Hash == "aa" && back[1].Id == "Wristwatch" && r.Remaining == 0);
    var none = LoaderModCheck.Read(R(new NetWriter()));
    Check("joiner from before the check: an empty list", none != null && none.Count == 0);
    w = new NetWriter(); w.VarU32(3); w.Str("x");
    Check("cut short: unreadable", LoaderModCheck.Read(R(w)) == null);
    w = new NetWriter(); w.VarU32(5000);
    Check("too many: unreadable", LoaderModCheck.Read(R(w)) == null);
    Check("unreadable: refused, says so", LoaderModCheck.Refusal(null).Contains("couldn't read"));
    w = new NetWriter(); LoaderModCheck.Write(w, new List<LoaderModCheck.Entry> { new LoaderModCheck.Entry(null, null, null, null) });
    var nulls = LoaderModCheck.Read(R(w));
    Check("a mod with no ID/name/version still travels", nulls != null && nulls.Count == 1 && nulls[0].Id == "");
    // the comparison
    Check("same mods: admitted", Refuse(host, new List<LoaderModCheck.Entry> { E("Wristwatch", "1.0", "bb"), E("Nitrous", "1.2", "aa") }) == null);
    Check("neither has any: admitted", Refuse(new List<LoaderModCheck.Entry>(), new List<LoaderModCheck.Entry>()) == null);
    string missing = Refuse(host, new List<LoaderModCheck.Entry> { E("Nitrous", "1.2", "aa") });
    Check("missing: install, with name and version", missing != null && missing.Contains("install Wristwatch mod 1.0") && missing.Contains("TLDLoaderMods"));
    string extra = Refuse(host, new List<LoaderModCheck.Entry> { E("Nitrous", "1.2", "aa"), E("Wristwatch", "1.0", "bb"), E("Hardcore", "2.0", "cc") });
    Check("extra: remove", extra != null && extra.Contains("remove Hardcore mod 2.0 (the host doesn't have it)"));
    string ver = Refuse(host, new List<LoaderModCheck.Entry> { E("Nitrous", "1.1", "zz"), E("Wristwatch", "1.0", "bb") });
    Check("other version: both named", ver != null && ver.Contains("Nitrous mod: the host has 1.2, you 1.1") && !ver.Contains("DLL differs"));
    string dll = Refuse(host, new List<LoaderModCheck.Entry> { E("Nitrous", "1.2", "zz"), E("Wristwatch", "1.0", "bb") });
    Check("same version, other DLL", dll != null && dll.Contains("Nitrous mod 1.2: your DLL differs"));
    Check("old joiner, host with mods: told to install them", Refuse(host, new List<LoaderModCheck.Entry>()).Contains("install Nitrous mod 1.2"));
    Check("old host data, joiner with mods: told to remove them", Refuse(new List<LoaderModCheck.Entry>(), host).Contains("remove Nitrous mod 1.2"));
    var many = new List<LoaderModCheck.Entry>(); for (int i = 0; i < 9; i++) many.Add(E("M" + i, "1", "h" + i));
    string cut = Refuse(many, new List<LoaderModCheck.Entry>());
    Check("long list: six shown, the rest counted", cut.Contains("install M5 mod 1") && !cut.Contains("M6 mod") && cut.Contains("and 3 more"));
    var dup = new List<LoaderModCheck.Entry> { E("Twin", "1", "h"), E("Twin", "1", "h") };
    Check("one DLL listed twice: no false difference", Refuse(dup, dup) == null);
    Check("no version shown as such", Refuse(new List<LoaderModCheck.Entry> { E("V", "", "h") }, new List<LoaderModCheck.Entry> { E("V", "2", "h") }).Contains("the host has no version, you 2"));
    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }
}
