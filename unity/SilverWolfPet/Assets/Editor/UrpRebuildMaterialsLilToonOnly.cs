using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class UrpRebuildMaterialsLilToonOnly
{
    private const string MaterialsFolder = "Assets/SceneExport/MaterialsLilToon";
    private const string ScenePath = "Assets/Scenes/BlenderIndoorScene.unity";
    private const string ReportPath = "Assets/SceneExport/MaterialsLilToon_full_folder_restore_report.txt";

    [MenuItem("Tools/URP Copy/Rebuild MaterialsLilToon Only")]
    public static void RebuildFromRestoredStandardMaterials()
    {
        var report = new StringBuilder();
        report.AppendLine("MaterialsLilToon full folder restore + URP shader remap");
        report.AppendLine("Folder: " + MaterialsFolder);
        report.AppendLine();

        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        if (litShader == null)
        {
            throw new InvalidOperationException("Universal Render Pipeline/Lit shader was not found.");
        }

        var scanned = 0;
        var changed = 0;
        var textured = 0;
        var nonWhiteBaseColor = 0;
        var whiteBaseColor = 0;
        var whiteColor = 0;

        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { MaterialsFolder }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                continue;
            }

            scanned++;
            var snapshot = MaterialSnapshot.Capture(material);

            material.shader = litShader;
            ApplyBaseSurface(material, snapshot);
            ApplyPbr(material, snapshot);
            ApplyRenderMode(material, snapshot);

            if (snapshot.BaseTexture != null)
            {
                textured++;
            }

            var color = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.white;
            if (IsWhite(color))
            {
                whiteBaseColor++;
            }
            else
            {
                nonWhiteBaseColor++;
            }

            if (material.HasProperty("_Color") && IsWhite(material.GetColor("_Color")))
            {
                whiteColor++;
            }

            EditorUtility.SetDirty(material);
            changed++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (File.Exists(ProjectFile(ScenePath)))
        {
            EditorSceneManager.OpenScene(ScenePath);
            ValidateSceneMaterials(report);
        }

        report.AppendLine("Material assets scanned: " + scanned);
        report.AppendLine("Material assets remapped to URP/Lit: " + changed);
        report.AppendLine("Materials with base texture: " + textured);
        report.AppendLine("Materials with non-white _BaseColor after remap: " + nonWhiteBaseColor);
        report.AppendLine("Materials with white _BaseColor after remap: " + whiteBaseColor);
        report.AppendLine("Materials with white _Color after source restore: " + whiteColor);
        report.AppendLine();
        report.AppendLine("Note: white _BaseColor now means the original _Color was also white, not a leftover default _BaseColor.");

        File.WriteAllText(ProjectFile(ReportPath), report.ToString(), Encoding.UTF8);
        AssetDatabase.ImportAsset(ReportPath);
        Debug.Log(report.ToString());
    }

    private static void ApplyBaseSurface(Material material, MaterialSnapshot snapshot)
    {
        SetTexture(material, "_BaseMap", snapshot.BaseTexture, snapshot.BaseScale, snapshot.BaseOffset);
        SetTexture(material, "_MainTex", snapshot.BaseTexture, snapshot.BaseScale, snapshot.BaseOffset);
        SetColor(material, "_BaseColor", snapshot.BaseColor);
        SetColor(material, "_Color", snapshot.BaseColor);
    }

    private static void ApplyPbr(Material material, MaterialSnapshot snapshot)
    {
        SetFloat(material, "_Metallic", Mathf.Clamp01(snapshot.Metallic));
        SetFloat(material, "_Smoothness", Mathf.Clamp01(snapshot.Smoothness));
        SetFloat(material, "_BumpScale", snapshot.BumpScale);
        SetFloat(material, "_OcclusionStrength", snapshot.OcclusionStrength);

        SetTexture(material, "_BumpMap", snapshot.NormalTexture, Vector2.one, Vector2.zero);
        SetTexture(material, "_MetallicGlossMap", snapshot.MetallicTexture, Vector2.one, Vector2.zero);
        SetTexture(material, "_OcclusionMap", snapshot.OcclusionTexture, Vector2.one, Vector2.zero);
        SetTexture(material, "_EmissionMap", snapshot.EmissionTexture, Vector2.one, Vector2.zero);

        SetColor(material, "_EmissionColor", snapshot.EmissionColor);
        if (snapshot.NormalTexture != null)
        {
            material.EnableKeyword("_NORMALMAP");
        }
        else
        {
            material.DisableKeyword("_NORMALMAP");
        }

        if (snapshot.MetallicTexture != null)
        {
            material.EnableKeyword("_METALLICSPECGLOSSMAP");
        }
        else
        {
            material.DisableKeyword("_METALLICSPECGLOSSMAP");
        }

        if (snapshot.EmissionTexture != null || snapshot.EmissionColor.maxColorComponent > 0.01f)
        {
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags &= ~MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }
        else
        {
            material.DisableKeyword("_EMISSION");
        }
    }

    private static void ApplyRenderMode(Material material, MaterialSnapshot snapshot)
    {
        var transparent = snapshot.BaseColor.a < 0.99f
            || ContainsAny(material.name, "transparent", "glass", "window");

        if (transparent)
        {
            SetFloat(material, "_Surface", 1f);
            SetFloat(material, "_Blend", 0f);
            SetFloat(material, "_AlphaClip", 0f);
            SetFloat(material, "_ZWrite", 0f);
            SetFloat(material, "_Cull", 0f);
            SetFloat(material, "_SrcBlend", (float)BlendMode.SrcAlpha);
            SetFloat(material, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.renderQueue = (int)RenderQueue.Transparent;
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
        }
        else
        {
            SetFloat(material, "_Surface", 0f);
            SetFloat(material, "_Blend", 0f);
            SetFloat(material, "_AlphaClip", 0f);
            SetFloat(material, "_ZWrite", 1f);
            SetFloat(material, "_Cull", 2f);
            SetFloat(material, "_SrcBlend", (float)BlendMode.One);
            SetFloat(material, "_DstBlend", (float)BlendMode.Zero);
            material.renderQueue = (int)RenderQueue.Geometry;
            material.SetOverrideTag("RenderType", "Opaque");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
        }
    }

    private static void ValidateSceneMaterials(StringBuilder report)
    {
        var slots = 0;
        var slotsInFolder = 0;
        var folderSlotsWhite = 0;
        var folderSlotsTextured = 0;

        foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (renderer == null || !renderer.gameObject.scene.IsValid())
            {
                continue;
            }

            foreach (var material in renderer.sharedMaterials)
            {
                slots++;
                if (material == null)
                {
                    continue;
                }

                var path = AssetDatabase.GetAssetPath(material);
                if (!path.StartsWith(MaterialsFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                slotsInFolder++;
                if (GetTexture(material, "_BaseMap", "_MainTex") != null)
                {
                    folderSlotsTextured++;
                }

                if (material.HasProperty("_BaseColor") && IsWhite(material.GetColor("_BaseColor")) && GetTexture(material, "_BaseMap", "_MainTex") == null)
                {
                    folderSlotsWhite++;
                }
            }
        }

        report.AppendLine("Scene renderer material slots checked: " + slots);
        report.AppendLine("Scene slots using MaterialsLilToon: " + slotsInFolder);
        report.AppendLine("MaterialsLilToon scene slots with texture: " + folderSlotsTextured);
        report.AppendLine("MaterialsLilToon scene slots white with no texture: " + folderSlotsWhite);
        report.AppendLine();
    }

    private sealed class MaterialSnapshot
    {
        public Texture BaseTexture;
        public Vector2 BaseScale = Vector2.one;
        public Vector2 BaseOffset = Vector2.zero;
        public Color BaseColor = Color.white;
        public Texture NormalTexture;
        public Texture MetallicTexture;
        public Texture OcclusionTexture;
        public Texture EmissionTexture;
        public Color EmissionColor = Color.black;
        public float Metallic;
        public float Smoothness = 0.5f;
        public float BumpScale = 1f;
        public float OcclusionStrength = 1f;

        public static MaterialSnapshot Capture(Material material)
        {
            var snapshot = new MaterialSnapshot();
            snapshot.BaseTexture = GetTexture(material, "_MainTex", "_BaseMap");
            if (snapshot.BaseTexture != null)
            {
                var property = material.HasProperty("_MainTex") && material.GetTexture("_MainTex") != null ? "_MainTex" : "_BaseMap";
                snapshot.BaseScale = material.GetTextureScale(property);
                snapshot.BaseOffset = material.GetTextureOffset(property);
            }

            snapshot.BaseColor = ChooseColor(material);
            snapshot.NormalTexture = GetTexture(material, "_BumpMap", "_NormalMap");
            snapshot.MetallicTexture = GetTexture(material, "_MetallicGlossMap", "_MetallicMap");
            snapshot.OcclusionTexture = GetTexture(material, "_OcclusionMap");
            snapshot.EmissionTexture = GetTexture(material, "_EmissionMap");
            snapshot.EmissionColor = GetColor(material, "_EmissionColor", Color.black);
            snapshot.Metallic = GetFloat(material, "_Metallic", 0f);
            snapshot.Smoothness = GetFloat(material, "_Glossiness", GetFloat(material, "_Smoothness", 0.5f));
            snapshot.BumpScale = GetFloat(material, "_BumpScale", 1f);
            snapshot.OcclusionStrength = GetFloat(material, "_OcclusionStrength", 1f);
            return snapshot;
        }

        private static Color ChooseColor(Material material)
        {
            var color = GetColor(material, "_Color", Color.white);
            if (!IsWhite(color))
            {
                return color;
            }

            var baseColor = GetColor(material, "_BaseColor", color);
            return IsWhite(color) && !IsWhite(baseColor) ? baseColor : color;
        }
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

    private static Color GetColor(Material material, string name, Color fallback)
    {
        return material.HasProperty(name) ? material.GetColor(name) : fallback;
    }

    private static float GetFloat(Material material, string name, float fallback)
    {
        return material.HasProperty(name) ? material.GetFloat(name) : fallback;
    }

    private static void SetTexture(Material material, string name, Texture texture, Vector2 scale, Vector2 offset)
    {
        if (!material.HasProperty(name))
        {
            return;
        }

        material.SetTexture(name, texture);
        material.SetTextureScale(name, scale);
        material.SetTextureOffset(name, offset);
    }

    private static void SetColor(Material material, string name, Color color)
    {
        if (material.HasProperty(name))
        {
            material.SetColor(name, color);
        }
    }

    private static void SetFloat(Material material, string name, float value)
    {
        if (material.HasProperty(name))
        {
            material.SetFloat(name, value);
        }
    }

    private static bool IsWhite(Color color)
    {
        return color.r > 0.96f && color.g > 0.96f && color.b > 0.96f && color.a > 0.96f;
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string ProjectFile(string assetPath)
    {
        return Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath.Replace('/', Path.DirectorySeparatorChar));
    }
}
