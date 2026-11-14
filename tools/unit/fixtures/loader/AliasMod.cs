// Compiled against the public shape, then loaded with the beta shape by LoaderRetargetTest.
public static class AliasMod
{
    public static UnityEngine.GameObject Display = new UnityEngine.GameObject();
    public static void Write(fpscontroller p, radioscript r, carscript c, mainscript m, menuhandler menu,
                             UnityEngine.Transform body, DataFromMenuScript data, settingsscript settings,
                             gamemodesettingsscript.gamemodesettings mode)
    {
        p.Tb = body; p.M = m; r.v = 0.625f; c.lampak = 2;
        menu.DFMS = data; m.DFMS = data; m.SettingObj = settings; menu.Settings = settings; m.GSettings = mode;
        m.godmode = true; m.Showkmh = true;
        c.KmhObj = Display;
    }

    public static bool Read(fpscontroller p, radioscript r, carscript c, mainscript m, menuhandler menu,
                            UnityEngine.Transform body, DataFromMenuScript data, settingsscript settings,
                            gamemodesettingsscript.gamemodesettings mode)
    {
        return p.Tb == body && p.M == m && r.v == 0.625f && c.lampak == 2 &&
               menu.DFMS == data && m.DFMS == data && m.SettingObj == settings && menu.Settings == settings && m.GSettings == mode && m.godmode && m.Showkmh && c.KmhObj == Display;
    }

    static void Set<T>(ref T field, T value) { field = value; }
    public static void Address(fpscontroller p, radioscript r, carscript c, mainscript m, menuhandler menu,
                               UnityEngine.Transform body, DataFromMenuScript data, settingsscript settings,
                               gamemodesettingsscript.gamemodesettings mode)
    {
        Set(ref p.Tb, body); Set(ref p.M, m); Set(ref r.v, 0.625f); Set(ref c.lampak, 2);
        Set(ref menu.DFMS, data); Set(ref m.DFMS, data); Set(ref m.SettingObj, settings);
        Set(ref menu.Settings, settings); Set(ref m.GSettings, mode);
        Set(ref m.godmode, true); Set(ref m.Showkmh, true);
        Set(ref c.KmhObj, Display);
    }

    public static void Calls(fpscontroller p, radioscript r, mainscript m, headlightscript h, survivalscript s)
    {
        p.RightHandRelease(); r.UpdValues(true); m.PressedEscape();
        h.SwitchState(2); h.SwitchState(); s.Damage(3f, true); s.DamageInstant(4f, false);
        m.ChGodMode(false); m.ChKmh(false);
        m.FoodCh(1); m.WaterCh(2); m.HpCh(3); m.PissCh(4); m.ShitCh(5);
    }
}
