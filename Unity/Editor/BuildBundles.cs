// Builds three bundles from the L115 prefabs and everything they use (meshes, materials, textures, M700 controller + clips + StaticData):
//   l115_awm.bundle           <- weapon_l115_awm_container.prefab   (step 4 output)
//   mag_l115_awm_5rnd.bundle  <- mag_l115_awm_5rnd.prefab
//   mag_l115_awm_8rnd.bundle  <- mag_l115_awm_8rnd.prefab
// Scripts are not assets: they are stored as references to Assembly-CSharp.
// Menu: Tools > Build AssetBundles   ->  AssetBundles/*.bundle
// Copy them to:  <mod>/bundles/weapons/l115_awm.bundle   and   <mod>/bundles/mags/mag_l115_awm_5rnd.bundle (+ 8rnd)
// Use the Unity version the game's own bundles were built with (2022.3.43f1 for SPT 4.1.x).
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildBundles
{
    const string OutDir = "AssetBundles";

    static readonly string[,] Targets =
    {
        { "Assets/L115/Prefabs/weapon_l115_awm_container.prefab", "l115_awm.bundle" },
        { "Assets/L115/Prefabs/mag_l115_awm_5rnd.prefab",         "mag_l115_awm_5rnd.bundle" },
        { "Assets/L115/Prefabs/mag_l115_awm_8rnd.prefab",         "mag_l115_awm_8rnd.bundle" },
    };

    [MenuItem("Tools/Build AssetBundles")]
    static void Build()
    {
        // 1. dependencies of every target
        var deps = new List<HashSet<string>>();
        var names = new List<string>();
        for (int i = 0; i < Targets.GetLength(0); i++)
        {
            string prefab = Targets[i, 0];
            if (!File.Exists(prefab)) { Debug.LogWarning("[L115] Skipped " + Targets[i, 1] + ": missing " + prefab); continue; }
            var set = new HashSet<string>();
            foreach (var p in AssetDatabase.GetDependencies(prefab, true))
            {
                if (!p.StartsWith("Assets/")) continue;                              // built-in / package assets are not bundled
                if (p.EndsWith(".cs") || p.EndsWith(".dll") || p.EndsWith(".asmdef")) continue;
                set.Add(p);
            }
            deps.Add(set); names.Add(Targets[i, 1]);
        }
        if (deps.Count == 0) { EditorUtility.DisplayDialog("L115", "Nothing to build.", "OK"); return; }

        // 2. an asset may live in ONE bundle only: anything used by more than one target goes into the first (weapon) bundle.
        //    The magazine bundles then depend on it (bundles.json lists weapons/l115_awm.bundle as their dependency).
        var owner = new Dictionary<string, int>();
        for (int i = 0; i < deps.Count; i++)
            foreach (var a in deps[i])
                owner[a] = owner.ContainsKey(a) ? 0 : i;

        var builds = new List<AssetBundleBuild>();
        for (int i = 0; i < deps.Count; i++)
        {
            var list = new List<string>();
            foreach (var a in deps[i]) if (owner[a] == i) list.Add(a);
            Debug.Log("[L115] " + names[i] + ": " + list.Count + " assets");
            builds.Add(new AssetBundleBuild { assetBundleName = names[i], assetNames = list.ToArray() });
        }

        Directory.CreateDirectory(OutDir);
        var manifest = BuildPipeline.BuildAssetBundles(OutDir, builds.ToArray(),
            BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        if (manifest == null) { Debug.LogError("[L115] Bundle build failed, see the console above."); return; }
        EditorUtility.RevealInFinder(Path.Combine(OutDir, builds[0].assetBundleName));
    }
}
#endif
