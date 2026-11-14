// Public and beta shapes for the rewriter tests. No game code is used here.
namespace UnityEngine
{
    public class Transform { }
    public class GameObject { }
    public struct Vector3 { public float x, y, z; }
}
public class DataFromMenuScript { public static DataFromMenuScript s; }
public class settingsscript { public static settingsscript s; }
public class gamemodesettingsscript
{
    public class gamemodesettings { }
    public static gamemodesettingsscript s;
    public gamemodesettings S;
}
public class usablescript { public float tunedLerp; public int currentTurnState; }
public class fpscontroller
{
#if PUBLIC_GAME
    public UnityEngine.Transform Tb;
    public mainscript M;
    public void RightHandRelease() { }
#else
    public UnityEngine.Transform BodyRot;
    public bool Released;
    public void RightHandRelease(bool sync) { Released = sync; }
#endif
}
public class radioscript
{
#if PUBLIC_GAME
    public float v;
    public void UpdValues(bool multi) { }
#else
    public usablescript Bvolume = new usablescript();
    public int Updates;
    public void UpdValues() { Updates++; }
#endif
}
public class carscript
{
#if PUBLIC_GAME
    public int lampak;
    public UnityEngine.GameObject KmhObj;
#else
    public usablescript lightUsable = new usablescript();
    public UnityEngine.GameObject debugCanvas;
#endif
}
public class mainscript
{
    public static mainscript s;
#if PUBLIC_GAME
    public DataFromMenuScript DFMS;
    public settingsscript SettingObj;
    public gamemodesettingsscript.gamemodesettings GSettings;
    public bool godmode, Showkmh;
    public void ChGodMode(bool value) { }
    public void ChKmh(bool value) { }
    public void FoodCh(float value) { }
    public void WaterCh(float value) { }
    public void HpCh(float value) { }
    public void PissCh(float value) { }
    public void ShitCh(float value) { }
    public void PressedEscape() { }
#else
    public bool Screenshot, MainMenu;
    public void PressedEscape(bool screenshot, bool mainMenu) { Screenshot = screenshot; MainMenu = mainMenu; }
#endif
}
public class kaposztaleves
{
    public struct devSettings { public bool god, kmh; }
    public static kaposztaleves s;
    public devSettings settings;
    public float Food, Water, Hp, Piss, Shit;
    public void PressedFoodCh(float value) { Food = value; }
    public void PressedWaterCh(float value) { Water = value; }
    public void PressedHpCh(float value) { Hp = value; }
    public void PressedPissCh(float value) { Piss = value; }
    public void PressedShitCh(float value) { Shit = value; }
}
public class menuhandler
{
#if PUBLIC_GAME
    public DataFromMenuScript DFMS;
    public settingsscript Settings;
#endif
}
public class headlightscript
{
    public int switchToState;
    public System.Collections.Generic.List<int> states = new System.Collections.Generic.List<int> { 0, 1, 2 };
#if PUBLIC_GAME
    public void SwitchState() { }
    public void SwitchState(int state) { }
#else
    public usablescript Sender;
    public int Calls;
    public void SwitchState(int state, usablescript sender) { switchToState = System.Math.Min(state, states.Count - 1); Sender = sender; Calls++; }
#endif
}
public class survivalscript
{
#if PUBLIC_GAME
    public void Damage(float amount, bool show) { }
    public void DamageInstant(float amount, bool show) { }
#else
    public float Lost, InstantLost;
    public bool Shown, InstantShown;
    public UnityEngine.Vector3 Direction, InstantDirection;
    public void Damage(float amount, UnityEngine.Vector3 direction, bool show) { Lost = amount; Direction = direction; Shown = show; }
    public void DamageInstant(float amount, UnityEngine.Vector3 direction, bool show) { InstantLost = amount; InstantDirection = direction; InstantShown = show; }
#endif
}
