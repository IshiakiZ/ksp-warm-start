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
    /// (Only in a build of this library made with WSPROBE, never in the one that is installed: for items 9.2 and 9.3, a save's
    /// load and the way from the space centre to flight, and Smooth Running's 8.1, the scene changes.) Where a change of scene
    /// spends its time. Measured from the game, a launch took 9.2 s, a revert 6.0, the pad back to the space centre 5.9, with
    /// the log silent for 4.2, 2.1 and 2.0 s while a flight loads: this says what fills them. From the moment a scene is asked
    /// for until it has settled (its ready event, then two seconds of frames under 100 ms, or 30 s at most), every frame's length
    /// is kept, and the steps the game and the mods take are timed through Harmony: the save's load and each scenario's, each
    /// craft's and each part's, the terrain's start, every mod's own Awake and Start (and each part module's OnStart), and the
    /// game's events, each by its name (all that listen to it together). Then the log is told the change's whole time, its long
    /// frames and the steps by the time they took, and the mods' share by library. A step inside another is counted in both.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class SceneProbe : MonoBehaviour
    {
        sealed class Step { public string name, library; public long ticks, most; public int calls, inside; public float mostAt; }

        static readonly Dictionary<MethodBase, Step> steps = new Dictionary<MethodBase, Step>();
        static readonly Dictionary<string, Step> events = new Dictionary<string, Step>();
        static FieldInfo eventName;
        static bool open;
        static GameScenes from, to;
        static float began, readyAt, quietSince;
        static long beganTicks;
        static readonly List<KeyValuePair<float, float>> longFrames = new List<KeyValuePair<float, float>>();
        static float lastFrame;
        static int frames;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            var harmony = new Harmony("warmstart.sceneprobe");
            MethodInfo pre = typeof(SceneProbe).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo post = typeof(SceneProbe).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic);
            int wrapped = 0, failed = 0;
            void Wrap(MethodBase m, string library)
            {
                if (m == null || m.IsAbstract || m.ContainsGenericParameters || steps.ContainsKey(m)) return;
                try
                {
                    if (m.GetMethodBody() == null) return;
                    harmony.Patch(m, new HarmonyMethod(pre) { priority = Priority.First }, new HarmonyMethod(post) { priority = Priority.Last });
                    steps[m] = new Step { name = m.DeclaringType.Name + "." + m.Name, library = library };
                    wrapped++;
                }
                catch (Exception) { failed++; }
            }
            void All(Type t, params string[] names)
            {
                if (t == null) return;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (Array.IndexOf(names, m.Name) >= 0) Wrap(m, "the game");
            }
            // (the game's own steps in loading a save and a scene)
            All(typeof(HighLogic), "LoadScene");
            All(typeof(GamePersistence), "LoadGame", "LoadSFSFile", "LoadGameCfg", "SaveGame");
            All(typeof(Game), "Load", "Start", "Updated");
            All(typeof(ProtoScenarioModule), "Load");
            All(typeof(ProtoVessel), "Load");
            All(typeof(ProtoPartSnapshot), "Load");
            All(typeof(Vessel), "Load", "Unpack", "GoOffRails", "Initialize");
            All(typeof(ShipConstruction), "LoadShip", "AssembleForLaunch", "PutShipToGround", "CreateBackup");
            All(typeof(FlightDriver), "Awake", "Start", "setStartupNewVessel");
            All(typeof(FlightGlobals), "Awake", "Start", "SetActiveVessel", "ForceSetActiveVessel");
            All(typeof(PSystemSetup), "SetPQSActive", "SetPQSInactive", "LoadTimer");
            All(typeof(PQS), "StartUpSphere", "ResetSphere", "ForceStart", "RebuildSphere", "SetupMods", "SetupBuildDelegates");
            All(typeof(AddonLoader), "StartAddons");
            All(typeof(EditorLogic), "Awake", "Start");
            All(typeof(EditorDriver), "Awake", "Start");
            All(typeof(SpaceCenter), "Start");
            All(typeof(PartLoader), "getPartInfoByName");
            // (every mod's and the game's addons: their own Awake, Start and OnEnable; each part module's OnStart and each
            // scenario's OnLoad and OnAwake)
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = Array.FindAll(ex.Types, t => t != null); } catch (Exception) { continue; }
                string library = a.GetName().Name;
                foreach (Type t in types)
                {
                    if (t == null || t.IsAbstract) continue;
                    bool addon = false;
                    try { addon = t.GetCustomAttributes(typeof(KSPAddon), true).Length > 0; } catch (Exception) { }
                    string[] names = addon ? new[] { "Awake", "Start", "OnEnable" }
                                   : typeof(PartModule).IsAssignableFrom(t) ? new[] { "OnStart", "OnStartFinished", "OnLoad" }
                                   : typeof(ScenarioModule).IsAssignableFrom(t) ? new[] { "OnLoad", "OnAwake", "Start" }
                                   : null;
                    if (names == null) continue;
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (Array.IndexOf(names, m.Name) >= 0) Wrap(m, library);
                }
            }
            // (Since 2026-10-19 evening, for the first flight's frame of 11.7 s in which nothing is logged, 9.3: every one of the game's
            // own behaviours, its Awake, Start and OnEnable, and its Update, LateUpdate and FixedUpdate, whose first call in a scene
            // compiles it; each step's longest single call is kept with when it came, to say what ran in a long frame)
            int before = wrapped;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != "Assembly-CSharp") continue;
                Type[] types;
                try { types = a.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = Array.FindAll(ex.Types, t => t != null); }
                foreach (Type t in types)
                {
                    if (t == null || t.IsAbstract || t.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (m.GetParameters().Length == 0 && Array.IndexOf(Behaviour, m.Name) >= 0) Wrap(m, "the game");
                }
            }
            UnityEngine.Debug.Log("[Warm Start probe] the game's own behaviours: " + (wrapped - before) + " of their Awake, Start, OnEnable and updates timed");
            // (the game's events: all that listen to one, together, by the event's name)
            eventName = AccessTools.Field(typeof(BaseGameEvent), "eventName");
            MethodInfo preFire = typeof(SceneProbe).GetMethod(nameof(PreFire), BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo postFire = typeof(SceneProbe).GetMethod(nameof(PostFire), BindingFlags.Static | BindingFlags.NonPublic);
            foreach (Type t in new[] { typeof(EventVoid), typeof(EventData<GameScenes>), typeof(EventData<Vessel>), typeof(EventData<ConfigNode>), typeof(EventData<Game>),
                                       typeof(EventData<GameEvents.FromToAction<GameScenes, GameScenes>>), typeof(EventData<ProtoVessel>), typeof(EventData<Part>) })
            {
                MethodInfo fire = t.GetMethod("Fire", BindingFlags.Instance | BindingFlags.Public);
                try { if (fire != null) { harmony.Patch(fire, new HarmonyMethod(preFire) { priority = Priority.First }, new HarmonyMethod(postFire) { priority = Priority.Last }); wrapped++; } }
                catch (Exception) { failed++; }
            }
            GameEvents.onGameSceneLoadRequested.Add(new Asked().Heard);
            GameEvents.onLevelWasLoadedGUIReady.Add(new Ready().Heard);
            UnityEngine.Debug.Log("[Warm Start probe] scene changes: " + wrapped + " steps timed (" + failed + " could not be)");
        }

        static readonly string[] Behaviour = { "Awake", "Start", "OnEnable", "Update", "LateUpdate", "FixedUpdate" };

        // (the game's event lists take a delegate's Target: a static method has none, so these small objects hear them)
        sealed class Asked { public void Heard(GameScenes scene) { Begin(scene); } }
        sealed class Ready { public void Heard(GameScenes scene) { if (open && readyAt < 0f) readyAt = Time.realtimeSinceStartup - began; } }

        static void Begin(GameScenes scene)
        {
            if (open) End("another scene was asked for");
            foreach (Step s in steps.Values) { s.ticks = 0; s.calls = 0; s.most = 0; s.mostAt = 0f; }
            events.Clear();
            longFrames.Clear();
            from = HighLogic.LoadedScene; to = scene;
            began = lastFrame = Time.realtimeSinceStartup;
            beganTicks = Stopwatch.GetTimestamp();
            readyAt = quietSince = -1f;
            frames = 0;
            open = true;
        }

        void Update()
        {
            if (!open) return;
            float now = Time.realtimeSinceStartup, frame = now - lastFrame;
            lastFrame = now;
            frames++;
            if (frame > 0.1f) { longFrames.Add(new KeyValuePair<float, float>(now - frame - began, frame)); quietSince = -1f; }
            else if (quietSince < 0f) quietSince = now;
            bool settled = readyAt >= 0f && HighLogic.LoadedScene == to && quietSince >= 0f && now - quietSince > 2f
                           && (to != GameScenes.FLIGHT || (FlightGlobals.ready && FlightGlobals.ActiveVessel != null && !FlightGlobals.ActiveVessel.packed));
            if (settled) End("settled");
            else if (now - began > 30f) End("not settled after 30 s");
        }

        static void End(string how)
        {
            open = false;
            double ms = 1000.0 / Stopwatch.Frequency;
            float settledAt = quietSince >= 0f ? quietSince - began : (float)((Stopwatch.GetTimestamp() - beganTicks) * ms / 1000.0);
            var said = new StringBuilder("[Warm Start probe] " + from + " to " + to + ": " + how + " at " + settledAt.ToString("F2") + " s (the scene's ready event at "
                                         + (readyAt >= 0f ? readyAt.ToString("F2") + " s" : "never") + "), " + frames + " frames");
            said.Append("\n  frames over 0.1 s (from the ask, how long):");
            foreach (KeyValuePair<float, float> f in longFrames) said.Append(' ').Append(f.Key.ToString("F2")).Append('+').Append(f.Value.ToString("F2"));
            var rows = new List<Step>();
            foreach (Step s in steps.Values) if (s.calls > 0) rows.Add(s);
            foreach (KeyValuePair<string, Step> e in events) if (e.Value.calls > 0) rows.Add(e.Value);
            rows.Sort((x, y) => y.ticks.CompareTo(x.ticks));
            said.Append("\n  steps by their time (one inside another counted in both):");
            for (int i = 0; i < rows.Count && i < 40; i++)
            {
                Step s = rows[i];
                if (s.ticks * ms < 5 && i >= 10) break;
                said.Append("\n    ").Append(s.name).Append(s.library != null && s.library != "the game" ? " [" + s.library + "]" : "").Append(": ")
                    .Append((s.ticks * ms).ToString("F0")).Append(" ms in ").Append(s.calls).Append(s.calls == 1 ? " call" : " calls");
            }
            // (the longest single calls, and when each came: what ran in the long frames above)
            var single = new List<Step>();
            foreach (Step s in steps.Values) if (s.calls > 0 && s.most * ms >= 20) single.Add(s);
            single.Sort((x, y) => y.most.CompareTo(x.most));
            said.Append("\n  the longest single calls (from the ask, how long):");
            for (int i = 0; i < single.Count && i < 25; i++)
                said.Append("\n    ").Append(single[i].name).Append(single[i].library != null && single[i].library != "the game" ? " [" + single[i].library + "]" : "")
                    .Append(": ").Append((single[i].most * ms).ToString("F0")).Append(" ms at ").Append(single[i].mostAt.ToString("F2")).Append(" s");
            // (the mods' own Awake, Start and OnStart together, by library: what each costs a scene change)
            var byLibrary = new Dictionary<string, long>();
            foreach (Step s in steps.Values)
                if (s.calls > 0 && s.library != "the game") { byLibrary.TryGetValue(s.library, out long t); byLibrary[s.library] = t + s.ticks; }
            var libs = new List<KeyValuePair<string, long>>(byLibrary);
            libs.Sort((x, y) => y.Value.CompareTo(x.Value));
            said.Append("\n  each library's own Awake, Start, OnStart and OnLoad together:");
            for (int i = 0; i < libs.Count && i < 15; i++) said.Append(' ').Append(libs[i].Key).Append(' ').Append((libs[i].Value * ms).ToString("F0")).Append(" ms;");
            UnityEngine.Debug.Log(said.ToString());
        }

        static void Pre(MethodBase __originalMethod, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (steps.TryGetValue(__originalMethod, out Step s)) s.inside++;
        }

        static void Post(MethodBase __originalMethod, long __state)
        {
            long took = Stopwatch.GetTimestamp() - __state;
            if (!steps.TryGetValue(__originalMethod, out Step s)) return;
            s.inside--;
            if (!open) return;
            s.calls++;
            // (a step that calls itself is counted once, at the outermost call)
            if (s.inside == 0) s.ticks += took;
            if (took > s.most) { s.most = took; s.mostAt = Time.realtimeSinceStartup - began - (float)(took / (double)Stopwatch.Frequency); }
        }

        static void PreFire(object __instance, out long __state) { __state = Stopwatch.GetTimestamp(); }

        static void PostFire(object __instance, long __state)
        {
            if (!open) return;
            long took = Stopwatch.GetTimestamp() - __state;
            string named = eventName != null ? eventName.GetValue(__instance) as string : null;
            string name = "event " + (named ?? __instance.GetType().Name);
            if (!events.TryGetValue(name, out Step s)) events[name] = s = new Step { name = name, library = "the game" };
            s.calls++;
            s.ticks += took;
        }
    }
}
#endif
