using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class UrpFixWhiteSceneVisuals
{
    private const string ScenePath = "Assets/Scenes/BlenderIndoorScene.unity";
    private const string ReportPath = "Assets/SceneExport/URP_white_scene_visual_fix_report.txt";

    [MenuItem("Tools/URP Copy/Fix White Scene Visuals")]
    public static void FixWhiteSceneVisuals()
    {
        EditorSceneManager.OpenScene(ScenePath);
        var report = new StringBuilder();
        report.AppendLine("URP white scene visual fix");
        report.AppendLine("Scene: " + ScenePath);
        report.AppendLine();

        var planeFixes = FixWindowPlanes(report);
        var whiteMaterialFixes = SoftenWhiteNoTextureMaterials(report);
        var lightFixes = ClampRealtimeLightEnergy(report);
        var exposureFixes = ReduceExposure(report);

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        report.AppendLine();
        report.AppendLine("Plane material fixes: " + planeFixes);
        report.AppendLine("White no-texture materials softened: " + whiteMaterialFixes);
        report.AppendLine("Realtime lights clamped: " + lightFixes);
        report.AppendLine("Exposure/volume settings changed: " + exposureFixes);

        var reportFile = ProjectFile(ReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportFile));
        File.WriteAllText(reportFile, report.ToString(), Encoding.UTF8);
        AssetDatabase.ImportAsset(ReportPath);
        Debug.Log(report.ToString());
    }

    private static int FixWindowPlanes(StringBuilder report)
    {
        var changes = 0;
        changes += AssignMaterialByRendererName("平面", "Assets/SceneExport/Materials/平面_Visible.mat", report);
        changes += AssignMaterialByRendererName("平面.001", "Assets/SceneExport/Materials/平面.001_Visible.mat", report);

        foreach (var path in new[] { "Assets/SceneExport/Materials/平面_Visible.mat", "Assets/SceneExport/Materials/平面.001_Visible.mat" })
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                continue;
            }

            var texture = GetTexture(material, "_BaseMap", "_MainTex");
            if (texture != null)
            {
                changes += SetTexture(material, "_BaseMap", texture) ? 1 : 0;
                changes += SetTexture(material, "_MainTex", texture) ? 1 : 0;
            }

            changes += SetColor(material, "_BaseColor", Color.white) ? 1 : 0;
            changes += SetColor(material, "_Color", Color.white) ? 1 : 0;
            SetFloat(material, "_Surface", 0f);
            SetFloat(material, "_Blend", 0f);
            SetFloat(material, "_ZWrite", 1f);
            SetFloat(material, "_Cull", 0f);
            material.renderQueue = (int)RenderQueue.Geometry;
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            EditorUtility.SetDirty(material);
        }

        return changes;
    }

    private static int AssignMaterialByRendererName(string rendererName, string materialPath, StringBuilder report)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            report.AppendLine("Missing plane material: " + materialPath);
            return 0;
        }

        var changes = 0;
        foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (renderer == null || renderer.name != rendererName || !renderer.gameObject.scene.IsValid())
            {
                continue;
            }

            var materials = renderer.sharedMaterials;
            if (materials.Length == 0)
            {
                materials = new[] { material };
                changes++;
            }
            else if (materials[0] != material)
            {
                materials[0] = material;
                changes++;
            }

            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            EditorUtility.SetDirty(renderer);
            report.AppendLine("Assigned " + materialPath + " -> " + HierarchyPath(renderer.transform));
        }

        return changes;
    }

    private static int SoftenWhiteNoTextureMaterials(StringBuilder report)
    {
        var changed = 0;
        var visited = new HashSet<Material>();
        var softWhite = new Color(0.64f, 0.62f, 0.58f, 1f);

        foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (renderer == null || !renderer.gameObject.scene.IsValid())
            {
                continue;
            }

            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null || !visited.Add(material) || HasTexture(material, "_BaseMap", "_MainTex"))
                {
                    continue;
                }

                if (IsSpecialMaterial(material))
                {
                    continue;
                }

                var current = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") :
                    material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
                if (!IsBrightNeutral(current))
                {
                    continue;
                }

                SetColor(material, "_BaseColor", softWhite);
                SetColor(material, "_Color", softWhite);
                SetFloat(material, "_Metallic", 0f);
                SetFloat(material, "_Smoothness", 0.32f);
                EditorUtility.SetDirty(material);
                changed++;
                report.AppendLine("Softened bright/no-texture material: " + material.name + " | " + AssetDatabase.GetAssetPath(material));
            }
        }

        return changed;
    }

    private static bool IsSpecialMaterial(Material material)
    {
        var name = material.name.ToLowerInvariant();
        return name.Contains("glass")
            || name.Contains("window")
            || name.Contains("transparent")
            || name.Contains("backdrop");
    }

    private static int ClampRealtimeLightEnergy(StringBuilder report)
    {
        var changed = 0;
        foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (light == null || !light.gameObject.scene.IsValid() || !light.enabled)
            {
                continue;
            }

            var max = MaxIntensityFor(light);
            if (light.intensity <= max)
            {
                continue;
            }

            report.AppendLine("Clamped light " + HierarchyPath(light.transform) + " " + light.type + " intensity " + light.intensity.ToString("0.###") + " -> " + max.ToString("0.###"));
            light.intensity = max;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = Mathf.Min(light.shadowStrength, 0.65f);
            EditorUtility.SetDirty(light);
            changed++;
        }

        return changed;
    }

    private static float MaxIntensityFor(Light light)
    {
        switch (light.type)
        {
            case LightType.Directional:
                return 0.42f;
            case LightType.Spot:
                return 0.85f;
            case LightType.Point:
                return 0.75f;
            case LightType.Rectangle:
            case LightType.Disc:
                return 1.2f;
            default:
                return 0.75f;
        }
    }

    private static int ReduceExposure(StringBuilder report)
    {
        var changed = 0;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 0.42f;
        RenderSettings.reflectionIntensity = 0.3f;
        changed += 3;

        if (RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Exposure"))
        {
            RenderSettings.skybox.SetFloat("_Exposure", 0.24f);
            EditorUtility.SetDirty(RenderSettings.skybox);
            changed++;
            report.AppendLine("Skybox exposure set to 0.24");
        }

        foreach (var volume in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (volume == null || volume.profile == null || !volume.gameObject.scene.IsValid())
            {
                continue;
            }

            if (volume.profile.TryGet<ColorAdjustments>(out var colorAdjustments))
            {
                colorAdjustments.postExposure.Override(-1.75f);
                colorAdjustments.contrast.Override(3f);
                colorAdjustments.saturation.Override(3f);
                changed += 3;
                report.AppendLine("Volume color exposure reduced: " + HierarchyPath(volume.transform));
            }

            if (volume.profile.TryGet<Bloom>(out var bloom))
            {
                bloom.intensity.Override(0.005f);
                bloom.threshold.Override(1.55f);
                bloom.scatter.Override(0.35f);
                changed += 3;
                report.AppendLine("Volume bloom softened: " + HierarchyPath(volume.transform));
            }

            if (volume.profile.TryGet<Tonemapping>(out var tonemapping))
            {
                tonemapping.mode.Override(TonemappingMode.ACES);
                changed++;
            }

            EditorUtility.SetDirty(volume.profile);
        }

        foreach (var camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (camera == null || !camera.gameObject.scene.IsValid())
            {
                continue;
            }

            camera.allowHDR = true;
            var data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            EditorUtility.SetDirty(camera);
            EditorUtility.SetDirty(data);
            changed++;
        }

        return changed;
    }

    private static Texture GetTexture(Material material, params string[] names)
    {
        foreach (var name in names)
        {
            if (material.HasProperty(name))
            {
                var texture = material.GetTexture(name);
                if (texture != null)
                {
                    return texture;
                }
            }
        }

        return null;
    }

    private static bool HasTexture(Material material, params string[] names)
    {
        return GetTexture(material, names) != null;
    }

    private static bool SetTexture(Material material, string name, Texture texture)
    {
        if (texture == null || !material.HasProperty(name) || material.GetTexture(name) == texture)
        {
            return false;
        }

        material.SetTexture(name, texture);
        EditorUtility.SetDirty(material);
        return true;
    }

    private static bool SetColor(Material material, string name, Color color)
    {
        if (!material.HasProperty(name))
        {
            return false;
        }

        var existing = material.GetColor(name);
        if (Mathf.Abs(existing.r - color.r) < 0.001f
            && Mathf.Abs(existing.g - color.g) < 0.001f
            && Mathf.Abs(existing.b - color.b) < 0.001f
            && Mathf.Abs(existing.a - color.a) < 0.001f)
        {
            return false;
        }

        material.SetColor(name, color);
        EditorUtility.SetDirty(material);
        return true;
    }

    private static void SetFloat(Material material, string name, float value)
    {
        if (material.HasProperty(name))
        {
            material.SetFloat(name, value);
            EditorUtility.SetDirty(material);
        }
    }

    private static bool IsWhite(Color color)
    {
        return color.r > 0.96f && color.g > 0.96f && color.b > 0.96f && color.a > 0.96f;
    }

    private static bool IsBrightNeutral(Color color)
    {
        return color.r > 0.72f && color.g > 0.72f && color.b > 0.72f && color.a > 0.96f;
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
