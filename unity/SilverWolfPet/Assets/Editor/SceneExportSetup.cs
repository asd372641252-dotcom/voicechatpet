using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class SceneExportSetup
{
    private const string ModelPath = "Assets/SceneExport/Models/blender_indoor_scene.fbx";
    private const string ScenePath = "Assets/Scenes/BlenderIndoorScene.unity";

    public static void Build()
    {
        Directory.CreateDirectory("Assets/Scenes");
        ConfigureTextures();
        ConfigureModelImporter();

        AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ImportRecursive);

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (prefab == null)
        {
            throw new FileNotFoundException("Could not load exported FBX prefab.", ModelPath);
        }

        var root = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        root.name = "Blender Indoor Scene";
        root.transform.position = Vector3.zero;
        root.transform.rotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;

        EnsureCamera();
        EnsureLight();

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("SceneExportSetup complete: " + ScenePath);
    }

    public static void Verify()
    {
        AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ImportRecursive);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        var report = new StringBuilder();
        report.AppendLine("Scene export Unity verification");
        report.AppendLine("Model: " + ModelPath);
        report.AppendLine("Scene: " + ScenePath);
        report.AppendLine("Scene exists: " + File.Exists(ScenePath));

        if (prefab == null)
        {
            report.AppendLine("Model prefab loaded: false");
        }
        else
        {
            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            var materialSlots = renderers.Sum(renderer => renderer.sharedMaterials.Length);
            var materials = renderers.SelectMany(renderer => renderer.sharedMaterials).Where(material => material != null).Distinct().ToArray();
            var texturedMaterials = materials.Count(material => material.mainTexture != null);
            var dependencies = AssetDatabase.GetDependencies(ModelPath, true);
            var textureDependencies = dependencies.Count(path => path.StartsWith("Assets/SceneExport", System.StringComparison.OrdinalIgnoreCase) && IsTexturePath(path));

            report.AppendLine("Model prefab loaded: true");
            report.AppendLine("Renderer count: " + renderers.Length);
            report.AppendLine("Material slot count: " + materialSlots);
            report.AppendLine("Unique imported material count: " + materials.Length);
            report.AppendLine("Materials with main texture: " + texturedMaterials);
            report.AppendLine("SceneExport texture dependencies: " + textureDependencies);
        }

        var reportPath = "Assets/SceneExport/unity_verify_report.txt";
        File.WriteAllText(reportPath, report.ToString());
        AssetDatabase.ImportAsset(reportPath);
        Debug.Log(report.ToString());
    }

    public static void OpenForViewing()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var sceneRoot = GameObject.Find("Blender Indoor Scene");
        if (sceneRoot != null)
        {
            Selection.activeObject = sceneRoot;
            EditorGUIUtility.PingObject(sceneRoot);
        }

        var bounds = GetSceneRendererBounds();
        var viewSize = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) * 0.75f;
        if (viewSize < 1f)
        {
            viewSize = 4f;
        }

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

    public static void ApplyBrightDayEnvironment()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var skybox = EnsureBrightDaySkyboxMaterial();

        RenderSettings.skybox = skybox;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.72f, 0.84f, 1.00f);
        RenderSettings.ambientEquatorColor = new Color(0.92f, 0.95f, 1.00f);
        RenderSettings.ambientGroundColor = new Color(0.70f, 0.74f, 0.80f);
        RenderSettings.ambientIntensity = 1.25f;
        RenderSettings.reflectionIntensity = 0.55f;
        RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Skybox;
        RenderSettings.fog = false;

        foreach (var camera in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
        {
            camera.clearFlags = CameraClearFlags.Skybox;
        }

        foreach (SceneView sceneView in SceneView.sceneViews)
        {
            sceneView.sceneLighting = true;
            sceneView.sceneViewState.showSkybox = true;
            sceneView.Repaint();
        }

        var report = new StringBuilder();
        report.AppendLine("Bright day environment applied");
        report.AppendLine("Skybox: " + AssetDatabase.GetAssetPath(skybox));
        report.AppendLine("Sky tint: light blue, sun disk disabled");
        report.AppendLine("Ambient sky: " + RenderSettings.ambientSkyColor);
        report.AppendLine("Ambient equator: " + RenderSettings.ambientEquatorColor);
        report.AppendLine("Ambient ground: " + RenderSettings.ambientGroundColor);
        report.AppendLine("Ambient intensity: " + RenderSettings.ambientIntensity);
        const string reportPath = "Assets/SceneExport/bright_day_environment_report.txt";
        File.WriteAllText(reportPath, report.ToString());
        AssetDatabase.ImportAsset(reportPath);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log(report.ToString());
    }

    public static void ApplyBlenderLikeQuality()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        PlayerSettings.colorSpace = ColorSpace.Linear;

        var bounds = GetSceneRendererBounds();
        var skybox = EnsureHdriSkyboxMaterial();
        RenderSettings.skybox = skybox;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 0.95f;
        RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Skybox;
        RenderSettings.reflectionIntensity = 0.82f;
        RenderSettings.reflectionBounces = 2;
        RenderSettings.fog = false;

        QualitySettings.pixelLightCount = Mathf.Max(QualitySettings.pixelLightCount, 8);
        QualitySettings.shadows = ShadowQuality.All;
        QualitySettings.shadowResolution = ShadowResolution.High;
        QualitySettings.shadowDistance = Mathf.Max(QualitySettings.shadowDistance, 35f);
        QualitySettings.antiAliasing = Mathf.Max(QualitySettings.antiAliasing, 4);
        QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
        QualitySettings.realtimeReflectionProbes = true;
        QualitySettings.lodBias = Mathf.Max(QualitySettings.lodBias, 1.6f);

        DestroyIfExists("Blender Quality");
        var qualityRoot = new GameObject("Blender Quality");
        var probeObject = new GameObject("Room Reflection Probe - HDRI");
        probeObject.transform.SetParent(qualityRoot.transform);
        probeObject.transform.position = bounds.center;
        var probe = probeObject.AddComponent<ReflectionProbe>();
        probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
        probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.OnAwake;
        probe.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.IndividualFaces;
        probe.clearFlags = UnityEngine.Rendering.ReflectionProbeClearFlags.Skybox;
        probe.hdr = true;
        probe.resolution = 256;
        probe.intensity = 1.05f;
        probe.boxProjection = true;
        probe.size = new Vector3(
            Mathf.Max(bounds.size.x * 1.20f, 4f),
            Mathf.Max(bounds.size.y * 1.25f, 3f),
            Mathf.Max(bounds.size.z * 1.20f, 4f));
        probe.center = Vector3.zero;
        probe.nearClipPlane = 0.05f;
        probe.farClipPlane = Mathf.Max(bounds.size.magnitude * 2.0f, 25f);
        probe.cullingMask = ~0;
        probe.importance = 1;

        var adjustedMaterialCount = 0;
        var glassMaterialCount = 0;
        foreach (var material in GetSceneMaterials())
        {
            if (material == null || material.shader == null || material.shader.name != "Standard")
            {
                continue;
            }

            if (material.HasProperty("_SpecularHighlights"))
            {
                material.SetFloat("_SpecularHighlights", 1f);
            }
            if (material.HasProperty("_GlossyReflections"))
            {
                material.SetFloat("_GlossyReflections", 1f);
            }

            if (IsStandardWindowGlassMaterial(material))
            {
                SetMaterialFloat(material, "_Glossiness", 0.78f);
                SetMaterialFloat(material, "_Metallic", 0f);
                glassMaterialCount++;
            }

            EditorUtility.SetDirty(material);
            adjustedMaterialCount++;
        }

        foreach (var camera in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
        {
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.allowHDR = true;
            camera.allowMSAA = true;
            camera.usePhysicalProperties = true;
            camera.aperture = 5.6f;
            camera.iso = 160;
            camera.shutterSpeed = 1f / 80f;
            EditorUtility.SetDirty(camera);
        }

        foreach (SceneView sceneView in SceneView.sceneViews)
        {
            sceneView.sceneLighting = true;
            sceneView.sceneViewState.showSkybox = true;
            sceneView.Repaint();
        }

        var report = new StringBuilder();
        report.AppendLine("Blender-like quality applied");
        report.AppendLine("Color space: " + PlayerSettings.colorSpace);
        report.AppendLine("Skybox: " + AssetDatabase.GetAssetPath(skybox));
        report.AppendLine("Ambient mode: Skybox");
        report.AppendLine("Ambient intensity: " + RenderSettings.ambientIntensity);
        report.AppendLine("Reflection intensity: " + RenderSettings.reflectionIntensity);
        report.AppendLine("Reflection probe center: " + probeObject.transform.position);
        report.AppendLine("Reflection probe size: " + probe.size);
        report.AppendLine("Quality anti aliasing: " + QualitySettings.antiAliasing);
        report.AppendLine("Standard materials with reflections enabled: " + adjustedMaterialCount);
        report.AppendLine("Glass materials made glossier: " + glassMaterialCount);
        const string reportPath = "Assets/SceneExport/blender_like_quality_report.txt";
        File.WriteAllText(reportPath, report.ToString());
        AssetDatabase.ImportAsset(reportPath);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log(report.ToString());
    }

    public static void ApplyBlenderPbrMapsToStandard()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Directory.CreateDirectory("Assets/SceneExport/GeneratedPBR");
        Directory.CreateDirectory("Assets/SceneExport/BlenderPBRSources");

        var jsonPath = "Assets/SceneExport/blender_pbr_material_map.json";
        var jsonAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(jsonPath);
        if (jsonAsset == null)
        {
            throw new FileNotFoundException("Blender PBR material map was not found.", jsonPath);
        }

        var map = JsonUtility.FromJson<BlenderPbrMap>(jsonAsset.text);
        var entriesByName = new Dictionary<string, BlenderPbrMaterialEntry>();
        foreach (var entry in map.materials)
        {
            var key = NormalizeMaterialKey(entry.cleanName);
            if (!entriesByName.ContainsKey(key))
            {
                entriesByName.Add(key, entry);
            }
        }

        var texturePaths = AssetDatabase.FindAssets("t:Texture", new[] { "Assets/SceneExport/Textures", "Assets/SceneExport/Models/blender_indoor_scene.fbm", "Assets/SceneExport/BlenderPBRSources" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(IsTexturePath)
            .ToArray();

        var matchedMaterials = 0;
        var normalMapsApplied = 0;
        var metallicSmoothnessMapsGenerated = 0;
        var occlusionMapsApplied = 0;
        var defaultsApplied = 0;
        var skippedNonStandard = 0;
        var missingTextureCount = 0;
        var report = new StringBuilder();
        report.AppendLine("Blender PBR maps applied to Unity Standard");

        foreach (var material in GetSceneMaterials().OrderBy(material => material.name))
        {
            if (material == null || material.shader == null)
            {
                continue;
            }

            var materialKey = NormalizeMaterialKey(CleanUnityMaterialName(material.name));
            if (!entriesByName.TryGetValue(materialKey, out var entry))
            {
                continue;
            }

            matchedMaterials++;
            if (material.shader.name != "Standard")
            {
                skippedNonStandard++;
                report.AppendLine("Skipped non-Standard material: " + material.name + " shader=" + material.shader.name);
                continue;
            }

            var normalPath = ResolveBlenderTexturePath(entry.normal, texturePaths);
            var metallicPath = ResolveBlenderTexturePath(entry.metallic, texturePaths);
            var roughnessPath = ResolveBlenderTexturePath(entry.roughness, texturePaths);
            var aoPath = ResolveBlenderTexturePath(entry.ao, texturePaths);

            if (HasImageInfo(entry.normal) && string.IsNullOrEmpty(normalPath))
            {
                missingTextureCount++;
                report.AppendLine("Missing normal texture for " + material.name + ": " + entry.normal.name + " / " + entry.normal.basename);
            }
            if (HasImageInfo(entry.metallic) && string.IsNullOrEmpty(metallicPath))
            {
                missingTextureCount++;
                report.AppendLine("Missing metallic texture for " + material.name + ": " + entry.metallic.name + " / " + entry.metallic.basename);
            }
            if (HasImageInfo(entry.roughness) && string.IsNullOrEmpty(roughnessPath))
            {
                missingTextureCount++;
                report.AppendLine("Missing roughness texture for " + material.name + ": " + entry.roughness.name + " / " + entry.roughness.basename);
            }
            if (HasImageInfo(entry.ao) && string.IsNullOrEmpty(aoPath))
            {
                missingTextureCount++;
                report.AppendLine("Missing AO texture for " + material.name + ": " + entry.ao.name + " / " + entry.ao.basename);
            }

            ApplyStandardDefaultsFromBlender(material, entry);
            defaultsApplied++;

            if (!string.IsNullOrEmpty(normalPath))
            {
                ConfigureTextureImporter(normalPath, TextureImporterType.NormalMap, false, true);
                var normal = AssetDatabase.LoadAssetAtPath<Texture>(normalPath);
                if (normal != null && material.HasProperty("_BumpMap"))
                {
                    material.SetTexture("_BumpMap", normal);
                    SetMaterialFloat(material, "_BumpScale", 1f);
                    material.EnableKeyword("_NORMALMAP");
                    normalMapsApplied++;
                    report.AppendLine("Normal -> " + material.name + ": " + normalPath);
                }
            }

            if (!string.IsNullOrEmpty(metallicPath) || !string.IsNullOrEmpty(roughnessPath))
            {
                var generatedPath = GenerateMetallicSmoothnessMap(material, entry, metallicPath, roughnessPath);
                ConfigureTextureImporter(generatedPath, TextureImporterType.Default, false, false);
                var metallicSmoothness = AssetDatabase.LoadAssetAtPath<Texture>(generatedPath);
                if (metallicSmoothness != null && material.HasProperty("_MetallicGlossMap"))
                {
                    material.SetTexture("_MetallicGlossMap", metallicSmoothness);
                    SetMaterialFloat(material, "_GlossMapScale", 1f);
                    SetMaterialFloat(material, "_SmoothnessTextureChannel", 0f);
                    material.EnableKeyword("_METALLICGLOSSMAP");
                    metallicSmoothnessMapsGenerated++;
                    report.AppendLine("Metallic/Smoothness -> " + material.name + ": " + generatedPath);
                }
            }

            if (!string.IsNullOrEmpty(aoPath))
            {
                ConfigureTextureImporter(aoPath, TextureImporterType.Default, false, true);
                var ao = AssetDatabase.LoadAssetAtPath<Texture>(aoPath);
                if (ao != null && material.HasProperty("_OcclusionMap"))
                {
                    material.SetTexture("_OcclusionMap", ao);
                    SetMaterialFloat(material, "_OcclusionStrength", 1f);
                    occlusionMapsApplied++;
                    report.AppendLine("AO -> " + material.name + ": " + aoPath);
                }
            }

            EditorUtility.SetDirty(material);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        report.AppendLine("Matched scene materials: " + matchedMaterials);
        report.AppendLine("Skipped non-Standard materials: " + skippedNonStandard);
        report.AppendLine("Defaults applied: " + defaultsApplied);
        report.AppendLine("Normal maps applied: " + normalMapsApplied);
        report.AppendLine("Metallic/smoothness maps generated: " + metallicSmoothnessMapsGenerated);
        report.AppendLine("Occlusion maps applied: " + occlusionMapsApplied);
        report.AppendLine("Missing source texture references: " + missingTextureCount);
        const string reportPath = "Assets/SceneExport/blender_pbr_standard_mapping_report.txt";
        File.WriteAllText(reportPath, report.ToString());
        AssetDatabase.ImportAsset(reportPath);
        Debug.Log(report.ToString());
    }

    public static void FixWindowPlanes()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var shader = Shader.Find("SceneExport/DoubleSidedOpaqueTexture");
        if (shader == null)
        {
            throw new FileNotFoundException("Double-sided opaque shader was not imported.");
        }

        Directory.CreateDirectory("Assets/SceneExport/Materials");
        var fixedCount = 0;
        var report = new StringBuilder();
        report.AppendLine("Window plane visibility fix");

        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!IsWindowPlaneName(renderer.gameObject.name))
            {
                continue;
            }

            var sourceMaterial = renderer.sharedMaterial;
            var sourceTexture = sourceMaterial != null && sourceMaterial.HasProperty("_MainTex") ? sourceMaterial.mainTexture : null;
            var materialPath = "Assets/SceneExport/Materials/" + SanitizeAssetName(renderer.gameObject.name) + "_Visible.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, materialPath);
            }
            else
            {
                material.shader = shader;
            }

            material.mainTexture = sourceTexture;
            material.color = Color.white;
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            renderer.sharedMaterial = material;
            renderer.enabled = true;
            renderer.gameObject.SetActive(true);
            fixedCount++;

            report.AppendLine(renderer.gameObject.name + " -> " + materialPath + " texture=" + (sourceTexture != null ? AssetDatabase.GetAssetPath(sourceTexture) : "<none>"));
        }

        report.AppendLine("Fixed renderer count: " + fixedCount);
        File.WriteAllText("Assets/SceneExport/window_plane_fix_report.txt", report.ToString());
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log(report.ToString());
    }

    public static void ApplyBlenderLighting()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var oldRoot = GameObject.Find("Blender Lighting");
        if (oldRoot != null)
        {
            Object.DestroyImmediate(oldRoot);
        }
        var oldRealtimeRoot = GameObject.Find("Blender Realtime Lighting");
        if (oldRealtimeRoot != null)
        {
            Object.DestroyImmediate(oldRealtimeRoot);
        }

        var disabledImportedLights = 0;
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
        {
            light.enabled = false;
            disabledImportedLights++;
        }

        var bounds = GetSceneRendererBounds();
        var center = bounds.center;
        var size = bounds.size;
        var radius = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        if (radius < 1f)
        {
            radius = 4f;
        }

        var root = new GameObject("Blender Realtime Lighting");
        QualitySettings.pixelLightCount = Mathf.Max(QualitySettings.pixelLightCount, 8);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.50f, 0.46f, 0.40f);
        RenderSettings.ambientEquatorColor = new Color(0.23f, 0.21f, 0.20f);
        RenderSettings.ambientGroundColor = new Color(0.070f, 0.065f, 0.060f);
        RenderSettings.ambientIntensity = 1.6f;
        RenderSettings.reflectionIntensity = 0.55f;
        RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Skybox;
        RenderSettings.fog = false;

        AddDirectional(root.transform, "Realtime Sun - warm window key", new Vector3(47f, -126f, 12f), new Color(1.0f, 0.82f, 0.58f), 2.2f);
        AddSpot(root.transform, "Realtime Window Area Proxy", center + new Vector3(size.x * 0.45f, size.y * 0.35f, -size.z * 0.65f), center + new Vector3(0f, size.y * 0.05f, 0f), new Color(1.0f, 0.76f, 0.58f), 9.5f, radius * 2.4f, 88f, true);
        AddSpot(root.transform, "Realtime Soft Ceiling Fill", center + new Vector3(-size.x * 0.18f, size.y * 0.75f, size.z * 0.05f), center, new Color(0.85f, 0.92f, 1.0f), 3.2f, radius * 1.9f, 105f, false);
        AddPoint(root.transform, "Realtime Warm Room Bounce", center + new Vector3(-size.x * 0.30f, size.y * 0.25f, size.z * 0.25f), new Color(1.0f, 0.70f, 0.55f), 3.4f, radius * 1.35f);
        AddPoint(root.transform, "Realtime Desk Glow Lower", center + new Vector3(0f, -size.y * 0.10f, -size.z * 0.18f), new Color(0.74f, 0.86f, 1.0f), 2.5f, radius * 0.85f);
        AddPoint(root.transform, "Realtime Desk Glow Upper", center + new Vector3(size.x * 0.18f, size.y * 0.22f, -size.z * 0.22f), new Color(1.0f, 0.93f, 0.82f), 1.8f, radius * 0.75f);

        var camera = Camera.main;
        if (camera != null)
        {
            camera.usePhysicalProperties = true;
            camera.focalLength = 16f;
            camera.fieldOfView = 74f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 200f;
            camera.backgroundColor = new Color(0.05f, 0.05f, 0.055f);
        }

        var report = new StringBuilder();
        report.AppendLine("Blender-like lighting applied");
        report.AppendLine("Source: Blender EEVEE, Filmic, Medium Contrast, exposure +0.1");
        report.AppendLine("Realtime lights created: 6");
        report.AppendLine("Disabled imported/baked lights: " + disabledImportedLights);
        report.AppendLine("Scene bounds center: " + center);
        report.AppendLine("Scene bounds size: " + size);
        report.AppendLine("Ambient: warm HDRI approximation, strength 2.4 translated to Unity Trilight");
        File.WriteAllText("Assets/SceneExport/blender_lighting_unity_report.txt", report.ToString());
        AssetDatabase.ImportAsset("Assets/SceneExport/blender_lighting_unity_report.txt");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log(report.ToString());
    }

    public static void ApplyWindowSunLighting()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        DestroyIfExists("Blender Lighting");
        DestroyIfExists("Blender Realtime Lighting");
        DestroyIfExists("Window Sun Lighting");

        var disabledLights = 0;
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
        {
            if (IsGeneratedLighting(light.transform))
            {
                continue;
            }

            light.enabled = false;
            light.gameObject.name = light.gameObject.name.Contains("(Blender Imported Disabled)")
                ? light.gameObject.name
                : light.gameObject.name + " (Blender Imported Disabled)";
            disabledLights++;
        }

        var bounds = GetSceneRendererBounds();
        var windowRenderer = FindWindowRenderer();
        var windowCenter = windowRenderer != null ? windowRenderer.bounds.center : bounds.center + new Vector3(bounds.extents.x, 0f, -bounds.extents.z);
        var target = bounds.center + new Vector3(0f, -bounds.extents.y * 0.22f, 0f);
        var inward = (target - windowCenter).normalized;
        if (inward.sqrMagnitude < 0.01f)
        {
            inward = new Vector3(-0.45f, -0.2f, 0.87f).normalized;
        }

        var radius = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        var root = new GameObject("Window Sun Lighting");
        QualitySettings.pixelLightCount = Mathf.Max(QualitySettings.pixelLightCount, 8);

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.19f, 0.20f, 0.22f);
        RenderSettings.ambientEquatorColor = new Color(0.09f, 0.085f, 0.08f);
        RenderSettings.ambientGroundColor = new Color(0.025f, 0.023f, 0.022f);
        RenderSettings.ambientIntensity = 0.42f;
        RenderSettings.reflectionIntensity = 0.20f;
        RenderSettings.fog = false;

        AddDirectional(root.transform, "Window Sun - directional shadows", Quaternion.LookRotation(inward, Vector3.up).eulerAngles, new Color(1.0f, 0.78f, 0.52f), 0.72f);

        var cookie = EnsureWindowSunCookie();
        var outsideWindow = windowCenter - inward * Mathf.Max(1.2f, radius * 0.26f) + Vector3.up * (bounds.extents.y * 0.16f);
        var beam = AddSpot(root.transform, "Window Sun - focused beam through glass", outsideWindow, target, new Color(1.0f, 0.73f, 0.46f), 2.15f, radius * 1.75f, 46f, true);
        beam.cookie = cookie;
        beam.cookieSize = 1.0f;

        AddSpot(root.transform, "Window Sun - soft spill", outsideWindow + Vector3.up * 0.35f, bounds.center, new Color(1.0f, 0.79f, 0.58f), 0.55f, radius * 1.55f, 72f, false);
        AddPoint(root.transform, "Very Soft Interior Bounce", bounds.center + new Vector3(-bounds.extents.x * 0.25f, bounds.extents.y * 0.05f, bounds.extents.z * 0.10f), new Color(0.62f, 0.70f, 0.82f), 0.22f, radius * 0.95f);

        var report = new StringBuilder();
        report.AppendLine("Window sun lighting applied");
        report.AppendLine("Disabled previous lights: " + disabledLights);
        report.AppendLine("Window renderer: " + (windowRenderer != null ? windowRenderer.gameObject.name : "<fallback>"));
        report.AppendLine("Window center: " + windowCenter);
        report.AppendLine("Sun direction: " + inward);
        report.AppendLine("Beam source: " + outsideWindow);
        report.AppendLine("Target: " + target);
        report.AppendLine("Ambient intensity: " + RenderSettings.ambientIntensity);
        File.WriteAllText("Assets/SceneExport/window_sun_lighting_report.txt", report.ToString());
        AssetDatabase.ImportAsset("Assets/SceneExport/window_sun_lighting_report.txt");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log(report.ToString());
    }

    public static void ApplyWindowShadowAccent()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        DestroyIfExists("Window Shadow Accent");

        var bounds = GetSceneRendererBounds();
        var windowRenderer = FindWindowRenderer();
        var windowCenter = windowRenderer != null ? windowRenderer.bounds.center : bounds.center + new Vector3(-bounds.extents.x, bounds.extents.y * 0.15f, bounds.extents.z * 0.35f);
        var inward = bounds.center - windowCenter;
        inward.y = 0f;
        if (inward.sqrMagnitude < 0.01f)
        {
            inward = new Vector3(0.85f, 0f, -0.45f);
        }
        inward.Normalize();

        var radius = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        var floorY = bounds.min.y + 0.025f;
        var root = new GameObject("Window Shadow Accent");
        QualitySettings.pixelLightCount = Mathf.Max(QualitySettings.pixelLightCount, 8);

        var rendererCount = 0;
        var windowCasterOffCount = 0;
        var materialCount = 0;
        var touchedMaterials = new HashSet<Material>();
        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
        {
            if (renderer.transform.IsChildOf(root.transform))
            {
                continue;
            }

            var isWindowSurface = IsWindowSurface(renderer);
            renderer.receiveShadows = !isWindowSurface;
            renderer.shadowCastingMode = isWindowSurface
                ? UnityEngine.Rendering.ShadowCastingMode.Off
                : UnityEngine.Rendering.ShadowCastingMode.On;
            rendererCount++;
            if (isWindowSurface)
            {
                windowCasterOffCount++;
            }

            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null || material.shader == null || !material.shader.name.Contains("lilToon") || !touchedMaterials.Add(material))
                {
                    continue;
                }

                ApplyLilToonShadowReceiving(material);
                materialCount++;
            }
        }

        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
        {
            if (light.type == LightType.Directional || light.gameObject.name.EndsWith(" Realtime Proxy"))
            {
                light.enabled = true;
                light.renderMode = LightRenderMode.ForcePixel;
                light.shadows = LightShadows.Soft;
                light.shadowStrength = Mathf.Max(light.shadowStrength, 0.58f);
                light.shadowBias = 0.025f;
                light.shadowNormalBias = 0.18f;
            }
        }

        var cookie = EnsureWindowGridCookie();
        var target = new Vector3(
            windowCenter.x + inward.x * radius * 0.34f,
            floorY + 0.08f,
            windowCenter.z + inward.z * radius * 0.34f);
        var source = windowCenter - inward * Mathf.Max(0.8f, radius * 0.18f) + Vector3.up * Mathf.Max(0.7f, bounds.extents.y * 0.36f);
        var beam = AddSpot(root.transform, "Window Shadow Accent - cookie beam", source, target, new Color(1.0f, 0.82f, 0.58f), 2.4f, radius * 1.45f, 48f, true);
        beam.cookie = cookie;
        beam.cookieSize = 1.15f;
        beam.shadowStrength = 0.78f;

        var decalMaterial = EnsureWindowShadowDecalMaterial();
        var decal = GameObject.CreatePrimitive(PrimitiveType.Quad);
        decal.name = "Window Shadow Accent - floor window shadow";
        decal.transform.SetParent(root.transform);
        decal.transform.position = new Vector3(
            windowCenter.x + inward.x * radius * 0.40f,
            floorY,
            windowCenter.z + inward.z * radius * 0.40f);
        decal.transform.rotation = Quaternion.LookRotation(Vector3.up, inward);
        var windowWidth = windowRenderer != null ? Mathf.Max(windowRenderer.bounds.size.x, windowRenderer.bounds.size.z) : bounds.extents.x * 0.45f;
        var shadowWidth = Mathf.Clamp(windowWidth * 1.55f, 1.15f, 2.65f);
        decal.transform.localScale = new Vector3(shadowWidth, shadowWidth * 1.58f, 1f);
        var collider = decal.GetComponent<Collider>();
        if (collider != null)
        {
            Object.DestroyImmediate(collider);
        }
        var decalRenderer = decal.GetComponent<MeshRenderer>();
        decalRenderer.sharedMaterial = decalMaterial;
        decalRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        decalRenderer.receiveShadows = false;

        var report = new StringBuilder();
        report.AppendLine("Window shadow accent applied");
        report.AppendLine("Window renderer: " + (windowRenderer != null ? windowRenderer.gameObject.name : "<fallback>"));
        report.AppendLine("Window center: " + windowCenter);
        report.AppendLine("Inward direction: " + inward);
        report.AppendLine("Cookie beam source: " + source);
        report.AppendLine("Cookie beam target: " + target);
        report.AppendLine("Renderers configured: " + rendererCount);
        report.AppendLine("Window/glass/backdrop casters disabled: " + windowCasterOffCount);
        report.AppendLine("lilToon materials configured to receive shadows: " + materialCount);
        report.AppendLine("Decal scale: " + decal.transform.localScale);
        File.WriteAllText("Assets/SceneExport/window_shadow_accent_report.txt", report.ToString());
        AssetDatabase.ImportAsset("Assets/SceneExport/window_shadow_accent_report.txt");
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log(report.ToString());
    }

    public static void ApplyRealtimeWindowShadowRig()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        DestroyIfExists("Window Shadow Accent");
        DestroyIfExists("Window Realtime Projection");

        var bounds = GetSceneRendererBounds();
        var windowRenderer = FindWindowRenderer();
        var windowCenter = windowRenderer != null ? windowRenderer.bounds.center : bounds.center + new Vector3(-bounds.extents.x, bounds.extents.y * 0.15f, bounds.extents.z * 0.35f);
        var inward = bounds.center - windowCenter;
        inward.y = 0f;
        if (inward.sqrMagnitude < 0.01f)
        {
            inward = new Vector3(0.85f, 0f, -0.45f);
        }
        inward.Normalize();

        var radius = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        var floorY = bounds.min.y + 0.035f;
        var root = new GameObject("Window Realtime Projection");
        var rootRotation = Quaternion.LookRotation(inward, Vector3.up);
        var windowSize = windowRenderer != null ? windowRenderer.bounds.size : new Vector3(1.6f, 1.4f, 0.05f);
        var frameWidth = Mathf.Clamp(Mathf.Max(windowSize.x, windowSize.z) * 0.92f, 1.25f, 2.15f);
        var frameHeight = Mathf.Clamp(windowSize.y * 0.92f, 1.05f, 1.95f);
        var frameDepth = 0.055f;
        var barThickness = Mathf.Clamp(frameWidth * 0.055f, 0.055f, 0.095f);
        var frameCenter = windowCenter - inward * 0.035f + Vector3.up * Mathf.Clamp(frameHeight * 0.03f, 0.02f, 0.07f);

        var shadowMaterial = EnsureRealtimeShadowCasterMaterial();
        var bars = new List<GameObject>();
        bars.Add(CreateRealtimeShadowBar(root.transform, "Window Shadow Caster - center vertical", frameCenter, rootRotation, new Vector3(0f, 0f, 0f), new Vector3(barThickness, frameHeight, frameDepth), shadowMaterial));
        bars.Add(CreateRealtimeShadowBar(root.transform, "Window Shadow Caster - center horizontal", frameCenter, rootRotation, new Vector3(0f, 0f, 0f), new Vector3(frameWidth, barThickness, frameDepth), shadowMaterial));
        bars.Add(CreateRealtimeShadowBar(root.transform, "Window Shadow Caster - left side", frameCenter, rootRotation, new Vector3(-frameWidth * 0.5f, 0f, 0f), new Vector3(barThickness, frameHeight, frameDepth), shadowMaterial));
        bars.Add(CreateRealtimeShadowBar(root.transform, "Window Shadow Caster - right side", frameCenter, rootRotation, new Vector3(frameWidth * 0.5f, 0f, 0f), new Vector3(barThickness, frameHeight, frameDepth), shadowMaterial));
        bars.Add(CreateRealtimeShadowBar(root.transform, "Window Shadow Caster - top side", frameCenter, rootRotation, new Vector3(0f, frameHeight * 0.5f, 0f), new Vector3(frameWidth, barThickness, frameDepth), shadowMaterial));
        bars.Add(CreateRealtimeShadowBar(root.transform, "Window Shadow Caster - bottom side", frameCenter, rootRotation, new Vector3(0f, -frameHeight * 0.5f, 0f), new Vector3(frameWidth, barThickness, frameDepth), shadowMaterial));

        var lightTarget = new Vector3(
            windowCenter.x + inward.x * radius * 0.46f,
            floorY + 0.12f,
            windowCenter.z + inward.z * radius * 0.46f);
        var lightSource = windowCenter - inward * Mathf.Max(1.25f, radius * 0.38f) + Vector3.up * Mathf.Max(1.15f, frameHeight * 0.72f);
        var lightRange = Mathf.Max(radius * 2.1f, 8f);
        var right = rootRotation * Vector3.right;
        var softOffsets = new[]
        {
            Vector3.zero,
            right * 0.18f,
            -right * 0.18f,
            Vector3.up * 0.14f,
            -Vector3.up * 0.10f
        };
        var softLights = new List<Light>();
        for (var index = 0; index < softOffsets.Length; index++)
        {
            var source = lightSource + softOffsets[index];
            var target = lightTarget - softOffsets[index] * 0.18f;
            softLights.Add(CreateRealtimeProjectionLight(root.transform, "Window Realtime Projection - soft sun spot " + (index + 1), source, target, lightRange));
        }

        var receiveCount = 0;
        var passThroughCount = 0;
        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
        {
            if (renderer.transform.IsChildOf(root.transform))
            {
                continue;
            }

            if (IsLightPassWindowRenderer(renderer))
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                passThroughCount++;
                continue;
            }

            renderer.receiveShadows = true;
            receiveCount++;
        }

        QualitySettings.pixelLightCount = Mathf.Max(QualitySettings.pixelLightCount, 8);
        QualitySettings.shadows = ShadowQuality.All;
        QualitySettings.shadowResolution = ShadowResolution.High;
        QualitySettings.shadowDistance = Mathf.Max(QualitySettings.shadowDistance, 30f);
        RenderSettings.skybox = EnsureBrightDaySkyboxMaterial();
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.72f, 0.84f, 1.00f);
        RenderSettings.ambientEquatorColor = new Color(0.88f, 0.92f, 0.98f);
        RenderSettings.ambientGroundColor = new Color(0.58f, 0.62f, 0.70f);
        RenderSettings.ambientIntensity = 0.92f;
        RenderSettings.reflectionIntensity = 0.45f;
        RenderSettings.fog = false;

        var report = new StringBuilder();
        report.AppendLine("Realtime window shadow rig applied");
        report.AppendLine("This uses realtime shadow maps from shadow-only window bars, not a floor decal.");
        report.AppendLine("Window renderer: " + (windowRenderer != null ? GetHierarchyPath(windowRenderer.transform) : "<fallback>"));
        report.AppendLine("Window center: " + windowCenter);
        report.AppendLine("Inward direction: " + inward);
        report.AppendLine("Frame center: " + frameCenter);
        report.AppendLine("Frame size: " + frameWidth + " x " + frameHeight);
        report.AppendLine("Realtime light source: " + lightSource);
        report.AppendLine("Realtime light target: " + lightTarget);
        report.AppendLine("Soft realtime light count: " + softLights.Count);
        report.AppendLine("Soft realtime total intensity: " + softLights.Sum(light => light.intensity));
        report.AppendLine("Per-light shadow strength: " + softLights[0].shadowStrength);
        report.AppendLine("Shadow-only bar count: " + bars.Count);
        report.AppendLine("Renderers receiving realtime shadows: " + receiveCount);
        report.AppendLine("Glass/window pass-through renderers: " + passThroughCount);
        const string reportPath = "Assets/SceneExport/realtime_window_shadow_report.txt";
        File.WriteAllText(reportPath, report.ToString());
        AssetDatabase.ImportAsset(reportPath);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log(report.ToString());
    }

    public static void FixStandardWindowGlassAndLightPass()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var report = new StringBuilder();
        report.AppendLine("Standard window glass and light pass fix");

        var touchedMaterials = new HashSet<Material>();
        var glassMaterialCount = 0;
        var windowMaterialCount = 0;
        var rendererCount = 0;
        var enabledLightCount = 0;
        var configuredLightCount = 0;

        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
        {
            if (!IsLightPassWindowRenderer(renderer))
            {
                continue;
            }

            renderer.gameObject.SetActive(true);
            renderer.enabled = true;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            rendererCount++;

            report.AppendLine("Renderer lets light pass: " + GetHierarchyPath(renderer.transform));

            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null || !touchedMaterials.Add(material))
                {
                    continue;
                }

                if (IsStandardWindowGlassMaterial(material))
                {
                    ConfigureStandardTransparentGlass(material);
                    glassMaterialCount++;
                    report.AppendLine("Glass material transparent Standard: " + AssetDatabase.GetAssetPath(material));
                }
                else if (IsLightPassWindowMaterial(material))
                {
                    ConfigureNoShadowWindowMaterial(material);
                    windowMaterialCount++;
                    report.AppendLine("Window material no shadow caster: " + AssetDatabase.GetAssetPath(material));
                }
            }
        }

        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/SceneExport" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null || touchedMaterials.Contains(material))
            {
                continue;
            }

            if (IsStandardWindowGlassMaterial(material))
            {
                ConfigureStandardTransparentGlass(material);
                touchedMaterials.Add(material);
                glassMaterialCount++;
                report.AppendLine("Glass material asset transparent Standard: " + path);
            }
            else if (IsLightPassWindowMaterial(material))
            {
                ConfigureNoShadowWindowMaterial(material);
                touchedMaterials.Add(material);
                windowMaterialCount++;
                report.AppendLine("Window material asset no shadow caster: " + path);
            }
        }

        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
        {
            if (!ShouldEnableWindowPassLight(light))
            {
                continue;
            }

            light.gameObject.SetActive(true);
            light.enabled = true;
            light.cullingMask = ~0;
            enabledLightCount++;

            if (IsWindowAccentLight(light))
            {
                light.renderMode = LightRenderMode.ForcePixel;
                light.shadows = LightShadows.Soft;
                light.shadowStrength = Mathf.Max(light.shadowStrength, 0.65f);
                light.shadowBias = Mathf.Min(Mathf.Max(light.shadowBias, 0.015f), 0.035f);
                light.shadowNormalBias = Mathf.Min(Mathf.Max(light.shadowNormalBias, 0.08f), 0.22f);
                light.intensity = Mathf.Max(light.intensity, 2.4f);
                light.range = Mathf.Max(light.range, GetSceneRendererBounds().size.magnitude * 0.45f);
                configuredLightCount++;
                report.AppendLine("Window accent light enabled: " + GetHierarchyPath(light.transform) + " intensity=" + light.intensity + " range=" + light.range);
            }
            else
            {
                report.AppendLine("Original/proxy light enabled: " + GetHierarchyPath(light.transform) + " type=" + light.type + " intensity=" + light.intensity);
            }
        }

        QualitySettings.pixelLightCount = Mathf.Max(QualitySettings.pixelLightCount, 8);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientIntensity = Mathf.Max(RenderSettings.ambientIntensity, 0.75f);
        RenderSettings.reflectionIntensity = Mathf.Max(RenderSettings.reflectionIntensity, 0.35f);

        report.AppendLine("Light-pass renderers configured: " + rendererCount);
        report.AppendLine("Glass materials configured: " + glassMaterialCount);
        report.AppendLine("Window/backdrop materials configured: " + windowMaterialCount);
        report.AppendLine("Lights enabled for pass: " + enabledLightCount);
        report.AppendLine("Window accent lights configured: " + configuredLightCount);

        const string reportPath = "Assets/SceneExport/standard_window_glass_light_fix_report.txt";
        File.WriteAllText(reportPath, report.ToString());
        AssetDatabase.ImportAsset(reportPath);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log(report.ToString());
    }

    public static void RestoreOriginalBlenderLights()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        DestroyIfExists("Blender Lighting");
        DestroyIfExists("Blender Realtime Lighting");
        DestroyIfExists("Window Sun Lighting");

        var restored = 0;
        var report = new StringBuilder();
        report.AppendLine("Original Blender lights restored");

        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
        {
            light.gameObject.SetActive(true);
            light.enabled = true;
            light.gameObject.name = light.gameObject.name.Replace(" (Blender Imported Disabled)", string.Empty);
            light.shadows = LightShadows.Soft;
            report.AppendLine(light.gameObject.name + " type=" + light.type + " intensity=" + light.intensity + " enabled=" + light.enabled);
            restored++;
        }

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.212f, 0.227f, 0.259f);
        RenderSettings.ambientEquatorColor = new Color(0.114f, 0.125f, 0.133f);
        RenderSettings.ambientGroundColor = new Color(0.047f, 0.043f, 0.035f);
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.reflectionIntensity = 1f;

        report.AppendLine("Restored light count: " + restored);
        File.WriteAllText("Assets/SceneExport/original_blender_lights_restored.txt", report.ToString());
        AssetDatabase.ImportAsset("Assets/SceneExport/original_blender_lights_restored.txt");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log(report.ToString());
    }

    public static void MakeOriginalBlenderLightsRealtime()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        DestroyIfExists("Blender Lighting");
        DestroyIfExists("Blender Realtime Lighting");
        DestroyIfExists("Window Sun Lighting");

        foreach (var proxy in GameObject.FindObjectsByType<Transform>(FindObjectsInactive.Include))
        {
            if (proxy.name.EndsWith(" Realtime Proxy"))
            {
                Object.DestroyImmediate(proxy.gameObject);
            }
        }

        var bounds = GetSceneRendererBounds();
        var radius = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        var report = new StringBuilder();
        report.AppendLine("Original Blender lights made realtime");
        report.AppendLine("Scene radius: " + radius);

        QualitySettings.pixelLightCount = Mathf.Max(QualitySettings.pixelLightCount, 8);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.30f, 0.29f, 0.27f);
        RenderSettings.ambientEquatorColor = new Color(0.13f, 0.12f, 0.115f);
        RenderSettings.ambientGroundColor = new Color(0.040f, 0.036f, 0.032f);
        RenderSettings.ambientIntensity = 0.82f;
        RenderSettings.reflectionIntensity = 0.35f;
        RenderSettings.fog = false;

        var converted = 0;
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
        {
            if (light.gameObject.name.EndsWith(" Realtime Proxy") || IsGeneratedLighting(light.transform))
            {
                continue;
            }

            light.gameObject.SetActive(true);
            light.enabled = true;
            light.gameObject.name = light.gameObject.name.Replace(" (Blender Imported Disabled)", string.Empty);
            light.color = light.color.linear;

            if (light.type == LightType.Directional)
            {
                light.intensity = 1.05f;
                light.shadows = LightShadows.Soft;
                light.shadowStrength = 0.42f;
                light.renderMode = LightRenderMode.ForcePixel;
                report.AppendLine(light.gameObject.name + " kept as realtime Directional intensity=1.05");
                converted++;
                continue;
            }

            light.enabled = true;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.55f;
            var proxy = new GameObject(light.gameObject.name + " Realtime Proxy");
            proxy.transform.SetParent(light.transform, false);
            proxy.transform.localPosition = Vector3.zero;
            proxy.transform.localRotation = Quaternion.identity;
            proxy.transform.localScale = Vector3.one;

            var proxyLight = proxy.AddComponent<Light>();
            proxyLight.type = LightType.Point;
            proxyLight.color = light.color;
            proxyLight.range = Mathf.Max(1.2f, radius * 0.55f);
            proxyLight.intensity = Mathf.Clamp(light.intensity * 0.45f + 0.65f, 0.75f, 4.8f);
            proxyLight.shadows = light.intensity > 2f ? LightShadows.Soft : LightShadows.None;
            proxyLight.shadowStrength = 0.35f;
            proxyLight.renderMode = LightRenderMode.ForcePixel;

            report.AppendLine(light.gameObject.name + " kept Rectangle/Area component enabled, added Point proxy intensity=" + proxyLight.intensity + " range=" + proxyLight.range);
            converted++;
        }

        report.AppendLine("Realtime original light entries: " + converted);
        File.WriteAllText("Assets/SceneExport/original_blender_lights_realtime.txt", report.ToString());
        AssetDatabase.ImportAsset("Assets/SceneExport/original_blender_lights_realtime.txt");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log(report.ToString());
    }

    public static void EnableOriginalBlenderLightComponents()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var enabledOriginals = 0;
        var enabledProxies = 0;
        var report = new StringBuilder();
        report.AppendLine("Original Blender light components enabled");

        QualitySettings.pixelLightCount = Mathf.Max(QualitySettings.pixelLightCount, 8);
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
        {
            var isProxy = light.gameObject.name.EndsWith(" Realtime Proxy");
            var isGenerated = IsGeneratedLighting(light.transform);
            if (isGenerated && !isProxy)
            {
                continue;
            }

            light.gameObject.SetActive(true);
            light.enabled = true;
            light.gameObject.name = light.gameObject.name.Replace(" (Blender Imported Disabled)", string.Empty);
            light.renderMode = LightRenderMode.ForcePixel;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = Mathf.Max(light.shadowStrength, isProxy ? 0.50f : 0.58f);
            light.shadowBias = 0.025f;
            light.shadowNormalBias = 0.18f;
            if (!isProxy)
            {
                light.lightmapBakeType = LightmapBakeType.Mixed;
            }

            if (isProxy)
            {
                enabledProxies++;
            }
            else
            {
                enabledOriginals++;
            }

            report.AppendLine((isProxy ? "Proxy enabled: " : "Original enabled: ") + light.gameObject.name + " type=" + light.type + " intensity=" + light.intensity + " shadows=" + light.shadows);
        }

        report.AppendLine("Original count: " + enabledOriginals);
        report.AppendLine("Proxy count: " + enabledProxies);
        File.WriteAllText("Assets/SceneExport/original_light_components_enabled.txt", report.ToString());
        AssetDatabase.ImportAsset("Assets/SceneExport/original_light_components_enabled.txt");
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log(report.ToString());
    }

    public static void InspectTransparentCandidates()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var report = new StringBuilder();
        report.AppendLine("Transparent/window candidate renderers");
        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
        {
            var name = renderer.gameObject.name;
            var material = renderer.sharedMaterial;
            var shaderName = material != null && material.shader != null ? material.shader.name : "<none>";
            var texturePath = material != null && material.mainTexture != null ? AssetDatabase.GetAssetPath(material.mainTexture) : "<none>";
            var color = material != null && material.HasProperty("_Color") ? material.color : Color.clear;
            if (
                name.Contains("窗") || name.Contains("玻") || name.Contains("璃") || name.ToLowerInvariant().Contains("window") || name.ToLowerInvariant().Contains("glass")
                || IsWindowPlaneName(name)
                || shaderName.ToLowerInvariant().Contains("transparent")
                || color.a < 0.99f
                || texturePath.Contains("024_")
            )
            {
                report.AppendLine(name + " active=" + renderer.gameObject.activeInHierarchy + " enabled=" + renderer.enabled);
                report.AppendLine("  bounds center=" + renderer.bounds.center + " size=" + renderer.bounds.size);
                report.AppendLine("  material=" + (material != null ? material.name : "<none>") + " shader=" + shaderName + " color=" + color + " texture=" + texturePath);
            }
        }

        File.WriteAllText("Assets/SceneExport/transparent_candidates_report.txt", report.ToString());
        AssetDatabase.ImportAsset("Assets/SceneExport/transparent_candidates_report.txt");
        Debug.Log(report.ToString());
    }

    public static void FixWindowTransparency()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var backdropShader = Shader.Find("SceneExport/IndoorOnlyBackdropTexture");
        var glassShader = Shader.Find("SceneExport/DoubleSidedTransparentGlass");
        if (backdropShader == null || glassShader == null)
        {
            throw new FileNotFoundException("Window transparency shaders are not imported.");
        }

        Directory.CreateDirectory("Assets/SceneExport/Materials");
        var report = new StringBuilder();
        report.AppendLine("Window transparency fix");

        var bounds = GetSceneRendererBounds();
        var backdrop = FindWindowRenderer();
        if (backdrop != null)
        {
            var source = backdrop.sharedMaterial;
            var texture = source != null && source.HasProperty("_MainTex") ? source.mainTexture : null;
            var materialPath = "Assets/SceneExport/Materials/Window_Backdrop_IndoorOnly.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(backdropShader);
                AssetDatabase.CreateAsset(material, materialPath);
            }

            material.shader = backdropShader;
            material.mainTexture = texture;
            material.color = Color.white;
            var visibleNormal = (bounds.center - backdrop.bounds.center).normalized;
            material.SetVector("_VisibleNormal", new Vector4(visibleNormal.x, visibleNormal.y, visibleNormal.z, 0f));
            backdrop.sharedMaterial = material;
            report.AppendLine("Backdrop made indoor-only: " + backdrop.gameObject.name + " normal=" + visibleNormal + " texture=" + (texture != null ? AssetDatabase.GetAssetPath(texture) : "<none>"));
        }

        var glassCount = 0;
        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
        {
            if (renderer == backdrop || IsWindowPlaneName(renderer.gameObject.name))
            {
                continue;
            }

            var source = renderer.sharedMaterial;
            if (source == null || !source.HasProperty("_Color"))
            {
                continue;
            }

            var color = source.color;
            if (color.a >= 0.95f)
            {
                continue;
            }

            var materialPath = "Assets/SceneExport/Materials/" + SanitizeAssetName(source.name) + "_TransparentGlass.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(glassShader);
                AssetDatabase.CreateAsset(material, materialPath);
            }

            material.shader = glassShader;
            material.color = new Color(color.r, color.g, color.b, Mathf.Clamp(color.a, 0.18f, 0.55f));
            if (source.HasProperty("_MainTex"))
            {
                material.mainTexture = source.mainTexture;
            }
            renderer.sharedMaterial = material;
            renderer.enabled = true;
            renderer.gameObject.SetActive(true);
            glassCount++;
            report.AppendLine("Transparent glass: " + renderer.gameObject.name + " material=" + materialPath + " alpha=" + material.color.a);
        }

        report.AppendLine("Transparent glass renderer count: " + glassCount);
        File.WriteAllText("Assets/SceneExport/window_transparency_fix_report.txt", report.ToString());
        AssetDatabase.ImportAsset("Assets/SceneExport/window_transparency_fix_report.txt");
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log(report.ToString());
    }

    public static void ConvertSceneMaterialsToLilToon()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var lilOpaque = Shader.Find("lilToon");
        var lilTransparent = Shader.Find("Hidden/lilToonTransparent");
        if (lilOpaque == null || lilTransparent == null)
        {
            throw new FileNotFoundException("lilToon shaders were not found. Make sure jp.lilxyzw.liltoon is installed.");
        }

        Directory.CreateDirectory("Assets/SceneExport/MaterialsLilToon");
        var converted = new Dictionary<Material, Material>();
        var preserved = new List<string>();
        var rendererCount = 0;
        var slotCount = 0;

        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
        {
            var materials = renderer.sharedMaterials;
            var changed = false;
            for (var i = 0; i < materials.Length; i++)
            {
                var source = materials[i];
                if (source == null)
                {
                    continue;
                }

                if (ShouldPreserveFunctionalWindowMaterial(renderer, source))
                {
                    preserved.Add(renderer.gameObject.name + " -> " + source.name);
                    continue;
                }

                if (!converted.TryGetValue(source, out var target))
                {
                    target = CreateLilToonMaterial(source, lilOpaque, lilTransparent);
                    converted[source] = target;
                }

                materials[i] = target;
                changed = true;
                slotCount++;
            }

            if (changed)
            {
                renderer.sharedMaterials = materials;
                rendererCount++;
            }
        }

        var report = new StringBuilder();
        report.AppendLine("Scene materials converted to lilToon");
        report.AppendLine("lilToon package: jp.lilxyzw.liltoon 2.3.2");
        report.AppendLine("Unique source materials converted: " + converted.Count);
        report.AppendLine("Renderer count touched: " + rendererCount);
        report.AppendLine("Material slots replaced: " + slotCount);
        report.AppendLine("Preserved functional window materials: " + preserved.Count);
        foreach (var item in preserved)
        {
            report.AppendLine("PRESERVED " + item);
        }

        File.WriteAllText("Assets/SceneExport/liltoon_conversion_report.txt", report.ToString());
        AssetDatabase.ImportAsset("Assets/SceneExport/liltoon_conversion_report.txt");
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log(report.ToString());
    }

    public static void VerifyLilToonConversion()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var totalSlots = 0;
        var lilSlots = 0;
        var preservedSlots = 0;
        var other = new HashSet<string>();

        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
        {
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null)
                {
                    continue;
                }

                totalSlots++;
                var shaderName = material.shader != null ? material.shader.name : "<none>";
                if (shaderName.Contains("lilToon"))
                {
                    lilSlots++;
                }
                else if (renderer.gameObject.name == "平面" && shaderName == "SceneExport/IndoorOnlyBackdropTexture")
                {
                    preservedSlots++;
                }
                else
                {
                    other.Add(renderer.gameObject.name + " -> " + material.name + " [" + shaderName + "]");
                }
            }
        }

        var report = new StringBuilder();
        report.AppendLine("lilToon conversion verification");
        report.AppendLine("Total material slots: " + totalSlots);
        report.AppendLine("lilToon slots: " + lilSlots);
        report.AppendLine("Preserved functional slots: " + preservedSlots);
        report.AppendLine("Other slots: " + other.Count);
        foreach (var item in other)
        {
            report.AppendLine("OTHER " + item);
        }

        File.WriteAllText("Assets/SceneExport/liltoon_verify_report.txt", report.ToString());
        AssetDatabase.ImportAsset("Assets/SceneExport/liltoon_verify_report.txt");
        Debug.Log(report.ToString());
    }

    private static Material CreateLilToonMaterial(Material source, Shader lilOpaque, Shader lilTransparent)
    {
        var isTransparent = IsTransparentMaterial(source);
        var shader = isTransparent ? lilTransparent : lilOpaque;
        var materialPath = "Assets/SceneExport/MaterialsLilToon/" + SanitizeAssetName(source.name) + "_" + Mathf.Abs(source.GetInstanceID()) + "_lilToon.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, materialPath);
        }
        else
        {
            material.shader = shader;
        }

        var color = GetMaterialColor(source);
        if (!isTransparent)
        {
            color.a = 1f;
        }

        material.SetColor("_Color", color);
        var mainTexture = GetMainTexture(source);
        if (mainTexture != null)
        {
            material.SetTexture("_MainTex", mainTexture);
            material.SetTextureScale("_MainTex", GetTextureScale(source, "_MainTex"));
            material.SetTextureOffset("_MainTex", GetTextureOffset(source, "_MainTex"));
        }

        if (material.HasProperty("_UseShadow"))
        {
            material.SetInt("_UseShadow", 1);
        }
        if (material.HasProperty("_ShadowColor"))
        {
            material.SetColor("_ShadowColor", new Color(0.76f, 0.80f, 0.94f, 1f));
        }
        if (material.HasProperty("_Shadow2ndColor"))
        {
            material.SetColor("_Shadow2ndColor", new Color(0.66f, 0.70f, 0.88f, 1f));
        }
        if (material.HasProperty("_ShadowBorder"))
        {
            material.SetFloat("_ShadowBorder", 0.58f);
        }
        if (material.HasProperty("_ShadowBlur"))
        {
            material.SetFloat("_ShadowBlur", 0.035f);
        }
        if (material.HasProperty("_ShadowStrength"))
        {
            material.SetFloat("_ShadowStrength", 0.52f);
        }
        if (material.HasProperty("_AsUnlit"))
        {
            material.SetFloat("_AsUnlit", 0.03f);
        }
        if (material.HasProperty("_VertexLightStrength"))
        {
            material.SetFloat("_VertexLightStrength", 1f);
        }
        if (material.HasProperty("_LightMinLimit"))
        {
            material.SetFloat("_LightMinLimit", 0.20f);
        }
        if (material.HasProperty("_LightMaxLimit"))
        {
            material.SetFloat("_LightMaxLimit", 1.45f);
        }
        if (material.HasProperty("_lilDirectionalLightStrength"))
        {
            material.SetFloat("_lilDirectionalLightStrength", 1f);
        }
        if (material.HasProperty("_MonochromeLighting"))
        {
            material.SetFloat("_MonochromeLighting", 0f);
        }
        ApplyAnimeFreshStyle(material, isTransparent);

        var normalMap = GetFirstTexture(source, "_BumpMap", "_NormalMap");
        if (normalMap != null && material.HasProperty("_BumpMap"))
        {
            material.SetInt("_UseBumpMap", 1);
            material.SetTexture("_BumpMap", normalMap);
        }

        var emissionMap = GetFirstTexture(source, "_EmissionMap", "_EmissionTex");
        var emissionColor = GetSourceColor(source, "_EmissionColor", Color.black);
        if ((emissionMap != null || emissionColor.maxColorComponent > 0.01f) && material.HasProperty("_UseEmission"))
        {
            material.SetInt("_UseEmission", 1);
            material.SetColor("_EmissionColor", emissionColor.maxColorComponent > 0.01f ? emissionColor : Color.white);
            if (emissionMap != null)
            {
                material.SetTexture("_EmissionMap", emissionMap);
            }
        }

        if (material.HasProperty("_Cull"))
        {
            material.SetInt("_Cull", isTransparent ? 0 : 2);
        }

        ConfigureLilToonRendering(material, isTransparent);
        EditorUtility.SetDirty(material);
        return material;
    }

    public static void RepairLilToonLightingResponse()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var materialCount = 0;
        var slotCount = 0;
        var changed = new HashSet<Material>();
        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
        {
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null || material.shader == null || !material.shader.name.Contains("lilToon"))
                {
                    continue;
                }

                slotCount++;
                if (changed.Add(material))
                {
                    ApplyLilToonLightingResponse(material);
                    materialCount++;
                }
            }
        }

        QualitySettings.pixelLightCount = Mathf.Max(QualitySettings.pixelLightCount, 8);
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
        {
            if (light.gameObject.name.EndsWith(" Realtime Proxy") || light.type == LightType.Directional)
            {
                light.enabled = true;
                light.renderMode = LightRenderMode.ForcePixel;
            }
        }

        var report = new StringBuilder();
        report.AppendLine("lilToon lighting response repaired");
        report.AppendLine("Unique lilToon materials touched: " + materialCount);
        report.AppendLine("lilToon material slots touched: " + slotCount);
        report.AppendLine("_VertexLightStrength=1, _AsUnlit=0.03, _LightMinLimit=0.20, _LightMaxLimit=1.45, _MonochromeLighting=0");
        File.WriteAllText("Assets/SceneExport/liltoon_lighting_repair_report.txt", report.ToString());
        AssetDatabase.ImportAsset("Assets/SceneExport/liltoon_lighting_repair_report.txt");
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log(report.ToString());
    }

    private static void ApplyLilToonLightingResponse(Material material)
    {
        if (material.HasProperty("_AsUnlit"))
        {
            material.SetFloat("_AsUnlit", 0.03f);
        }
        if (material.HasProperty("_VertexLightStrength"))
        {
            material.SetFloat("_VertexLightStrength", 1f);
        }
        if (material.HasProperty("_LightMinLimit"))
        {
            material.SetFloat("_LightMinLimit", 0.20f);
        }
        if (material.HasProperty("_LightMaxLimit"))
        {
            material.SetFloat("_LightMaxLimit", 1.45f);
        }
        if (material.HasProperty("_lilDirectionalLightStrength"))
        {
            material.SetFloat("_lilDirectionalLightStrength", 1f);
        }
        if (material.HasProperty("_MonochromeLighting"))
        {
            material.SetFloat("_MonochromeLighting", 0f);
        }

        EditorUtility.SetDirty(material);
    }

    private static void ApplyAnimeFreshStyle(Material material, bool isTransparent)
    {
        if (isTransparent)
        {
            if (material.HasProperty("_UseOutline"))
            {
                material.SetInt("_UseOutline", 0);
            }
            if (material.HasProperty("_UseRim"))
            {
                material.SetInt("_UseRim", 0);
            }

            return;
        }

        if (material.HasProperty("_UseOutline"))
        {
            material.SetInt("_UseOutline", 1);
        }
        if (material.HasProperty("_OutlineWidth"))
        {
            material.SetFloat("_OutlineWidth", 0.012f);
        }
        if (material.HasProperty("_OutlineFixWidth"))
        {
            material.SetFloat("_OutlineFixWidth", 0.75f);
        }
        if (material.HasProperty("_OutlineEnableLighting"))
        {
            material.SetFloat("_OutlineEnableLighting", 0.45f);
        }
        if (material.HasProperty("_OutlineColor"))
        {
            material.SetColor("_OutlineColor", new Color(0.36f, 0.40f, 0.56f, 1f));
        }

        if (material.HasProperty("_UseRim"))
        {
            material.SetInt("_UseRim", 1);
        }
        if (material.HasProperty("_RimColor"))
        {
            material.SetColor("_RimColor", new Color(0.78f, 0.93f, 1f, 0.55f));
        }
        if (material.HasProperty("_RimMainStrength"))
        {
            material.SetFloat("_RimMainStrength", 0.12f);
        }
        if (material.HasProperty("_RimNormalStrength"))
        {
            material.SetFloat("_RimNormalStrength", 0.65f);
        }
        if (material.HasProperty("_RimBorder"))
        {
            material.SetFloat("_RimBorder", 0.58f);
        }
        if (material.HasProperty("_RimBlur"))
        {
            material.SetFloat("_RimBlur", 0.42f);
        }
        if (material.HasProperty("_RimFresnelPower"))
        {
            material.SetFloat("_RimFresnelPower", 3.2f);
        }
        if (material.HasProperty("_RimEnableLighting"))
        {
            material.SetFloat("_RimEnableLighting", 0.75f);
        }
        if (material.HasProperty("_RimShadowMask"))
        {
            material.SetFloat("_RimShadowMask", 0.18f);
        }
    }

    private static void ApplyLilToonShadowReceiving(Material material)
    {
        if (material.HasProperty("_UseShadow"))
        {
            material.SetInt("_UseShadow", 1);
        }
        if (material.HasProperty("_ShadowReceive"))
        {
            material.SetFloat("_ShadowReceive", 1f);
        }
        if (material.HasProperty("_Shadow2ndReceive"))
        {
            material.SetFloat("_Shadow2ndReceive", 0.65f);
        }
        if (material.HasProperty("_Shadow3rdReceive"))
        {
            material.SetFloat("_Shadow3rdReceive", 0.25f);
        }
        if (material.HasProperty("_ShadowStrength"))
        {
            material.SetFloat("_ShadowStrength", 0.68f);
        }
        if (material.HasProperty("_ShadowBorder"))
        {
            material.SetFloat("_ShadowBorder", 0.54f);
        }
        if (material.HasProperty("_ShadowBlur"))
        {
            material.SetFloat("_ShadowBlur", 0.025f);
        }
        if (material.HasProperty("_ShadowColor"))
        {
            material.SetColor("_ShadowColor", new Color(0.62f, 0.66f, 0.86f, 1f));
        }
        if (material.HasProperty("_Shadow2ndColor"))
        {
            material.SetColor("_Shadow2ndColor", new Color(0.46f, 0.50f, 0.73f, 1f));
        }
        if (material.HasProperty("_LightMinLimit"))
        {
            material.SetFloat("_LightMinLimit", 0.12f);
        }

        EditorUtility.SetDirty(material);
    }

    private static bool IsWindowSurface(Renderer renderer)
    {
        var name = renderer.gameObject.name;
        if (IsWindowPlaneName(name) || name == "Line006.002" || name == "对象039" || name == "对象010" || name == "Rectangle006")
        {
            return true;
        }

        foreach (var material in renderer.sharedMaterials)
        {
            if (material == null || material.shader == null)
            {
                continue;
            }

            var shaderName = material.shader.name;
            var materialName = material.name;
            if (shaderName.Contains("TransparentGlass")
                || shaderName.Contains("IndoorOnlyBackdrop")
                || materialName.Contains("TransparentGlass")
                || materialName.Contains("Window_Backdrop"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLightPassWindowRenderer(Renderer renderer)
    {
        if (renderer == null)
        {
            return false;
        }

        var name = renderer.gameObject.name;
        if (name == "Line006.002" || name == "Rectangle006")
        {
            return true;
        }

        foreach (var material in renderer.sharedMaterials)
        {
            if (IsLightPassWindowMaterial(material))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLightPassWindowMaterial(Material material)
    {
        if (material == null)
        {
            return false;
        }

        var shaderName = material.shader != null ? material.shader.name : string.Empty;
        return ContainsIgnoreCase(material.name, "TransparentGlass")
            || ContainsIgnoreCase(material.name, "Window_Backdrop")
            || ContainsIgnoreCase(shaderName, "TransparentGlass")
            || ContainsIgnoreCase(shaderName, "IndoorOnlyBackdrop");
    }

    private static bool IsStandardWindowGlassMaterial(Material material)
    {
        if (material == null)
        {
            return false;
        }

        var shaderName = material.shader != null ? material.shader.name : string.Empty;
        return ContainsIgnoreCase(material.name, "TransparentGlass")
            || ContainsIgnoreCase(shaderName, "TransparentGlass");
    }

    private static void ConfigureStandardTransparentGlass(Material material)
    {
        var standard = Shader.Find("Standard");
        if (standard != null)
        {
            material.shader = standard;
        }

        var color = GetSourceColor(material, "_Color", GetSourceColor(material, "_BaseColor", Color.white));
        if (color.a >= 0.95f)
        {
            color.a = GetFallbackGlassAlpha(material.name);
        }

        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }

        material.SetOverrideTag("RenderType", "Transparent");
        SetMaterialFloat(material, "_Mode", 3f);
        SetMaterialInt(material, "_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        SetMaterialInt(material, "_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        SetMaterialInt(material, "_ZWrite", 0);
        SetMaterialFloat(material, "_Metallic", 0f);
        SetMaterialFloat(material, "_Glossiness", 0.35f);
        SetMaterialFloat(material, "_Cutoff", 0f);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        material.SetShaderPassEnabled("ShadowCaster", false);
        EditorUtility.SetDirty(material);
    }

    private static void ConfigureNoShadowWindowMaterial(Material material)
    {
        if (material == null)
        {
            return;
        }

        material.SetShaderPassEnabled("ShadowCaster", false);
        EditorUtility.SetDirty(material);
    }

    private static bool ShouldEnableWindowPassLight(Light light)
    {
        if (light == null)
        {
            return false;
        }

        return IsWindowAccentLight(light)
            || light.gameObject.name.EndsWith(" Realtime Proxy")
            || !IsGeneratedLighting(light.transform);
    }

    private static bool IsWindowAccentLight(Light light)
    {
        if (light == null)
        {
            return false;
        }

        var current = light.transform;
        while (current != null)
        {
            if (current.name == "Window Shadow Accent")
            {
                return true;
            }
            current = current.parent;
        }

        return light.gameObject.name.StartsWith("Window Shadow Accent");
    }

    private static void SetMaterialFloat(Material material, string property, float value)
    {
        if (material.HasProperty(property))
        {
            material.SetFloat(property, value);
        }
    }

    private static void SetMaterialInt(Material material, string property, int value)
    {
        if (material.HasProperty(property))
        {
            material.SetInt(property, value);
        }
    }

    private static void SetMaterialColor(Material material, string property, Color value)
    {
        if (material.HasProperty(property))
        {
            material.SetColor(property, value);
        }
    }

    private static float GetFallbackGlassAlpha(string materialName)
    {
        if (ContainsIgnoreCase(materialName, "Default.005"))
        {
            return 0.26f;
        }
        if (ContainsIgnoreCase(materialName, ".010"))
        {
            return 0.55f;
        }
        if (ContainsIgnoreCase(materialName, ".032"))
        {
            return 0.35f;
        }

        return 0.38f;
    }

    private static bool ContainsIgnoreCase(string value, string needle)
    {
        return !string.IsNullOrEmpty(value)
            && !string.IsNullOrEmpty(needle)
            && value.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetHierarchyPath(Transform transform)
    {
        var names = new List<string>();
        while (transform != null)
        {
            names.Add(transform.name);
            transform = transform.parent;
        }
        names.Reverse();
        return string.Join("/", names.ToArray());
    }

    private static void ConfigureLilToonRendering(Material material, bool isTransparent)
    {
        if (material.HasProperty("_TransparentMode"))
        {
            material.SetInt("_TransparentMode", isTransparent ? 2 : 0);
        }
        if (material.HasProperty("_SrcBlend"))
        {
            material.SetInt("_SrcBlend", isTransparent ? (int)UnityEngine.Rendering.BlendMode.SrcAlpha : (int)UnityEngine.Rendering.BlendMode.One);
        }
        if (material.HasProperty("_DstBlend"))
        {
            material.SetInt("_DstBlend", isTransparent ? (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : (int)UnityEngine.Rendering.BlendMode.Zero);
        }
        if (material.HasProperty("_SrcBlendAlpha"))
        {
            material.SetInt("_SrcBlendAlpha", (int)UnityEngine.Rendering.BlendMode.One);
        }
        if (material.HasProperty("_DstBlendAlpha"))
        {
            material.SetInt("_DstBlendAlpha", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        }
        if (material.HasProperty("_ZWrite"))
        {
            material.SetInt("_ZWrite", isTransparent ? 0 : 1);
        }
        if (material.HasProperty("_AlphaToMask"))
        {
            material.SetInt("_AlphaToMask", 0);
        }
        material.renderQueue = isTransparent ? (int)UnityEngine.Rendering.RenderQueue.Transparent : -1;
    }

    private static bool ShouldPreserveFunctionalWindowMaterial(Renderer renderer, Material material)
    {
        var shaderName = material.shader != null ? material.shader.name : string.Empty;
        return renderer.gameObject.name == "平面" && shaderName == "SceneExport/IndoorOnlyBackdropTexture";
    }

    private static bool IsTransparentMaterial(Material material)
    {
        var shaderName = material.shader != null ? material.shader.name.ToLowerInvariant() : string.Empty;
        return shaderName.Contains("transparent")
            || shaderName.Contains("glass")
            || GetMaterialColor(material).a < 0.95f
            || GetSourceFloat(material, "_Mode", 0f) >= 2f
            || GetSourceFloat(material, "_Surface", 0f) > 0f;
    }

    private static Color GetMaterialColor(Material material)
    {
        return GetSourceColor(material, "_Color", Color.white);
    }

    private static Color GetSourceColor(Material material, string name, Color fallback)
    {
        return material.HasProperty(name) ? material.GetColor(name) : fallback;
    }

    private static float GetSourceFloat(Material material, string name, float fallback)
    {
        return material.HasProperty(name) ? material.GetFloat(name) : fallback;
    }

    private static Texture GetMainTexture(Material material)
    {
        return GetFirstTexture(material, "_MainTex", "_BaseMap", "_BaseColorMap");
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

    private static Vector2 GetTextureScale(Material material, string name)
    {
        return material.HasProperty(name) ? material.GetTextureScale(name) : Vector2.one;
    }

    private static Vector2 GetTextureOffset(Material material, string name)
    {
        return material.HasProperty(name) ? material.GetTextureOffset(name) : Vector2.zero;
    }

    private static void AddDirectional(Transform parent, string name, Vector3 euler, Color color, float intensity)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent);
        obj.transform.rotation = Quaternion.Euler(euler);
        var light = obj.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = color;
        light.intensity = intensity;
        light.shadows = LightShadows.Soft;
        light.shadowStrength = 0.72f;
        light.shadowBias = 0.035f;
        light.shadowNormalBias = 0.25f;
    }

    private static void AddArea(Transform parent, string name, Vector3 position, Quaternion rotation, Color color, float intensity, float size)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent);
        obj.transform.SetPositionAndRotation(position, rotation);
        var light = obj.AddComponent<Light>();
        light.type = LightType.Rectangle;
        light.color = color;
        light.intensity = intensity;
        light.areaSize = new Vector2(size, size * 0.8f);
        light.shadows = LightShadows.Soft;
        light.shadowStrength = 0.55f;
    }

    private static Light AddSpot(Transform parent, string name, Vector3 position, Vector3 target, Color color, float intensity, float range, float spotAngle, bool shadows)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent);
        obj.transform.position = position;
        obj.transform.rotation = Quaternion.LookRotation(target - position, Vector3.up);
        var light = obj.AddComponent<Light>();
        light.type = LightType.Spot;
        light.color = color;
        light.intensity = intensity;
        light.range = range;
        light.spotAngle = spotAngle;
        light.innerSpotAngle = spotAngle * 0.55f;
        light.shadows = shadows ? LightShadows.Soft : LightShadows.None;
        light.shadowStrength = 0.65f;
        light.renderMode = LightRenderMode.ForcePixel;
        return light;
    }

    private static void AddPoint(Transform parent, string name, Vector3 position, Color color, float intensity, float range)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent);
        obj.transform.position = position;
        var light = obj.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = color;
        light.intensity = intensity;
        light.range = range;
        light.shadows = LightShadows.None;
        light.renderMode = LightRenderMode.ForcePixel;
    }

    private static Material EnsureBrightDaySkyboxMaterial()
    {
        const string materialPath = "Assets/SceneExport/Lighting/Bright_Day_Procedural_Skybox.mat";
        Directory.CreateDirectory("Assets/SceneExport/Lighting");

        var shader = Shader.Find("Skybox/Procedural");
        if (shader == null)
        {
            throw new FileNotFoundException("Unity procedural skybox shader was not found.");
        }

        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, materialPath);
        }
        else
        {
            material.shader = shader;
        }

        SetMaterialFloat(material, "_SunDisk", 0f);
        SetMaterialFloat(material, "_SunSize", 0.018f);
        SetMaterialFloat(material, "_SunSizeConvergence", 4f);
        SetMaterialFloat(material, "_AtmosphereThickness", 0.32f);
        SetMaterialColor(material, "_SkyTint", new Color(0.53f, 0.68f, 1.00f, 1f));
        SetMaterialColor(material, "_GroundColor", new Color(0.74f, 0.79f, 0.86f, 1f));
        SetMaterialFloat(material, "_Exposure", 1.08f);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static Material EnsureHdriSkyboxMaterial()
    {
        const string texturePath = "Assets/SceneExport/Textures/044_tears_of_steel_bridge_4k_ff8f4133eb.hdr";
        const string materialPath = "Assets/SceneExport/Lighting/HDRI_Tears_Of_Steel_Skybox.mat";
        Directory.CreateDirectory("Assets/SceneExport/Lighting");

        var shader = Shader.Find("Skybox/Panoramic");
        if (shader == null)
        {
            throw new FileNotFoundException("Unity panoramic skybox shader was not found.");
        }

        var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
        if (texture == null)
        {
            throw new FileNotFoundException("HDRI texture was not found.", texturePath);
        }

        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, materialPath);
        }
        else
        {
            material.shader = shader;
        }

        material.SetTexture("_Tex", texture);
        SetMaterialFloat(material, "_Exposure", 0.85f);
        SetMaterialFloat(material, "_Rotation", 0f);
        SetMaterialFloat(material, "_Mapping", 1f);
        SetMaterialFloat(material, "_ImageType", 0f);
        SetMaterialFloat(material, "_MirrorOnBack", 0f);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static Material EnsureRealtimeShadowCasterMaterial()
    {
        const string materialPath = "Assets/SceneExport/Materials/Window_Realtime_ShadowCaster.mat";
        Directory.CreateDirectory("Assets/SceneExport/Materials");

        var shader = Shader.Find("Standard");
        if (shader == null)
        {
            throw new FileNotFoundException("Unity Standard shader was not found.");
        }

        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, materialPath);
        }
        else
        {
            material.shader = shader;
        }

        material.SetColor("_Color", Color.black);
        SetMaterialFloat(material, "_Mode", 0f);
        SetMaterialInt(material, "_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
        SetMaterialInt(material, "_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
        SetMaterialInt(material, "_ZWrite", 1);
        material.DisableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = -1;
        EditorUtility.SetDirty(material);
        return material;
    }

    private static GameObject CreateRealtimeShadowBar(Transform parent, string name, Vector3 frameCenter, Quaternion frameRotation, Vector3 localOffset, Vector3 scale, Material material)
    {
        var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        obj.name = name;
        obj.transform.SetParent(parent);
        obj.transform.SetPositionAndRotation(frameCenter + frameRotation * localOffset, frameRotation);
        obj.transform.localScale = scale;

        var collider = obj.GetComponent<Collider>();
        if (collider != null)
        {
            Object.DestroyImmediate(collider);
        }

        var renderer = obj.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        renderer.receiveShadows = false;
        renderer.enabled = true;
        return obj;
    }

    private static Light CreateRealtimeProjectionLight(Transform parent, string name, Vector3 source, Vector3 target, float range)
    {
        var lightObject = new GameObject(name);
        lightObject.transform.SetParent(parent);
        lightObject.transform.position = source;
        lightObject.transform.rotation = Quaternion.LookRotation(target - source, Vector3.up);
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Spot;
        light.lightmapBakeType = LightmapBakeType.Realtime;
        light.color = new Color(1.0f, 0.90f, 0.76f);
        light.intensity = 8.2f;
        light.range = range;
        light.spotAngle = 50f;
        light.innerSpotAngle = 31f;
        light.shadows = LightShadows.Soft;
        light.shadowStrength = 0.48f;
        light.shadowBias = 0.012f;
        light.shadowNormalBias = 0.065f;
        light.renderMode = LightRenderMode.ForcePixel;
        light.cullingMask = ~0;
        return light;
    }

    private static HashSet<Material> GetSceneMaterials()
    {
        var materials = new HashSet<Material>();
        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
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

    [System.Serializable]
    private class BlenderPbrMap
    {
        public BlenderPbrMaterialEntry[] materials = new BlenderPbrMaterialEntry[0];
    }

    [System.Serializable]
    private class BlenderPbrMaterialEntry
    {
        public string name;
        public string cleanName;
        public BlenderPbrImageInfo baseColor;
        public BlenderPbrImageInfo roughness;
        public BlenderPbrImageInfo metallic;
        public BlenderPbrImageInfo normal;
        public BlenderPbrImageInfo ao;
        public float roughnessDefault = 0.5f;
        public float metallicDefault = 0f;
        public float alphaDefault = 1f;
    }

    [System.Serializable]
    private class BlenderPbrImageInfo
    {
        public string name;
        public string path;
        public string basename;
        public bool packed;
        public string unityAsset;
    }

    private static string CleanUnityMaterialName(string materialName)
    {
        var clean = materialName ?? string.Empty;
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"_\d{5}_lilToon$", string.Empty);
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"_\d{5}$", string.Empty);
        clean = clean.Replace("_TransparentGlass", string.Empty);
        clean = clean.Replace("_Visible", string.Empty);
        return clean;
    }

    private static string NormalizeMaterialKey(string value)
    {
        return NormalizeSearchText(value);
    }

    private static string ResolveBlenderTexturePath(BlenderPbrImageInfo imageInfo, string[] texturePaths)
    {
        if (!HasImageInfo(imageInfo))
        {
            return string.Empty;
        }

        if (!string.IsNullOrEmpty(imageInfo.unityAsset))
        {
            var direct = imageInfo.unityAsset.Replace("\\", "/");
            if (AssetDatabase.LoadAssetAtPath<Texture>(direct) != null)
            {
                return direct;
            }
        }

        var candidates = new[]
        {
            imageInfo.name,
            Path.GetFileNameWithoutExtension(imageInfo.basename),
            Path.GetFileNameWithoutExtension(imageInfo.path),
            imageInfo.basename
        }
        .Where(candidate => !string.IsNullOrEmpty(candidate))
        .Select(NormalizeSearchText)
        .Where(candidate => candidate.Length >= 3)
        .Distinct()
        .ToArray();

        foreach (var path in texturePaths)
        {
            var normalizedPath = NormalizeSearchText(Path.GetFileNameWithoutExtension(path));
            foreach (var candidate in candidates)
            {
                if (normalizedPath.Contains(candidate) || candidate.Contains(normalizedPath))
                {
                    return path;
                }
            }
        }

        return string.Empty;
    }

    private static string NormalizeSearchText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static void ApplyStandardDefaultsFromBlender(Material material, BlenderPbrMaterialEntry entry)
    {
        var roughness = Mathf.Clamp01(entry.roughnessDefault);
        var smoothness = Mathf.Clamp01(1f - roughness);
        SetMaterialFloat(material, "_Glossiness", smoothness);
        SetMaterialFloat(material, "_GlossMapScale", smoothness);

        if (HasImageInfo(entry.metallic) || LooksLikeMetalMaterial(material.name))
        {
            SetMaterialFloat(material, "_Metallic", Mathf.Clamp01(entry.metallicDefault));
        }
        else
        {
            SetMaterialFloat(material, "_Metallic", Mathf.Min(GetSourceFloat(material, "_Metallic", 0f), 0.08f));
        }

        if (material.HasProperty("_SpecularHighlights"))
        {
            material.SetFloat("_SpecularHighlights", 1f);
        }
        if (material.HasProperty("_GlossyReflections"))
        {
            material.SetFloat("_GlossyReflections", 1f);
        }
    }

    private static bool LooksLikeMetalMaterial(string materialName)
    {
        return ContainsIgnoreCase(materialName, "metal")
            || ContainsIgnoreCase(materialName, "金属")
            || ContainsIgnoreCase(materialName, "不锈钢")
            || ContainsIgnoreCase(materialName, "steel")
            || ContainsIgnoreCase(materialName, "iron")
            || ContainsIgnoreCase(materialName, "chrome");
    }

    private static bool HasImageInfo(BlenderPbrImageInfo imageInfo)
    {
        return imageInfo != null
            && (!string.IsNullOrEmpty(imageInfo.name)
                || !string.IsNullOrEmpty(imageInfo.path)
                || !string.IsNullOrEmpty(imageInfo.basename)
                || !string.IsNullOrEmpty(imageInfo.unityAsset));
    }

    private static string GenerateMetallicSmoothnessMap(Material material, BlenderPbrMaterialEntry entry, string metallicPath, string roughnessPath)
    {
        var metal = !string.IsNullOrEmpty(metallicPath) ? LoadReadableTexture(metallicPath, false) : null;
        var rough = !string.IsNullOrEmpty(roughnessPath) ? LoadReadableTexture(roughnessPath, false) : null;
        var width = Mathf.Max(16, Mathf.Max(metal != null ? metal.width : 0, rough != null ? rough.width : 0));
        var height = Mathf.Max(16, Mathf.Max(metal != null ? metal.height : 0, rough != null ? rough.height : 0));
        var generated = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        var metallicDefault = Mathf.Clamp01(entry.metallicDefault);
        var roughnessDefault = Mathf.Clamp01(entry.roughnessDefault);

        for (var y = 0; y < height; y++)
        {
            var v = height > 1 ? y / (float)(height - 1) : 0f;
            for (var x = 0; x < width; x++)
            {
                var u = width > 1 ? x / (float)(width - 1) : 0f;
                var metallic = metal != null ? GrayscaleLinear(metal.GetPixelBilinear(u, v)) : metallicDefault;
                var roughness = rough != null ? GrayscaleLinear(rough.GetPixelBilinear(u, v)) : roughnessDefault;
                var smoothness = Mathf.Clamp01(1f - roughness);
                generated.SetPixel(x, y, new Color(metallic, metallic, metallic, smoothness));
            }
        }

        generated.Apply(false, false);
        var path = "Assets/SceneExport/GeneratedPBR/" + SanitizeAssetName(CleanUnityMaterialName(material.name)) + "_MetallicSmoothness.png";
        File.WriteAllBytes(path, generated.EncodeToPNG());
        Object.DestroyImmediate(generated);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        return path;
    }

    private static Texture2D LoadReadableTexture(string path, bool sRgb)
    {
        ConfigureTextureImporter(path, TextureImporterType.Default, sRgb, true);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    private static void ConfigureTextureImporter(string path, TextureImporterType textureType, bool sRgb, bool readable)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            return;
        }

        var changed = false;
        if (importer.textureType != textureType)
        {
            importer.textureType = textureType;
            changed = true;
        }
        if (importer.sRGBTexture != sRgb && textureType != TextureImporterType.NormalMap)
        {
            importer.sRGBTexture = sRgb;
            changed = true;
        }
        if (importer.isReadable != readable)
        {
            importer.isReadable = readable;
            changed = true;
        }
        if (textureType == TextureImporterType.NormalMap && importer.convertToNormalmap)
        {
            importer.convertToNormalmap = false;
            changed = true;
        }
        if (changed)
        {
            importer.SaveAndReimport();
        }
    }

    private static float GrayscaleLinear(Color color)
    {
        return Mathf.Clamp01(color.r * 0.299f + color.g * 0.587f + color.b * 0.114f);
    }

    private static Bounds GetSceneRendererBounds()
    {
        var renderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude);
        var initialized = false;
        var bounds = new Bounds(Vector3.zero, Vector3.one);
        foreach (var renderer in renderers)
        {
            if (renderer.gameObject.name == "Blender Lighting" || renderer.gameObject.name == "Blender Realtime Lighting")
            {
                continue;
            }

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

        return initialized ? bounds : new Bounds(Vector3.zero, new Vector3(4f, 3f, 4f));
    }

    private static void DestroyIfExists(string name)
    {
        var obj = GameObject.Find(name);
        if (obj != null)
        {
            Object.DestroyImmediate(obj);
        }
    }

    private static bool IsGeneratedLighting(Transform transform)
    {
        while (transform != null)
        {
            var name = transform.name;
            if (name == "Blender Lighting" || name == "Blender Realtime Lighting" || name == "Window Sun Lighting" || name == "Window Shadow Accent")
            {
                return true;
            }
            transform = transform.parent;
        }

        return false;
    }

    private static Renderer FindWindowRenderer()
    {
        Renderer best = null;
        var bestScore = float.NegativeInfinity;
        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
        {
            var name = renderer.gameObject.name;
            if (!IsWindowPlaneName(name))
            {
                continue;
            }

            var size = renderer.bounds.size;
            var area = size.x * size.y + size.x * size.z + size.y * size.z;
            var score = area;
            if (renderer.sharedMaterial != null && renderer.sharedMaterial.mainTexture != null)
            {
                score += 1000f;
            }
            if (name == "平面")
            {
                score += 100f;
            }

            if (score > bestScore)
            {
                best = renderer;
                bestScore = score;
            }
        }

        return best;
    }

    private static Texture EnsureWindowSunCookie()
    {
        const string cookiePath = "Assets/SceneExport/Lighting/window_sun_cookie.png";
        Directory.CreateDirectory("Assets/SceneExport/Lighting");
        if (!File.Exists(cookiePath))
        {
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var nx = (x + 0.5f) / size * 2f - 1f;
                    var ny = (y + 0.5f) / size * 2f - 1f;
                    var falloff = Mathf.Clamp01(1f - Mathf.Pow(Mathf.Sqrt(nx * nx + ny * ny), 1.7f));
                    var frame = Mathf.Min(Mathf.Abs(nx), Mathf.Abs(ny)) < 0.055f ? 0.18f : 1f;
                    var edge = Mathf.Abs(nx) > 0.88f || Mathf.Abs(ny) > 0.88f ? 0.22f : 1f;
                    var value = Mathf.Clamp01(0.12f + falloff * frame * edge);
                    texture.SetPixel(x, y, new Color(value, value, value, value));
                }
            }

            File.WriteAllBytes(cookiePath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
        }

        AssetDatabase.ImportAsset(cookiePath, ImportAssetOptions.ForceSynchronousImport);
        var importer = AssetImporter.GetAtPath(cookiePath) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Cookie;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Texture>(cookiePath);
    }

    private static Texture EnsureWindowGridCookie()
    {
        const string cookiePath = "Assets/SceneExport/Lighting/window_grid_cookie.png";
        Directory.CreateDirectory("Assets/SceneExport/Lighting");
        const int size = 256;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var nx = (x + 0.5f) / size * 2f - 1f;
                var ny = (y + 0.5f) / size * 2f - 1f;
                var distance = Mathf.Sqrt(nx * nx * 0.75f + ny * ny);
                var falloff = Mathf.Clamp01(1f - Mathf.Pow(distance, 2.2f));
                var bar = Mathf.Max(
                    SoftBar(nx, 0.035f),
                    Mathf.Max(SoftBar(ny, 0.040f), Mathf.Max(SoftBar(Mathf.Abs(nx) - 0.52f, 0.025f), SoftBar(Mathf.Abs(ny) - 0.58f, 0.025f))));
                var border = Mathf.Max(SmoothEdge(Mathf.Abs(nx), 0.78f, 0.89f), SmoothEdge(Mathf.Abs(ny), 0.78f, 0.89f));
                var shadow = Mathf.Clamp01(Mathf.Max(bar, border) * 0.92f);
                var value = Mathf.Clamp01(0.05f + falloff * (1f - shadow));
                texture.SetPixel(x, y, new Color(value, value, value, value));
            }
        }

        File.WriteAllBytes(cookiePath, texture.EncodeToPNG());
        Object.DestroyImmediate(texture);
        AssetDatabase.ImportAsset(cookiePath, ImportAssetOptions.ForceSynchronousImport);
        var importer = AssetImporter.GetAtPath(cookiePath) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Cookie;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Texture>(cookiePath);
    }

    private static Material EnsureWindowShadowDecalMaterial()
    {
        const string shaderPath = "Assets/SceneExport/Shaders/WindowShadowDecal.shader";
        const string texturePath = "Assets/SceneExport/Lighting/window_shadow_decal.png";
        const string materialPath = "Assets/SceneExport/Materials/Window_Shadow_Decal.mat";
        Directory.CreateDirectory("Assets/SceneExport/Shaders");
        Directory.CreateDirectory("Assets/SceneExport/Lighting");
        Directory.CreateDirectory("Assets/SceneExport/Materials");
        EnsureWindowShadowDecalShader(shaderPath);
        EnsureWindowShadowDecalTexture(texturePath);
        AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);

        var shader = Shader.Find("SceneExport/WindowShadowDecal");
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, materialPath);
        }
        else
        {
            material.shader = shader;
        }

        material.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture>(texturePath));
        material.SetColor("_Color", new Color(0.20f, 0.24f, 0.38f, 0.46f));
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 20;
        EditorUtility.SetDirty(material);
        return material;
    }

    private static void EnsureWindowShadowDecalShader(string shaderPath)
    {
        var shaderText = string.Join("\n", new[]
        {
            "Shader \"SceneExport/WindowShadowDecal\"",
            "{",
            "    Properties",
            "    {",
            "        _MainTex (\"Texture\", 2D) = \"white\" {}",
            "        _Color (\"Color\", Color) = (0.2,0.24,0.38,0.46)",
            "    }",
            "    SubShader",
            "    {",
            "        Tags { \"Queue\"=\"Transparent+20\" \"RenderType\"=\"Transparent\" }",
            "        ZWrite Off Cull Off Blend SrcAlpha OneMinusSrcAlpha",
            "        Pass",
            "        {",
            "            CGPROGRAM",
            "            #pragma vertex vert",
            "            #pragma fragment frag",
            "            #include \"UnityCG.cginc\"",
            "            sampler2D _MainTex;",
            "            float4 _MainTex_ST;",
            "            fixed4 _Color;",
            "            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };",
            "            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };",
            "            v2f vert(appdata v)",
            "            {",
            "                v2f o;",
            "                o.vertex = UnityObjectToClipPos(v.vertex);",
            "                o.uv = TRANSFORM_TEX(v.uv, _MainTex);",
            "                return o;",
            "            }",
            "            fixed4 frag(v2f i) : SV_Target",
            "            {",
            "                fixed4 tex = tex2D(_MainTex, i.uv);",
            "                fixed4 col = _Color;",
            "                col.a *= tex.a;",
            "                return col;",
            "            }",
            "            ENDCG",
            "        }",
            "    }",
            "}"
        });
        File.WriteAllText(shaderPath, shaderText);
    }

    private static void EnsureWindowShadowDecalTexture(string texturePath)
    {
        const int size = 512;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var nx = (x + 0.5f) / size * 2f - 1f;
                var ny = (y + 0.5f) / size * 2f - 1f;
                var falloff = Mathf.Clamp01(1f - Mathf.Pow(Mathf.Sqrt(nx * nx * 0.65f + ny * ny), 2.5f));
                var bar = Mathf.Max(
                    SoftBar(nx, 0.030f),
                    Mathf.Max(SoftBar(ny, 0.036f), Mathf.Max(SoftBar(Mathf.Abs(nx) - 0.52f, 0.022f), SoftBar(Mathf.Abs(ny) - 0.58f, 0.022f))));
                var border = Mathf.Max(SmoothEdge(Mathf.Abs(nx), 0.78f, 0.91f), SmoothEdge(Mathf.Abs(ny), 0.78f, 0.91f));
                var alpha = Mathf.Clamp01(Mathf.Max(bar, border) * falloff);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        File.WriteAllBytes(texturePath, texture.EncodeToPNG());
        Object.DestroyImmediate(texture);
        AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
        var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }
    }

    private static float SoftBar(float value, float halfWidth)
    {
        return 1f - Mathf.SmoothStep(halfWidth, halfWidth * 2.2f, Mathf.Abs(value));
    }

    private static float SmoothEdge(float value, float start, float end)
    {
        return Mathf.SmoothStep(start, end, value);
    }

    private static bool IsWindowPlaneName(string name)
    {
        return name == "平面" || name == "平面.001" || name.Contains("Plane") || name.Contains("window");
    }

    private static string SanitizeAssetName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(invalidChars.Contains(character) ? '_' : character);
        }
        return builder.ToString();
    }

    private static bool IsTexturePath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".tga" || extension == ".tif" || extension == ".tiff" || extension == ".bmp" || extension == ".hdr" || extension == ".exr";
    }

    private static void ConfigureTextures()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Texture", new[] { "Assets/SceneExport" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                continue;
            }

            var lower = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (lower.Contains("normal") || lower.Contains("norm") || lower.Contains("bump") || lower.Contains("_n_"))
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.sRGBTexture = false;
            }
            else if (lower.Contains("rough") || lower.Contains("metal") || lower.Contains("mask") || lower.Contains("ao_") || lower.Contains("height"))
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;
            }
            else
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
            }

            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
        }
    }

    private static void ConfigureModelImporter()
    {
        var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer == null)
        {
            AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceSynchronousImport);
            importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        }

        if (importer == null)
        {
            return;
        }

        importer.importCameras = true;
        importer.importLights = true;
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
        importer.materialName = ModelImporterMaterialName.BasedOnMaterialName;
        importer.materialSearch = ModelImporterMaterialSearch.Everywhere;
        importer.SaveAndReimport();
    }

    private static void EnsureCamera()
    {
        if (Object.FindAnyObjectByType<Camera>() != null)
        {
            return;
        }

        var cameraObject = new GameObject("Main Camera");
        var camera = cameraObject.AddComponent<Camera>();
        cameraObject.tag = "MainCamera";
        cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 2.2f, -6f), Quaternion.Euler(15f, 0f, 0f));
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 1000f;
    }

    private static void EnsureLight()
    {
        if (Object.FindAnyObjectByType<Light>() != null)
        {
            return;
        }

        var lightObject = new GameObject("Directional Light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
    }
}

[InitializeOnLoad]
public static class SceneExportAutoActions
{
    private const string WindowShadowFlagPath = "Assets/SceneExport/run_window_shadow_accent.flag";

    static SceneExportAutoActions()
    {
        EditorApplication.delayCall += RunPendingActions;
    }

    private static void RunPendingActions()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || !File.Exists(WindowShadowFlagPath))
        {
            return;
        }

        File.Delete(WindowShadowFlagPath);
        try
        {
            SceneExportSetup.ApplyWindowShadowAccent();
        }
        catch (System.Exception exception)
        {
            File.WriteAllText("Assets/SceneExport/window_shadow_accent_error.txt", exception.ToString());
            AssetDatabase.ImportAsset("Assets/SceneExport/window_shadow_accent_error.txt");
            Debug.LogException(exception);
        }
    }
}
