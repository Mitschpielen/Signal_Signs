using System.IO;
using UnityEditor;
using UnityEngine;

public class BuildAssetBundles
{
    [MenuItem("Peak/Build AssetBundles")]
    static void BuildAll()
    {
        // Ordner im Projekt-Root (neben Assets/, Packages/, ...)
        string outputPath = Path.Combine(Directory.GetCurrentDirectory(), "AssetBundles");

        if (!Directory.Exists(outputPath))
            Directory.CreateDirectory(outputPath);

        BuildPipeline.BuildAssetBundles(
            outputPath,
            BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64
        );

        Debug.Log("AssetBundles gebaut nach: " + outputPath);
    }
}