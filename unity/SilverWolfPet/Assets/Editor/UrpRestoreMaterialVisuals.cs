using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class UrpRestoreMaterialVisuals
{
    private const string SceneExportFolder = "Assets/SceneExport";
    private const string ReportPath = "Assets/SceneExport/URP_material_visual_restore_report.txt";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly Regex ParentRegex = new Regex(
        @"m_Parent:\s*\{fileID:\s*-?\d+,\s*guid:\s*(?<guid>[0-9a-fA-F]+),\s*type:\s*\d+\}",
        RegexOptions.Compiled);

    private static readonly Regex TextureRegex = new Regex(
        @"-\s*(?<name>_[^:\r\n]+):\s*\r?\n\s*m_Texture:\s*\{fileID:\s*-?\d+,\s*guid:\s*(?<guid>[0-9a-fA-F]*),\s*type:\s*\d+\}\s*\r?\n\s*m_Scale:\s*\{x:\s*(?<sx>[-+0-9.eE]+),\s*y:\s*(?<sy>[-+0-9.eE]+)\}\s*\r?\n\s*m_Offset:\s*\{x:\s*(?<ox>[-+0-9.eE]+),\s*y:\s*(?<oy>[-+0-9.eE]+)\}",
        RegexOptions.Compiled);

    private static readonly Regex ColorRegex = new Regex(
        @"-\s*(?<name>_[^:\r\n]+):\s*\{r:\s*(?<r>[-+0-9.eE]+),\s*g:\s*(?<g>[-+0-9.eE]+),\s*b:\s*(?<b>[-+0-9.eE]+),\s*a:\s*(?<a>[-+0-9.eE]+)\}",
        RegexOptions.Compiled);

    private static readonly Regex FloatRegex = new Regex(
        @"-\s*(?<name>_[^:\r\n]+):\s*(?<value>[-+0-9.eE]+)\s*(?:\r?\n|$)",
        RegexOptions.Compiled);

    [MenuItem("Tools/URP Copy/Restore Material Visuals From Original")]
    public static void RestoreFromOriginalProjectAndValidate()
    {
        var copyRoot = Directory.GetParent(Application.dataPath).FullName;
        var originalRoot = ResolveOriginalProjectRoot(copyRoot);
        var stats = new RestoreStats();
        var snapshotCache = new Dictionary<string, MaterialSnapshot>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(originalRoot))
        {
            WriteReport(copyRoot, "Original project was not found: " + originalRoot);
            throw new DirectoryNotFoundException(originalRoot);
        }

        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { SceneExportFolder }))
        {
            var materialPath = AssetDatabase.GUIDToAssetPath(guid);
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                continue;
            }

            stats.MaterialsScanned++;
            if (!File.Exists(ProjectFile(originalRoot, materialPath)))
            {
                stats.MissingOriginal++;
                continue;
            }

            stats.MaterialsWithOriginal++;
            var snapshot = LoadSnapshot(materialPath, originalRoot, snapshotCache, new HashSet<string>(StringComparer.OrdinalIgnoreCase), stats);
            var beforeWhite = IsMaterialWhiteWithoutBaseTexture(material);
            if (ApplySnapshotToMaterial(material, snapshot, stats))
            {
                stats.ChangedMaterials++;
                EditorUtility.SetDirty(material);
            }

            if (beforeWhite && !IsMaterialWhiteWithoutBaseTexture(material))
            {
                stats.WhiteMaterialsRecovered++;
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        OpenBestSceneForValidation(stats);
        ValidateSceneRenderers(stats);

        var report = BuildReport(copyRoot, originalRoot, stats);
        WriteReport(copyRoot, report);
        AssetDatabase.ImportAsset(ReportPath);
        Debug.Log(report);
    }

    private static bool ApplySnapshotToMaterial(Material material, MaterialSnapshot snapshot, RestoreStats stats)
    {
        var changed = false;

        changed |= RestoreBaseColor(material, snapshot, stats);
        changed |= RestoreTexture(material, snapshot, stats, "base map", new[] { "_BaseMap", "_MainTex" }, "_BaseMap", "_MainTex");
        changed |= RestoreTexture(material, snapshot, stats, "normal map", new[] { "_BumpMap", "_NormalMap" }, "_BumpMap", "_NormalMap");
        changed |= RestoreTexture(material, snapshot, stats, "metallic map", new[] { "_MetallicGlossMap", "_MetallicMap" }, "_MetallicGlossMap");
        changed |= RestoreTexture(material, snapshot, stats, "occlusion map", new[] { "_OcclusionMap" }, "_OcclusionMap");
        changed |= RestoreTexture(material, snapshot, stats, "emission map", new[] { "_EmissionMap" }, "_EmissionMap");

        changed |= RestoreFloat(material, snapshot, "_Metallic", "_Metallic", stats);
        changed |= RestoreSmoothness(material, snapshot, stats);
        changed |= RestoreFloat(material, snapshot, "_BumpScale", "_BumpScale", stats);
        changed |= RestoreFloat(material, snapshot, "_OcclusionStrength", "_OcclusionStrength", stats);

        if (HasTexture(material, "_BumpMap", "_NormalMap"))
        {
            material.EnableKeyword("_NORMALMAP");
        }
        if (HasTexture(material, "_MetallicGlossMap"))
        {
            material.EnableKeyword("_METALLICSPECGLOSSMAP");
        }
        if (HasTexture(material, "_EmissionMap"))
        {
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags &= ~MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }

        return changed;
    }

    private static bool RestoreBaseColor(Material material, MaterialSnapshot snapshot, RestoreStats stats)
    {
        if (!TryChooseBaseColor(snapshot, out var color))
        {
            return false;
        }

        var changed = false;
        changed |= SetColor(material, "_BaseColor", color);
        changed |= SetColor(material, "_Color", color);
        if (changed)
        {
            stats.ColorsRestored++;
        }
        return changed;
    }

    private static bool RestoreTexture(Material material, MaterialSnapshot snapshot, RestoreStats stats, string label, string[] sourceNames, params string[] targetNames)
    {
        if (!TryGetTextureSlot(snapshot, out var slot, sourceNames))
        {
            return false;
        }

        var texturePath = AssetDatabase.GUIDToAssetPath(slot.Guid);
        var texture = string.IsNullOrEmpty(texturePath) ? null : AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
        if (texture == null)
        {
            stats.UnresolvedTextureGuids++;
            return false;
        }

        var changed = false;
        foreach (var targetName in targetNames)
        {
            if (!material.HasProperty(targetName))
            {
                continue;
            }

            if (material.GetTexture(targetName) != texture)
            {
                material.SetTexture(targetName, texture);
                changed = true;
            }

            if (material.GetTextureScale(targetName) != slot.Scale)
            {
                material.SetTextureScale(targetName, slot.Scale);
                changed = true;
            }

            if (material.GetTextureOffset(targetName) != slot.Offset)
            {
                material.SetTextureOffset(targetName, slot.Offset);
                changed = true;
            }
        }

        if (!changed)
        {
            return false;
        }

        if (label == "base map")
        {
            stats.BaseMapsRestored++;
        }
        else if (label == "normal map")
        {
            stats.NormalMapsRestored++;
            EnsureNormalTexture(texturePath);
        }
        else if (label == "metallic map")
        {
            stats.MetallicMapsRestored++;
        }
        else if (label == "occlusion map")
        {
            stats.OcclusionMapsRestored++;
        }
        else if (label == "emission map")
        {
            stats.EmissionMapsRestored++;
        }

        return true;
    }

    private static bool RestoreFloat(Material material, MaterialSnapshot snapshot, string sourceName, string targetName, RestoreStats stats)
    {
        if (!material.HasProperty(targetName) || !snapshot.Floats.TryGetValue(sourceName, out var value))
        {
            return false;
        }

        value = targetName == "_Metallic" || targetName == "_OcclusionStrength" ? Mathf.Clamp01(value) : value;
        if (Mathf.Abs(material.GetFloat(targetName) - value) < 0.0005f)
        {
            return false;
        }

        material.SetFloat(targetName, value);
        stats.FloatsRestored++;
        return true;
    }

    private static bool RestoreSmoothness(Material material, MaterialSnapshot snapshot, RestoreStats stats)
    {
        if (!material.HasProperty("_Smoothness"))
        {
            return false;
        }

        var hasValue = snapshot.Floats.TryGetValue("_Smoothness", out var smoothness);
        if (!hasValue)
        {
            hasValue = snapshot.Floats.TryGetValue("_Glossiness", out smoothness);
        }
        if (!hasValue && snapshot.Floats.TryGetValue("_Roughness", out var roughness))
        {
            smoothness = 1f - roughness;
            hasValue = true;
        }
        if (!hasValue)
        {
            return false;
        }

        smoothness = Mathf.Clamp01(smoothness);
        if (Mathf.Abs(material.GetFloat("_Smoothness") - smoothness) < 0.0005f)
        {
            return false;
        }

        material.SetFloat("_Smoothness", smoothness);
        stats.FloatsRestored++;
        return true;
    }

    private static MaterialSnapshot LoadSnapshot(string assetPath, string originalRoot, Dictionary<string, MaterialSnapshot> cache, HashSet<string> stack, RestoreStats stats)
    {
        if (cache.TryGetValue(assetPath, out var cached))
        {
            return cached;
        }

        if (!stack.Add(assetPath))
        {
            return new MaterialSnapshot();
        }

        var filePath = ProjectFile(originalRoot, assetPath);
        if (!File.Exists(filePath))
        {
            return new MaterialSnapshot();
        }

        var localSnapshot = MaterialSnapshot.Parse(File.ReadAllText(filePath));
        var merged = new MaterialSnapshot();
        if (!string.IsNullOrEmpty(localSnapshot.ParentGuid))
        {
            var parentAssetPath = AssetDatabase.GUIDToAssetPath(localSnapshot.ParentGuid);
            if (!string.IsNullOrEmpty(parentAssetPath) && File.Exists(ProjectFile(originalRoot, parentAssetPath)))
            {
                merged.Overlay(LoadSnapshot(parentAssetPath, originalRoot, cache, stack, stats));
                stats.ParentLinksFollowed++;
            }
            else
            {
                stats.UnresolvedParentGuids++;
            }
        }

        merged.Overlay(localSnapshot);
        cache[assetPath] = merged;
        stack.Remove(assetPath);
        return merged;
    }

    private static void ValidateSceneRenderers(RestoreStats stats)
    {
        foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>())
        {
            if (renderer == null || renderer.gameObject == null || !renderer.gameObject.scene.IsValid())
            {
                continue;
            }

            foreach (var material in renderer.sharedMaterials)
            {
                stats.RendererMaterialSlotsChecked++;
                if (material == null)
                {
                    stats.NullRendererMaterialSlots++;
                    continue;
                }

                if (IsMaterialWhiteWithoutBaseTexture(material))
                {
                    stats.WhiteRendererMaterialSlots++;
                    stats.AddWhiteExample(material.name);
                }
            }
        }
    }

    private static void OpenBestSceneForValidation(RestoreStats stats)
    {
        var sceneGuids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });
        if (sceneGuids.Length == 0)
        {
            return;
        }

        var selectedPath = AssetDatabase.GUIDToAssetPath(sceneGuids[0]);
        foreach (var guid in sceneGuids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.IndexOf("BlenderIndoorScene", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                selectedPath = path;
                break;
            }
        }

        EditorSceneManager.OpenScene(selectedPath);
        stats.ValidationScenePath = selectedPath;
    }

    private static bool TryChooseBaseColor(MaterialSnapshot snapshot, out Color color)
    {
        if (snapshot.Colors.TryGetValue("_Color", out color) && !IsDefaultWhite(color))
        {
            return true;
        }
        if (snapshot.Colors.TryGetValue("_BaseColor", out color) && !IsDefaultWhite(color))
        {
            return true;
        }
        if (snapshot.Colors.TryGetValue("_Color", out color))
        {
            return true;
        }
        if (snapshot.Colors.TryGetValue("_BaseColor", out color))
        {
            return true;
        }

        color = Color.white;
        return false;
    }

    private static bool TryGetTextureSlot(MaterialSnapshot snapshot, out TextureSlot slot, params string[] names)
    {
        foreach (var name in names)
        {
            if (snapshot.Textures.TryGetValue(name, out slot))
            {
                return true;
            }
        }

        slot = null;
        return false;
    }

    private static bool SetColor(Material material, string propertyName, Color color)
    {
        if (!material.HasProperty(propertyName))
        {
            return false;
        }

        if (SameColor(material.GetColor(propertyName), color))
        {
            return false;
        }

        material.SetColor(propertyName, color);
        return true;
    }

    private static bool HasTexture(Material material, params string[] names)
    {
        foreach (var name in names)
        {
            if (material.HasProperty(name) && material.GetTexture(name) != null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMaterialWhiteWithoutBaseTexture(Material material)
    {
        if (HasTexture(material, "_BaseMap", "_MainTex"))
        {
            return false;
        }

        var color = Color.white;
        if (material.HasProperty("_BaseColor"))
        {
            color = material.GetColor("_BaseColor");
        }
        else if (material.HasProperty("_Color"))
        {
            color = material.GetColor("_Color");
        }

        return IsDefaultWhite(color);
    }

    private static bool IsDefaultWhite(Color color)
    {
        return color.r > 0.96f && color.g > 0.96f && color.b > 0.96f && color.a > 0.96f;
    }

    private static bool SameColor(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.0005f
            && Mathf.Abs(a.g - b.g) < 0.0005f
            && Mathf.Abs(a.b - b.b) < 0.0005f
            && Mathf.Abs(a.a - b.a) < 0.0005f;
    }

    private static void EnsureNormalTexture(string assetPath)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null || importer.textureType == TextureImporterType.NormalMap)
        {
            return;
        }

        importer.textureType = TextureImporterType.NormalMap;
        importer.SaveAndReimport();
    }

    private static string ResolveOriginalProjectRoot(string copyRoot)
    {
        var parent = Directory.GetParent(copyRoot).FullName;
        var copyName = Path.GetFileName(copyRoot);
        var markerIndex = copyName.IndexOf("_URP", StringComparison.OrdinalIgnoreCase);
        var originalName = markerIndex > 0 ? copyName.Substring(0, markerIndex) : copyName;
        return Path.Combine(parent, originalName);
    }

    private static string ProjectFile(string projectRoot, string assetPath)
    {
        return Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void WriteReport(string copyRoot, string text)
    {
        var reportFile = ProjectFile(copyRoot, ReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportFile));
        File.WriteAllText(reportFile, text, Encoding.UTF8);
    }

    private static string BuildReport(string copyRoot, string originalRoot, RestoreStats stats)
    {
        var builder = new StringBuilder();
        builder.AppendLine("URP material visual restore report");
        builder.AppendLine("Copy project: " + copyRoot);
        builder.AppendLine("Original project read-only source: " + originalRoot);
        builder.AppendLine("Validation scene: " + (string.IsNullOrEmpty(stats.ValidationScenePath) ? "(not opened)" : stats.ValidationScenePath));
        builder.AppendLine();
        builder.AppendLine("Materials scanned: " + stats.MaterialsScanned);
        builder.AppendLine("Materials with original source: " + stats.MaterialsWithOriginal);
        builder.AppendLine("Materials missing original source: " + stats.MissingOriginal);
        builder.AppendLine("Changed materials: " + stats.ChangedMaterials);
        builder.AppendLine("White-looking materials recovered: " + stats.WhiteMaterialsRecovered);
        builder.AppendLine("Parent material links followed: " + stats.ParentLinksFollowed);
        builder.AppendLine("Unresolved parent guids: " + stats.UnresolvedParentGuids);
        builder.AppendLine("Unresolved texture guids: " + stats.UnresolvedTextureGuids);
        builder.AppendLine();
        builder.AppendLine("Base maps restored: " + stats.BaseMapsRestored);
        builder.AppendLine("Colors restored: " + stats.ColorsRestored);
        builder.AppendLine("Normal maps restored: " + stats.NormalMapsRestored);
        builder.AppendLine("Metallic maps restored: " + stats.MetallicMapsRestored);
        builder.AppendLine("Occlusion maps restored: " + stats.OcclusionMapsRestored);
        builder.AppendLine("Emission maps restored: " + stats.EmissionMapsRestored);
        builder.AppendLine("PBR floats restored: " + stats.FloatsRestored);
        builder.AppendLine();
        builder.AppendLine("Renderer material slots checked: " + stats.RendererMaterialSlotsChecked);
        builder.AppendLine("Null renderer material slots: " + stats.NullRendererMaterialSlots);
        builder.AppendLine("White renderer slots without base texture: " + stats.WhiteRendererMaterialSlots);
        if (stats.WhiteExamples.Count > 0)
        {
            builder.AppendLine("White slot examples:");
            foreach (var example in stats.WhiteExamples)
            {
                builder.AppendLine("- " + example);
            }
        }

        return builder.ToString();
    }

    private sealed class RestoreStats
    {
        public int MaterialsScanned;
        public int MaterialsWithOriginal;
        public int MissingOriginal;
        public int ChangedMaterials;
        public int WhiteMaterialsRecovered;
        public int ParentLinksFollowed;
        public int UnresolvedParentGuids;
        public int UnresolvedTextureGuids;
        public int BaseMapsRestored;
        public int ColorsRestored;
        public int NormalMapsRestored;
        public int MetallicMapsRestored;
        public int OcclusionMapsRestored;
        public int EmissionMapsRestored;
        public int FloatsRestored;
        public int RendererMaterialSlotsChecked;
        public int NullRendererMaterialSlots;
        public int WhiteRendererMaterialSlots;
        public string ValidationScenePath;
        public readonly List<string> WhiteExamples = new List<string>();

        public void AddWhiteExample(string materialName)
        {
            if (WhiteExamples.Count >= 25 || WhiteExamples.Contains(materialName))
            {
                return;
            }

            WhiteExamples.Add(materialName);
        }
    }

    private sealed class MaterialSnapshot
    {
        public string ParentGuid;
        public readonly Dictionary<string, TextureSlot> Textures = new Dictionary<string, TextureSlot>(StringComparer.Ordinal);
        public readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        public readonly Dictionary<string, float> Floats = new Dictionary<string, float>(StringComparer.Ordinal);

        public static MaterialSnapshot Parse(string yaml)
        {
            var snapshot = new MaterialSnapshot();
            var parentMatch = ParentRegex.Match(yaml);
            if (parentMatch.Success)
            {
                snapshot.ParentGuid = parentMatch.Groups["guid"].Value;
            }

            foreach (Match match in TextureRegex.Matches(yaml))
            {
                var guid = match.Groups["guid"].Value;
                if (string.IsNullOrEmpty(guid))
                {
                    continue;
                }

                snapshot.Textures[match.Groups["name"].Value.Trim()] = new TextureSlot
                {
                    Guid = guid,
                    Scale = new Vector2(ParseFloat(match.Groups["sx"].Value), ParseFloat(match.Groups["sy"].Value)),
                    Offset = new Vector2(ParseFloat(match.Groups["ox"].Value), ParseFloat(match.Groups["oy"].Value))
                };
            }

            foreach (Match match in ColorRegex.Matches(yaml))
            {
                snapshot.Colors[match.Groups["name"].Value.Trim()] = new Color(
                    ParseFloat(match.Groups["r"].Value),
                    ParseFloat(match.Groups["g"].Value),
                    ParseFloat(match.Groups["b"].Value),
                    ParseFloat(match.Groups["a"].Value));
            }

            foreach (Match match in FloatRegex.Matches(yaml))
            {
                snapshot.Floats[match.Groups["name"].Value.Trim()] = ParseFloat(match.Groups["value"].Value);
            }

            return snapshot;
        }

        public void Overlay(MaterialSnapshot other)
        {
            if (!string.IsNullOrEmpty(other.ParentGuid))
            {
                ParentGuid = other.ParentGuid;
            }

            foreach (var pair in other.Textures)
            {
                Textures[pair.Key] = pair.Value;
            }
            foreach (var pair in other.Colors)
            {
                Colors[pair.Key] = pair.Value;
            }
            foreach (var pair in other.Floats)
            {
                Floats[pair.Key] = pair.Value;
            }
        }
    }

    private sealed class TextureSlot
    {
        public string Guid;
        public Vector2 Scale = Vector2.one;
        public Vector2 Offset = Vector2.zero;
    }

    private static float ParseFloat(string value)
    {
        return float.Parse(value, Invariant);
    }
}
