using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Thundergun.Editor
{
    /// <summary>
    /// Unity 2022.3.43f1 editor helpers for the Thundergun.
    /// Menu: Thundergun > 1. Import Thundergun, then Thundergun > 2. Build bundles.
    /// Batch: Unity.exe -batchmode -quit -projectPath ... -executeMethod Thundergun.Editor.TgBundleTools.RunAllBatch
    /// </summary>
    public static class TgBundleTools
    {
        private const string BuildDir = "AssetBundles";
        // Only this install. Do NOT point at D:\OTHER LAUNCHERS\BSTATE GAMES.
        private const string ServerModDir = @"D:\OTHER LAUNCHERS\BUTTERSTATE\SPT_Runtime\user\mods\Thundergun";
        // Bundle keys are paths under the server mod's bundles folder; the item templates point at the same keys
        private const string KeyFolder = "Thundergun";

        private static readonly (string bundle, string asset)[] Bundles =
        {
            ($"{TgPaths.ContainerName}.bundle", TgImporter.ContainerPrefabPath),
            ($"{TgPaths.CanisterName}.bundle", TgImporter.CanisterPrefabPath),
        };

        [MenuItem("Thundergun/2. Build bundles")]
        public static void BuildMenu()
        {
            if (Build())
                EditorUtility.RevealInFinder(Path.Combine(ServerModDir, "bundles"));
        }

        public static bool Build()
        {
            foreach (var (_, asset) in Bundles)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(asset) == null)
                {
                    Debug.LogError($"[Thundergun] No prefab at {asset}. Run the import first.");
                    return false;
                }
            }

            Directory.CreateDirectory(BuildDir);
            // EFT finds the prefab by name: the asset whose name is the bundle's file name without the extension
            AssetBundleBuild[] builds = Bundles.Select(b => new AssetBundleBuild { assetBundleName = b.bundle, assetNames = new[] { b.asset } }).ToArray();
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(BuildDir, builds, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
            if (manifest == null)
            {
                Debug.LogError("[Thundergun] Bundle build failed; see console.");
                return false;
            }

            string dest = Path.Combine(ServerModDir, "bundles", KeyFolder);
            Directory.CreateDirectory(dest);
            foreach (var (bundle, _) in Bundles)
            {
                File.Copy(Path.Combine(BuildDir, bundle), Path.Combine(dest, bundle), true);
                Debug.Log($"[Thundergun] {bundle}: {new FileInfo(Path.Combine(dest, bundle)).Length / 1024} KB");
            }

            // SPT loads a mod's bundles from bundles.json next to its DLL. Each bundle is self-contained (its shaders
            // and textures are inside), so nothing depends on the game's own bundles.
            string entries = string.Join(",\n", Bundles.Select(b => $"    {{ \"key\": \"{KeyFolder}/{b.bundle}\", \"dependencyKeys\": [] }}"));
            File.WriteAllText(Path.Combine(ServerModDir, "bundles.json"), "{\n  \"manifest\": [\n" + entries + "\n  ]\n}\n");
            Debug.Log($"[Thundergun] Built and copied bundles -> {dest}");
            return true;
        }

        public static void RunAllBatch()
        {
            try
            {
                TgImporter.ImportAll();
                TgPreview.RenderBatch();
                TgIcons.RenderAll();
                if (!Build())
                    EditorApplication.Exit(1);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>Import and previews only (no bundles), for checking the retarget.</summary>
        public static void PreviewBatch()
        {
            try
            {
                TgImporter.ImportAll();
                TgPreview.RenderBatch();
                TgIcons.RenderAll();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }
    }
}
