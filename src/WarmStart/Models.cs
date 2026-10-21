using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

namespace WarmStart
{
    /// <summary>
    /// The models, read without a frame between one and the next. The game's loader hands each model to its loader for .mu files as
    /// a coroutine of its own and waits for it to finish; that loader never waits for anything, but Unity lets the game's loader go on
    /// only at the next frame, so the models come in one a frame. Measured with the start probe (2026-10-17): 642 models took 3.8
    /// seconds, of which reading them, in the game's own reader, was 0.9; the rest was frames drawn between them.
    ///
    /// Here, once the game has read its first model (so that all it does before its models is done), the rest are read by the game's
    /// own reader (any mod's changes to it apply as ever), in slices of a sixth of a second a frame, each set up as the game's loader
    /// sets it up (named by its path, under the database, switched off, put on its two lists, in the order of the files), and each
    /// line the game writes to its log for a model written as it writes it. Coming to a model it already has, the game passes over it,
    /// as it does the textures handed over: their files are dated the year 1 for it until it is through (it reloads a model whose file
    /// is newer than its last loading, which at the first loading is every one). A model the game's reader could not make is left to
    /// the game, which tries it itself and says so in its log as ever. Where a mod has a loader of its own for .mu files, the game would
    /// run both: then the models are left to it.
    /// </summary>
    static class Models
    {
        /// <summary>How long a frame's slice of reading may take (seconds): the loading screen still moves, a few frames in all.</summary>
        const double Slice = 0.16;

        static MethodInfo read;                 // the game's own reader: PartReader.Read(UrlDir.UrlFile), internal to the game
        static int stage = -1;                  // -1 not doing it; 0 waiting for the game's first model; 1 reading; 2 done
        static List<UrlDir.UrlFile> files;
        static int at, handed, refused;
        static double took;
        static readonly List<KeyValuePair<UrlDir.UrlFile, DateTime>> passed = new List<KeyValuePair<UrlDir.UrlFile, DateTime>>();
        public static string Report = "";
        // (the models the game has begun on itself: it puts each on its list only at the next frame, after the line it writes for
        // it; read here as well in between, five came out twice on the list at the first try)
        static readonly HashSet<string> theirs = new HashSet<string>();
        static bool mine, listening;

        public static bool Busy => stage == 0 || stage == 1;

        static void Heard(string text, string stack, LogType type)
        {
            if (mine || type != LogType.Log || text == null || !text.StartsWith("Load(Model): ", StringComparison.Ordinal)) return;
            string url = text.Substring(13);
            if (url.EndsWith(" OUT OF DATE", StringComparison.Ordinal)) url = url.Substring(0, url.Length - 12);
            theirs.Add(url);
        }

        public static void Begin()
        {
            stage = -1;
            if (Warm.Dated == null) { Warm.Log("this game does not keep its files' dates where they were expected: models are left to it"); return; }
            Type reader = typeof(PartLoader).Assembly.GetType("PartReader");
            read = reader != null ? reader.GetMethod("Read", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(UrlDir.UrlFile) }, null) : null;
            if (read == null || read.ReturnType != typeof(GameObject)) { Warm.Log("the game's model reader is not as it was when this was written: models are left to it"); return; }
            // (a mod with a loader of its own for .mu files: the game would run both on each file)
            foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
            {
                Type[] types;
                try { types = a.assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch (Exception) { continue; }
                if (types == null) continue;
                foreach (Type t in types)
                {
                    if (t == null || t == typeof(DatabaseLoaderModel_MU) || !t.IsSubclassOf(typeof(DatabaseLoader<GameObject>))) continue;
                    foreach (object note in t.GetCustomAttributes(true))
                        if (note is DatabaseLoaderAttrib its && its.extensions != null && Array.IndexOf(its.extensions, "mu") >= 0)
                        {
                            Warm.Log(t.FullName + " loads .mu files too: the models are left to the game");
                            return;
                        }
                }
            }
            stage = 0;
            Application.logMessageReceived += Heard;
            listening = true;
        }

        /// <summary>Every frame while the game loads: once it has its first model, the rest read in slices, ahead of it.</summary>
        public static void Step()
        {
            if (stage != 0 && stage != 1) return;
            GameDatabase db = GameDatabase.Instance;
            if (db == null) return;
            if (stage == 0)
            {
                if (theirs.Count == 0 && (db.databaseModel == null || db.databaseModel.Count == 0)) return;     // (the game is not at its models yet)
                files = new List<UrlDir.UrlFile>();
                foreach (UrlDir.UrlFile file in db.root.GetFiles(UrlDir.FileType.Model))
                    if (file != null && file.fileExtension == "mu") files.Add(file);
                at = 0; stage = 1;
            }
            long began = Stopwatch.GetTimestamp();
            long until = began + (long)(Slice * Stopwatch.Frequency);
            var none = new object[1];
            for (; at < files.Count && Stopwatch.GetTimestamp() < until; at++)
            {
                UrlDir.UrlFile file = files[at];
                if (theirs.Contains(file.url) || db.ExistsModel(file.url)) continue;
                mine = true;
                try { UnityEngine.Debug.Log("Load(Model): " + file.url); }
                finally { mine = false; }
                GameObject obj;
                try { none[0] = file; obj = read.Invoke(null, none) as GameObject; }
                catch (Exception) { obj = null; }
                if (obj == null) { refused++; continue; }                               // (the game tries it itself, and says what is wrong)
                obj.transform.name = file.url;
                obj.transform.parent = db.transform;
                obj.transform.localPosition = Vector3.zero;
                obj.transform.localRotation = Quaternion.identity;
                obj.SetActive(false);
                db.databaseModel.Add(obj);
                db.databaseModelFiles.Add(file);
                passed.Add(new KeyValuePair<UrlDir.UrlFile, DateTime>(file, file.fileTime));
                Warm.Dated.SetValue(file, Warm.Never);
                handed++;
            }
            took += (Stopwatch.GetTimestamp() - began) / (double)Stopwatch.Frequency;
            if (at >= files.Count)
            {
                stage = 2;
                Report = handed + " models read without a frame between them, in " + took.ToString("F1") + " s" + (refused > 0 ? " (" + refused + " left to the game)" : "") + ".";
                Warm.Log(handed + " models read without a frame between them, in " + (took * 1000.0).ToString("F0") + " ms" + (refused > 0 ? "; " + refused + " the game's reader could not make, left to the game" : ""));
            }
        }

        /// <summary>At the main menu: the files have their dates again (a reloading of the game's database goes by them).</summary>
        public static void Through()
        {
            if (listening) { Application.logMessageReceived -= Heard; listening = false; }
            theirs.Clear();
            foreach (KeyValuePair<UrlDir.UrlFile, DateTime> p in passed) Warm.Dated.SetValue(p.Key, p.Value);
            passed.Clear();
            files = null;
        }
    }
}
