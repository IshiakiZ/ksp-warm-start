using System;
using System.Reflection;
using HarmonyLib;
#if WSPROBE
using System.Collections.Generic;
using System.Linq;
#endif

namespace WarmStartHarmony
{
    /// <summary>
    /// (Since 0.5.0, with the look-ups by table.) The game asking for its flight's globals while it loads, where there are none:
    /// "none" at once, not a search through everything loaded.
    ///
    /// Measured (2026-10-20, the probe build timing every call the part compiler makes where it makes it, on a made-up install of
    /// 4,755 parts and 3,396 models): every part the game compiles asks three times whether a flight is going on
    /// (FlightGlobals.fetch: once in PartLoader.ParsePart, twice in the loop around it, CompileParts), and while there is none
    /// the game looks for one through every object it has loaded (FindObjectOfType), each search longer than the last as the
    /// models and parts pile up: ParsePart's own ask 0.34 ms a part in the first 500 parts, 2.46 ms in the last. With the asks
    /// answered at once the parts took 14 s of that start where they had taken 37 (the main menu after 52.6 s, not 75.2), the
    /// last parts no slower than the first; here (474 parts, 642 models) some 0.2 s.
    ///
    /// Only on the loading screen, where there is no flight to find: at the first scene after it (the main menu) the game's own
    /// getter is put back as it was. Checked: one ask in 64 the game's own search is run as well, and if it ever finds a flight
    /// there, the game searches for itself from then on.
    /// </summary>
    static class NoFlightYet
    {
        static FieldInfo kept;
        static Harmony harmony;
        static MethodInfo getter, ask, answered;
        static int asked, checkedTimes;
        static bool broken, patched, back, putBack;
#if WSPROBE
        // (the probe build: who asks, every eighth ask's callers counted)
        static readonly Dictionary<string, int> callers = new Dictionary<string, int>();
#endif

        public static void Begin(Harmony h)
        {
            if (HighLogic.LoadedScene != GameScenes.LOADING) return;
            kept = AccessTools.Field(typeof(FlightGlobals), "_fetch");
            getter = AccessTools.PropertyGetter(typeof(FlightGlobals), "fetch");
            if (kept == null || !kept.IsStatic || getter == null) return;
            ask = typeof(NoFlightYet).GetMethod(nameof(Ask), BindingFlags.Static | BindingFlags.NonPublic);
            answered = typeof(NoFlightYet).GetMethod(nameof(Answered), BindingFlags.Static | BindingFlags.NonPublic);
            h.Patch(getter, new HarmonyMethod(ask), new HarmonyMethod(answered));
            harmony = h;
            patched = true;
            GameEvents.onLevelWasLoaded.Add(new Menu().Heard);
        }

        // (the game's event lists take a delegate's Target: a static method has none, so this small object hears the scene)
        sealed class Menu { public void Heard(GameScenes scene) { Loaded(scene); } }

        /// <summary>At the first scene after the loading screen: the game's own getter as it was, nothing of this mod's in it in play.</summary>
        static void Loaded(GameScenes scene)
        {
            if (back || scene == GameScenes.LOADING) return;
            back = true;
            try
            {
                harmony.Unpatch(getter, ask);
                harmony.Unpatch(getter, answered);
                Patches left = Harmony.GetPatchInfo(getter);
                if (left != null) foreach (Patch one in left.Prefixes) if (one.PatchMethod == ask) throw new Exception("it is still there");
                putBack = true;
            }
            // (left in, it lets the game answer for itself after its first line, as it does wherever the game is not loading)
            catch (Exception ex) { WarmStart.Companion.Log("the asks for a flight could not be taken out of the game's getter at the main menu (" + ex.Message + "); the game answers them itself, after one line of this mod's"); }
        }

        static bool Ask(ref FlightGlobals __result, out bool __state)
        {
            __state = false;
            // (only while the game loads, and only while it has no flight's globals kept: once it has, its own answer is a field read)
            if (broken || HighLogic.LoadedScene != GameScenes.LOADING || kept.GetValue(null) != null) return true;
            asked++;
#if WSPROBE
            if ((asked & 7) == 0)
            {
                var st = new System.Diagnostics.StackTrace(1, false);
                var who = new System.Text.StringBuilder();
                for (int i = 0; i < st.FrameCount && i < 6; i++)
                {
                    MethodBase m = st.GetFrame(i).GetMethod();
                    if (m == null) continue;
                    if (who.Length > 0) who.Append(" < ");
                    who.Append(m.DeclaringType != null ? m.DeclaringType.Name : "?").Append('.').Append(m.Name);
                }
                string k = who.ToString();
                callers.TryGetValue(k, out int n);
                callers[k] = n + 1;
            }
#endif
            if ((asked & 63) == 0) { __state = true; return true; }
            __result = null;
            return false;
        }

        static void Answered(FlightGlobals __result, bool __state)
        {
            if (!__state) return;
            checkedTimes++;
            if (__result == null) return;
            broken = true;
            WarmStart.Companion.Log("THE ASKS FOR A FLIGHT WHILE LOADING PUT AWAY: the game found its flight's globals on the loading screen; it searches for them itself from here on. Please tell the mod's author.");
        }

        /// <summary>A line for the look-ups' report: empty where nothing was asked.</summary>
        public static string Said()
        {
            if (!patched || asked == 0) return "";
            if (broken) return " The asks for a flight while loading were put away: the game found one.";
            return " " + asked.ToString("N0") + " asks for a flight while loading answered at once (" + checkedTimes.ToString("N0") + " checked against the game's own search, which found none)"
                + (putBack ? "; from the main menu on, the game answers them itself." : ".")
#if WSPROBE
                + "\n  [Warm Start probe] who asked for a flight while loading (every eighth ask, by its callers):\n    "
                + string.Join("\n    ", callers.OrderByDescending(c => c.Value).Take(12).Select(c => c.Value + " x " + c.Key))
#endif
                ;
        }
    }
}
