using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

#if !DEV
[assembly: KSPAssembly("WarmStartHarmony", 0, 1)]
[assembly: KSPAssemblyDependency("WarmStart", 0, 1)]
[assembly: KSPAssemblyDependency("HarmonyKSP", 1, 0)]
#endif

namespace WarmStartHarmony
{
    /// <summary>
    /// Warm Start's companion for the Harmony mod: the game's look-ups done by a table instead of a walk. The game loads
    /// this library only where Harmony (HarmonyKSP) is installed; Warm Start itself never needs it, and changes none of the
    /// game's code. This does: it is the one part of Warm Start that puts code of its own in place of the game's.
    ///
    /// The game finds a model, a sound or a picture by its name by going through the whole list of them and comparing
    /// names (for a model and a sound, asking Unity for each name on the way, which makes a new piece of text every time):
    /// 177 millionths of a second for the last of 642 models where this was measured. A part's record in the game's own
    /// part database is found the same way. Every part asks several times as it is compiled, so the cost grows with the
    /// square of an install's size: nothing with the game alone, most of a minute at every start with thousands of parts
    /// and models. Here each list has a table beside it, name to place, and the answer is what the walk would have given:
    /// the first of that name.
    ///
    /// Kept true: a table is good only for the very list it was made from, as that list was (the list's own count of the
    /// changes made to it says so); where things were only added at the end, they are added to the table; anything else
    /// and the table is made again. A hit is looked at before it is given (is that still its name?). And it checks
    /// itself: one look-up in 256 the game's own walk is run as well and the two answers compared. One difference and
    /// every table is put away for the rest of the session, the game doing its own look-ups as it always has; the mod's
    /// page says so.
    ///
    /// What it cannot see: something already in a list being given another name without the list being touched. Nothing
    /// in the game does that; a mod that did would be found by the check sooner or later, not at once.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class AtStart : MonoBehaviour
    {
        void Awake()
        {
            try { Tables.Begin(); }
            catch (Exception ex) { WarmStart.Companion.Log("the look-ups by table could not be put in (" + ex.Message + "): the game does its own"); }
            try { Icons.Begin(); }
            catch (Exception ex) { WarmStart.Companion.Log("the icons made without waking could not be put in (" + ex.Message + "): the game makes its own"); }
            try { Words.Begin(); }
            catch (Exception ex) { WarmStart.Companion.Log("the words looked up once could not be put in (" + ex.Message + "): the localiser answers for itself"); }
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// A list's table: each name to the place of the first thing of that name in the list. One look-up at a time: the game
    /// asks from its own thread, but nothing stops a mod asking for a picture by name from another.
    /// </summary>
    sealed class Table<T> where T : class
    {
        readonly Dictionary<string, int> first;
        readonly Dictionary<string, int> folders;
        readonly Func<T, string> named;
        readonly AccessTools.FieldRef<List<T>, int> changes = AccessTools.FieldRefAccess<List<T>, int>("_version");
        List<T> list;
        int version, count;
        T last;
        public int Made;

        public Table(Func<T, string> named, bool anyCase, bool withFolders)
        {
            this.named = named;
            first = new Dictionary<string, int>(anyCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            folders = withFolders ? new Dictionary<string, int>(StringComparer.Ordinal) : null;
        }

        void Know(List<T> now)
        {
            int v = changes(now);
            if (ReferenceEquals(now, list) && v == version) return;
            // (only added to at the end since: as many changes as it is longer by, and what was last is still where it was)
            if (ReferenceEquals(now, list) && now.Count > count && v - version == now.Count - count && (count == 0 || ReferenceEquals(now[count - 1], last)))
            {
                for (int i = count; i < now.Count; i++) Put(now[i], i);
            }
            else
            {
                first.Clear();
                if (folders != null) folders.Clear();
                for (int i = 0; i < now.Count; i++) Put(now[i], i);
                Made++;
            }
            list = now; version = v; count = now.Count; last = count > 0 ? now[count - 1] : null;
        }

        void Put(T thing, int at)
        {
            string name = named(thing);
            if (name == null) return;
            if (!first.ContainsKey(name)) first[name] = at;
            if (folders == null) return;
            int cut = name.LastIndexOf('/');
            if (cut < 0) return;
            string folder = name.Substring(0, cut);
            if (!folders.ContainsKey(folder)) folders[folder] = at;
        }

        /// <summary>Forget everything: the next look-up makes the table again.</summary>
        public void Forget() { lock (first) list = null; }

        /// <summary>The place of the first thing of that name, or -1.</summary>
        public int Find(List<T> now, string name)
        {
            if (name == null || now == null) return -1;
            lock (first)
            {
                Know(now);
                return first.TryGetValue(name, out int at) ? at : -1;
            }
        }

        /// <summary>The place of the first thing in that folder (its name up to the last stroke), or -1.</summary>
        public int FindIn(List<T> now, string folder)
        {
            if (folder == null || now == null) return -1;
            lock (first)
            {
                Know(now);
                return folders.TryGetValue(folder, out int at) ? at : -1;
            }
        }
    }

    static class Tables
    {
        const string Id = "warmstart.tables";
        static bool broken, all;
        static string why = "";
        static int asked, checkedTimes;

        static readonly Table<GameObject> models = new Table<GameObject>(g => g != null ? g.name : null, false, true);
        static readonly Table<AudioClip> sounds = new Table<AudioClip>(a => a != null ? a.name : null, false, false);
        static readonly Table<GameDatabase.TextureInfo> pictures = new Table<GameDatabase.TextureInfo>(t => t != null ? t.name : null, true, false);
        static AccessTools.FieldRef<GameDatabase, Dictionary<string, string>> swaps;

        // (the parts' records)
        static ConfigNode recordsOf;
        static int recordsCount;
        static readonly Dictionary<string, ConfigNode> records = new Dictionary<string, ConfigNode>(StringComparer.Ordinal);
        static Func<PartLoader, ConfigNode, AvailablePart, bool> stampsGood;
        static Action<PartLoader, ConfigNode, Part> stamp;

        public static void Begin()
        {
            if (!WarmStart.Companion.WantsTables) return;
            all = WarmStart.Companion.Kept("tablecheck") == "all";
            swaps = AccessTools.FieldRefAccess<GameDatabase, Dictionary<string, string>>("flagSwaps");
            stampsGood = AccessTools.MethodDelegate<Func<PartLoader, ConfigNode, AvailablePart, bool>>(AccessTools.Method(typeof(PartLoader), "CheckPartDatabaseTimestamps"));
            stamp = AccessTools.MethodDelegate<Action<PartLoader, ConfigNode, Part>>(AccessTools.Method(typeof(PartLoader), "SetDatabaseFilestamps"));
            var h = new Harmony(Id);
            try { h.UnpatchAll(Id); } catch (Exception) { }
            Type me = typeof(Tables), db = typeof(GameDatabase);
            const BindingFlags mine = BindingFlags.Static | BindingFlags.NonPublic;
            HarmonyMethod Pre(string name) => new HarmonyMethod(me.GetMethod(name, mine));
            var said = new HarmonyMethod(me.GetMethod(nameof(Said), mine));
            h.Patch(AccessTools.Method(db, "ExistsModel", new[] { typeof(string) }), Pre(nameof(ExistsModel)), new HarmonyMethod(me.GetMethod(nameof(SaidBool), mine)));
            h.Patch(AccessTools.Method(db, "GetModelPrefab", new[] { typeof(string) }), Pre(nameof(GetModelPrefab)), said);
            h.Patch(AccessTools.Method(db, "GetModelPrefabIn", new[] { typeof(string) }), Pre(nameof(GetModelPrefabIn)), said);
            h.Patch(AccessTools.Method(db, "GetModel", new[] { typeof(string) }), Pre(nameof(GetModel)));
            h.Patch(AccessTools.Method(db, "GetModelIn", new[] { typeof(string) }), Pre(nameof(GetModelIn)));
            h.Patch(AccessTools.Method(db, "GetModelFile", new[] { typeof(string) }), Pre(nameof(GetModelFile)), said);
            h.Patch(AccessTools.Method(db, "ExistsTexture", new[] { typeof(string) }), Pre(nameof(ExistsTexture)), new HarmonyMethod(me.GetMethod(nameof(SaidBool), mine)));
            h.Patch(AccessTools.Method(db, "GetTextureInfo", new[] { typeof(string) }), Pre(nameof(GetTextureInfo)), said);
            h.Patch(AccessTools.Method(db, "GetTexture", new[] { typeof(string), typeof(bool) }), Pre(nameof(GetTexture)), said);
            h.Patch(AccessTools.Method(db, "ExistsAudioClip", new[] { typeof(string) }), Pre(nameof(ExistsAudioClip)), new HarmonyMethod(me.GetMethod(nameof(SaidBool), mine)));
            h.Patch(AccessTools.Method(db, "GetAudioClip", new[] { typeof(string) }), Pre(nameof(GetAudioClip)), said);
            h.Patch(AccessTools.Method(db, "GetAudioFile", new[] { typeof(string) }), Pre(nameof(GetAudioFile)), said);
            h.Patch(AccessTools.Method(typeof(PartLoader), "GetDatabaseConfig", new[] { typeof(Part) }), Pre(nameof(Record)), said);
            // (and the asks for a flight while the game loads, where there is none to find: see NoFlightYet)
            try { NoFlightYet.Begin(h); } catch (Exception ex) { WarmStart.Companion.Log("the asks for a flight while loading were left to the game (" + ex.Message + ")"); }
            WarmStart.Companion.TablesReport = Report;
            WarmStart.Companion.Log("look-ups by table are in: models, pictures, sounds and the parts' records are found by name in a table, not by walking their lists" + (all ? " (every look-up checked against the game's own walk, as asked)" : ""));
        }

        static string Report()
        {
            if (broken) return "Look-ups by table were put away this session: " + why + " The game does its own look-ups, as it always has.";
            return "Look-ups by table: " + asked.ToString("N0") + " so far (models, pictures, sounds, parts' records); " + checkedTimes.ToString("N0") + " of them checked against the game's own walk, and all the same." + NoFlightYet.Said();
        }

        /// <summary>What a look-up found by the table, kept while the game's own walk is run beside it.</summary>
        struct Mine { public bool Checking; public object Found; public bool Yes; public string What, Name; }

        static bool Check() { asked++; return all || (asked & 255) == 0; }

        static void Broken(string what, string name, object mine, object theirs)
        {
            if (broken) return;
            broken = true;
            why = "for " + what + " \"" + name + "\" the table said " + Words(mine) + " and the game's own walk " + Words(theirs) + ".";
            WarmStart.Companion.Log("LOOK-UPS BY TABLE PUT AWAY: " + why + " The game does its own look-ups from here on. Please tell the mod's author.");
        }

        static string Words(object o) => o == null ? "nothing" : o is UnityEngine.Object u ? (u != null ? u.name : "something destroyed") : o is GameDatabase.TextureInfo t ? t.name : o.ToString();

        static void Said(object __result, Mine __state)
        {
            if (!__state.Checking) return;
            checkedTimes++;
            if (!ReferenceEquals(__state.Found, __result)) Broken(__state.What, __state.Name, __state.Found, __result);
        }

        static void SaidBool(bool __result, Mine __state)
        {
            if (!__state.Checking) return;
            checkedTimes++;
            if (__state.Yes != __result) Broken(__state.What, __state.Name, __state.Yes, __result);
        }

        // ---- models

        /// <summary>The place of the model of that name: the table's answer, looked at before it is given (a model given another name since is found out here).</summary>
        static int Model(GameDatabase db, string url)
        {
            int at = models.Find(db.databaseModel, url);
            if (at >= 0 && db.databaseModel[at].name != url) { models.Forget(); at = models.Find(db.databaseModel, url); }
            return at;
        }

        static int ModelIn(GameDatabase db, string folder)
        {
            int at = models.FindIn(db.databaseModel, folder);
            if (at >= 0)
            {
                string name = db.databaseModel[at].name;
                int cut = name.LastIndexOf('/');
                if (cut < 0 || name.Substring(0, cut) != folder) { models.Forget(); at = models.FindIn(db.databaseModel, folder); }
            }
            return at;
        }

        static bool ExistsModel(GameDatabase __instance, string url, ref bool __result, out Mine __state)
        {
            __state = default(Mine);
            if (broken) return true;
            bool yes = Model(__instance, url) >= 0;
            if (Check()) { __state = new Mine { Checking = true, Yes = yes, What = "whether there is a model", Name = url }; return true; }
            __result = yes;
            return false;
        }

        static bool GetModelPrefab(GameDatabase __instance, string url, ref GameObject __result, out Mine __state)
        {
            __state = default(Mine);
            if (broken) return true;
            int at = Model(__instance, url);
            GameObject found = at >= 0 ? __instance.databaseModel[at] : null;
            if (Check()) { __state = new Mine { Checking = true, Found = found, What = "the model", Name = url }; return true; }
            __result = found;
            return false;
        }

        static bool GetModelPrefabIn(GameDatabase __instance, string url, ref GameObject __result, out Mine __state)
        {
            __state = default(Mine);
            if (broken) return true;
            int at = ModelIn(__instance, url);
            GameObject found = at >= 0 ? __instance.databaseModel[at] : null;
            if (Check()) { __state = new Mine { Checking = true, Found = found, What = "the model in the folder", Name = url }; return true; }
            __result = found;
            return false;
        }

        // (a copy of the model: made of whatever the table finds; never checked, a check would make a second copy)
        static bool GetModel(GameDatabase __instance, string url, ref GameObject __result)
        {
            if (broken) return true;
            asked++;
            int at = Model(__instance, url);
            __result = at >= 0 ? UnityEngine.Object.Instantiate(__instance.databaseModel[at]) : null;
            return false;
        }

        static bool GetModelIn(GameDatabase __instance, string url, ref GameObject __result)
        {
            if (broken) return true;
            asked++;
            int at = ModelIn(__instance, url);
            __result = at >= 0 ? UnityEngine.Object.Instantiate(__instance.databaseModel[at]) : null;
            return false;
        }

        static bool GetModelFile(GameDatabase __instance, string url, ref UrlDir.UrlFile __result, out Mine __state)
        {
            __state = default(Mine);
            if (broken) return true;
            int at = Model(__instance, url);
            // (the files are a second list beside the models', place for place: where it is shorter the game's walk would have failed, and is left to)
            if (at >= __instance.databaseModelFiles.Count) return true;
            UrlDir.UrlFile found = at >= 0 ? __instance.databaseModelFiles[at] : null;
            if (Check()) { __state = new Mine { Checking = true, Found = found, What = "the file of the model", Name = url }; return true; }
            __result = found;
            return false;
        }

        // ---- pictures (names compared without regard to capitals, as the game does; a flag's name may stand for another's)

        static GameDatabase.TextureInfo Picture(GameDatabase db, string url)
        {
            for (int hops = 0; url != null && hops < 8; hops++)
            {
                int at = pictures.Find(db.databaseTexture, url);
                if (at >= 0)
                {
                    GameDatabase.TextureInfo found = db.databaseTexture[at];
                    if (found != null && string.Equals(found.name, url, StringComparison.OrdinalIgnoreCase)) return found;
                    pictures.Forget();
                    at = pictures.Find(db.databaseTexture, url);
                    if (at >= 0) return db.databaseTexture[at];
                }
                Dictionary<string, string> other = swaps(db);
                if (other == null || !other.TryGetValue(url, out string next)) return null;
                url = next;
            }
            return null;
        }

        static bool ExistsTexture(GameDatabase __instance, string url, ref bool __result, out Mine __state)
        {
            __state = default(Mine);
            if (broken) return true;
            bool yes = Picture(__instance, url) != null;
            if (Check()) { __state = new Mine { Checking = true, Yes = yes, What = "whether there is a picture", Name = url }; return true; }
            __result = yes;
            return false;
        }

        static bool GetTextureInfo(GameDatabase __instance, string url, ref GameDatabase.TextureInfo __result, out Mine __state)
        {
            __state = default(Mine);
            if (broken) return true;
            GameDatabase.TextureInfo found = Picture(__instance, url);
            if (Check()) { __state = new Mine { Checking = true, Found = found, What = "the picture", Name = url }; return true; }
            __result = found;
            return false;
        }

        static bool GetTexture(GameDatabase __instance, string url, bool asNormalMap, ref Texture2D __result, out Mine __state)
        {
            __state = default(Mine);
            if (broken) return true;
            GameDatabase.TextureInfo info = Picture(__instance, url);
            // (asking a picture for itself can make the game work it out, for a normal map: so the game's own words are used here, once)
            Texture2D found = info == null ? null : asNormalMap ? info.normalMap : info.texture;
            if (Check()) { __state = new Mine { Checking = true, Found = found, What = "the picture's texture", Name = url }; return true; }
            __result = found;
            return false;
        }

        // ---- sounds

        static int Sound(GameDatabase db, string url)
        {
            int at = sounds.Find(db.databaseAudio, url);
            if (at >= 0 && (db.databaseAudio[at] == null || db.databaseAudio[at].name != url)) { sounds.Forget(); at = sounds.Find(db.databaseAudio, url); }
            return at;
        }

        static bool ExistsAudioClip(GameDatabase __instance, string url, ref bool __result, out Mine __state)
        {
            __state = default(Mine);
            if (broken) return true;
            bool yes = Sound(__instance, url) >= 0;
            if (Check()) { __state = new Mine { Checking = true, Yes = yes, What = "whether there is a sound", Name = url }; return true; }
            __result = yes;
            return false;
        }

        static bool GetAudioClip(GameDatabase __instance, string url, ref AudioClip __result, out Mine __state)
        {
            __state = default(Mine);
            if (broken) return true;
            int at = Sound(__instance, url);
            AudioClip found = at >= 0 ? __instance.databaseAudio[at] : null;
            if (Check()) { __state = new Mine { Checking = true, Found = found, What = "the sound", Name = url }; return true; }
            __result = found;
            return false;
        }

        static bool GetAudioFile(GameDatabase __instance, string url, ref UrlDir.UrlFile __result, out Mine __state)
        {
            __state = default(Mine);
            if (broken) return true;
            int at = Sound(__instance, url);
            if (at >= __instance.databaseAudioFiles.Count) return true;
            UrlDir.UrlFile found = at >= 0 ? __instance.databaseAudioFiles[at] : null;
            if (Check()) { __state = new Mine { Checking = true, Found = found, What = "the file of the sound", Name = url }; return true; }
            __result = found;
            return false;
        }

        // ---- the parts' records in the game's own part database

        /// <summary>
        /// The game's own method, done again here step for step but for the finding: the record of the part's address, made
        /// if there is none (with the game's own line in the log), and looked at for its file dates by the game's own code.
        /// </summary>
        static bool Record(PartLoader __instance, Part p, ref ConfigNode __result, ConfigNode ___partDatabase, out Mine __state)
        {
            __state = default(Mine);
            if (broken || ___partDatabase == null || p == null || p.partInfo == null || p.partInfo.partUrl == null) return true;
            string url = p.partInfo.partUrl;
            if (!ReferenceEquals(recordsOf, ___partDatabase) || recordsCount != ___partDatabase.nodes.Count)
            {
                records.Clear();
                for (int i = 0, n = ___partDatabase.nodes.Count; i < n; i++)
                {
                    ConfigNode node = ___partDatabase.nodes[i];
                    if (node.name != "PART") continue;
                    string its = node.GetValue("url");
                    if (its != null && !records.ContainsKey(its)) records[its] = node;
                }
                recordsOf = ___partDatabase; recordsCount = ___partDatabase.nodes.Count;
            }
            records.TryGetValue(url, out ConfigNode found);
            if (found != null && found.GetValue("url") != url) return true;       // (a record given another address since: the game's own walk decides)
            if (Check() && found != null) { __state = new Mine { Checking = true, Found = found, What = "the record of the part", Name = url }; return true; }
            if (found == null)
            {
                Debug.Log("PartLoader: Part '" + url + "' has no database record. Creating.");
                found = new ConfigNode("PART");
                found.AddValue("url", url);
                stamp(__instance, found, p);
                ___partDatabase.AddNode(found);
                records[url] = found; recordsCount = ___partDatabase.nodes.Count;
            }
            else if (!stampsGood(__instance, found, p.partInfo)) return true;     // (the game's own code resets it, and says so)
            __result = found;
            return false;
        }
    }
}
