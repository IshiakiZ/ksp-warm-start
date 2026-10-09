using System;
using System.Collections;
using Keystone;
using UnityEngine;

#if !DEV
[assembly: KSPAssembly("WarmStart", 0, 1)]
[assembly: KSPAssemblyDependency("Keystone", 0, 4)]
#endif

namespace WarmStart
{
    /// <summary>
    /// Warm Start: the game starts faster, because what it works out afresh at every start is kept from the
    /// last one.
    ///
    /// Asked for as "a mod that improves the loading speed", and then: "really deep research how games load
    /// and how we can improve it... your own way, your own program, your own code". So the game's start was
    /// timed here, stage by stage, from its own log, and its loader read. On the Mac this was made on the
    /// game took 55 seconds to its main menu, and four things, each worked out afresh at every start and
    /// each coming out the same every time, took 34 of them:
    ///
    ///   18.3 s  unpacking the two expansions' three big files into memory            (Bundles)
    ///    8.0 s  making 667 PNG pictures into textures the graphics card takes        (Textures)
    ///    5.0 s  opening 228 small bundles to read a short description out of each    (Definitions)
    ///    2.2 s  reading 972 DDS pictures one after another                           (Textures)
    ///
    /// Each is kept, or done ahead, by a file of its own here, and can be switched off by itself. With all of
    /// them the same start takes 21 seconds.
    ///
    /// None of the game's code is changed. Each of them hands the game something through a door the game
    /// already has: a list it looks at before it loads a thing, a field it reads, a line it writes in its
    /// log. And what the game ends up with was compared with what it makes by itself: every texture's size,
    /// kind and pixels, every description, their order (see the wiki's How-it-works).
    ///
    /// What was tried and is not here, because it earned nothing that could be measured: asking for all the
    /// sound files at once (the waiting goes, but taking the sounds from Unity, which is most of the time, has
    /// to be done on the game's own thread either way), and turning up Unity's own settings for loading in
    /// the background (nothing is loaded that way any more once the above are done).
    /// </summary>
    static class Warm
    {
        public const string Version = "0.1.0";

        public static Mod Page;
        public static Toggle On, Expansions, Fades, Frames, Pictures, Small;

        /// <summary>
        /// Where the game keeps the date of a file on its list. The game passes over a file whose texture it has
        /// already, unless the file is newer than the last loading: so a file that has been seen to is given, while the game
        /// goes through them, the date the game itself gives a file whose date it cannot read (the year 1).
        /// </summary>
        public static readonly System.Reflection.FieldInfo Dated = Field();
        public static readonly DateTime Never = new DateTime(1, 1, 1);

        static System.Reflection.FieldInfo Field()
        {
            System.Reflection.FieldInfo f = typeof(UrlDir.UrlFile).GetField("_fileTime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return f != null && f.FieldType == typeof(DateTime) ? f : null;
        }

        /// <summary>Where what is kept between starts is kept. (A PluginData folder: the game does not read those, so nothing in it is ever taken for a config or a texture.)</summary>
        public static string Folder => KSPUtil.ApplicationRootPath + "GameData/WarmStart/PluginData/";

        public static void Setup()
        {
            if (Page != null && Kit.Find("Warm Start") == Page) return;
            Page = Kit.Register("Warm Start", Version, "The game starts faster: what it works out afresh at every start is kept from the last one.");
            On = Page.Toggle("on", "Warm start", true, "Off: the game starts as it always has. What has been kept stays on the disk, and is used again when this is switched back on.");
            Fades = Page.Toggle("fades", "Do not wait on the loading screen", true, "While one loading picture fades into the next, the game's loader does one file and then waits for the next frame to be drawn, so that the fade is smooth: for a second or two in every several it loads at the speed the screen is drawn at. With this it goes on loading at full speed, and the fades are jerky instead.");
            Frames = Page.Toggle("frames", "Load at any frame rate", true, "Some of the game's loading waits for a frame to be drawn between one file and the next (every PNG picture does). The game holds its frame rate to the limit in its settings while it loads, so those files come in no faster than that. With this the limit is lifted until the main menu is there, and put back.");
            Page.Heading("What is kept");
            Pictures = Page.Toggle("pictures", "Textures, ready made", true, "What the game makes of each PNG picture (unpacking it, squeezing it into the graphics card's own format, making its smaller copies) is kept from the first start, and handed back at every later one; and the DDS pictures, which are finished already, are read by several threads ahead of the game. A few hundred megabytes of disk. A picture whose file changes is made again.");
            Small = Page.Toggle("small", "What is in the small bundles, kept", true, "The game opens every one of its couple of hundred small bundles (the pages of its manual, mostly) at every start, only to read a short description of what is in each: and opening one means unpacking all of it. With this the descriptions are kept from one start to the next, and while no bundle has changed none is opened. Next to no disk.");
            Expansions = Page.Toggle("expansions", "The expansions' files, unpacked", true, "Making History and Breaking Ground keep their models and scenes in three files that the game has to unpack whole, into memory, at every start: about a third of the whole wait. With this, a copy of each is kept that needs no unpacking, and the game is handed that. It takes about a gigabyte of disk, is made once (in the background, at the main menu, the first time), and is made again by itself if the expansion's own file ever changes.");
        }

        public static void Log(string text) => Kit.Log("Warm Start", text);

        /// <summary>
        /// What is in a file, as sixteen bytes (MD5). A kept thing is taken to be good while the file it was made from has the
        /// size and the date it had; where only the date is different (a mod put back in place, a game checked over by its
        /// shop, and two bundles the game itself writes out afresh whenever it reads them) this decides instead.
        /// </summary>
        public static byte[] Sum(string path)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var s = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read, 1 << 16))
                return md5.ComputeHash(s);
        }

        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>Whether another mod that takes the game's loading over is here: where it is, this one leaves the same things alone.</summary>
        public static bool OthersDo
        {
            get
            {
                foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
                    if (a != null && a.name == "KSPCommunityFixes") return true;
                return false;
            }
        }
    }

    /// <summary>
    /// Begins with the game, before it has loaded anything, and stays for good: hands over what was kept,
    /// times the start, and at the main menu makes whatever is not kept yet.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class Starter : MonoBehaviour
    {
        static Starter only;
        float began, menuAt = -1f;
        string making = "";
        string said = "";
        bool handed;

        void Awake()
        {
            if (only != null) { Destroy(gameObject); return; }
            only = this;
            DontDestroyOnLoad(gameObject);
            began = Time.realtimeSinceStartup;
            Warm.Setup();
            Warm.Page.Panel = Panel;
            // (where another mod has taken the game's loading over, this one does nothing at all: two at the same job would only be in each other's way)
            others = Warm.OthersDo;
            if (others) Warm.Log("KSP Community Fixes is installed, and loads the game its own way: Warm Start leaves everything to it");
            if (Warm.On && !others)
            {
                if (Warm.Expansions)
                {
                    try { handed = Bundles.Offer(); }
                    catch (Exception ex) { Warm.Log("could not hand over the expansions' files (" + ex.Message + "): the game will load them itself"); }
                }
                if (Warm.Pictures) { Textures.Begin(); pictures = true; }
                if (Warm.Small) { Definitions.Begin(); small = true; }
            }
            GameEvents.onLevelWasLoadedGUIReady.Add(Arrived);
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += Scene;
        }

        void Scene(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            if (menuAt < 0f && waits.Count < 40) waits.Add(Time.realtimeSinceStartup.ToString("F1") + " s: the scene " + scene.name + " is loaded" + (mode == UnityEngine.SceneManagement.LoadSceneMode.Additive ? " (added)" : ""));
        }

        bool pictures, small, warm, partly, others;
        float lastFrame;
        readonly System.Collections.Generic.List<string> waits = new System.Collections.Generic.List<string>();

        void Update()
        {
            // (the textures: handed over once the game has listed its files, and what it made of the rest taken down once it is through them)
            if (pictures)
            {
                try { Textures.Step(); pictures = Textures.Busy; }
                catch (Exception ex) { pictures = false; Warm.Log("could not see to the textures (" + ex.Message + "): the game loads what is missing itself"); }
            }
            if (small) { Definitions.Step(); small = Definitions.Busy; }
            // (the long waits between one frame and the next, while the game starts: said at the main menu, for whoever wants to know where the time went)
            float now = Time.realtimeSinceStartup;
            if (menuAt < 0f && lastFrame > 0f && now - lastFrame > 0.3f && waits.Count < 40) waits.Add(lastFrame.ToString("F1") + " s: " + (now - lastFrame).ToString("F1"));
            lastFrame = now;
            if (menuAt >= 0f || HighLogic.LoadedScene != GameScenes.LOADING)
            {
                if (lifted) { Application.targetFrameRate = limitWas; lifted = false; }
                return;
            }
            // The game's loader does a slice of work and then lets a frame be drawn. How long a slice is, is the loading screen's to
            // say (LoadingScreen.minFrameTime): half a second as a rule, but nothing at all while one picture fades into the next,
            // which means one file a frame. It is put back to half a second before every slice, until the main menu is there.
            if (others) return;
            if (Warm.On && Warm.Fades && LoadingScreen.minFrameTime < 0.5f) { LoadingScreen.minFrameTime = 0.5f; hurried++; }
            // (the limit on frames a second: whatever sets it back while the game loads, it is lifted again)
            if (Warm.On && Warm.Frames && Application.targetFrameRate != Unlimited)
            {
                if (!lifted) limitWas = Application.targetFrameRate;
                Application.targetFrameRate = Unlimited; lifted = true;
            }
            frames++;
        }

        const int Unlimited = 100000;
        int hurried, frames, limitWas;
        bool lifted;

        void OnDestroy()
        {
            if (only != this) return;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= Scene;
            Definitions.End();
            GameEvents.onLevelWasLoadedGUIReady.Remove(Arrived);
            if (Warm.Page != null && Warm.Page.Panel == Panel) Warm.Page.Panel = null;
            only = null;
        }

        void Arrived(GameScenes scene)
        {
            if (scene != GameScenes.MAINMENU || menuAt >= 0f) return;
            menuAt = Time.realtimeSinceStartup;
            // How long this start took, from the game being started to the main menu being there, kept beside how long the last
            // start with nothing handed over took: so that the page can say what the mod is worth here.
            string took = menuAt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            // (warm: everything that is switched on had something kept to hand over. Cold: nothing had.)
            bool on = Warm.On && !others;
            bool some = handed || Textures.Kept > 0 || Definitions.Gave;
            bool all = on && (handed || !Warm.Expansions || !Bundles.Any) && (Textures.Kept > 0 || !Warm.Pictures) && (Definitions.Gave || !Warm.Small) && some;
            Warm.Log("the main menu after " + took + " s" + (all ? ", with everything that is kept handed over" : some ? ", with some of what is kept handed over" : ", with nothing handed over") + (hurried > 0 ? "; the loader was kept from waiting on a fade in " + hurried + " frames" : "") + "; " + frames + " frames were drawn while it loaded" + (lifted ? " (the limit of " + limitWas + " a second lifted)" : ""));
            if (lifted) { Application.targetFrameRate = limitWas; lifted = false; }
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= Scene;
            if (waits.Count > 0) Warm.Log("waits of more than 0.3 s between frames while the game started (when, how long): " + string.Join("; ", waits.ToArray()));
            if (all) Warm.Page.Keep("took", took); else if (!some) Warm.Page.Keep("cold", took);
            warm = all; partly = some && !all;
            // (asked for by hand, in the mod's settings file: "list = <a file>" writes down every texture the game has, for comparing starts)
            string list = Warm.Page.Kept("list");
            if (small) { Definitions.Step(); Definitions.End(); small = false; }
            if (!string.IsNullOrEmpty(list))
            {
                try { Textures.Tell(Warm.Folder + list); Definitions.Tell(Warm.Folder + "defs_" + list); }
                catch (Exception ex) { Warm.Log("could not write the lists for comparing (" + ex.Message + ")"); }
            }
            if (Warm.On && Warm.Expansions && !others) StartCoroutine(Bundles.Make(text => making = text));
        }

        void Panel()
        {
            string cold = Warm.Page.Kept("cold"), took = Warm.Page.Kept("took");
            if (others) { GUILayout.Label("KSP Community Fixes is installed, and loads the game its own way. Warm Start leaves everything to it.", Host.Small); return; }
            said = (menuAt >= 0f ? "This start took " + menuAt.ToString("F1") + " seconds to the main menu" + (warm ? "." : partly ? ", with some of what can be kept not kept yet." : ", with nothing kept to hand over.") : "Still starting.")
                 + (!string.IsNullOrEmpty(cold) && (warm || partly) ? " The last start with nothing handed over took " + cold + "." : "")
                 + (!string.IsNullOrEmpty(took) && !warm ? " The last with everything handed over took " + took + "." : "")
                 + "\n" + Bundles.Report + (Textures.Report.Length > 0 ? "\n" + Textures.Report : "") + (Definitions.Report.Length > 0 ? "\n" + Definitions.Report : "") + (making.Length > 0 ? "\n" + making : "");
            GUILayout.Label(said, Host.Small);
        }

        static GUIStyle words;

        void OnGUI()
        {
            // (while the copies are being made, a line at the foot of the main menu says so: it is a minute of the processor's time
            // that the player did not ask for, and should know about)
            if (making.Length == 0 || HighLogic.LoadedScene != GameScenes.MAINMENU || Event.current.type != EventType.Repaint) return;
            if (words == null) words = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.LowerLeft, wordWrap = false };
            words.normal.textColor = new Color(1f, 1f, 1f, 0.85f);
            GUI.Label(new Rect(12f, Screen.height - 34f, Screen.width - 24f, 26f), making, words);
        }
    }
}
