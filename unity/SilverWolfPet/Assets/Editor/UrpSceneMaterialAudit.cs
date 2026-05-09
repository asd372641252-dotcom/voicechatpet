using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class UrpSceneMaterialAudit
{
    private const string ScenePath = "Assets/Scenes/BlenderIndoorScene.unity";
    private const string SummaryPath = "Assets/SceneExport/URP_scene_material_audit_report.txt";
    private const string CsvPath = "Assets/SceneExport/URP_scene_material_audit.csv";
    private const string PreviewAssetPath = "Assets/SceneExport/URP_scene_material_audit_preview.png";
    private const string PreviewDiskPath = "D:/pet/urp_scene_material_audit_preview.png";

    [MenuItem("Tools/URP Copy/Audit Scene Materials And Preview")]
    public static void AuditSceneMaterialsAndPreview()
    {
        EditorSceneManager.OpenScene(ScenePath);

        var rows = new List<Row>();
        var shaderCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var texturePathCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int nullSlots = 0;

        foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (renderer == null || !renderer.gameObject.scene.IsValid())
            {
                continue;
            }

            var materials = renderer.sharedMaterials;
            for (var i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null)
                {
                    nullSlots++;
                    rows.Add(Row.Null(renderer, i));
                    continue;
                }

                var row = Row.From(renderer, i, material);
                rows.Add(row);

                Increment(shaderCounts, row.ShaderName);
                if (!string.IsNullOrEmpty(row.BaseTexturePath))
                {
                    Increment(texturePathCounts, row.BaseTexturePath);
                }
            }
        }

        var preview = RenderPreview();
        WriteCsv(rows);
        WriteSummary(rows, shaderCounts, texturePathCounts, nullSlots, preview);
        AssetDatabase.Refresh();
        Debug.Log(File.ReadAllText(ProjectFile(SummaryPath)));
    }

    private static PreviewStats RenderPreview()
    {
        var camera = Camera.main;
        var tempCameraObject = default(GameObject);
        if (camera == null)
        {
            tempCameraObject = new GameObject("URP Audit Preview Camera");
            camera = tempCameraObject.AddComponent<Camera>();
            PositionCameraAtScene(camera);
        }

        var previousTarget = camera.targetTexture;
        var previousEnabled = camera.enabled;
        var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32)
        {
            antiAliasing = 4
        };
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);

        try
        {
            camera.enabled = false;
            camera.targetTexture = rt;
            camera.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply(false, false);

            var bytes = tex.EncodeToPNG();
            File.WriteAllBytes(ProjectFile(PreviewAssetPath), bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(PreviewDiskPath));
            File.WriteAllBytes(PreviewDiskPath, bytes);

            return PreviewStats.FromPixels(tex.GetPixels32());
        }
        finally
        {
            camera.targetTexture = previousTarget;
            camera.enabled = previousEnabled;
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            if (tempCameraObject != null)
            {
                UnityEngine.Object.DestroyImmediate(tempCameraObject);
            }
        }
    }

    private static void PositionCameraAtScene(Camera camera)
    {
        var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(r => r != null && r.gameObject.scene.IsValid())
            .ToArray();

        if (renderers.Length == 0)
        {
            camera.transform.SetPositionAndRotation(new Vector3(0, 1.6f, -6f), Quaternion.Euler(12f, 0f, 0f));
            return;
        }

        var bounds = renderers[0].bounds;
        foreach (var renderer in renderers.Skip(1))
        {
            bounds.Encapsulate(renderer.bounds);
        }

        var center = bounds.center;
        var radius = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
        var direction = new Vector3(0.55f, 0.28f, -1f).normalized;
        camera.transform.position = center - direction * Mathf.Max(radius * 2.2f, 4f);
        camera.transform.LookAt(center + Vector3.up * radius * 0.12f);
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = Mathf.Max(radius * 8f, 100f);
        camera.fieldOfView = 45f;
        camera.clearFlags = CameraClearFlags.Skybox;
    }

    private static void WriteCsv(List<Row> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("renderer,slot,material,path,shader,baseTexture,baseColor,color,isWhiteNoTexture,isTextureAssigned");
        foreach (var row in rows.OrderBy(r => r.RendererName, StringComparer.Ordinal).ThenBy(r => r.Slot))
        {
            builder.AppendCsv(row.RendererName).Append(',');
            builder.Append(row.Slot).Append(',');
            builder.AppendCsv(row.MaterialName).Append(',');
            builder.AppendCsv(row.MaterialPath).Append(',');
            builder.AppendCsv(row.ShaderName).Append(',');
            builder.AppendCsv(row.BaseTexturePath).Append(',');
            builder.AppendCsv(row.BaseColor).Append(',');
            builder.AppendCsv(row.Color).Append(',');
            builder.Append(row.IsWhiteNoTexture).Append(',');
            builder.Append(row.IsTextureAssigned).AppendLine();
        }

        File.WriteAllText(ProjectFile(CsvPath), builder.ToString(), Encoding.UTF8);
    }

    private static void WriteSummary(
        List<Row> rows,
        Dictionary<string, int> shaderCounts,
        Dictionary<string, int> texturePathCounts,
        int nullSlots,
        PreviewStats preview)
    {
        var totalSlots = rows.Count;
        var textureSlots = rows.Count(r => r.IsTextureAssigned);
        var whiteNoTexture = rows.Where(r => r.IsWhiteNoTexture).ToList();
        var whiteTexture = rows.Where(r => r.IsTextureAssigned && r.IsBaseColorWhite).ToList();

        var builder = new StringBuilder();
        builder.AppendLine("URP scene material audit");
        builder.AppendLine("Scene: " + ScenePath);
        builder.AppendLine("Preview asset: " + PreviewAssetPath);
        builder.AppendLine("Preview disk copy: " + PreviewDiskPath);
        builder.AppendLine("CSV: " + CsvPath);
        builder.AppendLine();
        builder.AppendLine("Renderer material slots: " + totalSlots);
        builder.AppendLine("Null material slots: " + nullSlots);
        builder.AppendLine("Slots with base texture: " + textureSlots);
        builder.AppendLine("Slots with white color and no base texture: " + whiteNoTexture.Count);
        builder.AppendLine("Slots with texture but white tint: " + whiteTexture.Count);
        builder.AppendLine("Unique base textures used: " + texturePathCounts.Count);
        builder.AppendLine();
        builder.AppendLine("Rendered preview average RGB: " + preview.AverageRgb);
        builder.AppendLine("Rendered preview near-white pixel ratio: " + preview.NearWhiteRatio.ToString("P2", CultureInfo.InvariantCulture));
        builder.AppendLine("Rendered preview saturated-white pixel ratio: " + preview.SaturatedWhiteRatio.ToString("P2", CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.AppendLine("Shader counts:");
        foreach (var pair in shaderCounts.OrderByDescending(p => p.Value))
        {
            builder.AppendLine("- " + pair.Key + ": " + pair.Value);
        }
        builder.AppendLine();
        builder.AppendLine("White/no-texture material examples:");
        foreach (var row in whiteNoTexture.Take(25))
        {
            builder.AppendLine("- " + row.MaterialName + " | " + row.MaterialPath + " | renderer " + row.RendererName);
        }
        builder.AppendLine();
        builder.AppendLine("First textured material examples:");
        foreach (var row in rows.Where(r => r.IsTextureAssigned).Take(25))
        {
            builder.AppendLine("- " + row.MaterialName + " | " + row.BaseTexturePath + " | renderer " + row.RendererName);
        }

        File.WriteAllText(ProjectFile(SummaryPath), builder.ToString(), Encoding.UTF8);
    }

    private static void Increment(Dictionary<string, int> counts, string key)
    {
        key = string.IsNullOrEmpty(key) ? "(empty)" : key;
        counts.TryGetValue(key, out var value);
        counts[key] = value + 1;
    }

    private static string ProjectFile(string assetPath)
    {
        return Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed class Row
    {
        public string RendererName;
        public int Slot;
        public string MaterialName;
        public string MaterialPath;
        public string ShaderName;
        public string BaseTexturePath;
        public string BaseColor;
        public string Color;
        public bool IsWhiteNoTexture;
        public bool IsTextureAssigned;
        public bool IsBaseColorWhite;

        public static Row Null(Renderer renderer, int slot)
        {
            return new Row
            {
                RendererName = HierarchyPath(renderer.transform),
                Slot = slot,
                MaterialName = "(null)",
                MaterialPath = "",
                ShaderName = "",
                BaseTexturePath = "",
                BaseColor = "",
                Color = "",
                IsWhiteNoTexture = false,
                IsTextureAssigned = false,
                IsBaseColorWhite = false
            };
        }

        public static Row From(Renderer renderer, int slot, Material material)
        {
            var baseTexture = GetTexture(material, "_BaseMap", "_MainTex");
            var baseColor = GetColor(material, "_BaseColor");
            var color = GetColor(material, "_Color");
            var effectiveColor = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") :
                material.HasProperty("_Color") ? material.GetColor("_Color") : UnityEngine.Color.white;

            return new Row
            {
                RendererName = HierarchyPath(renderer.transform),
                Slot = slot,
                MaterialName = material.name,
                MaterialPath = AssetDatabase.GetAssetPath(material),
                ShaderName = material.shader != null ? material.shader.name : "(null shader)",
                BaseTexturePath = baseTexture != null ? AssetDatabase.GetAssetPath(baseTexture) : "",
                BaseColor = baseColor,
                Color = color,
                IsWhiteNoTexture = baseTexture == null && IsWhite(effectiveColor),
                IsTextureAssigned = baseTexture != null,
                IsBaseColorWhite = IsWhite(effectiveColor)
            };
        }

        private static Texture GetTexture(Material material, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                if (material.HasProperty(propertyName) && material.GetTexture(propertyName) != null)
                {
                    return material.GetTexture(propertyName);
                }
            }

            return null;
        }

        private static string GetColor(Material material, string propertyName)
        {
            return material.HasProperty(propertyName) ? FormatColor(material.GetColor(propertyName)) : "";
        }

        private static bool IsWhite(UnityEngine.Color color)
        {
            return color.r > 0.96f && color.g > 0.96f && color.b > 0.96f && color.a > 0.96f;
        }
    }

    private readonly struct PreviewStats
    {
        public readonly string AverageRgb;
        public readonly float NearWhiteRatio;
        public readonly float SaturatedWhiteRatio;

        private PreviewStats(string averageRgb, float nearWhiteRatio, float saturatedWhiteRatio)
        {
            AverageRgb = averageRgb;
            NearWhiteRatio = nearWhiteRatio;
            SaturatedWhiteRatio = saturatedWhiteRatio;
        }

        public static PreviewStats FromPixels(Color32[] pixels)
        {
            double r = 0;
            double g = 0;
            double b = 0;
            var nearWhite = 0;
            var saturatedWhite = 0;

            foreach (var pixel in pixels)
            {
                r += pixel.r;
                g += pixel.g;
                b += pixel.b;
                if (pixel.r >= 240 && pixel.g >= 240 && pixel.b >= 240)
                {
                    nearWhite++;
                }
                if (pixel.r >= 252 && pixel.g >= 252 && pixel.b >= 252)
                {
                    saturatedWhite++;
                }
            }

            var count = Mathf.Max(1, pixels.Length);
            var average = string.Format(CultureInfo.InvariantCulture, "{0:F1}, {1:F1}, {2:F1}", r / count, g / count, b / count);
            return new PreviewStats(average, nearWhite / (float)count, saturatedWhite / (float)count);
        }
    }

    private static string HierarchyPath(Transform transform)
    {
        var names = new Stack<string>();
        var current = transform;
        while (current != null)
        {
            names.Push(current.name);
            current = current.parent;
        }

        return string.Join("/", names);
    }

    private static string FormatColor(UnityEngine.Color color)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0:F3} {1:F3} {2:F3} {3:F3}", color.r, color.g, color.b, color.a);
    }

    private static StringBuilder AppendCsv(this StringBuilder builder, string value)
    {
        if (value == null)
        {
            return builder;
        }

        builder.Append('"');
        builder.Append(value.Replace("\"", "\"\""));
        builder.Append('"');
        return builder;
    }
}
