using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using KSP.Localization;

namespace WarmStartHarmony
{
    /// <summary>
    /// (Since 0.5.0.) The game's words looked up once: what the localiser makes of a tag, kept and given again.
    ///
    /// Measured (2026-10-19, the probe build, this install's 474 parts): while the parts are compiled the game asks its
    /// localiser for words 436,229 times with nothing to put into them, and some 3,700 times with numbers or names to put in.
    /// Every field, event and action of every part module has its name looked up as it is made (some 220 a module), and each
    /// ask looks the tag up, unescapes the answer into a new piece of text and, with something to put in, hands it to the
    /// grammar library (48 thousandths of a millisecond each): the same few thousand words again and again.
    ///
    /// Here, while the game loads (until its main menu, when everything kept is let go: in play the localiser answers for
    /// itself, as ever), on the game's own thread, each answer is kept by what was asked (the tag, and what was put into it)
    /// and given again; while the language stays what it was, and while the localiser is neither showing its keys nor logging
    /// the ones it lacks. It checks itself: one answer in 256 is asked of the localiser as well and the two compared, and at
    /// the first difference everything kept is thrown away and the localiser answers for itself from then on.
    ///
    /// Measured (2026-10-20, the probe build, 474 parts, a start with it and one without): the localiser's asks with something
    /// put in 154 ms without, 70 with; every module's description for the editor 214 ms without, 144 with; the plain asks 155
    /// and 130-139 (most of their time is the asking itself). 374,556 of 430,734 asks answered from what was kept, 54,689 kept,
    /// 1,489 checked and all the same. A tenth of a second a start here, and more with more parts.
    /// </summary>
    static class Words
    {
        const string Id = "warmstart.words";
        const int Most = 100000;
        static readonly Dictionary<string, string> kept = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly StringBuilder key = new StringBuilder(256);
        static int mainThread = -1;
        static string language;
        static bool broken, over;
        static string why = "";
        static long asked, given, checkedTimes;
        static int held;
        static Func<bool> missingLogged, keysShown;

        /// <summary>What a prefix hands its postfix: the key the answer is to be kept under, and what was kept for it if this one is a check.</summary>
        struct Ask { public string Key, Kept; }

        public static void Begin()
        {
            if (!WarmStart.Companion.WantsWords) return;
            mainThread = Thread.CurrentThread.ManagedThreadId;
            MethodInfo logging = AccessTools.PropertyGetter(typeof(Localizer), "debugWriteMissingKeysToLog");
            if (logging != null) missingLogged = AccessTools.MethodDelegate<Func<bool>>(logging);
            MethodInfo keys = AccessTools.PropertyGetter(typeof(Localizer), "ShowKeysOnScreen");
            if (keys == null || logging == null) { WarmStart.Companion.Log("words looked up once left out: the localiser is not as expected"); return; }
            keysShown = AccessTools.MethodDelegate<Func<bool>>(keys);
            var h = new Harmony(Id);
            try { h.UnpatchAll(Id); } catch (Exception) { }
            Type me = typeof(Words);
            const BindingFlags mine = BindingFlags.Static | BindingFlags.NonPublic;
            var said = new HarmonyMethod(me.GetMethod(nameof(Said), mine));
            h.Patch(AccessTools.Method(typeof(Localizer), nameof(Localizer.Format), new[] { typeof(string) }), new HarmonyMethod(me.GetMethod(nameof(Plain), mine)), said);
            h.Patch(AccessTools.Method(typeof(Localizer), nameof(Localizer.Format), new[] { typeof(string), typeof(string[]) }), new HarmonyMethod(me.GetMethod(nameof(WithWords), mine)), said);
            h.Patch(AccessTools.Method(typeof(Localizer), nameof(Localizer.Format), new[] { typeof(string), typeof(object[]) }), new HarmonyMethod(me.GetMethod(nameof(WithThings), mine)), said);
            WarmStart.Companion.WordsReport = Report;
            WarmStart.Companion.Log("words looked up once are in: what the localiser makes of a tag is kept and given again");
        }

        static string Report()
        {
            if (broken) return "Words looked up once were put away this session: " + why + " The localiser answers for itself, as it always has.";
            return "Words looked up once: " + given.ToString("N0") + " of " + asked.ToString("N0") + " asks answered from what was kept while the game loaded (" + (over ? held : kept.Count).ToString("N0") + " kept" + (over ? ", let go at the main menu" : "") + "); "
                + checkedTimes.ToString("N0") + " answers checked against the localiser's own, and all the same.";
        }

        /// <summary>Whether an ask can be answered from what is kept: the game's thread, nothing amiss, the localiser as it was.</summary>
        static bool Usable()
        {
            if (over || broken || Thread.CurrentThread.ManagedThreadId != mainThread || Localizer.Instance == null || keysShown() || missingLogged()) return false;
            // (only while the game loads: at its main menu everything kept is let go)
            if (HighLogic.LoadedScene != GameScenes.LOADING) { over = true; held = kept.Count; kept.Clear(); return false; }
            string now = Localizer.CurrentLanguage;
            if (!ReferenceEquals(now, language) && now != language) { kept.Clear(); language = now; }
            return true;
        }

        /// <summary>The kept answer, or the localiser's own (and then kept), or one in 256 asked again and compared.</summary>
        static bool Answer(string k, ref string __result, out Ask __state)
        {
            __state = default;
            asked++;
            if (kept.TryGetValue(k, out string had))
            {
                if ((asked & 255) != 0) { __result = had; given++; return false; }
                __state = new Ask { Key = k, Kept = had };
                return true;
            }
            __state = new Ask { Key = k };
            return true;
        }

        static bool Plain(string template, ref string __result, out Ask __state)
        {
            __state = default;
            if (template == null || !Usable()) return true;
            return Answer(template, ref __result, out __state);
        }

        static bool WithWords(string template, string[] list, ref string __result, out Ask __state)
        {
            __state = default;
            if (template == null || list == null || !Usable()) return true;
            key.Length = 0;
            key.Append(template);
            foreach (string s in list) { key.Append('\u0001'); if (s == null) key.Append('\u0002'); else key.Append(s); }
            return Answer(key.ToString(), ref __result, out __state);
        }

        static bool WithThings(string template, object[] list, ref string __result, out Ask __state)
        {
            __state = default;
            if (template == null || list == null || !Usable()) return true;
            key.Length = 0;
            key.Append(template).Append('\u0003');
            foreach (object o in list)
            {
                // (a thing that is not a plain number or word is left to the localiser every time: what it says of itself may change)
                if (o == null || !(o is string || o is int || o is float || o is double || o is long || o is bool)) return true;
                key.Append('\u0001').Append(o is string s ? s : Convert.ToString(o, System.Globalization.CultureInfo.CurrentCulture));
            }
            return Answer(key.ToString(), ref __result, out __state);
        }

        /// <summary>After the localiser has answered: its answer kept, or set beside what was kept.</summary>
        static void Said(string __result, Ask __state)
        {
            if (__state.Key == null || broken) return;
            if (__state.Kept != null)
            {
                checkedTimes++;
                if (__state.Kept == __result) return;
                broken = true;
                why = "for \"" + __state.Key.Replace('\u0001', '|').Replace('\u0003', '|') + "\" the kept answer was \"" + __state.Kept + "\" and the localiser's own \"" + __result + "\".";
                kept.Clear();
                WarmStart.Companion.Log("WORDS LOOKED UP ONCE PUT AWAY: " + why + " The localiser answers for itself from here on. Please tell the mod's author.");
                return;
            }
            if (__result != null && kept.Count < Most) kept[__state.Key] = __result;
        }
    }
}
