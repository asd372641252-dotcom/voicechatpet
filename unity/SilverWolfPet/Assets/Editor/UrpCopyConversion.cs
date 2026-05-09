using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class UrpCopyConversion
{
    private const string ScenePath = "Assets/Scenes/BlenderIndoorScene.unity";
    private const string SettingsFolder = "Assets/URP_转换设置";
    private const string PipelineAssetPath = SettingsFolder + "/URP_转换版_PipelineAsset.asset";
    private const string RendererAssetPath = SettingsFolder + "/URP_转换版_Renderer.asset";
    private const string ReportPath = "Assets/SceneExport/URP转换报告.txt";

    public static void InstallUniversalRp()
    {
        var request = Client.Add("com.unity.render-pipelines.universal");
        while (!request.IsCompleted)
        {
            Thread.Sleep(250);
        }

        if (request.Status == StatusCode.Failure)
        {
            Debug.LogError("URP package install failed: " + request.Error.message);
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log("URP package installed: " + request.Result.packageId);
    }

    public static void ConvertCopyToUrp()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Directory.CreateDirectory(SettingsFolder);

        var report = new System.Text.StringBuilder();
        report.AppendLine("URP conversion report");
        report.AppendLine("Project label: 场景工程_URP转换版_20260503");
        report.AppendLine("Original project preserved: D:/pet/场景工程");

        var pipelineAsset = EnsureUrpPipelineAssets(report);
        UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline = pipelineAsset;
        QualitySettings.renderPipeline = pipelineAsset;

        ConfigureUrpQuality();
        ConvertStandardMaterials(report);
        ConfigureCameraForUrp(report);
        ConfigureSceneView();

        report.AppendLine("GraphicsSettings.defaultRenderPipeline: " + AssetDatabase.GetAssetPath(UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline));
        report.AppendLine("QualitySettings.renderPipeline: " + AssetDatabase.GetAssetPath(QualitySettings.renderPipeline));

        File.WriteAllText(ReportPath, report.ToString());
        AssetDatabase.ImportAsset(ReportPath);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log(report.ToString());
    }

    public static void OpenUrpCopyForViewing()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var root = GameObject.Find("Blender Indoor Scene");
        if (root != null)
        {
            Selection.activeObject = root;
            EditorGUIUtility.PingObject(root);
        }

        var bounds = GetSceneRendererBounds();
        var viewSize = Mathf.Max(4f, Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) * 0.75f);
        foreach (SceneView sceneView in SceneView.sceneViews)
        {
            sceneView.in2DMode = false;
            sceneView.orthographic = false;
            sceneView.sceneLighting = true;
            sceneView.sceneViewState.showSkybox = true;
            sceneView.LookAt(bounds.center, Quaternion.Euler(24f, -135f, 0f), viewSize, false, true);
            sceneView.Repaint();
        }
    }

    private static UnityEngine.Rendering.RenderPipelineAsset EnsureUrpPipelineAssets(System.Text.StringBuilder report)
    {
        var pipelineType = FindType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset");
        var rendererDataType = FindType("UnityEngine.Rendering.Universal.UniversalRendererData");
        if (pipelineType == null || rendererDataType == null)
        {
            throw new InvalidOperationException("URP runtime types were not found. Install com.unity.render-pipelines.universal first.");
        }

        var rendererData = AssetDatabase.LoadAssetAtPath<ScriptableObject>(RendererAssetPath);
        if (rendererData == null)
        {
            rendererData = ScriptableObject.CreateInstance(rendererDataType);
            AssetDatabase.CreateAsset(rendererData, RendererAssetPath);
        }

        var pipelineAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.RenderPipelineAsset>(PipelineAssetPath);
        if (pipelineAsset == null)
        {
            pipelineAsset = ScriptableObject.CreateInstance(pipelineType) as UnityEngine.Rendering.RenderPipelineAsset;
            AssetDatabase.CreateAsset(pipelineAsset, PipelineAssetPath);
        }

        WireRendererData(pipelineAsset, rendererData);
        report.AppendLine("Created/updated URP Pipeline Asset: " + PipelineAssetPath);
        report.AppendLine("Created/updated URP Renderer: " + RendererAssetPath);
        return pipelineAsset;
    }

    private static void WireRendererData(UnityEngine.Object pipelineAsset, UnityEngine.Object rendererData)
    {
        var serialized = new SerializedObject(pipelineAsset);
        var list = serialized.FindProperty("m_RendererDataList");
        if (list != null)
        {
            list.arraySize = Mathf.Max(1, list.arraySize);
            list.GetArrayElementAtIndex(0).objectReferenceValue = rendererData;
        }

        var renderer = serialized.FindProperty("m_RendererData");
        if (renderer != null)
        {
            renderer.objectReferenceValue = rendererData;
        }

        var defaultRenderer = serialized.FindProperty("m_DefaultRendererIndex");
        if (defaultRenderer != null)
        {
            defaultRenderer.intValue = 0;
        }

        SetSerializedBool(serialized, "m_SupportsHDR", true);
        SetSerializedBool(serialized, "m_UseSRPBatcher", true);
        SetSerializedInt(serialized, "m_MSAA", 4);
        SetSerializedFloat(serialized, "m_RenderScale", 1f);
        SetSerializedFloat(serialized, "m_ShadowDistance", 35f);
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(pipelineAsset);
        EditorUtility.SetDirty(rendererData);
    }

    private static void ConvertStandardMaterials(System.Text.StringBuilder report)
    {
        var urpLit = Shader.Find("Universal Render Pipeline/Lit");
        var urpUnlit = Shader.Find("Universal Render Pipeline/Unlit");
        if (urpLit == null)
        {
            throw new InvalidOperationException("URP Lit shader was not found.");
        }

        var materials = GetSceneMaterials()
            .Concat(AssetDatabase.FindAssets("t:Material", new[] { "Assets/SceneExport" }).Select(guid => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid))))
            .Where(material => material != null)
            .Distinct()
            .OrderBy(material => material.name)
            .ToArray();

        var converted = 0;
        var transparent = 0;
        var skipped = 0;
        foreach (var material in materials)
        {
            var oldShader = material.shader != null ? material.shader.name : string.Empty;
            if (oldShader.Contains("Skybox"))
            {
                skipped++;
                continue;
            }

            var isDecal = material.name.Contains("Window_Shadow_Decal");
            var isTransparent = IsTransparentMaterial(material);
            var mainTex = GetFirstTexture(material, "_BaseMap", "_MainTex");
            var color = GetColor(material, "_BaseColor", GetColor(material, "_Color", Color.white));
            var normal = GetFirstTexture(material, "_BumpMap", "_NormalMap");
            var metallicGloss = GetFirstTexture(material, "_MetallicGlossMap");
            var occlusion = GetFirstTexture(material, "_OcclusionMap");
            var emission = GetFirstTexture(material, "_EmissionMap");
            var emissionColor = GetColor(material, "_EmissionColor", Color.black);
            var metallic = GetFloat(material, "_Metallic", 0f);
            var smoothness = GetFloat(material, "_Glossiness", GetFloat(material, "_Smoothness", 0.45f));

            material.shader = isDecal && urpUnlit != null ? urpUnlit : urpLit;
            SetTexture(material, "_BaseMap", mainTex);
            SetColor(material, "_BaseColor", color);
            SetFloat(material, "_Metallic", metallic);
            SetFloat(material, "_Smoothness", smoothness);
            SetTexture(material, "_BumpMap", normal);
            SetTexture(material, "_MetallicGlossMap", metallicGloss);
            SetTexture(material, "_OcclusionMap", occlusion);
            SetFloat(material, "_OcclusionStrength", GetFloat(material, "_OcclusionStrength", 1f));
            SetTexture(material, "_EmissionMap", emission);
            SetColor(material, "_EmissionColor", emissionColor);

            if (normal != null)
            {
                material.EnableKeyword("_NORMALMAP");
            }
            if (metallicGloss != null)
            {
                material.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
            if (occlusion != null)
            {
                material.EnableKeyword("_OCCLUSIONMAP");
            }
            if (emission != null || emissionColor.maxColorComponent > 0.01f)
            {
                material.EnableKeyword("_EMISSION");
            }

            ConfigureUrpSurface(material, isTransparent || isDecal);
            if (isTransparent || isDecal)
            {
                transparent++;
            }

            EditorUtility.SetDirty(material);
            converted++;
        }

        report.AppendLine("Materials scanned: " + materials.Length);
        report.AppendLine("Materials converted to URP shader: " + converted);
        report.AppendLine("Transparent materials configured: " + transparent);
        report.AppendLine("Skybox/materials skipped: " + skipped);
    }

    private static void ConfigureUrpSurface(Material material, bool transparent)
    {
        SetFloat(material, "_Surface", transparent ? 1f : 0f);
        SetFloat(material, "_Blend", 0f);
        SetFloat(material, "_AlphaClip", 0f);
        SetFloat(material, "_SrcBlend", transparent ? (float)UnityEngine.Rendering.BlendMode.SrcAlpha : (float)UnityEngine.Rendering.BlendMode.One);
        SetFloat(material, "_DstBlend", transparent ? (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : (float)UnityEngine.Rendering.BlendMode.Zero);
        SetFloat(material, "_ZWrite", transparent ? 0f : 1f);
        SetFloat(material, "_Cull", 2f);
        SetFloat(material, "_ReceiveShadows", 1f);
        material.SetOverrideTag("RenderType", transparent ? "Transparent" : "Opaque");
        material.renderQueue = transparent ? (int)UnityEngine.Rendering.RenderQueue.Transparent : -1;
        if (transparent)
        {
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }
        else
        {
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }
    }

    private static void ConfigureCameraForUrp(System.Text.StringBuilder report)
    {
        var count = 0;
        foreach (var camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            camera.allowHDR = true;
            camera.allowMSAA = true;
            camera.clearFlags = CameraClearFlags.Skybox;
            EditorUtility.SetDirty(camera);
            count++;
        }
        report.AppendLine("Cameras configured for URP HDR/MSAA: " + count);
    }

    private static void ConfigureUrpQuality()
    {
        QualitySettings.pixelLightCount = Mathf.Max(QualitySettings.pixelLightCount, 8);
        QualitySettings.shadows = ShadowQuality.All;
        QualitySettings.shadowResolution = ShadowResolution.High;
        QualitySettings.shadowDistance = Mathf.Max(QualitySettings.shadowDistance, 35f);
        QualitySettings.antiAliasing = Mathf.Max(QualitySettings.antiAliasing, 4);
        QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
        QualitySettings.realtimeReflectionProbes = true;
    }

    private static void ConfigureSceneView()
    {
        foreach (SceneView sceneView in SceneView.sceneViews)
        {
            sceneView.sceneLighting = true;
            sceneView.sceneViewState.showSkybox = true;
            sceneView.Repaint();
        }
    }

    private static Type FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName);
            if (type != null)
            {
                return type;
            }
        }
        return null;
    }

    private static void SetSerializedBool(SerializedObject obj, string propertyName, bool value)
    {
        var property = obj.FindProperty(propertyName);
        if (property != null)
        {
            property.boolValue = value;
        }
    }

    private static void SetSerializedInt(SerializedObject obj, string propertyName, int value)
    {
        var property = obj.FindProperty(propertyName);
        if (property != null)
        {
            property.intValue = value;
        }
    }

    private static void SetSerializedFloat(SerializedObject obj, string propertyName, float value)
    {
        var property = obj.FindProperty(propertyName);
        if (property != null)
        {
            property.floatValue = value;
        }
    }

    private static HashSet<Material> GetSceneMaterials()
    {
        var materials = new HashSet<Material>();
        foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            foreach (var material in renderer.sharedMaterials)
            {
                if (material != null)
                {
                    materials.Add(material);
                }
            }
        }
        return materials;
    }

    private static bool IsTransparentMaterial(Material material)
    {
        if (material == null)
        {
            return false;
        }
        var shaderName = material.shader != null ? material.shader.name : string.Empty;
        var color = GetColor(material, "_BaseColor", GetColor(material, "_Color", Color.white));
        return color.a < 0.96f
            || material.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent
            || GetFloat(material, "_Mode", 0f) >= 2f
            || material.name.Contains("TransparentGlass")
            || material.name.Contains("Window_Backdrop")
            || shaderName.Contains("Transparent");
    }

    private static Bounds GetSceneRendererBounds()
    {
        var initialized = false;
        var bounds = new Bounds(Vector3.zero, Vector3.one);
        foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!initialized)
            {
                bounds = renderer.bounds;
                initialized = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }
        return bounds;
    }

    private static Texture GetFirstTexture(Material material, params string[] names)
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

    private static void SetTexture(Material material, string propertyName, Texture texture)
    {
        if (texture != null && material.HasProperty(propertyName))
        {
            material.SetTexture(propertyName, texture);
        }
    }

    private static Color GetColor(Material material, string propertyName, Color fallback)
    {
        return material.HasProperty(propertyName) ? material.GetColor(propertyName) : fallback;
    }

    private static void SetColor(Material material, string propertyName, Color value)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetColor(propertyName, value);
        }
    }

    private static float GetFloat(Material material, string propertyName, float fallback)
    {
        return material.HasProperty(propertyName) ? material.GetFloat(propertyName) : fallback;
    }

    private static void SetFloat(Material material, string propertyName, float value)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetFloat(propertyName, value);
        }
    }
}
