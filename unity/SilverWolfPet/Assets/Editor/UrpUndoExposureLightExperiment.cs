using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class UrpUndoExposureLightExperiment
{
    private const string ScenePath = "Assets/Scenes/BlenderIndoorScene.unity";
    private const string SkyboxPath = "Assets/URP_Quality/URP_HDRI_Skybox.mat";
    private const string ReportPath = "Assets/SceneExport/URP_undo_exposure_light_experiment_report.txt";

    [MenuItem("Tools/URP Copy/Undo Exposure Light Experiment")]
    public static void RestorePreviousLightingAndExposure()
    {
        EditorSceneManager.OpenScene(ScenePath);

        var report = new StringBuilder();
        report.AppendLine("Undo exposure/light experiment");
        report.AppendLine("Scene: " + ScenePath);
        report.AppendLine();

        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1.05f;
        RenderSettings.reflectionIntensity = 1.05f;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;

        var skybox = AssetDatabase.LoadAssetAtPath<Material>(SkyboxPath);
        if (skybox != null)
        {
            RenderSettings.skybox = skybox;
            if (skybox.HasProperty("_Exposure"))
            {
                skybox.SetFloat("_Exposure", 1.05f);
            }
            if (skybox.HasProperty("_Tint"))
            {
                skybox.SetColor("_Tint", new Color(1f, 0.96f, 0.88f, 1f));
            }
            EditorUtility.SetDirty(skybox);
            report.AppendLine("Skybox exposure restored to 1.05");
        }

        RestoreVolume(report);
        RestoreLight("Window Realtime Projection - soft sun spot 1", 8.2f, report);
        RestoreLight("Window Realtime Projection - soft sun spot 2", 8.2f, report);
        RestoreLight("Window Realtime Projection - soft sun spot 3", 6.5f, report);
        RestoreLight("Window Realtime Projection - soft sun spot 4", 8.2f, report);
        RestoreLight("Window Realtime Projection - soft sun spot 5", 8.2f, report);
        RestoreLight("面光", 1.8f, report);
        RestoreLight("面光.001", 11.781f, report);
        RestoreLight("面光.001 Realtime Proxy", 2.8f, report);
        RestoreLight("面光.003 Realtime Proxy", 0.8267146f, report);

        DynamicGI.UpdateEnvironment();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        var reportFile = ProjectFile(ReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportFile));
        File.WriteAllText(reportFile, report.ToString(), Encoding.UTF8);
        AssetDatabase.ImportAsset(ReportPath);
        Debug.Log(report.ToString());
    }

    private static void RestoreVolume(StringBuilder report)
    {
        foreach (var volume in Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (volume == null || volume.profile == null || !volume.gameObject.scene.IsValid())
            {
                continue;
            }

            if (volume.profile.TryGet<Bloom>(out var bloom))
            {
                bloom.intensity.Override(0.08f);
                bloom.threshold.Override(1.05f);
                bloom.scatter.Override(0.45f);
            }

            if (volume.profile.TryGet<ColorAdjustments>(out var color))
            {
                color.postExposure.Override(0.08f);
                color.contrast.Override(8f);
                color.saturation.Override(7f);
                color.colorFilter.Override(new Color(1f, 0.98f, 0.93f, 1f));
            }

            if (volume.profile.TryGet<Tonemapping>(out var tonemapping))
            {
                tonemapping.mode.Override(TonemappingMode.ACES);
            }

            EditorUtility.SetDirty(volume.profile);
            report.AppendLine("Volume restored: " + HierarchyPath(volume.transform));
        }
    }

    private static void RestoreLight(string name, float intensity, StringBuilder report)
    {
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (light == null || light.name != name || !light.gameObject.scene.IsValid())
            {
                continue;
            }

            var old = light.intensity;
            light.intensity = intensity;
            EditorUtility.SetDirty(light);
            report.AppendLine("Light " + HierarchyPath(light.transform) + " intensity " + old.ToString("0.###") + " -> " + intensity.ToString("0.###"));
        }
    }

    private static string HierarchyPath(Transform transform)
    {
        var stack = new Stack<string>();
        var current = transform;
        while (current != null)
        {
            stack.Push(current.name);
            current = current.parent;
        }
        return string.Join("/", stack);
    }

    private static string ProjectFile(string assetPath)
    {
        return Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath.Replace('/', Path.DirectorySeparatorChar));
    }
}
