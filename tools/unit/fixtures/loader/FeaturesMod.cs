// A TLD Loader mod that uses TLD Loader's features beyond the lifecycle, and records what it saw.
using System.Collections.Generic;
using TLDLoader;

public class FeaturesMod : Mod
{
    public static readonly List<string> Seen = new List<string>();
    public static int ConfigCalls;

    public override string ID => "features.mod";
    public override string Name => "Features Mod";
    public override string Version => "2.0";
    public override bool UseLogger => true;

    public override void Config() { ConfigCalls++; }

    public override void OnLoad()
    {
        Seen.Add("loaded:" + ModLoader.LoadedMods.Count + ":" + (ModLoader.LoadedMods.Contains(this) ? "me" : "not me"));
        Seen.Add("menu:" + ModLoader.isOnMainMenu + ":" + ModLoader.GetCurrentScene());
        Seen.Add("category:" + Category);
        Logger.Log("a warning from the mod", TLDLoader.Logger.LogLevel.Warning);
        Logger.LogDebug("a debug line");
        Logger.LogCritical("a critical line");
    }

    public override void OnMenuLoad() { Seen.Add("menu:" + ModLoader.isOnMainMenu + ":" + ModLoader.GetCurrentScene()); }
    public override bool LoadInMenu => true;
}

/// Can't load: its base class lives in MissingDep.dll, which is not in the mods folder.
public class BrokenThing : MissingDep.Base { }
