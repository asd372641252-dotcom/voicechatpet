using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class UrpRestoreLilToonFromSourceYaml
{
    private const string CurrentMaterialsFolder = "Assets/SceneExport/MaterialsLilToon";
    private const string ScenePath = "Assets/Scenes/BlenderIndoorScene.unity";
    private const string SourceAssetsRoot = @"D:\pet\场景工程\Assets";
    private const string SourceMaterialsFolder = @"D:\pet\场景工程\Assets\SceneExport\MaterialsLilToon";
    private const string ReportPath = "Assets/SceneExport/MaterialsLilToon_source_yaml_restore_report.txt";

    private static readonly Dictionary<string, SourceMaterial> SourceCache = new Dictionary<string, SourceMaterial>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> SourceGuidToMaterial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    [MenuItem("Tools/URP Copy/Restore MaterialsLilToon From Source YAML")]
    public static void RestoreFromSourceYaml()
    {
        var report = new StringBuilder();
        report.AppendLine("MaterialsLilToon source YAML restore");
        report.AppendLine("Current folder: " + CurrentMaterialsFolder);
        report.AppendLine("Source folder: " + SourceMaterialsFolder);
        report.AppendLine();

        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        if (litShader == null)
        {
            throw new InvalidOperationException("Universal Render Pipeline/Lit shader was not found.");
        }

        BuildSourceGuidIndex();

        var scanned = 0;
        var restoredColors = 0;
        var restoredBaseMaps = 0;
        var restoredNormals = 0;
        var restoredMetallicMaps = 0;
        var restoredOcclusionMaps = 0;
        var sourceNonWhiteButCurrentWhite = 0;
        var colorMismatchAfterWrite = 0;

        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { CurrentMaterialsFolder }))
        {
            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material == null)
            {
                continue;
            }

            scanned++;
            var sourcePath = Path.Combine(SourceMaterialsFolder, Path.GetFileName(assetPath));
            if (!File.Exists(sourcePath))
            {
                report.AppendLine("Missing source material: " + sourcePath);
                continue;
            }

            BreakMaterialParent(material);
            material.shader = litShader;

            var color = ResolveColor(sourcePath, "_Color", Color.white);
            SetColor(material, "_Color", color);
            SetColor(material, "_BaseColor", color);
            restoredColors++;

            if (ApplyTexture(material, sourcePath, "_MainTex", "_MainTex", "_BaseMap"))
            {
                restoredBaseMaps++;
            }

            if (ApplyTexture(material, sourcePath, "_BumpMap", "_BumpMap"))
            {
                restoredNormals++;
                material.EnableKeyword("_NORMALMAP");
            }
            else
            {
                material.DisableKeyword("_NORMALMAP");
            }

            if (ApplyTexture(material, sourcePath, "_MetallicGlossMap", "_MetallicGlossMap"))
            {
                restoredMetallicMaps++;
                material.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
            else
            {
                material.DisableKeyword("_METALLICSPECGLOSSMAP");
            }

            if (ApplyTexture(material, sourcePath, "_OcclusionMap", "_OcclusionMap"))
            {
                restoredOcclusionMaps++;
            }

            ApplyTexture(material, sourcePath, "_EmissionMap", "_EmissionMap");
            var emissionColor = ResolveColor(sourcePath, "_EmissionColor", Color.black);
            SetColor(material, "_EmissionColor", emissionColor);
            if (GetTexture(material, "_EmissionMap") != null || emissionColor.maxColorComponent > 0.01f)
            {
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags &= ~MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }
            else
            {
                material.DisableKeyword("_EMISSION");
            }

            SetFloat(material, "_Metallic", ResolveFloat(sourcePath, "_Metallic", 0f));
            SetFloat(material, "_Smoothness", ResolveFloat(sourcePath, "_Smoothness", ResolveFloat(sourcePath, "_Glossiness", 0.5f)));
            SetFloat(material, "_Glossiness", ResolveFloat(sourcePath, "_Glossiness", ResolveFloat(sourcePath, "_Smoothness", 0.5f)));
            SetFloat(material, "_BumpScale", ResolveFloat(sourcePath, "_BumpScale", 1f));
            SetFloat(material, "_OcclusionStrength", ResolveFloat(sourcePath, "_OcclusionStrength", 1f));
            SetFloat(material, "_WorkflowMode", 1f);
            SetFloat(material, "_EnvironmentReflections", 1f);
            SetFloat(material, "_SpecularHighlights", 1f);
            SetFloat(material, "_ReceiveShadows", 1f);

            ApplyRenderMode(material, color);

            var currentColor = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.white;
            if (!IsWhite(color) && IsWhite(currentColor))
            {
                sourceNonWhiteButCurrentWhite++;
            }

            if (!Approximately(color, currentColor))
            {
                colorMismatchAfterWrite++;
            }

            EditorUtility.SetDirty(material);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (File.Exists(ProjectFile(ScenePath)))
        {
            EditorSceneManager.OpenScene(ScenePath);
        }

        report.AppendLine("Material assets scanned: " + scanned);
        report.AppendLine("Colors restored from source YAML: " + restoredColors);
        report.AppendLine("Base maps restored from source YAML: " + restoredBaseMaps);
        report.AppendLine("Normal maps restored from source YAML: " + restoredNormals);
        report.AppendLine("Metallic maps restored from source YAML: " + restoredMetallicMaps);
        report.AppendLine("Occlusion maps restored from source YAML: " + restoredOcclusionMaps);
        report.AppendLine("Source non-white but current still white: " + sourceNonWhiteButCurrentWhite);
        report.AppendLine("BaseColor mismatches after write: " + colorMismatchAfterWrite);

        File.WriteAllText(ProjectFile(ReportPath), report.ToString(), Encoding.UTF8);
        AssetDatabase.ImportAsset(ReportPath);
        Debug.Log(report.ToString());
    }

    private static bool ApplyTexture(Material material, string sourcePath, string sourceProperty, params string[] targetProperties)
    {
        var binding = ResolveTexture(sourcePath, sourceProperty);
        var texture = LoadTexture(binding);
        foreach (var property in targetProperties)
        {
            if (!material.HasProperty(property))
            {
                continue;
            }

            material.SetTexture(property, texture);
            material.SetTextureScale(property, binding.Scale);
            material.SetTextureOffset(property, binding.Offset);
        }

        return texture != null;
    }

    private static void ApplyRenderMode(Material material, Color baseColor)
    {
        var transparent = baseColor.a < 0.99f || ContainsAny(material.name, "transparent", "glass", "window", "玻璃");
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

    private static void BreakMaterialParent(Material material)
    {
        var serializedObject = new SerializedObject(material);
        var parent = serializedObject.FindProperty("m_Parent");
        if (parent != null)
        {
            parent.objectReferenceValue = null;
        }

        var modified = serializedObject.FindProperty("m_ModifiedSerializedProperties");
        if (modified != null)
        {
            if (modified.propertyType == SerializedPropertyType.Integer)
            {
                modified.intValue = 1;
            }
            else if (modified.propertyType == SerializedPropertyType.Boolean)
            {
                modified.boolValue = true;
            }
        }

        serializedObject.ApplyModifiedPropertiesWithoutUndo();
    }

    private static SourceMaterial LoadSource(string sourcePath)
    {
        SourceMaterial cached;
        if (SourceCache.TryGetValue(sourcePath, out cached))
        {
            return cached;
        }

        var material = SourceMaterial.Parse(sourcePath, File.ReadAllText(sourcePath, Encoding.UTF8));
        SourceCache[sourcePath] = material;
        return material;
    }

    private static Color ResolveColor(string sourcePath, string property, Color fallback)
    {
        var resolved = ResolveColor(sourcePath, property, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return resolved.HasValue ? resolved.Value : fallback;
    }

    private static Color? ResolveColor(string sourcePath, string property, HashSet<string> visited)
    {
        if (!visited.Add(sourcePath))
        {
            return null;
        }

        var material = LoadSource(sourcePath);
        Color color;
        if (material.Colors.TryGetValue(property, out color))
        {
            return color;
        }

        var parentPath = ResolveParentPath(material);
        return parentPath == null ? (Color?)null : ResolveColor(parentPath, property, visited);
    }

    private static TextureBinding ResolveTexture(string sourcePath, string property)
    {
        var resolved = ResolveTexture(sourcePath, property, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return resolved ?? TextureBinding.Empty;
    }

    private static TextureBinding ResolveTexture(string sourcePath, string property, HashSet<string> visited)
    {
        if (!visited.Add(sourcePath))
        {
            return null;
        }

        var material = LoadSource(sourcePath);
        TextureBinding binding;
        if (material.Textures.TryGetValue(property, out binding) && binding.HasReference)
        {
            return binding;
        }

        var parentPath = ResolveParentPath(material);
        return parentPath == null ? null : ResolveTexture(parentPath, property, visited);
    }

    private static float ResolveFloat(string sourcePath, string property, float fallback)
    {
        var resolved = ResolveFloat(sourcePath, property, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return resolved.HasValue ? resolved.Value : fallback;
    }

    private static float? ResolveFloat(string sourcePath, string property, HashSet<string> visited)
    {
        if (!visited.Add(sourcePath))
        {
            return null;
        }

        var material = LoadSource(sourcePath);
        float value;
        if (material.Floats.TryGetValue(property, out value))
        {
            return value;
        }

        var parentPath = ResolveParentPath(material);
        return parentPath == null ? (float?)null : ResolveFloat(parentPath, property, visited);
    }

    private static string ResolveParentPath(SourceMaterial material)
    {
        if (string.IsNullOrEmpty(material.ParentGuid))
        {
            return null;
        }

        string parentPath;
        return SourceGuidToMaterial.TryGetValue(material.ParentGuid, out parentPath) ? parentPath : null;
    }

    private static Texture LoadTexture(TextureBinding binding)
    {
        if (!binding.HasReference)
        {
            return null;
        }

        var assetPath = AssetDatabase.GUIDToAssetPath(binding.Guid);
        return string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
    }

    private static void BuildSourceGuidIndex()
    {
        SourceGuidToMaterial.Clear();
        foreach (var metaPath in Directory.GetFiles(SourceAssetsRoot, "*.mat.meta", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(metaPath, Encoding.UTF8);
            var match = Regex.Match(text, @"(?m)^guid:\s*([0-9a-fA-F]{32})\s*$");
            if (!match.Success)
            {
                continue;
            }

            var materialPath = metaPath.Substring(0, metaPath.Length - ".meta".Length);
            SourceGuidToMaterial[match.Groups[1].Value] = materialPath;
        }
    }

    private static Texture GetTexture(Material material, string property)
    {
        return material.HasProperty(property) ? material.GetTexture(property) : null;
    }

    private static void SetColor(Material material, string property, Color color)
    {
        if (material.HasProperty(property))
        {
            material.SetColor(property, color);
        }
    }

    private static void SetFloat(Material material, string property, float value)
    {
        if (material.HasProperty(property))
        {
            material.SetFloat(property, value);
        }
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

    private static bool IsWhite(Color color)
    {
        return color.r > 0.96f && color.g > 0.96f && color.b > 0.96f && color.a > 0.96f;
    }

    private static bool Approximately(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.002f
            && Mathf.Abs(a.g - b.g) < 0.002f
            && Mathf.Abs(a.b - b.b) < 0.002f
            && Mathf.Abs(a.a - b.a) < 0.002f;
    }

    private static float ParseFloat(string text)
    {
        return float.Parse(text, CultureInfo.InvariantCulture);
    }

    private static string ProjectFile(string assetPath)
    {
        return Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed class SourceMaterial
    {
        public readonly Dictionary<string, TextureBinding> Textures = new Dictionary<string, TextureBinding>(StringComparer.Ordinal);
        public readonly Dictionary<string, float> Floats = new Dictionary<string, float>(StringComparer.Ordinal);
        public readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        public string ParentGuid;

        public static SourceMaterial Parse(string path, string text)
        {
            var material = new SourceMaterial();

            var parentMatch = Regex.Match(text, @"m_Parent:\s*\{fileID:\s*2100000,\s*guid:\s*([0-9a-fA-F]{32}),\s*type:\s*2\}");
            if (parentMatch.Success)
            {
                material.ParentGuid = parentMatch.Groups[1].Value;
            }

            foreach (Match match in Regex.Matches(text, @"(?ms)-\s+([A-Za-z0-9_]+):\s*\r?\n\s*m_Texture:\s*\{fileID:\s*([^,}\s]+)(?:,\s*guid:\s*([0-9a-fA-F]{32}),\s*type:\s*\d+)?\}\s*\r?\n\s*m_Scale:\s*\{x:\s*([^,}]+),\s*y:\s*([^,}]+)\}\s*\r?\n\s*m_Offset:\s*\{x:\s*([^,}]+),\s*y:\s*([^,}]+)\}"))
            {
                material.Textures[match.Groups[1].Value] = new TextureBinding(
                    match.Groups[3].Success ? match.Groups[3].Value : string.Empty,
                    new Vector2(ParseFloat(match.Groups[4].Value), ParseFloat(match.Groups[5].Value)),
                    new Vector2(ParseFloat(match.Groups[6].Value), ParseFloat(match.Groups[7].Value)));
            }

            foreach (Match match in Regex.Matches(text, @"(?m)^\s*-\s+([A-Za-z0-9_]+):\s*([-+0-9.eE]+)\s*$"))
            {
                material.Floats[match.Groups[1].Value] = ParseFloat(match.Groups[2].Value);
            }

            foreach (Match match in Regex.Matches(text, @"(?m)^\s*-\s+([A-Za-z0-9_]+):\s*\{r:\s*([-+0-9.eE]+),\s*g:\s*([-+0-9.eE]+),\s*b:\s*([-+0-9.eE]+),\s*a:\s*([-+0-9.eE]+)\}\s*$"))
            {
                material.Colors[match.Groups[1].Value] = new Color(
                    ParseFloat(match.Groups[2].Value),
                    ParseFloat(match.Groups[3].Value),
                    ParseFloat(match.Groups[4].Value),
                    ParseFloat(match.Groups[5].Value));
            }

            return material;
        }
    }

    private sealed class TextureBinding
    {
        public static readonly TextureBinding Empty = new TextureBinding(string.Empty, Vector2.one, Vector2.zero);

        public readonly string Guid;
        public readonly Vector2 Scale;
        public readonly Vector2 Offset;

        public TextureBinding(string guid, Vector2 scale, Vector2 offset)
        {
            Guid = guid;
            Scale = scale;
            Offset = offset;
        }

        public bool HasReference
        {
            get { return !string.IsNullOrEmpty(Guid) && Guid != "00000000000000000000000000000000"; }
        }
    }
}
