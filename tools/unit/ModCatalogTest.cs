using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using TLDRevamp.Net;

static class ModCatalogTest
{
    static int fails;
    static readonly byte[] Dll = Encoding.UTF8.GetBytes("Synthetic mod bytes, never executable");
    static readonly Func<string, bool> ValidId = id => id == "test.mod";
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }
    static void Refused(string name, Action action)
    {
        try { action(); Check(name, false); }
        catch (InvalidDataException) { Check(name, true); }
    }
    static ModCatalog.Mod Mod() => new ModCatalog.Mod { Id = "test.mod", Name = "Test", Versions = new List<ModCatalog.Release>() };
    static ModCatalog.Release Release(byte[] data, string file = null) => new ModCatalog.Release {
        Version = "1.0.0", Url = "https://github.com/owner/repo/releases/download/v1/mod.dll", File = file,
        Size = data.Length, Hash = ModCatalog.Sha256(Dll)
    };
    static byte[] Zip(params (string name, byte[] bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var e in entries) { using var s = zip.CreateEntry(e.name).Open(); s.Write(e.bytes); }
        return stream.ToArray();
    }
    static byte[] Verify(byte[] data, string file = null, int limit = 1024 * 1024) =>
        ModCatalog.Verify(Mod(), Release(data, file), data, limit, ValidId);

    static int Main()
    {
        var catalog = ModCatalog.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "mods.json")));
        Check("real catalog includes World Map, Trailer Hitches, TV Browser, Road End and RayTrace (Rossa88 withdrawn)", catalog.Mods.Count == 10 &&
            catalog.Mods[0].Versions[0].Version == "0.4.0" && catalog.Mods[1].Versions[0].Version == "0.1.2" &&
            catalog.Mods[2].Id == "anonymoustld.busgut" && catalog.Mods[3].Id == "anonymoustld.mountanything" && catalog.Mods[4].Id == "anonymoustld.terrainchoice" && catalog.Mods[5].Id == "tldrevamp.worldmap" && catalog.Mods[6].Id == "tldrevamp.trailerhitches" && catalog.Mods[7].Id == "tldrevamp.tvbrowser" && catalog.Mods[8].Id == "tldrevamp.roadend" && catalog.Mods[9].Id == "tldrevamp.raytrace" && !catalog.Mods.Exists(m => m.Id == "anonymoustld.rossa88"));
        Check("HeadUnit valid in catalog", ModCatalog.ApprovalReason(catalog.Mods[0], catalog.Mods[0].Versions[0], 16 << 20, _ => true) == null);
        Check("CarTrailer released DLL valid in catalog", ModCatalog.ApprovalReason(catalog.Mods[1], catalog.Mods[1].Versions[0], 16 << 20, _ => true) == null);
        for (int i = 2; i < catalog.Mods.Count; i++)
            Check(catalog.Mods[i].Id + " released DLL valid in catalog", ModCatalog.ApprovalReason(catalog.Mods[i], catalog.Mods[i].Versions[0], 16 << 20, _ => true) == null);
        Check("real catalog offers Head Unit 0.2.0 an update to 0.4.0", ModCatalog.UpdateTo(catalog.Mods[0], "0.2.0", 16 << 20, _ => true)?.Version == "0.4.0");
        // Older versions remain installable for hosts still using them.
        foreach (var m in catalog.Mods)
            foreach (var v in m.Versions)
                Check(m.Id + " " + v.Version + " valid in catalog", ModCatalog.ApprovalReason(m, v, 16 << 20, _ => true) == null);
        // a release whose "revamp" is newer than this RevaMP needs that RevaMP's API: held back, and never hides an older one
        var needsNewer = new ModCatalog.Mod { Id = "test.api", Versions = new List<ModCatalog.Release> {
            new ModCatalog.Release { Version = "2.0.0", Url = "https://github.com/o/r/releases/download/v2/m.zip", Size = 10, Hash = new string('a', 64), Revamp = "0.70.0" },
            new ModCatalog.Release { Version = "1.0.0", Url = "https://github.com/o/r/releases/download/v1/m.zip", Size = 10, Hash = new string('b', 64), Revamp = "0.69.1" } } };
        Check("a release needing a newer RevaMP is held back", ModCatalog.ApprovalReason(needsNewer, needsNewer.Versions[0], 16 << 20, _ => true, "0.69.1") == "Needs RevaMP 0.70.0.");
        Check("the same release is fine on that RevaMP", ModCatalog.ApprovalReason(needsNewer, needsNewer.Versions[0], 16 << 20, _ => true, "0.70.0") == null);
        Check("the older release that works is offered instead", ModCatalog.Offered(needsNewer, 16 << 20, _ => true, "0.69.1").Version == "1.0.0");
        Check("no update to a release this RevaMP can't use", ModCatalog.UpdateTo(needsNewer, "1.0.0", 16 << 20, _ => true, "0.69.1") == null);
        string json = "{\"format\":1,\"future\":{\"a\":[1,true]},\"mods\":[{\"id\":\"test.mod\",\"future\":42,\"versions\":[{\"version\":\"1\",\"url\":\"https://github.com/o/r/releases/download/v/m.dll\",\"future\":\"ok\"},{\"url\":\"https://example.com/evil.dll\"}]}]}";
        var parsed = ModCatalog.Parse(Encoding.UTF8.GetBytes(json));
        Check("unknown fields ignored, bad URL removed", parsed.Mods.Count == 1 && parsed.Mods[0].Versions.Count == 1);
        foreach (string url in new[] { "http://github.com/o/r/releases/download/v/m.dll", "https://github.com.evil/o/r/releases/download/v/m.dll", "https://github.com@evil/o/r/releases/download/v/m.dll", "https://github.com/o/r/raw/main/mod.dll", "https://github.com/o/r/releases/download/v/m.dll?x=1", "file:///mod.dll" })
            Check("URL refused: " + url, !ModCatalog.ReleaseUrl(url));
        Check("URL dot segments refused before normalization", !ModCatalog.ReleaseUrl("https://github.com/o/r/releases/download/v/../m.dll") && !ModCatalog.ReleaseUrl("https://github.com/o/r/releases/download/v/%2e%2e"));
        Refused("unknown catalog format", () => ModCatalog.Parse(Encoding.UTF8.GetBytes("{\"format\":2,\"mods\":[]}")));
        Refused("catalog size bound", () => ModCatalog.Parse(new byte[ModCatalog.MaxCatalogBytes + 1]));
        Check("bare DLL verified", Verify(Dll).SequenceEqual(Dll));
        var choices = Mod(); var newer = Release(Dll); newer.Version = "2.0.0"; var older = Release(Dll);
        choices.Versions.Add(newer); choices.Versions.Add(older);
        Check("new catalog version is available without a RevaMP release", ModCatalog.Offered(choices, 1024, ValidId) == newer);
        newer.Hash = "invalid";
        Check("invalid newest entry does not hide a valid older release", ModCatalog.Offered(choices, 1024, ValidId) == older);
        newer.Hash = ModCatalog.Sha256(Dll);
        // the start-up update check: only a catalog version newer than the installed one
        Check("update offered to an older install", ModCatalog.UpdateTo(choices, "0.9.0", 1024, ValidId) == newer);
        Check("no update when the catalog version is installed", ModCatalog.UpdateTo(choices, "2.0.0", 1024, ValidId) == null);
        Check("no update to an older catalog version", ModCatalog.UpdateTo(choices, "2.2.0", 1024, ValidId) == null);
        Check("no update when not installed", ModCatalog.UpdateTo(choices, null, 1024, ValidId) == null);
        Check("versions compare as numbers", ModCatalog.Newer("0.10.0", "0.9.0") && !ModCatalog.Newer("0.9.0", "0.10.0"));
        var tampered = (byte[])Dll.Clone(); tampered[0] ^= 1;
        Refused("tampered DLL refused", () => Verify(tampered));
        var badHash = Release(Dll); badHash.Hash = "bad";
        Refused("invalid catalog hash refused", () => ModCatalog.Verify(Mod(), badHash, Dll, 1024, ValidId));
        Refused("invalid id validator refuses", () => ModCatalog.Verify(Mod(), Release(Dll), Dll, 1024, _ => false));
        Refused("advertised oversize refused", () => Verify(Dll, limit: Dll.Length - 1));
        var wrongSize = Release(Dll); wrongSize.Size--;
        Refused("actual size must match catalog", () => ModCatalog.Verify(Mod(), wrongSize, Dll, 1024, ValidId));
        byte[] zip = Zip(("BepInEx/plugins/Test.dll", Dll), ("README.txt", new byte[1]));
        Check("exact ZIP entry verified", Verify(zip, "BepInEx/plugins/Test.dll").SequenceEqual(Dll));
        Refused("tampered zipped DLL refused", () => Verify(Zip(("Test.dll", tampered)), "Test.dll"));
        foreach (string path in new[] { "../evil.txt", "/evil.txt", "a/../../evil.txt", "C:/evil.txt", "a\\evil.txt", "a/./evil.txt", "a/.. /evil.txt", "Other.dll " })
            Refused("ZIP path escape refused: " + path, () => Verify(Zip(("Test.dll", Dll), (path, new byte[1])), "Test.dll"));
        Refused("second DLL refused", () => Verify(Zip(("Test.dll", Dll), ("Other.DLL", Dll)), "Test.dll"));
        Refused("duplicate selected DLL refused", () => Verify(Zip(("Test.dll", Dll), ("Test.dll", Dll)), "Test.dll"));
        Refused("missing selected DLL refused", () => Verify(zip, "Missing.dll"));
        Refused("uncompressed oversize refused", () => Verify(Zip(("Test.dll", new byte[8192])), "Test.dll", 1024));
        Refused("extra uncompressed data oversize refused", () => Verify(Zip(("Test.dll", Dll), ("large.txt", new byte[8192])), "Test.dll", 1024));
        using (var b = new ModCatalog.LimitedBuffer(4))
        {
            b.ContentLength(5); Check("oversize Content-Length refused before buffering", !b.Add(new byte[1], 1) && b.Length == 0);
        }
        using (var b = new ModCatalog.LimitedBuffer(4))
        {
            b.ContentLength(0);
            Check("chunked response bounded during receipt", b.Add(new byte[3], 3) && !b.Add(new byte[2], 2) && b.Length == 3 && b.Exceeded);
        }
        using (var b = new ModCatalog.LimitedBuffer(4)) Check("exact limit accepted", b.Add(new byte[4], 4) && b.Bytes.Length == 4);

        string dir = Path.Combine(Path.GetTempPath(), "revamp-mod-test-" + Guid.NewGuid().ToString("N"));
        string root = dir; dir = Path.Combine(root, "BepInEx", "plugins", "TLDRevampMods"); Directory.CreateDirectory(dir);
        try
        {
            string old = ModFiles.Target(dir, "test.mod", "1.0.0"), next = ModFiles.Target(dir, "test.mod", "2.0.0");
            File.WriteAllBytes(old, Dll);
            Check("pending installed file discovered without loaded plugin", ModActivation.Files(dir, "test.mod").Single() == old);
            Check("update succeeds", ModFiles.Install(dir, "test.mod", "2.0.0", Dll, new[] { old }) == null);
            Check("update sets old aside", !File.Exists(old) && File.ReadAllBytes(old + ".replaced").SequenceEqual(Dll) && File.ReadAllBytes(next).SequenceEqual(Dll));
            Check("reinstall same target succeeds", ModFiles.Install(dir, "test.mod", "2.0.0", Dll, new[] { next }) == null && !File.Exists(next + ".part"));
            ModFiles.Remove(dir, next);
            Check("remove renames without deleting bytes", !File.Exists(next) && File.ReadAllBytes(next + ".removed").SequenceEqual(Dll));
            ModFiles.Install(dir, "test.mod", "2.0.0", Dll, Array.Empty<string>()); ModFiles.Remove(dir, next);
            Check("repeat removal preserves both backups", File.Exists(next + ".removed") && File.Exists(next + ".1.removed") && !File.Exists(next));
            File.WriteAllBytes(old, Dll);
            Check("repeated update succeeds without overwriting earlier backup", ModFiles.Install(dir, "test.mod", "2.0.0", Dll, new[] { old }) == null && File.Exists(old + ".1.replaced"));
            string other = Path.Combine(dir, "hand"); Directory.CreateDirectory(other); string hand = Path.Combine(other, "hand.dll"); File.WriteAllBytes(hand, Dll);
            bool refused = false; try { ModFiles.Remove(dir, hand); } catch (IOException) { refused = true; }
            Check("remove refuses unmanaged file", refused && File.Exists(hand));
            Check("remove refuses backups", !ModFiles.Managed(dir, next + ".removed"));
        }
        finally { Directory.Delete(root, true); }
        // every default an older RevaMP saved into the cfg reads today's address; other addresses stay
        foreach (var old in ModCatalog.OldDefaultUrls)
            Check("old default " + old + " reads today's address", ModCatalog.CatalogAddress(old) == ModCatalog.DefaultUrl);
        Check("0.69.1's own default is among them", Array.IndexOf(ModCatalog.OldDefaultUrls, "https://raw.githubusercontent.com/Normal99/TLD-RevaMP/dev/packaging/mods.json") >= 0);
        Check("today's default stays", ModCatalog.CatalogAddress(ModCatalog.DefaultUrl) == ModCatalog.DefaultUrl);
        Check("a test catalog stays", ModCatalog.CatalogAddress("file:///C:/catalog/mods.json") == "file:///C:/catalog/mods.json");
        Check("no address stays none", ModCatalog.CatalogAddress(null) == null);
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}
