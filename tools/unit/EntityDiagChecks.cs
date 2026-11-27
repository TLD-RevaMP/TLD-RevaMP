using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using TLDRevamp;
using TLDRevamp.Net;
using UnityEngine;

static partial class EntitySplitTest
{
    static void DiagChecks()
    {
        DiagSourceChecks();
        var rows = new List<string>();
        void Row(string name, object value) => rows.Add(name + "=" + Convert.ToString(value, CultureInfo.InvariantCulture));
        Entities.DiagMap.Clear();
        var player = new fpscontroller { Cam = new Component(), useLayer = new LayerMask { value = 1 << 3 }, pickupLayer = new LayerMask { value = 1 << 4 }, attachLayer = new LayerMask { value = 1 << 5 } };
        mainscript.s = new mainscript { player = player };
        var car = new tosaveitemscript { name = "car", id = 12, partslotscripts = new List<partslotscript>() };
        var entity = new Entities.Ent { NetId = 81, Root = car, RootId = 810, OwnerId = 1, Epoch = 3 };
        entity.Items.Add(car); Entities.DiagMap.Add(81, entity);
        var holder = new pickupable { tosave = car };
        var slot = new partslotscript { name = "hood\"slot", tosaveitem = car, pick = holder, tipus = new[] { "hood" } };
        car.partslotscripts.Add(slot);
        var part = new attachablescript { name = "hood", slot = slot, attached = true, T = new Transform(), condition = new DiagCondition { state = 1, state2 = 2, state3 = 3 } };
        part.tosave = new tosaveitemscript { name = "part", id = 14, idInSave = 99, P = new pickupable { crafted = true }, attachable = part };
        part.T.localPosition = new Vector3(.1f, -.2f, .3f); part.T.localEulerAngles = new Vector3(1, 2, 3);
        slot.Part = part; slot.parts.Add(part); entity.Items.Add(part.tosave);
        var renderer = new Renderer(); part.Children.Add(renderer);
        var collider = new Collider(); collider.gameObject.layer = 3; part.Children.Add(collider);
        var near = new List<attachablescript>();
        EntityDiag.NearParts(player, near); Row("near-first", string.Join(",", near.Select(x => x.name)));
        // Duplicate membership does not duplicate parts; the group radius is distinct from the part radius.
        var second = new Entities.Ent { NetId = 4, Root = car }; second.Items.Add(car); Entities.DiagMap.Add(4, second);
        var visit = new List<uint>(); Entities.VisitEnts(e => visit.Add(e.NetId));
        Check(visit.SequenceEqual(new uint[] { 81, 4 }), "visitor preserves insertion order for nonsequential IDs");
        EntityDiag.NearParts(player, near); Row("near-duplicate", near.Count);
        part.transform.position = new Vector3(12, 0, 0); EntityDiag.NearParts(player, near); Row("near-boundary", near.Count);
        part.transform.position = new Vector3(12.01f, 0, 0); EntityDiag.NearParts(player, near); Row("near-outside", near.Count);
        part.transform.position = Vector3.zero;
        string Reach() { var x = EntityDiag.Reach(part, player); return x.Visible + ":" + x.Targetable + ":" + x.Expected + ":" + x.Cols + ":" + x.Live + ":" + x.UseLayer + ":" + x.Held; }
        Row("reach-use", Reach()); collider.enabled = false; Row("reach-off", Reach());
        collider.enabled = true; collider.gameObject.layer = mainscript.pickedUpLayer; Row("reach-held", Reach());
        slot.disableCollidersOnCraft = true; Row("reach-deliberate", Reach()); slot.disableCollidersOnCraft = false;
        collider.gameObject.layer = 3;
        DevHooks.PartRenderers = _ => new[] { new Renderer { enabled = false } }; Row("reach-dev-renderers", Reach()); DevHooks.PartRenderers = null;
        Physics.Hits = Array.Empty<RaycastHit>(); Row("parts-json", EntityDiag.PartsDiagnostics());
        Entities.DiagMap.Remove(4); car.randoms = new List<DiagRandom> { new DiagRandom { rtipus = 2 }, null };
        car.transform.Children.Add(part.tosave); part.tosave.randoms = new List<DiagRandom> { new DiagRandom { rtipus = 7 } };
        Row("census-rows", string.Join("|", EntityDiag.CensusRows(entity)));
        // the resync's capture time is a real stopwatch reading (0.01 ms here, more on a slow CI runner): fixed, the rows check the format
        Entities.ResyncCaptureMsMax = 0.01; Entities.ResyncCaptureMsTotal = 0.01;
        Row("status-zero", Entities.Status(0)); Row("status-all", Entities.Status());
        // Capture detached sample values from the sampler, including the no-receipt NaN and excluded focus.
        SessionLog.History.Trace.Nets = new uint[] { 81 }; SessionLog.History.Trace.Rows.Clear();
        Time.realtimeSinceStartupAsDouble = 20; entity.TelemetryInterval = .125f; entity.TelemetryReceived = 18;
        Entities.DiagPose(entity, 2, 10, new Vector3d(1.25, -2.5, 3.75), Quaternion.identity, new Vector3(3, 0, 4), 2, 3, 4, .5f);
        entity.TelemetryReceived = 0; Entities.DiagPose(entity, 1, double.NaN, default, Quaternion.identity, default);
        Entities.DiagPose(new Entities.Ent { NetId = 99 }, 0, 0, default, Quaternion.identity, default);
        foreach (var sample in SessionLog.History.Trace.Rows) Row("sample", JsonSerializer.Serialize(sample, new JsonSerializerOptions { IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        Row("root-null", Entities.DiagCategory(null)); Row("root-car", Entities.DiagCategory(car)); Row("root-pickup", Entities.DiagCategory(part.tosave));
        var snapshot = new TelemetryState();
        snapshot.Add(new StatePlayer { Id = 1, Net = 81, Seat = 0, HasPos = true });
        snapshot.Add(new StateEntity { Net = 81, Owner = 1, Name = "car", Car = true, Fuel = float.NaN });
        Entities.DiagSlot(snapshot, entity, 0, 0, slot, part); Entities.DiagSlot(snapshot, entity, 0, 1, slot, null);
        Entities.DiagStations(snapshot, new List<mainscript.radioCH> { null, new mainscript.radioCH { custom = true }, new mainscript.radioCH { Index = 3, cTime = 1.25f } }, false);
        Entities.DiagStations(snapshot, new List<mainscript.radioCH> { new mainscript.radioCH { Index = 4, cTime = 2.5f } }, true);
        snapshot.Add(Entities.DiagUsable(new usablescript { name = "door", xRot = 1, yRot = 2, slideValue = .25f, currentTurnState = 3, tunedFloat = .5f }));
        using (var writer = new StringWriter(CultureInfo.InvariantCulture)) { snapshot.Write(writer); Row("mark-snapshot-json", writer.ToString()); using var json = JsonDocument.Parse(writer.ToString()); Check(json.RootElement.GetProperty("slots").GetArrayLength() == 2, "mark snapshot contains both projected slots"); }
        Row("anomaly-lines", Entities.AnomalyJson());
        const string path = "tools/unit/EntityDiagRows.txt";
        if (Environment.GetEnvironmentVariable("DIAG_CAPTURE") == "1") File.WriteAllText(path, string.Join("\r\n", rows) + "\r\n");
        Check(File.ReadAllLines(path).SequenceEqual(rows), "batch 26 fixed diagnostic rows match HEAD captures");
        Check(typeof(Entities).GetField("PartWatchNearbyM", Static) == null && typeof(EntityDiag).GetField("PartWatchNearbyM", Static) != null, "part watch settings have one backing owner");
        float radius = EntityDiag.PartWatchNearbyM;
        Check(BridgeReflect.Set("TLDRevamp.Net.EntityDiag.PartWatchNearbyM", "13") == "13" && EntityDiag.PartWatchNearbyM == 13, "the bridge path names the real owner");
        EntityDiag.PartWatchNearbyM = radius;
        Entities.DiagMap.Clear(); mainscript.s = null; DevHooks.PartRenderers = null; SessionLog.History.Trace.Rows.Clear();
    }
    static void DiagSourceChecks()
    {
        string Feature(string path, string owner)
        {
            string text = File.ReadAllText("src/TLDRevamp/Net/" + path).Replace("\r\n", "\n");
            string marker = "    internal static partial class " + owner;
            int start = text.IndexOf(marker, StringComparison.Ordinal);
            int end = text.IndexOf("\n    }", start, StringComparison.Ordinal);
            return text.Substring(start, end - start);
        }
        string[] allowed = { "Ent", "PartReach", "VisitEnts", "PartWatchGroup", "NetOfRoot", "StatusHeld", "StatusStored", "StoredPos", "V3", "MarkIsLocal", "MarkItemNet", "RefreshMarkFocus" };
        foreach (var entry in new[] { ("Entities.PartWatch.cs", "EntityDiag"), ("Entities.Status.cs", "EntityDiag"), ("Entities.Anomaly.cs", "EntityDiag"), ("Entities.Telemetry.cs", "EntityTelemetry"), ("TelemetryMarks.cs", "EntityTelemetry") })
        {
            string feature = Feature(entry.Item1, entry.Item2);
            if (entry.Item1 == "TelemetryMarks.cs") feature = feature.Substring(feature.IndexOf("// Detached mark rows;", StringComparison.Ordinal));
            Check(!feature.Contains("using static") && !feature.Contains("HarmonyPatch"), entry.Item1 + " diagnostic owner has no patch or static kernel import");
            foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(feature, @"\bEntities\.(\w+)")) Check(allowed.Contains(call.Groups[1].Value), "qualified diagnostic boundary " + call.Value);
            Check(!System.Text.RegularExpressions.Regex.IsMatch(feature, @"\b(?:ByNet|Server|PendingShare|SEnt|W|WS|Resolve|ToServer|ServerSendTo|ServerSendAll|SetProxy|SharedGroupOf|OwnEntity)\b"), entry.Item1 + " has no map, authority, transport or resolving lookup");
            // r is the detached PartReach created by Reach, never an entity or an alias of one.
            string writes = System.Text.RegularExpressions.Regex.Replace(feature, @"\br\.(?:Cols|Live|Held|UseLayer|Visible|Targetable|Expected)\s*(?:\+\+|=(?!=))", "detached");
            Check(!System.Text.RegularExpressions.Regex.IsMatch(writes, @"\b\w+(?:\s*(?:\[[^\]]*\]|\.\s*\w+))+\s*(?:[+\-*/%&|^]?=(?!=)|\+\+|--)|(?:\+\+|--)\s*\w+(?:\[[^\]]*\]|\.\w+)+"), entry.Item1 + " never assigns or increments an observed object's members");
            // These are the owned scratch/output collections. Chained member mutation is always rejected.
            string mutations = System.Text.RegularExpressions.Regex.Replace(feature, @"\b(?:rows|into|_pwCols|s)\.(?:Add|Clear|Sort)\s*\(|\bSessionLog\.History\.Trace\.Add\s*\(|\bSystem\.Array\.Sort\s*\(", "output(");
            Check(!System.Text.RegularExpressions.Regex.IsMatch(mutations, @"\b\w+(?:\s*(?:\[[^\]]*\]|\.\s*\w+))*\.(?:Add|AddRange|Remove|RemoveAt|RemoveAll|RemoveRange|Clear|Insert|Sort)\s*\("), entry.Item1 + " never mutates collections through observed objects");
        }
        string features = File.ReadAllText("src/TLDRevamp/Net/Entities.Features.cs").Replace("\r\n", "\n");
        Check(features.Contains("internal static void VisitEnts(System.Action<Ent> see)\n        {\n            foreach (var e in ByNet.Values) see(e);\n        }"), "visitor uses the original Values collection and its order without a copy");
        Check(File.ReadAllLines("tools/unit/EntityDiagSourceRows.txt").SequenceEqual(EntityDiagSources.Rows), "all retained and moved diagnostic bodies match the pre-move source capture");
        var order = new List<uint>(); Entities.VisitEnts(e => order.Add(e.NetId));
        Check(order.SequenceEqual(Entities.DiagMap.Values.Select(e => e.NetId)), "visitor invokes once per record in native map value order");
    }

}

