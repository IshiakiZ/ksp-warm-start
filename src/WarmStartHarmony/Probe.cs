#if WSPROBE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace WarmStartHarmony
{
    /// <summary>
    /// (Only in a build of this library made with WSPROBE, never in the one that is installed: for finding where each part's
    /// time goes while the game compiles its parts. The part loader's own steps, a module's loading and its description for
    /// the editor, the look-ups and the localiser are timed through Harmony from the game's very start, and at the main menu
    /// the log is told how long each took in all, how often, and how long the parts took in each tenth of their compiling,
    /// to see what grows with the number of parts already compiled. A method inside another timed one is counted in both.)
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class Probe : MonoBehaviour
    {
        static readonly Dictionary<MethodBase, long> ticks = new Dictionary<MethodBase, long>();
        static readonly Dictionary<MethodBase, int> calls = new Dictionary<MethodBase, int>();
        static readonly Dictionary<MethodBase, int> inside = new Dictionary<MethodBase, int>();
        static readonly List<long> parsed = new List<long>();
        static MethodBase parsePart;
        static readonly HashSet<MethodBase> infos = new HashSet<MethodBase>();
        static bool told;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            var harmony = new Harmony("warmstart.probe");
            MethodInfo pre = typeof(Probe).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo post = typeof(Probe).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic);
            int wrapped = 0;
            void Wrap(MethodBase m)
            {
                if (m == null || m.IsAbstract || m.ContainsGenericParameters || ticks.ContainsKey(m)) return;
                // (first in and last out, so that whatever else takes the method over is timed with it)
                try { harmony.Patch(m, new HarmonyMethod(pre) { priority = Priority.First }, new HarmonyMethod(post) { priority = Priority.Last }); ticks[m] = 0; calls[m] = 0; inside[m] = 0; wrapped++; }
                catch (Exception ex) { UnityEngine.Debug.Log("[Warm Start probe] could not wrap " + m.DeclaringType + "." + m.Name + ": " + ex.Message); }
            }
            void All(Type t, string name)
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == name) Wrap(m);
            }
            foreach (string name in new[] { "ParsePart", "CompilePartInfo", "CreatePartIcon", "SetPartIconMaterials", "GetPartBounds", "LoadSound", "ApplyPartValue", "CreatePart",
                                            "SetupSkinnedMeshes", "GetPartCollider", "AddVariants", "InferBulkheadProfiles", "ReplacePartTexture", "ReplacePartMaterialValue", "getPartInfoByName" })
                All(typeof(PartLoader), name);
            parsePart = AccessTools.Method(typeof(PartLoader), "ParsePart");
            All(typeof(Part), "AddModule");
            All(typeof(PartModule), "Load");
            All(typeof(BaseFieldList), "Load");
            All(typeof(GameDatabase), "GetModel");
            All(typeof(GameDatabase), "GetModelPrefab");
            All(typeof(GameDatabase), "GetTexture");
            All(typeof(KSP.Localization.Localizer), "Format");
            All(typeof(DragCubeSystem), "SetupDragCubeCoroutine");
            // (every module's description for the editor: each kind's own GetInfo)
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = Array.FindAll(ex.Types, t => t != null); } catch (Exception) { continue; }
                foreach (Type t in types)
                {
                    if (t == null || !typeof(PartModule).IsAssignableFrom(t)) continue;
                    MethodInfo m = t.GetMethod("GetInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (m != null) { Wrap(m); infos.Add(m); }
                }
            }
            UnityEngine.Debug.Log("[Warm Start probe] " + wrapped + " methods timed from here on");
        }

        static void Pre(MethodBase __originalMethod, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (inside.TryGetValue(__originalMethod, out int n)) inside[__originalMethod] = n + 1;
        }

        static void Post(MethodBase __originalMethod, long __state)
        {
            long took = Stopwatch.GetTimestamp() - __state;
            if (!inside.TryGetValue(__originalMethod, out int n)) return;
            inside[__originalMethod] = n - 1;
            calls[__originalMethod]++;
            // (a method that calls itself is counted once, at the outermost call)
            if (n == 1) ticks[__originalMethod] += took;
            if (__originalMethod == parsePart && n == 1) parsed.Add(took);
        }

        void Update()
        {
            if (told || HighLogic.LoadedScene != GameScenes.MAINMENU) return;
            told = true;
            double ms = 1000.0 / Stopwatch.Frequency;
            var said = new StringBuilder("[Warm Start probe] where the start's time went, by method (inside another timed one counted in both):");
            var rows = new List<MethodBase>(ticks.Keys);
            rows.RemoveAll(m => calls[m] == 0);
            long infoTicks = 0; int infoCalls = 0;
            foreach (MethodBase m in infos) { infoTicks += ticks[m]; infoCalls += calls[m]; }
            rows.Sort((x, y) => ticks[y].CompareTo(ticks[x]));
            foreach (MethodBase m in rows)
            {
                if (infos.Contains(m) && ticks[m] * ms < 30) continue;
                said.Append("\n  ").Append(m.DeclaringType.Name).Append('.').Append(m.Name).Append(": ").Append((ticks[m] * ms).ToString("F0")).Append(" ms in ")
                    .Append(calls[m]).Append(" calls, ").Append((ticks[m] * ms * 1000.0 / Math.Max(1, calls[m])).ToString("F0")).Append(" us each");
            }
            said.Append("\n  every module's GetInfo together: ").Append((infoTicks * ms).ToString("F0")).Append(" ms in ").Append(infoCalls).Append(" calls");
            if (parsed.Count >= 10)
            {
                said.Append("\n  ParsePart by tenths of the parts (").Append(parsed.Count).Append(" parts), ms each on the whole:");
                for (int k = 0; k < 10; k++)
                {
                    int a = parsed.Count * k / 10, b = parsed.Count * (k + 1) / 10;
                    long sum = 0;
                    for (int i = a; i < b; i++) sum += parsed[i];
                    said.Append(' ').Append((sum * ms / Math.Max(1, b - a)).ToString("F2"));
                }
            }
            UnityEngine.Debug.Log(said.ToString());
        }
    }
}
#endif
