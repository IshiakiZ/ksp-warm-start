using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace WarmStart
{
    /// <summary>
    /// The parts' drag cubes, kept across the times the game's record of them is thrown away.
    ///
    /// A part's drag cube says how much of it the air meets from each of six sides. The game works one out by taking six
    /// pictures of the part and measuring them: some thirty thousandths of a second a part, with frames drawn in between.
    /// It keeps them in a file of its own, PartDatabase.cfg, and looks there first. But ModuleManager deletes that file
    /// whenever anything about the mods has changed (a mod added, updated or taken out: a patch might have changed a part's
    /// shape), and the start that follows makes every cube of every part again: 12 seconds for 412 parts where this was
    /// measured, and minutes where there are thousands. Almost all of them come out exactly as they were, because almost
    /// all of the parts are exactly as they were.
    ///
    /// So each part's cubes are kept here with a mark of what the part was made from: its whole description as the game
    /// has it after every patch, the dates of its description's file and of its models, and which build of which library
    /// each of its modules comes from. Before the game compiles its parts (as a loader of its own in the game's list of
    /// loaders, after ModuleManager's and before the parts'), the cubes of every part whose mark is what it was are put
    /// back into the game's file, and the game finds them there. A part whose description, models or code changed has a
    /// different mark, and the game makes its cubes again, as it would have.
    ///
    /// None of the game's code is changed. And it checks itself: a few of the parts whose cubes could have been handed
    /// back are left to the game each time, and what the game makes is set beside what was kept. If one differs, the
    /// kept cubes are not to be trusted: everything kept is thrown away, the game's file with it, and the next start
    /// makes every cube again.
    ///
    /// What a mark cannot see: a module whose shape depends on something outside the part's own description (a setting
    /// of the mod's own, read while the part is made). The check is the guard against that, and the switch.
    /// </summary>
    static class Cubes
    {
        const string Kept = "cubes.txt";
        const int Sample = 4;
        static string Game => KSPUtil.ApplicationRootPath + "PartDatabase.cfg";
        static string Mine => Warm.Folder + Kept;

        public static string Report = "";
        /// <summary>The parts whose kept cubes were held back this start for the game to make again: url, and the block that was kept.</summary>
        static readonly Dictionary<string, string> held = new Dictionary<string, string>();
        /// <summary>The parts the game's file had before this start's compiling (so that what it has more afterwards was made in this start).</summary>
        static HashSet<string> before;
        /// <summary>
        /// The marks made before this start's compiling. A mark has to be made then and at no other time: the game takes a
        /// dozen values out of a part's description as it compiles it (its name, title, cost, what it says of itself), so the
        /// same part at the main menu reads otherwise than it did before. (Found the hard way: marks made at the main menu
        /// never matched the ones made before compiling, and nothing was ever handed back.)
        /// </summary>
        static Marker marks;
        static int gave, gone;
        static bool looked;

        sealed class Door : LoadingSystem
        {
            public bool Started;
            bool done;
            public override bool IsReady() => done;
            public override string ProgressTitle() => "Warm Start: the parts' drag cubes";
            public override float ProgressFraction() => done ? 1f : 0f;
            public override float LoadWeight() => 0.01f;
            public override void StartLoad()
            {
                Started = true;
                try { HandOver(); }
                catch (Exception ex) { Report = ""; Warm.Log("could not hand the parts' drag cubes back (" + ex.Message + "): the game makes what it is missing"); }
                done = true;
            }
        }

        static Door door;

        /// <summary>As the game begins: a loader of this mod's for the game's list of loaders, to stand just before the one that compiles the parts.</summary>
        public static void Begin()
        {
            LoadingScreen screen = LoadingScreen.Instance;
            if (screen == null || screen.loaders == null || door != null) return;
            door = new GameObject("Warm Start: drag cubes").AddComponent<Door>();
            Place();
        }

        /// <summary>
        /// Every frame while the game is still reading its files: this mod's loader is kept directly before the parts' loader.
        /// ModuleManager puts a loader of its own into the same list (it is the one that deletes the game's record of the
        /// cubes), and whichever of the two mods begins first, this one's has to come after that. The game goes through the
        /// list by number, so it is only rearranged while the game is still at its first entry.
        /// </summary>
        public static void Place()
        {
            if (door == null || door.Started) return;
            LoadingScreen screen = LoadingScreen.Instance;
            if (screen == null || screen.loaders == null || screen.loaders.Count == 0) return;
            System.Collections.Generic.List<LoadingSystem> list = screen.loaders;
            int parts = list.FindIndex(l => l is PartLoader), mine = list.IndexOf(door);
            if (parts < 0 || (mine >= 0 && mine == parts - 1)) return;
            LoadingSystem files = list.Find(l => l is GameDatabase);
            if (files == null || files.IsReady()) return;
            if (mine >= 0) list.RemoveAt(mine);
            list.Insert(list.FindIndex(l => l is PartLoader), door);
        }

        // ---- the files: the game's (a ConfigNode file, read here as the lines it is) and this mod's

        sealed class Record { public string Url, Key, Text; }

        /// <summary>The PART blocks of the game's file, in its order, and the block at its head (its version). Null where the file is not laid out as the game writes it.</summary>
        static List<Record> Blocks(string text, out string head)
        {
            head = null;
            var found = new List<Record>();
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length;)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0) { i++; continue; }
                if ((line != "PART" && line != "PART_DATABASE") || i + 1 >= lines.Length || lines[i + 1].TrimEnd('\r') != "{") return null;
                int end = i + 2;
                while (end < lines.Length && lines[end].TrimEnd('\r') != "}") end++;
                if (end >= lines.Length) return null;
                var block = new StringBuilder();
                string url = null;
                for (int k = i; k <= end; k++)
                {
                    string l = lines[k].TrimEnd('\r');
                    block.Append(l).Append('\n');
                    if (url == null && l.StartsWith("\turl = ", StringComparison.Ordinal)) url = l.Substring(7);
                }
                if (line == "PART_DATABASE") head = block.ToString();
                else if (url != null) found.Add(new Record { Url = url, Text = block.ToString() });
                i = end + 1;
            }
            return found;
        }

        static Dictionary<string, Record> Mined(out string gameWas, out string librariesWere)
        {
            gameWas = librariesWere = null;
            var kept = new Dictionary<string, Record>();
            if (!File.Exists(Mine)) return kept;
            Record now = null;
            StringBuilder block = null;
            foreach (string raw in File.ReadAllLines(Mine))
            {
                if (raw.StartsWith("#game ", StringComparison.Ordinal)) { gameWas = raw.Substring(6); continue; }
                if (raw.StartsWith("#libraries ", StringComparison.Ordinal)) { librariesWere = raw.Substring(11); continue; }
                if (raw.StartsWith("#part ", StringComparison.Ordinal))
                {
                    if (now != null) { now.Text = block.ToString(); kept[now.Url] = now; }
                    int tab = raw.IndexOf('\t');
                    if (tab < 0) { now = null; continue; }
                    now = new Record { Url = raw.Substring(6, tab - 6), Key = raw.Substring(tab + 1) };
                    block = new StringBuilder();
                    continue;
                }
                if (now != null) block.Append(raw).Append('\n');
            }
            if (now != null) { now.Text = block.ToString(); kept[now.Url] = now; }
            return kept;
        }

        static string Sum(string text)
        {
            using (MD5 md5 = MD5.Create()) return Hex(md5.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        static string Hex(byte[] bytes)
        {
            var s = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) s.Append(b.ToString("x2"));
            return s.ToString();
        }

        static void Replace(string path, string text)
        {
            string fresh = path + ".new";
            File.WriteAllText(fresh, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(fresh, path);
        }

        // ---- the mark of what a part is made from

        sealed class Marker
        {
            readonly Dictionary<string, UrlDir.UrlConfig> parts = new Dictionary<string, UrlDir.UrlConfig>();
            readonly Dictionary<string, string> models = new Dictionary<string, string>();
            readonly Dictionary<Assembly, string> builds = new Dictionary<Assembly, string>();
            readonly Dictionary<string, string> made = new Dictionary<string, string>();
            readonly StringBuilder words = new StringBuilder(8192);
            readonly MD5 md5 = MD5.Create();

            public Marker()
            {
                foreach (UrlDir.UrlConfig c in GameDatabase.Instance.GetConfigs("PART")) if (c != null && c.url != null && !parts.ContainsKey(c.url)) parts[c.url] = c;
                foreach (UrlDir.UrlFile f in GameDatabase.Instance.databaseModelFiles) if (f != null && f.url != null) models[f.url] = f.fileTime.Ticks.ToString(CultureInfo.InvariantCulture);
            }

            /// <summary>Every part there is, by its address.</summary>
            public IEnumerable<string> All => parts.Keys;

            public bool Has(string url) => parts.ContainsKey(url);

            /// <summary>The mark that was made of the part of that address, if one was (nothing is worked out).</summary>
            public string Made(string url) => made.TryGetValue(url, out string key) ? key : null;

            /// <summary>The mark of the part of that address as it is now, or null where there is no such part.</summary>
            public string Of(string url)
            {
                if (made.TryGetValue(url, out string key)) return key;
                if (!parts.TryGetValue(url, out UrlDir.UrlConfig c)) return made[url] = null;
                words.Length = 0;
                Walk(c.config);
                words.Append("|cfg ").Append(c.parent.fileTime.Ticks);
                // (the models: a part that names none takes the one beside its description, so every model in its folder counts)
                UrlDir dir = c.parent.parent;
                if (dir != null)
                    foreach (UrlDir.UrlFile f in dir.files)
                        if (f.fileType == UrlDir.FileType.Model) words.Append("|m ").Append(f.url).Append(' ').Append(f.fileTime.Ticks);
                foreach (ConfigNode model in c.config.GetNodes("MODEL"))
                {
                    string named = model.GetValue("model");
                    if (named != null) words.Append("|M ").Append(named).Append(' ').Append(models.TryGetValue(named, out string when) ? when : "?");
                }
                // (the code: which build of which library the part's class and each of its modules come from, with what they are built on)
                Code(typeof(Part), c.config.GetValue("module"));
                foreach (ConfigNode module in c.config.GetNodes("MODULE")) Code(typeof(PartModule), module.GetValue("name"));
                return made[url] = Hex(md5.ComputeHash(Encoding.UTF8.GetBytes(words.ToString())));
            }

            void Walk(ConfigNode node)
            {
                words.Append(node.name).Append('{');
                for (int i = 0, n = node.values.Count; i < n; i++) { ConfigNode.Value v = node.values[i]; words.Append(v.name).Append('=').Append(v.value).Append('\n'); }
                for (int i = 0, n = node.nodes.Count; i < n; i++) Walk(node.nodes[i]);
                words.Append('}');
            }

            void Code(Type root, string name)
            {
                if (string.IsNullOrEmpty(name)) return;
                Type t = null;
                try { t = AssemblyLoader.GetClassByName(root, name); } catch (Exception) { }
                if (t == null) { words.Append("|c ").Append(name).Append(" ?"); return; }
                for (; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    if (!builds.TryGetValue(t.Assembly, out string build))
                    {
                        try { build = t.Assembly.ManifestModule.ModuleVersionId.ToString("N"); } catch (Exception) { build = t.Assembly.FullName; }
                        builds[t.Assembly] = build;
                    }
                    words.Append("|c ").Append(build);
                }
            }
        }

        // ---- before the game compiles its parts

        /// <summary>The first line of this mod's file: what the game's record summed to when the cubes were last kept.</summary>
        static string GameWas()
        {
            try
            {
                if (!File.Exists(Mine)) return null;
                using (var r = new StreamReader(Mine))
                {
                    string first = r.ReadLine();
                    return first != null && first.StartsWith("#game ", StringComparison.Ordinal) ? first.Substring(6) : null;
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>Every library the game has loaded, as one sum: so that it can be told whether any has come, gone or been built anew since the cubes were kept.</summary>
        static string Libraries()
        {
            var all = new List<string>();
            foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
            {
                if (a == null || a.assembly == null) continue;
                try { all.Add(a.assembly.ManifestModule.ModuleVersionId.ToString("N")); } catch (Exception) { all.Add(a.name ?? "?"); }
            }
            all.Sort(StringComparer.Ordinal);
            return Sum(string.Join(" ", all.ToArray()));
        }

        static string Parts(int n) => n == 1 ? "1 part" : n + " parts";

        static void HandOver()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            looked = true;
            gave = gone = 0;
            held.Clear();
            marks = null;
            before = null;
            string text = File.Exists(Game) ? File.ReadAllText(Game) : null;
            // (the common case first, and cheaply: the game's record is what it was when the cubes were last kept)
            string gameWas = GameWas();
            if (text != null && gameWas != null && Sum(text) == gameWas) { Report = "The game's record of its parts' drag cubes is as it was left: nothing to hand back."; return; }
            string head = null;
            List<Record> theirs = text == null ? new List<Record>() : Blocks(text, out head);
            if (theirs == null) { Report = "The game's record of its parts' drag cubes is not laid out as the game writes it: left alone."; Warm.Log(Report); return; }
            // (a record made by another version of the game is one the game throws away itself: so is what is kept from it)
            string version = "\tversion = " + Versioning.VersionString;
            bool theirsGood = head != null && head.Contains(version + "\n");
            if (!theirsGood) { theirs.Clear(); head = "PART_DATABASE\n{\n" + version + "\n}\n"; }
            before = new HashSet<string>();
            foreach (Record r in theirs) before.Add(r.Url);
            Dictionary<string, Record> kept = Mined(out gameWas, out string librariesWere);
            // (Every part whose cubes will be kept anew at the main menu is marked now, as it is before the game compiles it:
            // one that is not kept yet, and one of which the game has other cubes than are kept.)
            marks = new Marker();
            foreach (string url in new List<string>(marks.All)) if (!kept.ContainsKey(url)) marks.Of(url);
            foreach (Record r in theirs) if (kept.TryGetValue(r.Url, out Record mine) && mine.Text != r.Text) marks.Of(r.Url);
            if (kept.Count == 0) { Report = "No part's drag cubes are kept yet: they are, from this start on."; Warm.Log(Report); return; }
            // (what the game is missing that is kept, and whose mark is what it was)
            var back = new List<Record>();
            foreach (Record r in kept.Values)
            {
                if (before.Contains(r.Url)) continue;
                string now = marks.Of(r.Url);
                if (now == null) continue;                      // (a part that is no longer there)
                if (now == r.Key) back.Add(r); else gone++;
            }
            if (back.Count == 0)
            {
                Report = gone > 0 ? "The game makes the drag cubes of " + Parts(gone) + " again: what was kept of them is of parts that have changed since." : "The game has every part's drag cubes that is kept here: nothing to hand back.";
                Warm.Log(Report);
                return;
            }
            // (A few are left to the game, to set what it makes beside what was kept; three times as many where a library has
            // come, gone or been built anew since: a library that changes how every cube is made is not in any part's mark.)
            int sample = Libraries() == librariesWere ? Sample : Sample * 3;
            var random = new System.Random();
            for (int n = 0; n < sample && back.Count > sample * 4; n++)
            {
                int pick = random.Next(back.Count);
                held[back[pick].Url] = back[pick].Text;
                back.RemoveAt(pick);
            }
            var whole = new StringBuilder(head, (text != null ? text.Length : 0) + back.Count * 400);
            foreach (Record r in theirs) whole.Append(r.Text);
            foreach (Record r in back) whole.Append(r.Text);
            Replace(Game, whole.ToString());
            gave = back.Count;
            Report = "The drag cubes of " + Parts(gave) + " were handed back to the game, which had thrown its record of them away" + (gone > 0 ? "; " + Parts(gone) + " had changed, and the game makes those again" : "")
                   + (held.Count > 0 ? "; " + held.Count + " more were left for the game to make, to check what is kept" : "") + ".";
            Warm.Log(Report + " (" + watch.ElapsedMilliseconds + " ms)");
        }

        // ---- at the main menu: what the game has now is kept, and the check is made

        public static void Keep()
        {
            if (!looked || !File.Exists(Game)) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string text = File.ReadAllText(Game);
            List<Record> theirs = Blocks(text, out string head);
            if (theirs == null || head == null) return;
            // (the check: what the game made of the parts that were held back, beside what was kept of them)
            int same = 0;
            string differs = null;
            if (held.Count > 0)
            {
                var now = new Dictionary<string, string>();
                foreach (Record r in theirs) now[r.Url] = r.Text;
                foreach (KeyValuePair<string, string> h in held)
                {
                    if (!now.TryGetValue(h.Key, out string made)) continue;        // (a part the game did not compile after all)
                    if (Alike(h.Value, made)) same++; else if (differs == null) differs = h.Key;
                }
            }
            if (differs != null)
            {
                // (what is kept cannot be trusted: it goes, and so does the game's record, which has kept cubes in it now)
                try { File.Delete(Mine); File.Delete(Game); } catch (Exception) { }
                Report = "The check failed: the game made other drag cubes for " + differs + " than were kept for it. Everything kept was thrown away, and the game's own record with it: the next start makes every part's cubes again.";
                Warm.Log(Report);
                held.Clear();
                return;
            }
            string sum = Sum(text);
            if (sum != GameWas())
            {
                // (A part takes the mark that was made of it before this start's compiling, where one was: it was either not kept,
                // or was missing from the game's record, or the game had other cubes of it than were kept. Any other keeps the
                // mark it had, its cubes being what they were. A part with neither is left out, and the file says that it is not
                // whole: the next start marks what is missing.)
                Dictionary<string, Record> kept = Mined(out string gameWas, out string librariesWere);
                var all = new StringBuilder(text.Length + theirs.Count * 48);
                int marked = 0, unmarked = 0;
                foreach (Record r in theirs)
                {
                    if (r.Text.IndexOf("\tDRAG_CUBE\n", StringComparison.Ordinal) < 0) continue;
                    string key = marks != null ? marks.Made(r.Url) : null;
                    if (key == null && kept.TryGetValue(r.Url, out Record old) && old.Text == r.Text) key = old.Key;
                    // (a record of a part that is no longer in the game is not kept, and is not waited for)
                    if (key == null) { if (marks == null || marks.Has(r.Url)) unmarked++; continue; }
                    all.Append("#part ").Append(r.Url).Append('\t').Append(key).Append('\n').Append(r.Text);
                    marked++;
                }
                Directory.CreateDirectory(Warm.Folder);
                Replace(Mine, "#game " + (unmarked > 0 ? "not whole" : sum) + "\n#libraries " + Libraries() + "\n" + all);
                Warm.Log("the drag cubes of " + Parts(marked) + " kept, each with the mark of what the part is made from" + (unmarked > 0 ? "; " + unmarked + " more at the next start" : "") + " (" + watch.ElapsedMilliseconds + " ms)");
            }
            marks = null;
            if (held.Count > 0)
            {
                Report += " The check: the game made the cubes of " + same + " of them again, and they came out as they were kept.";
                Warm.Log("the check: the drag cubes of " + Parts(same) + ", made again by the game, came out as they were kept");
            }
            held.Clear();
        }

        /// <summary>
        /// Whether two blocks of the game's record say the same cubes: the same lines, and each number within a fiftieth of its
        /// size (or of one). Not to the last figure: the game itself, making every cube twice over in two starts with nothing
        /// changed, gave 8 lines in 600 that differed, all of parts that move (a number of 0.5992 against 0.6023 was the most:
        /// the pictures are taken at a moment of the part's own animation). A part made a tenth larger differs by a fifth.
        /// </summary>
        static bool Alike(string a, string b)
        {
            if (a == b) return true;
            string[] one = a.Split('\n'), two = b.Split('\n');
            if (one.Length != two.Length) return false;
            for (int i = 0; i < one.Length; i++)
            {
                if (one[i] == two[i]) continue;
                string[] p = one[i].Split(new[] { ',', ' ', '\t', '=' }, StringSplitOptions.RemoveEmptyEntries), q = two[i].Split(new[] { ',', ' ', '\t', '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != q.Length) return false;
                for (int k = 0; k < p.Length; k++)
                {
                    if (p[k] == q[k]) continue;
                    if (!double.TryParse(p[k], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || !double.TryParse(q[k], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) return false;
                    if (Math.Abs(x - y) > 0.02 * Math.Max(1.0, Math.Max(Math.Abs(x), Math.Abs(y)))) return false;
                }
            }
            return true;
        }
    }
}
