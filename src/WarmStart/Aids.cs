#if DEV
using System.Collections.Generic;
using KSPAssets;
using KSPAssets.KSPedia;
using KSPAssets.Loaders;

namespace WarmStart
{
    /// <summary>For testing, in the development build only (called through the bridge's dev_call).</summary>
    public static class Aids
    {
        /// <summary>Opens the game's manual, whose pages are in the small bundles: the test that their kept descriptions serve.</summary>
        public static string Manual()
        {
            KSPediaSpawner.Show();
            return "the manual is asked for; the game's reader of bundles is " + (AssetLoader.Ready ? "ready" : "not ready") + ", with " + AssetLoader.AssetDefinitions.Count + " assets described and " + AssetLoader.LoadedBundles.Count + " bundles open";
        }

        /// <summary>Where every launch site is: for comparing one start with another.</summary>
        public static string Sites()
        {
            var lines = new List<string>();
            if (PSystemSetup.Instance == null) return "no launch sites yet";
            foreach (LaunchSite site in PSystemSetup.Instance.LaunchSites)
            {
                UnityEngine.Transform t = site.pqsCity != null ? site.pqsCity.transform : site.pqsCity2 != null ? site.pqsCity2.transform : null;
                lines.Add(site.name + ": " + (t != null ? t.localPosition.ToString("F3") + " " + t.localRotation.eulerAngles.ToString("F3") : "(no place)"));
            }
            return string.Join("\n", lines.ToArray());
        }
    }
}
#endif
