using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class UrpQualityPolish
{
    private const string ScenePath = "Assets/Scenes/BlenderIndoorScene.unity";
    private const string PipelineGuid = "dd1d162b493aecd4788483ec8df5d256";
    private const string RendererGuid = "4161a54a0ca24354a9235198f9fce262";
    private const string QualityFolder = "Assets/URP_Quality";
    private const string VolumeProfilePath = QualityFolder + "/URP_QualityVolumeProfile.asset";
    private const string SkyboxPath = QualityFolder + "/URP_HDRI_Skybox.mat";
    private const string ReportPath = "Assets/SceneExport/URP_quality_validation_report.txt";

    [MenuItem("Tools/URP Copy/Polish Materials And Validate")]
    public static void ApplyAndValidate()
    {
        var report = new StringBuilder();
        report.AppendLine("URP quality polish and validation report");
        report.AppendLine("Project: D:/pet/URP copy project");

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Directory.CreateDirectory(QualityFolder);

        var pipeline = LoadByGuid<RenderPipelineAsset>(PipelineGuid);
        if (pipeline == null)
        {
            throw new InvalidOperationException("URP pipeline asset was not found.");
        }

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        ConfigurePipeline(pipeline, report);
        ConfigureQualitySettings(report);
        ConfigureSkybox(report);
        ConfigureVolume(report);
        ConfigureRendererFeature(report);
        ConfigureCameras(report);
        ConfigureLights(report);

        int textureChanges = PolishTextureImporters();
        int materialChanges = PolishMaterials();
        int rendererChanges = PolishRenderers();
        report.AppendLine("Texture import settings changed: " + textureChanges);
        report.AppendLine("Materials polished: " + materialChanges);
        report.AppendLine("Scene renderers polished: " + rendererChanges);

        var errors = ValidateProject(report);
        File.WriteAllText(ReportPath, report.ToString(), Encoding.UTF8);
        AssetDatabase.ImportAsset(ReportPath);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Debug.LogError(error);
            }

            if (Application.isBatchMode)
            {
                EditorApplication.Exit(1);
            }
        }
        else
        {
            Debug.Log(report.ToString());
        }
    }

    public static void ValidateOnly()
    {
        var report = new StringBuilder();
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var errors = ValidateProject(report);
        File.WriteAllText(ReportPath, report.ToString(), Encoding.UTF8);
        AssetDatabase.ImportAsset(ReportPath);

        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Debug.LogError(error);
            }

            if (Application.isBatchMode)
            {
                EditorApplication.Exit(1);
            }
        }
        else
        {
            Debug.Log(report.ToString());
        }
    }

    private static void ConfigurePipeline(UnityEngine.Object pipeline, StringBuilder report)
    {
        var so = new SerializedObject(pipeline);
        SetBool(so, "m_RequireDepthTexture", true);
        SetBool(so, "m_RequireOpaqueTexture", true);
        SetBool(so, "m_SupportsHDR", true);
        SetInt(so, "m_MSAA", 4);
        SetFloat(so, "m_RenderScale", 1.15f);
        SetInt(so, "m_MainLightRenderingMode", 1);
        SetBool(so, "m_MainLightShadowsSupported", true);
        SetInt(so, "m_MainLightShadowmapResolution", 4096);
        SetInt(so, "m_AdditionalLightsRenderingMode", 1);
        SetInt(so, "m_AdditionalLightsPerObjectLimit", 4);
        SetBool(so, "m_AdditionalLightShadowsSupported", true);
        SetInt(so, "m_AdditionalLightsShadowmapResolution", 2048);
        SetInt(so, "m_AdditionalLightsShadowResolutionTierHigh", 1024);
        SetBool(so, "m_ReflectionProbeBlending", true);
        SetBool(so, "m_ReflectionProbeBoxProjection", true);
        SetFloat(so, "m_ShadowDistance", 70f);
        SetInt(so, "m_ShadowCascadeCount", 4);
        SetVector3(so, "m_Cascade4Split", new Vector3(0.08f, 0.22f, 0.5f));
        SetFloat(so, "m_CascadeBorder", 0.12f);
        SetFloat(so, "m_ShadowDepthBias", 0.55f);
        SetFloat(so, "m_ShadowNormalBias", 0.45f);
        SetBool(so, "m_SoftShadowsSupported", true);
        SetInt(so, "m_SoftShadowQuality", 2);
        SetBool(so, "m_UseSRPBatcher", true);
        SetBool(so, "m_MixedLightingSupported", true);
        SetBool(so, "m_SupportsLightCookies", true);
        SetInt(so, "m_ColorGradingLutSize", 32);
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(pipeline);

        report.AppendLine("URP pipeline quality: HDR, depth, opaque texture, MSAA 4x, render scale 1.15, cascaded soft shadows, additional light shadows, reflection probe blending.");
    }

    private static void ConfigureQualitySettings(StringBuilder report)
    {
        QualitySettings.antiAliasing = 4;
        QualitySettings.shadows = UnityEngine.ShadowQuality.All;
        QualitySettings.shadowProjection = ShadowProjection.CloseFit;
        QualitySettings.shadowResolution = UnityEngine.ShadowResolution.VeryHigh;
        QualitySettings.shadowCascades = 4;
        QualitySettings.shadowDistance = 70f;
        QualitySettings.softParticles = true;
        QualitySettings.realtimeReflectionProbes = true;
        QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1.05f;
        RenderSettings.reflectionIntensity = 1.05f;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        RenderSettings.fog = false;
        DynamicGI.UpdateEnvironment();
        report.AppendLine("Quality settings: very high shadows, 4 cascades, forced anisotropic filtering, skybox ambient/reflection.");
    }

    private static void ConfigureSkybox(StringBuilder report)
    {
        var hdr = FindTextureByName("044_tears_of_steel_bridge");
        var skyShader = Shader.Find("Skybox/Panoramic");
        if (hdr == null || skyShader == null)
        {
            report.AppendLine("Skybox polish skipped: HDR texture or Skybox/Panoramic shader missing.");
            return;
        }

        var skybox = AssetDatabase.LoadAssetAtPath<Material>(SkyboxPath);
        if (skybox == null)
        {
            skybox = new Material(skyShader);
            AssetDatabase.CreateAsset(skybox, SkyboxPath);
        }

        skybox.shader = skyShader;
        skybox.SetTexture("_MainTex", hdr);
        if (skybox.HasProperty("_Exposure"))
        {
            skybox.SetFloat("_Exposure", 1.05f);
        }
        if (skybox.HasProperty("_Tint"))
        {
            skybox.SetColor("_Tint", new Color(1f, 0.96f, 0.88f, 1f));
        }
        if (skybox.HasProperty("_Rotation"))
        {
            skybox.SetFloat("_Rotation", 0f);
        }

        RenderSettings.skybox = skybox;
        EditorUtility.SetDirty(skybox);
        report.AppendLine("HDRI skybox assigned from 044_tears_of_steel_bridge.");
    }

    private static void ConfigureVolume(StringBuilder report)
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, VolumeProfilePath);
        }

        if (!profile.TryGet<Bloom>(out var bloom))
        {
            bloom = profile.Add<Bloom>(true);
        }
        bloom.active = true;
        bloom.intensity.Override(0.08f);
        bloom.threshold.Override(1.05f);
        bloom.scatter.Override(0.45f);

        if (!profile.TryGet<Tonemapping>(out var tonemapping))
        {
            tonemapping = profile.Add<Tonemapping>(true);
        }
        tonemapping.active = true;
        tonemapping.mode.Override(TonemappingMode.ACES);

        if (!profile.TryGet<ColorAdjustments>(out var color))
        {
            color = profile.Add<ColorAdjustments>(true);
        }
        color.active = true;
        color.postExposure.Override(0.08f);
        color.contrast.Override(8f);
        color.saturation.Override(7f);
        color.colorFilter.Override(new Color(1f, 0.98f, 0.93f, 1f));

        var volumeObject = GameObject.Find("URP Quality Volume");
        if (volumeObject == null)
        {
            volumeObject = new GameObject("URP Quality Volume");
        }

        var volume = volumeObject.GetComponent<Volume>();
        if (volume == null)
        {
            volume = volumeObject.AddComponent<Volume>();
        }
        volume.isGlobal = true;
        volume.priority = 20f;
        volume.weight = 1f;
        volume.sharedProfile = profile;
        EditorUtility.SetDirty(profile);
        EditorUtility.SetDirty(volumeObject);

        report.AppendLine("Global volume: ACES tonemapping, light bloom, slightly warmer contrast/saturation.");
    }

    private static void ConfigureRendererFeature(StringBuilder report)
    {
        var rendererPath = AssetDatabase.GUIDToAssetPath(RendererGuid);
        var rendererData = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(rendererPath);
        if (rendererData == null)
        {
            report.AppendLine("SSAO skipped: renderer data not found.");
            return;
        }

        var ssao = rendererData.rendererFeatures.OfType<ScreenSpaceAmbientOcclusion>().FirstOrDefault();
        if (ssao == null)
        {
            ssao = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
            ssao.name = "SSAO_Quality_Enhance";
            AssetDatabase.AddObjectToAsset(ssao, rendererData);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ssao, out string _, out long localId);

            var so = new SerializedObject(rendererData);
            var features = so.FindProperty("m_RendererFeatures");
            var featureMap = so.FindProperty("m_RendererFeatureMap");
            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = ssao;
            featureMap.arraySize++;
            featureMap.GetArrayElementAtIndex(featureMap.arraySize - 1).longValue = localId;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        ssao.SetActive(true);
        var ssaoObject = new SerializedObject(ssao);
        SetInt(ssaoObject, "m_Settings.AOMethod", 0);
        SetBool(ssaoObject, "m_Settings.Downsample", false);
        SetBool(ssaoObject, "m_Settings.AfterOpaque", false);
        SetInt(ssaoObject, "m_Settings.Source", 1);
        SetInt(ssaoObject, "m_Settings.NormalSamples", 2);
        SetFloat(ssaoObject, "m_Settings.Intensity", 1.15f);
        SetFloat(ssaoObject, "m_Settings.DirectLightingStrength", 0.55f);
        SetFloat(ssaoObject, "m_Settings.Radius", 0.18f);
        SetInt(ssaoObject, "m_Settings.Samples", 2);
        SetInt(ssaoObject, "m_Settings.BlurQuality", 0);
        SetFloat(ssaoObject, "m_Settings.Falloff", 80f);
        SetInt(ssaoObject, "m_Settings.SampleCount", -1);
        ssaoObject.ApplyModifiedPropertiesWithoutUndo();
        rendererData.SetDirty();
        EditorUtility.SetDirty(ssao);
        EditorUtility.SetDirty(rendererData);
        report.AppendLine("Renderer feature: screen space ambient occlusion enabled.");
    }

    private static void ConfigureCameras(StringBuilder report)
    {
        int changed = 0;
        foreach (var camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            camera.allowHDR = true;
            camera.allowMSAA = true;
            var data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.requiresDepthTexture = true;
            data.requiresColorTexture = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.High;
            data.stopNaN = true;
            data.dithering = true;
            EditorUtility.SetDirty(camera);
            EditorUtility.SetDirty(data);
            changed++;
        }

        report.AppendLine("Cameras configured for URP post-processing/depth/color/SMAA: " + changed);
    }

    private static void ConfigureLights(StringBuilder report)
    {
        int changed = 0;
        foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (!light.enabled)
            {
                light.enabled = true;
            }

            light.useColorTemperature = true;
            if (light.type == LightType.Directional)
            {
                light.shadows = LightShadows.Soft;
                light.shadowStrength = Mathf.Clamp(light.shadowStrength <= 0f ? 0.65f : light.shadowStrength, 0.45f, 0.8f);
                light.shadowBias = Mathf.Min(light.shadowBias, 0.08f);
                light.shadowNormalBias = Mathf.Min(light.shadowNormalBias, 0.45f);
                light.shadowCustomResolution = 4096;
            }
            else if (light.type == LightType.Spot || light.type == LightType.Point)
            {
                light.shadows = LightShadows.Soft;
                light.shadowStrength = Mathf.Clamp(light.shadowStrength <= 0f ? 0.55f : light.shadowStrength, 0.3f, 0.7f);
                light.shadowBias = Mathf.Min(light.shadowBias, 0.05f);
                light.shadowNormalBias = Mathf.Min(light.shadowNormalBias, 0.35f);
                light.shadowCustomResolution = 512;
            }

            EditorUtility.SetDirty(light);
            changed++;
        }

        report.AppendLine("Lights enabled/polished for soft realtime shadows: " + changed);
    }

    private static int PolishTextureImporters()
    {
        int changed = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Texture", new[] { "Assets/SceneExport" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                continue;
            }

            bool dirty = false;
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            bool isNormal = name.Contains("normal") || name.Contains("norm") || name.EndsWith("_n", StringComparison.Ordinal);
            bool isData = isNormal || name.Contains("rough") || name.Contains("metal") || name.Contains("metallic") ||
                          name.Contains("ao") || name.Contains("occlusion") || name.Contains("mask") ||
                          name.Contains("height") || name.Contains("curv");

            if (isNormal && importer.textureType != TextureImporterType.NormalMap)
            {
                importer.textureType = TextureImporterType.NormalMap;
                dirty = true;
            }
            else if (!isNormal && importer.textureType != TextureImporterType.Default)
            {
                importer.textureType = TextureImporterType.Default;
                dirty = true;
            }

            if (importer.sRGBTexture == isData)
            {
                importer.sRGBTexture = !isData;
                dirty = true;
            }
            if (importer.maxTextureSize < 4096)
            {
                importer.maxTextureSize = 4096;
                dirty = true;
            }
            if (!importer.mipmapEnabled)
            {
                importer.mipmapEnabled = true;
                dirty = true;
            }
            if (importer.textureCompression != TextureImporterCompression.CompressedHQ)
            {
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                dirty = true;
            }
            if (importer.anisoLevel < 8)
            {
                importer.anisoLevel = 8;
                dirty = true;
            }
            if (importer.filterMode != FilterMode.Trilinear)
            {
                importer.filterMode = FilterMode.Trilinear;
                dirty = true;
            }

            if (dirty)
            {
                importer.SaveAndReimport();
                changed++;
            }
        }

        return changed;
    }

    private static int PolishMaterials()
    {
        int changed = 0;
        var generatedMetallic = AssetDatabase.LoadAssetAtPath<Texture>("Assets/SceneExport/GeneratedPBR/Material #81_MetallicSmoothness.png");

        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/SceneExport" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                continue;
            }

            bool isGlass = ContainsAny(material.name, "glass", "transparent", "window");
            bool isMetal = ContainsAny(material.name, "metal", "stainless", "steel") || material.name.Contains("金属") || material.name.Contains("不锈钢");
            bool isDecal = ContainsAny(material.name, "decal", "backdrop", "visible");

            var baseMap = GetTexture(material, "_BaseMap", "_MainTex");
            var bump = GetTexture(material, "_BumpMap", "_NormalMap");
            var metallicMap = GetTexture(material, "_MetallicGlossMap");
            var occlusion = GetTexture(material, "_OcclusionMap");
            var emission = GetTexture(material, "_EmissionMap");
            var baseColor = GetColor(material, "_Color", "_BaseColor", Color.white);
            var emissionColor = GetColor(material, "_EmissionColor", Color.black);
            var metallic = GetFloat(material, "_Metallic", isMetal ? 0.65f : 0f);
            var smoothness = GetFloat(material, "_Smoothness", GetFloat(material, "_Glossiness", 0.45f));

            if (material.name.Contains("Material #81") && generatedMetallic != null)
            {
                metallicMap = generatedMetallic;
            }

            var shader = Shader.Find(isDecal ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit");
            if (shader != null && material.shader != shader)
            {
                material.shader = shader;
            }

            if (baseMap != null)
            {
                SetTexture(material, "_BaseMap", baseMap);
                SetTexture(material, "_MainTex", baseMap);
            }
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", baseColor);
            }
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", baseColor);
            }

            if (!isDecal)
            {
                if (bump != null)
                {
                    SetTexture(material, "_BumpMap", bump);
                    SetFloat(material, "_BumpScale", isGlass ? 0.35f : 1.2f);
                    material.EnableKeyword("_NORMALMAP");
                }
                if (metallicMap != null)
                {
                    SetTexture(material, "_MetallicGlossMap", metallicMap);
                    material.EnableKeyword("_METALLICSPECGLOSSMAP");
                }
                if (occlusion != null)
                {
                    SetTexture(material, "_OcclusionMap", occlusion);
                    SetFloat(material, "_OcclusionStrength", 1f);
                }
                if (emission != null || emissionColor.maxColorComponent > 0.01f)
                {
                    SetTexture(material, "_EmissionMap", emission);
                    SetColor(material, "_EmissionColor", emissionColor.maxColorComponent > 0.01f ? emissionColor : Color.black);
                    material.EnableKeyword("_EMISSION");
                }

                if (isGlass)
                {
                    SetTransparent(material, Mathf.Clamp(baseColor.a <= 0f ? 0.28f : Mathf.Min(baseColor.a, 0.42f), 0.18f, 0.92f), 0.18f, 0.92f);
                }
                else
                {
                    SetOpaque(material);
                    SetFloat(material, "_Metallic", isMetal ? Mathf.Max(metallic, 0.65f) : metallic);
                    SetFloat(material, "_Smoothness", isMetal ? Mathf.Max(smoothness, 0.62f) : Mathf.Clamp(Mathf.Max(smoothness, 0.38f), 0.32f, 0.68f));
                }
            }
            else
            {
                if (ContainsAny(material.name, "decal", "backdrop"))
                {
                    SetTransparent(material, Mathf.Clamp(baseColor.a <= 0f ? 0.78f : baseColor.a, 0.25f, 1f), 0f, 0f);
                }
            }

            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            EditorUtility.SetDirty(material);
            changed++;
        }

        return changed;
    }

    private static int PolishRenderers()
    {
        int changed = 0;
        foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            var joined = string.Join(" ", renderer.sharedMaterials.Where(m => m != null).Select(m => m.name)).ToLowerInvariant();
            bool transparentUtility = joined.Contains("glass") || joined.Contains("transparent") || joined.Contains("decal") || joined.Contains("backdrop");
            renderer.receiveShadows = !joined.Contains("backdrop");
            renderer.shadowCastingMode = transparentUtility ? UnityEngine.Rendering.ShadowCastingMode.Off : UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbesAndSkybox;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.BlendProbes;
            EditorUtility.SetDirty(renderer);
            changed++;
        }

        return changed;
    }

    private static List<string> ValidateProject(StringBuilder report)
    {
        var errors = new List<string>();
        if (GraphicsSettings.defaultRenderPipeline == null)
        {
            errors.Add("GraphicsSettings.defaultRenderPipeline is not assigned.");
        }
        if (QualitySettings.renderPipeline == null)
        {
            errors.Add("QualitySettings.renderPipeline is not assigned for the active quality tier.");
        }

        int badMaterials = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/SceneExport" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null || material.shader == null || material.shader.name.Contains("InternalErrorShader"))
            {
                badMaterials++;
                errors.Add("Bad material shader: " + path);
            }
        }

        int missingRendererMaterials = 0;
        foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null)
                {
                    missingRendererMaterials++;
                    errors.Add("Renderer has missing material: " + GetPath(renderer.transform));
                }
                else if (material.shader == null || material.shader.name.Contains("InternalErrorShader"))
                {
                    missingRendererMaterials++;
                    errors.Add("Renderer has bad shader material: " + GetPath(renderer.transform) + " -> " + material.name);
                }
            }
        }

        report.AppendLine("Validation bad material assets: " + badMaterials);
        report.AppendLine("Validation renderer material issues: " + missingRendererMaterials);
        report.AppendLine("Validation error count: " + errors.Count);
        return errors;
    }

    private static T LoadByGuid<T>(string guid) where T : UnityEngine.Object
    {
        var path = AssetDatabase.GUIDToAssetPath(guid);
        return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path);
    }

    private static Texture FindTextureByName(string namePart)
    {
        foreach (var guid in AssetDatabase.FindAssets(namePart, new[] { "Assets/SceneExport" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var texture = AssetDatabase.LoadAssetAtPath<Texture>(path);
            if (texture != null)
            {
                return texture;
            }
        }

        return null;
    }

    private static void SetOpaque(Material material)
    {
        SetFloat(material, "_Surface", 0f);
        SetFloat(material, "_Blend", 0f);
        SetFloat(material, "_AlphaClip", 0f);
        SetFloat(material, "_ZWrite", 1f);
        SetFloat(material, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
        SetFloat(material, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
        material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHATEST_ON");
    }

    private static void SetTransparent(Material material, float alpha, float metallic, float smoothness)
    {
        var color = GetColor(material, "_Color", "_BaseColor", Color.white);
        color.a = alpha;
        SetColor(material, "_BaseColor", color);
        SetColor(material, "_Color", color);
        SetFloat(material, "_Surface", 1f);
        SetFloat(material, "_Blend", 0f);
        SetFloat(material, "_AlphaClip", 0f);
        SetFloat(material, "_ZWrite", 0f);
        SetFloat(material, "_Cull", 0f);
        SetFloat(material, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        SetFloat(material, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        SetFloat(material, "_Metallic", metallic);
        SetFloat(material, "_Smoothness", smoothness);
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHATEST_ON");
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

    private static Color GetColor(Material material, string firstName, Color fallback)
    {
        return material.HasProperty(firstName) ? material.GetColor(firstName) : fallback;
    }

    private static Color GetColor(Material material, string firstName, string secondName, Color fallback)
    {
        if (material.HasProperty(firstName))
        {
            return material.GetColor(firstName);
        }
        if (material.HasProperty(secondName))
        {
            return material.GetColor(secondName);
        }
        return fallback;
    }

    private static float GetFloat(Material material, string name, float fallback)
    {
        return material.HasProperty(name) ? material.GetFloat(name) : fallback;
    }

    private static void SetTexture(Material material, string name, Texture texture)
    {
        if (texture != null && material.HasProperty(name))
        {
            material.SetTexture(name, texture);
        }
    }

    private static void SetFloat(Material material, string name, float value)
    {
        if (material.HasProperty(name))
        {
            material.SetFloat(name, value);
        }
    }

    private static void SetColor(Material material, string name, Color value)
    {
        if (material.HasProperty(name))
        {
            material.SetColor(name, value);
        }
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        var lower = value.ToLowerInvariant();
        return needles.Any(lower.Contains);
    }

    private static string GetPath(Transform transform)
    {
        var names = new Stack<string>();
        while (transform != null)
        {
            names.Push(transform.name);
            transform = transform.parent;
        }
        return string.Join("/", names);
    }

    private static void SetBool(SerializedObject so, string name, bool value)
    {
        var property = so.FindProperty(name);
        if (property != null && property.propertyType == SerializedPropertyType.Boolean)
        {
            property.boolValue = value;
        }
    }

    private static void SetInt(SerializedObject so, string name, int value)
    {
        var property = so.FindProperty(name);
        if (property == null)
        {
            return;
        }

        if (property.propertyType == SerializedPropertyType.Integer)
        {
            property.intValue = value;
        }
        else if (property.propertyType == SerializedPropertyType.Enum)
        {
            property.enumValueIndex = Mathf.Clamp(value, 0, Mathf.Max(0, property.enumDisplayNames.Length - 1));
        }
    }

    private static void SetFloat(SerializedObject so, string name, float value)
    {
        var property = so.FindProperty(name);
        if (property != null && property.propertyType == SerializedPropertyType.Float)
        {
            property.floatValue = value;
        }
    }

    private static void SetVector3(SerializedObject so, string name, Vector3 value)
    {
        var property = so.FindProperty(name);
        if (property != null && property.propertyType == SerializedPropertyType.Vector3)
        {
            property.vector3Value = value;
        }
    }
}
