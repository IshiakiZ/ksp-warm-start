using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WarmStart
{
    /// <summary>
    /// Where a start goes, mod by mod. With a few mods a start is the game's own work, and what can be kept of it is kept
    /// (Textures, Bundles, Definitions). With hundreds, most of it is the mods' own files and parts, one after another on
    /// the game's one thread, and nothing kept shortens that: but the player can be told which mods the seconds went to,
    /// which is the one thing that lets them shorten it themselves.
    ///
    /// The game says in its log what it is beginning on, a line for every part, model, picture and sound, each with the
    /// folder it comes from. Those lines are listened to as they are written (nothing of the game is changed), each with
    /// the moment it came; a thing took until the next thing was begun. Added up by the first folder of the path, which
    /// is the mod.
    ///
    /// What it cannot see: time a mod's own program spends starting up; ModuleManager's patching (its own log says);
    /// pictures that were handed over from what is kept (the game never begins on those: they are counted together, as
    /// this mod's own work); and, where another mod loads the game its own way, whatever that mod does not say.
    /// </summary>
    static class Where
    {
        struct Mark { public float At; public int Kind; public string Mod; }
        const int Parts = 0, Pictures = 1, Models = 2, Sounds = 3, Other = -1;
        static readonly List<Mark> marks = new List<Mark>(8192);
        static bool listening;
        /// <summary>What was found, in words for the page: empty until the main menu is there.</summary>
        public static string Report = "";

        public static void Begin()
        {
            if (listening) return;
            listening = true;
            Application.logMessageReceived += Heard;
        }

        static void Heard(string text, string stack, LogType type)
        {
            if (type != LogType.Log || text == null || text.Length < 13) return;
            int kind, from;
            if (text.StartsWith("PartLoader: Compiling Part '", StringComparison.Ordinal)) { kind = Parts; from = 28; }
            else if (text.StartsWith("Load(Texture): ", StringComparison.Ordinal)) { kind = Pictures; from = 15; }
            else if (text.StartsWith("Load(Model): ", StringComparison.Ordinal)) { kind = Models; from = 13; }
            else if (text.StartsWith("Load(Audio): ", StringComparison.Ordinal)) { kind = Sounds; from = 13; }
            else
            {
                // (a line that says the game has gone on to something else ends the last thing's time; any other line, a
                // part's own complaint among them, is that thing's)
                bool on = text.StartsWith("PartLoader: Compiling Internal", StringComparison.Ordinal) || text.StartsWith("GameDatabase: Assets loaded", StringComparison.Ordinal)
                       || text.StartsWith("Config(", StringComparison.Ordinal) || text.StartsWith("Compiling Configs", StringComparison.Ordinal) || text.StartsWith("[ModuleManager]", StringComparison.Ordinal)
                       || text.StartsWith("Expansions loaded", StringComparison.Ordinal);
                if (on && marks.Count > 0 && marks[marks.Count - 1].Kind != Other) marks.Add(new Mark { At = Time.realtimeSinceStartup, Kind = Other });
                return;
            }
            int slash = text.IndexOf('/', from);
            marks.Add(new Mark { At = Time.realtimeSinceStartup, Kind = kind, Mod = slash > from ? text.Substring(from, slash - from) : "(loose in GameData)" });
        }

        /// <summary>
        /// This mod is about to do work of its own between two of the game's files (handing over what is kept): the thing
        /// the game last began on is not to be charged with it. (As it first was, the first sound of a start was charged
        /// 2.8 seconds: the whole handing over of the textures, which comes after it.)
        /// </summary>
        public static void Aside()
        {
            if (listening && marks.Count > 0 && marks[marks.Count - 1].Kind != Other) marks.Add(new Mark { At = Time.realtimeSinceStartup, Kind = Other });
        }

        /// <summary>At the main menu: the listening ends and the sums are made.</summary>
        public static void End()
        {
            if (!listening) return;
            listening = false;
            Application.logMessageReceived -= Heard;
            if (marks.Count > 0 && marks[marks.Count - 1].Kind != Other) marks.Add(new Mark { At = Time.realtimeSinceStartup, Kind = Other });
            var by = new Dictionary<string, float[]>();
            var whole = new float[4];
            var counted = new int[4];
            for (int n = 0; n + 1 < marks.Count; n++)
            {
                Mark m = marks[n];
                if (m.Kind == Other) continue;
                float took = Mathf.Max(marks[n + 1].At - m.At, 0f);
                whole[m.Kind] += took; counted[m.Kind]++;
                if (!by.TryGetValue(m.Mod, out float[] its)) by[m.Mod] = its = new float[4];
                its[m.Kind] += took;
            }
            marks.Clear(); marks.TrimExcess();
            if (counted[Parts] + counted[Pictures] + counted[Models] + counted[Sounds] == 0) { Report = ""; return; }
            var names = new List<string>(by.Keys);
            names.Sort((a, b) => Sum(by[b]).CompareTo(Sum(by[a])));
            var said = new StringBuilder("Where this start went, by what the game says it is beginning on: ");
            said.Append(Said(whole[Parts])).Append(" s on ").Append(counted[Parts]).Append(" parts, ")
                .Append(Said(whole[Models])).Append(" on ").Append(counted[Models]).Append(" models, ")
                .Append(Said(whole[Pictures])).Append(" on ").Append(counted[Pictures]).Append(" pictures the game loaded itself, ")
                .Append(Said(whole[Sounds])).Append(" on ").Append(counted[Sounds]).Append(" sounds.");
            if (names.Count > 1)
            {
                said.Append("\nBy mod: ");
                float rest = 0f;
                for (int n = 0; n < names.Count; n++)
                {
                    float[] its = by[names[n]];
                    if (n >= 8) { rest += Sum(its); continue; }
                    if (n > 0) said.Append("; ");
                    said.Append(names[n]).Append(' ').Append(Said(Sum(its))).Append(" s");
                    // (what most of it was, where one kind is more than half)
                    int most = 0;
                    for (int k = 1; k < 4; k++) if (its[k] > its[most]) most = k;
                    if (its[most] > 0.5f * Sum(its) && Sum(its) >= 0.5f) said.Append(most == Parts ? " (parts)" : most == Models ? " (models)" : most == Pictures ? " (pictures)" : " (sounds)");
                }
                if (names.Count > 8) said.Append("; ").Append(names.Count - 8).Append(" more ").Append(Said(rest)).Append(" s");
                said.Append('.');
            }
            Report = said.ToString();
            Warm.Log(Report.Replace("\n", " "));
        }

        static float Sum(float[] v) => v[0] + v[1] + v[2] + v[3];
        static string Said(float seconds) => seconds.ToString(seconds < 10f ? "F1" : "F0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
