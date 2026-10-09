using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using KSPAssets;
using KSPAssets.Loaders;
using UnityEngine;

namespace WarmStart
{
    /// <summary>
    /// What is in the game's small asset bundles, kept.
    ///
    /// The game ships a couple of hundred small bundles (the pages of its manual, the KSPedia, most of them),
    /// and mods may add more. Each has, inside it, a short description of what it holds, and at every start
    /// the game opens every one of them only to read that description, and closes it again. Opening one means
    /// unpacking all of it (they are packed the slow way, LZMA, as the expansions' big files are). Timed on the
    /// Mac this was made on: 228 bundles, 68 MB, 5.4 seconds, of which one bundle of 29 MB takes 1.7. Nothing
    /// else is done with them while the game loads: the pages are opened again one at a time when read.
    ///
    /// The descriptions come out the same every time, so they are kept: after a start in which the game read
    /// them itself, every description is written down beside the size and date of the file it came from. At
    /// later starts, while every file is still the size and has the date it had, the game is given the
    /// descriptions and opens nothing.
    ///
    /// It is given them without any of its code being changed, like this. The game's reader of these bundles
    /// (KSPAssets.Loaders.AssetLoader.LoadDefinitionsAsync) writes a line in the log as the first thing it
    /// does, and another as the last but one; and a mod may listen to the log, and is called while the line is
    /// being written, in the middle of whatever wrote it. Between its two lines the reader lists the files
    /// whose names end in "ksp" (an ending it holds in a field), opens each, and puts what it reads on a list;
    /// then it names each description after its file, ties each to those it depends on, and lists the assets.
    ///
    /// * At the first line, if every file the game is about to open is as it was, the field is given an ending
    ///   that no file has. The game then finds no files and opens none.
    /// * At the second line the ending is put back, the kept descriptions are put on the reader's list in the
    ///   order of the files the game would have opened, the reader's list of files is made what it would have
    ///   been, and the reader's own two steps are run over what is now on the list. The game then goes on.
    ///
    /// If anything is not as it was (a file changed, added or gone) nothing is touched: the game reads its
    /// bundles itself, and at its second line what it read is kept afresh. That is also when it is checked that
    /// this lists the files as the game does: only while it does is anything ever handed over.
    /// </summary>
    static class Definitions
    {
        public static string Report = "";
        /// <summary>Whether the kept descriptions were handed over at this start.</summary>
        public static bool Gave { get; private set; }

        const string First = "AssetLoader: Loading bundle definitions", Last = "AssetLoader: Finished loading.";
        const string NoSuch = "warmstart-no-file-ends-in-this-5d1c";
        const string Sign = "WarmStartDefinitions3";
        static string Kept => Warm.Folder + "definitions.list";

        // ---------------------------------------------------------------- what is kept

        sealed class Of                             // one bundle file, and the descriptions the game read out of it
        {
            public string Path; public long Size, Stamp;
            public byte[] Sum;                      // what is in it (see Warm.Sum)
            public readonly List<BundleDefinition> Has = new List<BundleDefinition>();
        }

        static readonly Dictionary<string, Of> kept = new Dictionary<string, Of>();
        static bool keptAgrees;                     // whether the game's own list of files was the one this makes of them, when these were kept

        static void W(BinaryWriter w, string text) { w.Write(text != null); if (text != null) w.Write(text); }
        static string R(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

        static void Read()
        {
            kept.Clear(); keptAgrees = false;
            try
            {
                if (!File.Exists(Kept)) return;
                using (var r = new BinaryReader(File.OpenRead(Kept)))
                {
                    if (r.ReadString() != Sign || r.ReadString() != Application.unityVersion) return;
                    bool agrees = r.ReadBoolean();
                    for (int n = r.ReadInt32(); n > 0; n--)
                    {
                        var of = new Of { Path = r.ReadString(), Size = r.ReadInt64(), Stamp = r.ReadInt64(), Sum = r.ReadBytes(16) };
                        for (int d = r.ReadInt32(); d > 0; d--)
                        {
                            var def = new BundleDefinition { urlName = R(r), name = R(r), createdTime = r.ReadInt64(), autoLoad = r.ReadBoolean(), author = R(r), info = R(r) };
                            def.assets = new List<AssetDefinition>();
                            for (int a = r.ReadInt32(); a > 0; a--) def.assets.Add(new AssetDefinition { name = R(r), path = R(r), type = R(r), url = R(r), autoLoad = r.ReadBoolean() });
                            def.dependencyNames = new List<string>();
                            for (int a = r.ReadInt32(); a > 0; a--) def.dependencyNames.Add(R(r));
                            of.Has.Add(def);
                        }
                        kept[of.Path] = of;
                    }
                    keptAgrees = agrees;
                }
            }
            catch (Exception) { kept.Clear(); keptAgrees = false; }
        }

        static void Write(List<Of> all, bool agrees)
        {
            Directory.CreateDirectory(Warm.Folder);
            string making = Kept + ".making";
            using (var w = new BinaryWriter(File.Create(making)))
            {
                w.Write(Sign); w.Write(Application.unityVersion); w.Write(agrees);
                w.Write(all.Count);
                foreach (Of of in all)
                {
                    w.Write(of.Path); w.Write(of.Size); w.Write(of.Stamp); w.Write(of.Sum != null && of.Sum.Length == 16 ? of.Sum : new byte[16]); w.Write(of.Has.Count);
                    foreach (BundleDefinition def in of.Has)
                    {
                        W(w, def.urlName); W(w, def.name); w.Write(def.createdTime); w.Write(def.autoLoad); W(w, def.author); W(w, def.info);
                        w.Write(def.assets != null ? def.assets.Count : 0);
                        if (def.assets != null) foreach (AssetDefinition a in def.assets) { W(w, a.name); W(w, a.path); W(w, a.type); W(w, a.url); w.Write(a.autoLoad); }
                        w.Write(def.dependencyNames != null ? def.dependencyNames.Count : 0);
                        if (def.dependencyNames != null) foreach (string name in def.dependencyNames) W(w, name);
                    }
                }
            }
            if (File.Exists(Kept)) File.Delete(Kept);
            File.Move(making, Kept);
        }

        /// <summary>A description like a kept one, for the game to have: its own, since the game goes on to write in them.</summary>
        static BundleDefinition Like(BundleDefinition k, string path)
        {
            var def = new BundleDefinition { urlName = k.urlName, name = k.name, path = path, createdTime = k.createdTime, autoLoad = k.autoLoad, author = k.author, info = k.info };
            def.assets = new List<AssetDefinition>(k.assets.Count);
            foreach (AssetDefinition a in k.assets) def.assets.Add(new AssetDefinition { name = a.name, path = a.path, type = a.type, url = a.url, autoLoad = a.autoLoad });
            def.dependencyNames = new List<string>(k.dependencyNames);
            return def;
        }

        // ---------------------------------------------------------------- the game's reader, and its files

        const BindingFlags Inside = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo fKind = typeof(AssetLoader).GetField("assetExtension", Inside), fFolder = typeof(AssetLoader).GetField("assetDirectory", Inside), fCore = typeof(AssetLoader).GetField("coreDirectory", Inside),
                                  fFiles = typeof(AssetLoader).GetField("allFilesList", Inside), fList = typeof(AssetLoader).GetField("coreAndAutoloadDefinitions", Inside), fCount = typeof(AssetLoader).GetField("amountAutoLoadBundles", Inside);
        static readonly MethodInfo mName = typeof(AssetLoader).GetMethod("CompileBundleDefinitions", Inside, null, Type.EmptyTypes, null), mAssets = typeof(AssetLoader).GetMethod("CreateAssetDefinitionList", Inside, null, Type.EmptyTypes, null);

        static AssetLoader reader;
        static string kindWas, root, folder, first;
        static System.Threading.Thread lister;
        static FileInfo[] core, all;                // the bundle files in the game's first folder, and in all of them: as the game lists them
        static List<FileInfo> real;                 // the files the game would open
        static List<BundleDefinition> give;         // what it would read out of them
        static bool listening, armed, done;
        static int redated;
        static float armedAt;
        static double looked;
        static readonly List<string> toSay = new List<string>();      // (nothing is written in the log from inside the listening to it)

        public static bool Busy => listening || toSay.Count > 0;

        static string Under(string full) => full.StartsWith(root, StringComparison.Ordinal) ? full.Substring(root.Length) : full;

        /// <summary>The files the game would open, in its order: its own way of listing them (AssetLoader.LoadDefinitionsAsync and AddAssetFiles), done over.</summary>
        static List<FileInfo> Listing(List<string> leftOut)
        {
            var files = new List<FileInfo>();
            var names = new HashSet<string>();
            foreach (FileInfo f in core) if (!leftOut.Contains(Path.GetFileName(f.FullName))) { files.Add(f); names.Add(f.FullName); }
            foreach (FileInfo[] more in new[] { core, all })
                foreach (FileInfo f in more)
                {
                    string full = f.FullName;
                    if (leftOut.Contains(Path.GetFileName(full)) || names.Contains(full)) continue;
                    files.Add(f); names.Add(full);
                }
            return files;
        }

        static bool Good(FileInfo f, out Of of)
        {
            of = null;
            try
            {
                if (!kept.TryGetValue(Under(f.FullName), out of) || f.Length != of.Size) return false;
                long stamp = f.LastWriteTimeUtc.Ticks;
                if (stamp == of.Stamp) return true;
                // (the same size at another date: the game itself writes two of its bundles out afresh whenever it reads them. What is in the file decides.)
                if (!Warm.Same(Warm.Sum(f.FullName), of.Sum)) return false;
                of.Stamp = stamp; redated++;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>At the very start: reads what is kept, and begins to listen to the log.</summary>
        public static void Begin()
        {
            if (listening || done) return;
            if (fKind == null || fFolder == null || fCore == null || fFiles == null || fList == null || fCount == null || fCount.FieldType != typeof(int) || mName == null || mAssets == null)
            {
                done = true;
                Warm.Log("this game's reader of bundles is not as expected: the bundles' descriptions are left to it");
                return;
            }
            Read();
            // (the files are listed ahead of time, on another thread: listing them takes a tenth of a second, and nothing changes them while the game starts)
            reader = AssetLoader.Instance;
            if (reader != null)
            {
                Ask();
                lister = new System.Threading.Thread(List) { IsBackground = true, Name = "Warm Start listing bundles" };
                lister.Start();
            }
            listening = true;
            Application.logMessageReceived += Heard;
        }

        /// <summary>The reader's folders and the ending it looks for: asked of it on the game's own thread.</summary>
        static void Ask()
        {
            kindWas = (string)fKind.GetValue(reader);
            folder = AssetLoader.CreateApplicationPath((string)fFolder.GetValue(reader));
            first = (string)fCore.GetValue(reader);
        }

        /// <summary>The bundle files, as the game lists them: those in its first folder, and all of them. (On any thread.)</summary>
        static void List()
        {
            try
            {
                var top = new DirectoryInfo(folder);
                string under = top.FullName; if (!under.EndsWith(Path.DirectorySeparatorChar.ToString())) under += Path.DirectorySeparatorChar;
                FileInfo[] inFirst = new DirectoryInfo(Path.Combine(folder, first)).GetFiles("*." + kindWas, SearchOption.AllDirectories);
                FileInfo[] inAll = top.GetFiles("*." + kindWas, SearchOption.AllDirectories);
                // (each is asked its size and date now, and remembers them)
                long sizes = 0;
                foreach (FileInfo[] some in new[] { inFirst, inAll }) foreach (FileInfo f in some) sizes += f.Length + (f.LastWriteTimeUtc.Ticks & 1);
                root = under; core = inFirst; all = inAll;
            }
            catch (Exception) { core = null; all = null; }
        }

        /// <summary>Every frame while there is anything to see to: says what there is to say, and puts the game's ending back if the game never came to its second line.</summary>
        public static void Step()
        {
            if (toSay.Count > 0)
            {
                foreach (string text in toSay) Warm.Log(text);
                toSay.Clear();
            }
            if (armed && Time.realtimeSinceStartup - armedAt > 20f) { Restore(); Stop(); Warm.Log("the game did not finish with its bundles: its own ending for them is put back"); }
        }

        /// <summary>The game's own ending back in its place. Safe to call at any time.</summary>
        public static void Restore()
        {
            if (!armed) return;
            armed = false;
            try { if (reader != null && kindWas != null) fKind.SetValue(reader, kindWas); } catch (Exception) { }
        }

        static void Stop()
        {
            if (listening) Application.logMessageReceived -= Heard;
            listening = false; done = true;
        }

        public static void End() { Restore(); Stop(); }

        static void Heard(string text, string stack, LogType type)
        {
            if (type != LogType.Log || text == null || text.Length < 30 || text[0] != 'A') return;
            try
            {
                if (text == First) Starting();
                else if (text.StartsWith(Last, StringComparison.Ordinal)) Finishing();
            }
            catch (Exception ex)
            {
                Restore(); Stop();
                toSay.Add("could not see to the bundles' descriptions (" + ex.GetType().Name + ": " + ex.Message + "): they are left to the game");
            }
        }

        /// <summary>The game is about to list its bundles. If they are all as they were, it is made to find none.</summary>
        static void Starting()
        {
            if (armed || done) return;
            reader = AssetLoader.Instance;
            if (reader == null) return;
            long began = Stopwatch.GetTimestamp();
            if (lister != null) { lister.Join(); lister = null; }
            if (core == null || all == null || (string)fKind.GetValue(reader) != kindWas) { Ask(); List(); }
            if (core == null || all == null) return;
            real = Listing(AssetLoader.AssetBlacklist);
            give = null;
            if (kept.Count == 0 || !keptAgrees) return;                                      // (nothing kept that can be given: the game reads them itself, and that is kept)
            var ready = new List<BundleDefinition>();
            int changed = 0;
            foreach (FileInfo f in real)
            {
                if (!Good(f, out Of of)) { changed++; continue; }
                foreach (BundleDefinition k in of.Has) ready.Add(Like(k, f.FullName));
            }
            if (changed > 0) { toSay.Add(changed + " of the game's " + real.Count + " bundles are not as they were: the game reads them all itself this time"); return; }
            give = ready;
            fKind.SetValue(reader, NoSuch);
            armed = true; armedAt = Time.realtimeSinceStartup;
            looked = (Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>
        /// The game is through: it has only to say so (it is saying so) and to call itself ready. Where it was made to find no
        /// files, it is given what it would have read; where it read them itself, that is kept.
        /// </summary>
        static void Finishing()
        {
            if (reader == null || real == null) { Stop(); return; }
            var files = (List<FileInfo>)fFiles.GetValue(reader);
            var list = (List<BundleDefinition>)fList.GetValue(reader);
            if (armed)
            {
                Restore();
                Stop();
                if (files == null || list == null || files.Count != 0 || list.Count != 0)
                {
                    // (it found something with that ending after all, and has whatever it read out of it: its real bundles it reads now, a little late)
                    toSay.Add("the game found files where there should have been none: it is set to read its bundles again");
                    reader.StartCoroutine(reader.LoadDefinitionsAsync());
                    return;
                }
                long began = Stopwatch.GetTimestamp();
                list.AddRange(give);
                fCount.SetValue(reader, (int)fCount.GetValue(reader) + give.Count);
                files.AddRange(real);
                mName.Invoke(reader, null);
                mAssets.Invoke(reader, null);
                double took = (Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency;
                Gave = true;
                Report = "What is in the game's " + real.Count + " small bundles (" + give.Count + " descriptions) was handed to it as kept, and none of them had to be opened.";
                toSay.Add(give.Count + " descriptions of what is in " + real.Count + " bundles handed over as kept, with no bundle opened (looking at the files took " + looked.ToString("F0") + " ms, handing over " + took.ToString("F0") + " ms)");
                give = null;
                if (redated > 0)
                {
                    // (the dates as they are now, so that the files need not be read through again next time)
                    var all = new List<Of>(kept.Values);
                    bool agreed = keptAgrees;
                    new System.Threading.Thread(() => { try { Write(all, agreed); } catch (Exception) { } }) { IsBackground = true, Name = "Warm Start keeping descriptions" }.Start();
                    toSay.Add(redated + " of the bundles have another date than they had, and the same contents");
                }
                return;
            }

            // ---- the game read them itself: every file it opened is kept, with what it read out of it
            Stop();
            if (files == null || list == null) return;
            var byPath = new Dictionary<string, Of>();
            var every = new List<Of>();
            foreach (FileInfo f in files)
            {
                var its = new FileInfo(f.FullName);
                if (!its.Exists || byPath.ContainsKey(f.FullName)) { toSay.Add("the game's list of bundles is not one that can be kept (" + f.Name + ")"); return; }
                var of = new Of { Path = Under(f.FullName), Size = its.Length, Stamp = its.LastWriteTimeUtc.Ticks };
                byPath[f.FullName] = of; every.Add(of);
            }
            foreach (BundleDefinition def in list)
            {
                if (def == null || def.path == null || !byPath.TryGetValue(def.path, out Of of)) { toSay.Add("a bundle's description is of no file on the game's list (" + (def != null ? def.name : "none") + "): nothing is kept"); return; }
                of.Has.Add(Like(def, def.path));                    // (a copy: the game's own is the game's to change)
            }
            // (whether this lists the files as the game just did: only then can it stand in for the game's listing at the next start)
            bool agrees = real.Count == files.Count;
            for (int i = 0; agrees && i < real.Count; i++) agrees = real[i].FullName == files[i].FullName;
            // (written out on another thread, which first reads every file through for what is in it)
            string from = root;
            int descriptions = list.Count;
            new System.Threading.Thread(() =>
            {
                try
                {
                    foreach (Of of in every) of.Sum = Warm.Sum(System.IO.Path.IsPathRooted(of.Path) ? of.Path : from + of.Path);
                    Write(every, agrees);
                    UnityEngine.Debug.Log("[Warm Start] what the game read out of " + every.Count + " bundles (" + descriptions + " descriptions) is kept for the next start" + (agrees ? "" : "; but its list of them is not the one this makes, so they will not be handed over"));
                }
                catch (Exception ex) { UnityEngine.Debug.Log("[Warm Start] what the game read out of its bundles could not be kept (" + ex.Message + ")"); }
            }) { IsBackground = true, Name = "Warm Start keeping descriptions" }.Start();
            Report = "What the game read out of its " + every.Count + " small bundles this time (" + list.Count + " descriptions) is kept for the next start.";
        }

        // ---------------------------------------------------------------- for comparing one start with another

        /// <summary>Every description the game has, in its order, with all that is in it, and the reader's list of files: written out.</summary>
        public static void Tell(string to)
        {
            var lines = new List<string>();
            AssetLoader loader = AssetLoader.Instance;
            if (loader == null || fList == null) return;
            var list = (List<BundleDefinition>)fList.GetValue(loader);
            lines.Add("ready " + AssetLoader.Ready + "; counted " + fCount.GetValue(loader) + "; ending " + fKind.GetValue(loader) + "; " + list.Count + " descriptions; " + AssetLoader.AssetDefinitions.Count + " assets; left out: " + string.Join(", ", AssetLoader.AssetBlacklist.ToArray()));
            foreach (BundleDefinition d in list)
            {
                var deps = new List<string>();
                foreach (BundleDefinition other in d.dependencyBundles) deps.Add(other.name);
                lines.Add("BUNDLE " + d.name + " | url " + d.urlName + " | path " + d.path + " | made " + d.createdTime + " | auto " + d.autoLoad + " | by " + (d.author ?? "(null)") + " | info " + (d.info ?? "(null)")
                    + " | needs " + string.Join(",", d.dependencyNames.ToArray()) + " | found " + d.dependenciesFound + " -> " + string.Join(",", deps.ToArray()));
                foreach (AssetDefinition a in d.assets)
                    lines.Add("    " + a.name + " | " + a.path + " | " + a.type + " | " + a.url + " | auto " + a.autoLoad + " | of " + (a.bundle != null ? a.bundle.name : "(none)"));
            }
            int n = 0;
            foreach (AssetDefinition a in AssetLoader.AssetDefinitions) { n++; lines.Add("ASSET #" + n + " " + a.name + " of " + (a.bundle != null ? a.bundle.name : "(none)")); }
            foreach (FileInfo f in (List<FileInfo>)fFiles.GetValue(loader)) lines.Add("FILE " + f.FullName);
            File.WriteAllLines(to, lines.ToArray());
        }
    }
}
