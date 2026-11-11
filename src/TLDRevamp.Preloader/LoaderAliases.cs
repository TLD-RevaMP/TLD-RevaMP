using System.Collections.Generic;
using BepInEx.Logging;
using Mono.Cecil;

namespace TLDRevamp.Preloader
{
    /// TLDLoader mods are built against the public game, whose singletons have other names than the beta's:
    /// mainscript.M, itemdatabase.d and savedatascript.d are mainscript.s, itemdatabase.s and savedatascript.s here.
    /// Of the workshop's 205 mods (2026-11-10), 173 read mainscript.M and 150 itemdatabase.d: on the beta they stop with a
    /// MissingFieldException at the first line that does. This adds the old names as static fields of the same type
    /// (nothing else in the game changes); TLDRevamp's ModHost keeps them pointing at the current singletons.
    public static class LoaderAliases
    {
        public static IEnumerable<string> TargetDLLs { get { yield return "Assembly-CSharp.dll"; } }

        internal static readonly string[][] Aliases =
        {
            new[] { "mainscript", "M" },
            new[] { "itemdatabase", "d" },
            new[] { "savedatascript", "d" },
        };

        /// The public game's instance fields on mainscript that the beta keeps elsewhere: mods read them as
        /// mainscript.M.<name>. Instance fields marked NotSerialized (Unity never stores them; no save format has them);
        /// TLDRevamp's ModHost sets them while a world is up: seed = DataFromMenuScript.s.seed (the world seed), load = the
        /// world came from a save (latched while it loads; the beta clears its own flag on the world's first frame, before
        /// the mods' OnLoad), napszak = napszakvaltakozas.s (the day/night cycle). 2026-11-11: seed is named by 65 workshop
        /// DLLs, load by 58, napszak by 18.
        internal static readonly string[][] InstanceAliases =
        {
            new[] { "mainscript", "seed", "System.Int32" },
            new[] { "mainscript", "load", "System.Boolean" },
            new[] { "mainscript", "napszak", "napszakvaltakozas" },
        };

        public static void Patch(AssemblyDefinition assembly)
        {
            var log = Logger.CreateLogSource("RevaMP loader aliases");
            var module = assembly.MainModule;
            foreach (var a in Aliases)
            {
                var type = module.GetType(a[0]);
                if (type == null) { log.LogWarning(a[0] + " not found: no " + a[0] + "." + a[1]); continue; }
                bool taken = false;
                foreach (var f in type.Fields) if (f.Name == a[1]) taken = true;
                foreach (var p in type.Properties) if (p.Name == a[1]) taken = true;
                if (taken) continue;   // the game has it (a public build, or a future beta)
                type.Fields.Add(new FieldDefinition(a[1], FieldAttributes.Public | FieldAttributes.Static, type));
            }
            foreach (var a in InstanceAliases)
            {
                var type = module.GetType(a[0]);
                if (type == null) { log.LogWarning(a[0] + " not found: no " + a[0] + "." + a[1]); continue; }
                bool taken = false;
                foreach (var f in type.Fields) if (f.Name == a[1]) taken = true;
                foreach (var p in type.Properties) if (p.Name == a[1]) taken = true;
                if (taken) continue;
                TypeReference ft = a[2] == "System.Int32" ? module.TypeSystem.Int32 : a[2] == "System.Boolean" ? module.TypeSystem.Boolean : module.GetType(a[2]);
                if (ft == null) { log.LogWarning(a[2] + " not found: no " + a[0] + "." + a[1]); continue; }
                type.Fields.Add(new FieldDefinition(a[1], FieldAttributes.Public | FieldAttributes.NotSerialized, ft));
            }
        }
    }
}
