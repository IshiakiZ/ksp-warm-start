using System;
using System.Reflection;
using HarmonyLib;

namespace WarmStartHarmony
{
    /// <summary>
    /// KSP Community Fixes' own loading of the expansions' files (its patch ExpansionBundlePreload) held back wherever Warm Start
    /// has handed the game its unpacked copies of them, so that Warm Start's copies serve beside it too.
    ///
    /// Asked for by the owner, 2026-10-24 ("go ahead with warmstart"), as Warm Start taking the game's loading back from KSP
    /// Community Fixes, where it used to leave all of it to that mod. Measured first, on the PC (Windows 11, KSP 1.12.5, KSP
    /// Community Fixes 1.41.1, this workspace's mods), seconds to the main menu by Warm Start's own line:
    ///
    ///   Warm Start alone                                            19.2, 17.5, 17.7
    ///   both, KSP Community Fixes loading (Warm Start as before)    26.7, 26.3, 26.7
    ///   both, its loader put aside and Warm Start loading           27.6, 27.6
    ///
    /// So its loader was no slower than Warm Start's way; what cost the time was the expansions. Its ExpansionBundlePreload
    /// replaces the game's ExpansionsLoader.StartLoad and loads every expansion from its packed file, whatever is on the game's
    /// list of loaded bundles, and the main menu waited on it: Making History and Breaking Ground ready 18.3 s into the start,
    /// against 9.0 with Warm Start's copies handed over (the game's own loader passes over a bundle already on that list:
    /// "already loaded - skipping", which is how they are handed over). With only that held back:
    ///
    ///   both, KSP Community Fixes loading, the expansions handed over   18.4 (20.8 at the start that made the drag cubes again)
    ///   both, its loader put aside as well                             19.2, 19.5, 19.3
    ///
    /// So only this is done. (The loader put aside was tried on the way: every patch of its KSPCFFastLoader taken out, its
    /// look-ups by name with them. A second slower, and far more of another mod's insides touched: not kept.)
    ///
    /// Held back the way KSP Community Fixes itself holds a patch back: its CanApplyPatch says no, with the reason, and it writes
    /// "Patch ExpansionBundlePreload not applied (...)" in the log. That is asked once ModuleManager is through, long after Warm
    /// Start has handed the copies over at the very start; where it handed none over (no copies made yet, or their files
    /// changed), the patch goes in as ever.
    /// </summary>
    public static class Aside
    {
        const string Id = "warmstart.aside";

        /// <summary>Puts the hold in place: true once it is. Where it cannot be, why not is said, and Warm Start hands nothing over (the copies would be loaded twice).</summary>
        public static bool Preload(out string said)
        {
            Assembly theirs = null;
            foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
                if (a != null && a.name == "KSPCommunityFixes") { theirs = a.assembly; break; }
            Type preload = theirs != null ? theirs.GetType("KSPCommunityFixes.Performance.ExpansionBundlePreload", false) : null;
            MethodInfo can = preload != null ? preload.GetMethod("CanApplyPatch", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string).MakeByRefType() }, null) : null;
            // (its own override only: never the one every patch of theirs shares)
            if (can == null || can.ReturnType != typeof(bool) || can.DeclaringType != preload)
            {
                said = theirs == null ? "it is not loaded" : "its loading of the expansions is not as it was when this was written (KSP Community Fixes 1.41.1)";
                return false;
            }
            new Harmony(Id).Patch(can, new HarmonyMethod(typeof(Aside).GetMethod(nameof(Held), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
            said = "";
            return true;
        }

        static bool Held(ref bool __result, ref string reason)
        {
            if (!WarmStart.Companion.Handed) return true;
            reason = "Warm Start has handed the game its unpacked copies of the expansions' files";
            __result = false;
            return false;
        }
    }
}
