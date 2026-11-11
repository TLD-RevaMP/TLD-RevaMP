using System;
using TLDRevamp.Net;

/// Entities.PartWatch's rules: a fitted part that shows but can't be reached, found by the machine itself.
static class PartWatchTest
{
    private static int _failed;
    private static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _failed++;
    }

    static string ReportWords = "";
    static void FindReportWords()
    {
        string root = Environment.GetEnvironmentVariable("TLD_REVAMP_ROOT");
        for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); root == null && d != null; d = d.Parent)
            if (System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "src/TLDRevamp/TelemetryPrivacy.cs"))) root = d.FullName;
        if (root != null) ReportWords = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/TLDRevamp/TelemetryPrivacy.cs"));
    }

    static int Main()
    {
        FindReportWords();
        // the 10:57 hood: fitted, shown, its colliders should be on (the hood slot doesn't switch them off), none reachable
        Check("a shown part whose colliders should be on, none reachable, is broken", PartWatchRules.Broken(true, false, true, 0));
        // a tyre on a rim on a car: the game switched its colliders off (disableCollidersOnCraft up its chain)
        Check("a part the game switched off on craft is not (a tyre on a rim on a car)", !PartWatchRules.Broken(true, false, false, 0));
        Check("but on the held layer it is, whatever the slot", PartWatchRules.Broken(true, false, false, 2));
        Check("on the held layer with every collider off: the slot decides, not the layer (a tyre on a carried wheel)", !PartWatchRules.Broken(true, false, false, 2, 0));
        Check("a hidden part is not", !PartWatchRules.Broken(false, false, true, 0));
        Check("a reachable part is not", !PartWatchRules.Broken(true, true, true, 0));
        Check("colliders on the held layer: held layer", PartWatchRules.Why(2, 2) == PartWatchRules.HeldLayer);
        Check("no collider on: colliders off", PartWatchRules.Why(0, 0) == PartWatchRules.Off);
        Check("on, but on other layers: not on use layer", PartWatchRules.Why(3, 0) == PartWatchRules.NotUseLayer);
        Check("a part being fitted or taken off is given time", PartWatchRules.NearbyAfterS >= 2f && PartWatchRules.LookAfterS >= 0.5f);
        if (ReportWords.Length == 0) Console.WriteLine("SKIP report words: repo root not found");
        else
        {
            foreach (var reason in new[] { PartWatchRules.Off, PartWatchRules.HeldLayer, PartWatchRules.NotUseLayer, PartWatchRules.Blocked })
                Check("reason kept in bug reports: " + reason, ReportWords.Contains("\"" + reason + "\""));
            Check("event kept in bug reports: part-untargetable", ReportWords.Contains(" part-untargetable "));
        }
        Console.WriteLine(_failed == 0 ? "PASS" : "FAIL");
        return _failed == 0 ? 0 : 1;
    }
}
