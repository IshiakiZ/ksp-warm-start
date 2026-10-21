#if WSPROBE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
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
        // (2026-10-20, Warm Start 9.1: on a made-up install of 4,740 parts a part's compiling grew from 2.4 ms to 4.6 ms as the parts already
        // compiled grew in number: each step's time by every 500 parts compiled, and the collector's runs and the heap's size at each 500)
        const int Block = 500, Blocks = 16;
        static readonly Dictionary<MethodBase, long[]> byBlock = new Dictionary<MethodBase, long[]>();
        static readonly int[] gcAt = new int[Blocks + 1];
        static readonly long[] heapAt = new long[Blocks + 1];
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
            // (2026-10-20, 9.1: the part loader's every own method, and what ParsePart calls of others', to find what grows with the parts compiled)
            foreach (MethodInfo m in typeof(PartLoader).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!m.IsGenericMethodDefinition && m.ReturnType != typeof(System.Collections.IEnumerator) && m.Name.IndexOf('<') < 0) Wrap(m);
            foreach (MethodInfo m in typeof(DragCubeSystem).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!m.IsGenericMethodDefinition && m.ReturnType != typeof(System.Collections.IEnumerator) && m.Name.IndexOf('<') < 0) Wrap(m);
            All(typeof(ConfigNode), "LoadObjectFromConfig");
            All(typeof(ShipConstruction), "SanitizePartCosts");
            All(typeof(AvailablePart), "AddFileTime");
            All(typeof(FlightGlobals), "GetUniquepersistentId");
            All(typeof(Part), "Awake");
            All(typeof(GameDatabase), "GetConfigNode");
            // (2026-10-20, 9.1 again: none of those grew, so every call ParsePart makes is timed where it makes it, Unity's own included,
            // which no wrap reaches: a mark before and after each call site in ParsePart's own code)
            try { harmony.Patch(parsePart, transpiler: new HarmonyMethod(typeof(Probe).GetMethod(nameof(TimeSites), BindingFlags.Static | BindingFlags.NonPublic))); }
            catch (Exception ex) { UnityEngine.Debug.Log("[Warm Start probe] ParsePart's call sites could not be timed: " + ex.Message); }
            All(typeof(Part), "AddModule");
            All(typeof(PartModule), "Load");
            All(typeof(BaseFieldList), "Load");
            All(typeof(GameDatabase), "GetModel");
            All(typeof(GameDatabase), "GetModelPrefab");
            All(typeof(GameDatabase), "GetTexture");
            All(typeof(KSP.Localization.Localizer), "Format");
            All(typeof(DragCubeSystem), "SetupDragCubeCoroutine");
            // (the models: the game's reader of its .mu files, step by step)
            foreach (string name in new[] { "Read", "ReadChild", "ReadTextures", "ReadMeshRenderer", "ReadSkinnedMeshRenderer", "ReadMesh", "ReadMaterial", "ReadMaterial4",
                                            "ReadMaterialTexture", "ReadAnimation", "ReadAnimationEvents", "ReadLight", "ReadTagAndLayer", "ReadParticles", "ReadCamera", "NormalMapToUnityNormalMap" })
                All(AccessTools.TypeByName("PartReader"), name);
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

        static readonly List<string> siteNames = new List<string>();
        static long[,] siteTicks = new long[0, 0];
        static long[] siteStart = new long[0];

        static IEnumerable<CodeInstruction> TimeSites(IEnumerable<CodeInstruction> code)
        {
            MethodInfo enter = typeof(Probe).GetMethod(nameof(SiteEnter), BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo leave = typeof(Probe).GetMethod(nameof(SiteLeave), BindingFlags.Static | BindingFlags.NonPublic);
            var held = new List<CodeInstruction>();             // (a prefix such as constrained. must stay right before its call)
            var output = new List<CodeInstruction>();
            foreach (CodeInstruction c in code)
            {
                if (c.opcode.OpCodeType == OpCodeType.Prefix) { held.Add(c); continue; }
                bool site = (c.opcode == OpCodes.Call || c.opcode == OpCodes.Callvirt || c.opcode == OpCodes.Newobj) && c.operand is MethodBase;
                if (!site) { output.AddRange(held); held.Clear(); output.Add(c); continue; }
                var m = (MethodBase)c.operand;
                int k = siteNames.Count;
                siteNames.Add(m.DeclaringType?.Name + "." + m.Name);
                CodeInstruction first = held.Count > 0 ? held[0] : c;
                var mark = new CodeInstruction(OpCodes.Ldc_I4, k);
                mark.labels.AddRange(first.labels); first.labels.Clear();
                mark.blocks.AddRange(first.blocks); first.blocks.Clear();
                output.Add(mark);
                output.Add(new CodeInstruction(OpCodes.Call, enter));
                output.AddRange(held); held.Clear();
                output.Add(c);
                output.Add(new CodeInstruction(OpCodes.Ldc_I4, k));
                output.Add(new CodeInstruction(OpCodes.Call, leave));
            }
            output.AddRange(held);
            siteTicks = new long[siteNames.Count, Blocks];
            siteStart = new long[siteNames.Count];
            UnityEngine.Debug.Log("[Warm Start probe] ParsePart's call sites timed: " + siteNames.Count);
            return output;
        }

        static void SiteEnter(int k) { if (k < siteStart.Length) siteStart[k] = Stopwatch.GetTimestamp(); }

        static void SiteLeave(int k)
        {
            if (k >= siteStart.Length || siteStart[k] == 0) return;
            siteTicks[k, Math.Min(parsed.Count / Block, Blocks - 1)] += Stopwatch.GetTimestamp() - siteStart[k];
            siteStart[k] = 0;
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
            if (n == 1)
            {
                ticks[__originalMethod] += took;
                int block = Math.Min(parsed.Count / Block, Blocks - 1);
                if (!byBlock.TryGetValue(__originalMethod, out long[] row)) byBlock[__originalMethod] = row = new long[Blocks];
                row[block] += took;
            }
            if (__originalMethod == parsePart && n == 1)
            {
                parsed.Add(took);
                if (parsed.Count % Block == 0 && parsed.Count / Block <= Blocks) { gcAt[parsed.Count / Block] = GC.CollectionCount(0); heapAt[parsed.Count / Block] = GC.GetTotalMemory(false); }
            }
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
            int full = parsed.Count / Block;
            if (full >= 2)
            {
                said.Append("\n  by every ").Append(Block).Append(" parts compiled (ms a part), the steps of 100 ms and more:");
                foreach (MethodBase m in rows)
                {
                    if (ticks[m] * ms < 100 || !byBlock.TryGetValue(m, out long[] row)) continue;
                    said.Append("\n    ").Append(m.DeclaringType.Name).Append('.').Append(m.Name).Append(':');
                    for (int k = 0; k < full; k++) said.Append(' ').Append((row[k] * ms / Block).ToString("F2"));
                }
                // (ParsePart's own call sites, the ten that grew most from the second 500 to the last)
                var grew = new List<int>();
                for (int s = 0; s < siteNames.Count; s++) grew.Add(s);
                Func<int, double> growth = s => (siteTicks[s, full - 1] - siteTicks[s, Math.Min(1, full - 1)]) * ms / Block;
                grew.Sort((x, y) => growth(y).CompareTo(growth(x)));
                said.Append("\n  ParsePart's own calls by every ").Append(Block).Append(" parts (ms a part), the ten that grew most (site: what it calls):");
                for (int i = 0; i < grew.Count && i < 10; i++)
                {
                    int s = grew[i];
                    said.Append("\n    #").Append(s).Append(' ').Append(siteNames[s]).Append(':');
                    for (int b = 0; b < full; b++) said.Append(' ').Append((siteTicks[s, b] * ms / Block).ToString("F3"));
                }
                said.Append("\n  the collector's runs by then:");
                for (int k = 1; k <= full && k <= Blocks; k++) said.Append(' ').Append(gcAt[k]);
                said.Append("\n  the heap by then (MB):");
                for (int k = 1; k <= full && k <= Blocks; k++) said.Append(' ').Append((heapAt[k] / 1048576.0).ToString("F0"));
            }
            UnityEngine.Debug.Log(said.ToString());
        }
    }
}
#endif
