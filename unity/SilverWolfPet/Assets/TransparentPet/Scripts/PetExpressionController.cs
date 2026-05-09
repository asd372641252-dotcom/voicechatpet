using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class PetExpressionController : MonoBehaviour
{
    public Transform scanRoot;
    public string expressionMapPath = "GodotFinal/config/expression_map.json";
    public bool scanOnAwake = true;
    public float defaultEmotionWeight = 1f;
    public bool logBlendShapeReport;

    private readonly Dictionary<string, ExpressionBinding> _expressions = new Dictionary<string, ExpressionBinding>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<BlendShapeTarget>> _fallbackTargets = new Dictionary<string, List<BlendShapeTarget>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BlendShapeTarget> _targetsByExactName = new Dictionary<string, BlendShapeTarget>(StringComparer.Ordinal);
    private readonly Dictionary<string, BlendShapeTarget> _targetsByNormalizedName = new Dictionary<string, BlendShapeTarget>(StringComparer.OrdinalIgnoreCase);
    private readonly List<SkinnedMeshRenderer> _renderers = new List<SkinnedMeshRenderer>();

    private string _lipSyncExpression = "mouth_open";
    private bool _hasScanned;
    private bool _hasLoadedMap;

    private struct BlendShapeTarget
    {
        public SkinnedMeshRenderer Renderer;
        public int Index;
        public string Name;
    }

    private sealed class ExpressionBinding
    {
        public string Name;
        public string Group = "face";
        public bool ResetOthers = true;
        public readonly List<WeightedTarget> Targets = new List<WeightedTarget>();
    }

    private struct WeightedTarget
    {
        public BlendShapeTarget Target;
        public float Weight;
    }

    private void Awake()
    {
        if (scanOnAwake)
        {
            ScanBlendShapes();
        }
    }

    public void ScanBlendShapes()
    {
        _fallbackTargets.Clear();
        _targetsByExactName.Clear();
        _targetsByNormalizedName.Clear();
        _renderers.Clear();

        Transform root = scanRoot != null ? scanRoot : transform;
        _renderers.AddRange(root.GetComponentsInChildren<SkinnedMeshRenderer>(true));

        for (int r = 0; r < _renderers.Count; r++)
        {
            SkinnedMeshRenderer renderer = _renderers[r];
            Mesh mesh = renderer.sharedMesh;
            if (mesh == null)
            {
                continue;
            }

            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string blendShapeName = mesh.GetBlendShapeName(i);
                BlendShapeTarget target = new BlendShapeTarget
                {
                    Renderer = renderer,
                    Index = i,
                    Name = blendShapeName
                };

                if (!_targetsByExactName.ContainsKey(blendShapeName))
                {
                    _targetsByExactName.Add(blendShapeName, target);
                }

                string normalized = NormalizeBlendShapeName(blendShapeName);
                if (!string.IsNullOrEmpty(normalized) && !_targetsByNormalizedName.ContainsKey(normalized))
                {
                    _targetsByNormalizedName.Add(normalized, target);
                }

                string[] categories = ClassifyBlendShape(blendShapeName);
                for (int c = 0; c < categories.Length; c++)
                {
                    RegisterFallback(categories[c], target);
                }
            }
        }

        _hasScanned = true;
        LoadExpressionMap();
        if (logBlendShapeReport)
        {
            Debug.Log(BuildBlendShapeReport());
        }
    }

    public void SetEmotion(string emotion, float weight = -1f)
    {
        string expression = NormalizeExpressionName(emotion);
        SetExpressionWeight(expression, weight < 0f ? defaultEmotionWeight : weight);
    }

    public void SetExpressionWeight(string expressionName, float weight)
    {
        EnsureReady();
        string normalized = NormalizeExpressionName(expressionName);
        float clamped = Mathf.Clamp01(weight);

        if (_expressions.TryGetValue(normalized, out ExpressionBinding binding))
        {
            if (binding.ResetOthers)
            {
                ResetGroup(binding.Group);
            }

            if (binding.Targets.Count > 0)
            {
                ApplyBinding(binding, clamped);
            }
            else
            {
                ApplyFallback(normalized, clamped);
            }

            return;
        }

        ResetFallbackEmotionTargets();
        ApplyFallback(normalized, clamped);
    }

    public void SetMouth(float open01)
    {
        SetExpressionWeight(string.IsNullOrWhiteSpace(_lipSyncExpression) ? "mouth_open" : _lipSyncExpression, open01);
    }

    public string BuildBlendShapeReport()
    {
        EnsureReady();
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("BlendShape scan:");
        builder.AppendLine("- SkinnedMeshRenderer count: " + _renderers.Count);
        builder.AppendLine("- BlendShape exact count: " + _targetsByExactName.Count);
        foreach (KeyValuePair<string, List<BlendShapeTarget>> pair in _fallbackTargets)
        {
            builder.Append("- ").Append(pair.Key).Append(": ");
            for (int i = 0; i < pair.Value.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(pair.Value[i].Name);
            }
            builder.AppendLine();
        }

        foreach (KeyValuePair<string, ExpressionBinding> pair in _expressions)
        {
            builder.Append("- expression ").Append(pair.Key).Append(" targets=").Append(pair.Value.Targets.Count);
            if (pair.Value.Targets.Count > 0)
            {
                builder.Append(": ");
                for (int i = 0; i < pair.Value.Targets.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }
                    builder.Append(pair.Value.Targets[i].Target.Name);
                }
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private void EnsureReady()
    {
        if (!_hasScanned)
        {
            ScanBlendShapes();
        }

        if (!_hasLoadedMap)
        {
            LoadExpressionMap();
        }
    }

    private void LoadExpressionMap()
    {
        _expressions.Clear();
        _hasLoadedMap = true;

        string path = ResolveStreamingPath(expressionMapPath);
        if (!File.Exists(path))
        {
            Debug.LogWarning("Expression map missing: " + path);
            return;
        }

        try
        {
            Dictionary<string, object> root = TransparentPetJson.AsObject(TransparentPetJson.Parse(File.ReadAllText(path, Encoding.UTF8)));
            if (root == null)
            {
                return;
            }

            Dictionary<string, object> lip = TransparentPetJson.AsObject(root.ContainsKey("lip_sync") ? root["lip_sync"] : null);
            if (lip != null)
            {
                _lipSyncExpression = TransparentPetJson.GetString(lip, "expression", _lipSyncExpression);
            }

            Dictionary<string, object> expressions = TransparentPetJson.AsObject(root.ContainsKey("expressions") ? root["expressions"] : null);
            if (expressions == null)
            {
                return;
            }

            foreach (KeyValuePair<string, object> pair in expressions)
            {
                ExpressionBinding binding = ParseExpressionBinding(pair.Key, TransparentPetJson.AsObject(pair.Value));
                _expressions[pair.Key] = binding;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Expression map parse failed: " + ex.Message);
        }
    }

    private ExpressionBinding ParseExpressionBinding(string expressionName, Dictionary<string, object> data)
    {
        ExpressionBinding binding = new ExpressionBinding
        {
            Name = expressionName,
            Group = TransparentPetJson.GetString(data, "exclusive_group", "face"),
            ResetOthers = TransparentPetJson.GetBool(data, "reset_others", true)
        };

        List<object> blendShapes = data != null && data.ContainsKey("blend_shapes")
            ? TransparentPetJson.AsArray(data["blend_shapes"])
            : null;
        if (blendShapes == null)
        {
            return binding;
        }

        for (int i = 0; i < blendShapes.Count; i++)
        {
            Dictionary<string, object> item = TransparentPetJson.AsObject(blendShapes[i]);
            if (item == null)
            {
                continue;
            }

            float targetWeight = Mathf.Clamp01(TransparentPetJson.GetFloat(item, "weight", 1f));
            List<object> names = item.ContainsKey("names") ? TransparentPetJson.AsArray(item["names"]) : null;
            if (names == null)
            {
                continue;
            }

            for (int n = 0; n < names.Count; n++)
            {
                string alias = Convert.ToString(names[n]);
                if (TryFindBlendShape(alias, out BlendShapeTarget target))
                {
                    binding.Targets.Add(new WeightedTarget { Target = target, Weight = targetWeight });
                    break;
                }
            }
        }

        return binding;
    }

    private bool TryFindBlendShape(string name, out BlendShapeTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (_targetsByExactName.TryGetValue(name, out target))
        {
            return true;
        }

        string normalized = NormalizeBlendShapeName(name);
        return !string.IsNullOrEmpty(normalized) && _targetsByNormalizedName.TryGetValue(normalized, out target);
    }

    private void ApplyBinding(ExpressionBinding binding, float normalizedWeight)
    {
        for (int i = 0; i < binding.Targets.Count; i++)
        {
            WeightedTarget weighted = binding.Targets[i];
            SetTarget(weighted.Target, normalizedWeight * weighted.Weight);
        }
    }

    private void ResetGroup(string group)
    {
        foreach (ExpressionBinding binding in _expressions.Values)
        {
            if (!string.Equals(binding.Group, group, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ApplyBinding(binding, 0f);
        }
    }

    private void ApplyFallback(string expression, float weight)
    {
        string category = FallbackCategoryForExpression(expression);
        if (string.IsNullOrEmpty(category))
        {
            return;
        }

        ApplyFallbackCategory(category, weight);
    }

    private void ResetFallbackEmotionTargets()
    {
        ApplyFallbackCategory("happy", 0f);
        ApplyFallbackCategory("angry", 0f);
        ApplyFallbackCategory("sad", 0f);
        ApplyFallbackCategory("blink", 0f);
        ApplyFallbackCategory("sleepy", 0f);
        ApplyFallbackCategory("surprised", 0f);
    }

    private void ApplyFallbackCategory(string category, float weight)
    {
        if (!_fallbackTargets.TryGetValue(category, out List<BlendShapeTarget> targets))
        {
            return;
        }

        for (int i = 0; i < targets.Count; i++)
        {
            SetTarget(targets[i], weight);
        }
    }

    private void SetTarget(BlendShapeTarget target, float weight01)
    {
        if (target.Renderer == null || target.Renderer.sharedMesh == null)
        {
            return;
        }

        if (target.Index < 0 || target.Index >= target.Renderer.sharedMesh.blendShapeCount)
        {
            return;
        }

        target.Renderer.SetBlendShapeWeight(target.Index, Mathf.Clamp01(weight01) * 100f);
    }

    private void RegisterFallback(string category, BlendShapeTarget target)
    {
        if (!_fallbackTargets.TryGetValue(category, out List<BlendShapeTarget> targets))
        {
            targets = new List<BlendShapeTarget>();
            _fallbackTargets[category] = targets;
        }

        targets.Add(target);
    }

    private static string FallbackCategoryForExpression(string expression)
    {
        switch (NormalizeExpressionName(expression))
        {
            case "happy":
            case "clicked":
                return "happy";
            case "angry":
            case "mocking":
                return "angry";
            case "sad":
                return "sad";
            case "sleep":
            case "sleeping":
            case "sleepy":
                return "sleepy";
            case "surprised":
            case "interrupted":
                return "surprised";
            case "mouth_open":
            case "mouth_small":
            case "mouth_wide":
            case "mouth_round":
            case "mouth_closed":
            case "mouth_smirk":
            case "mouth":
                return "mouth";
            default:
                return "";
        }
    }

    private static string[] ClassifyBlendShape(string rawName)
    {
        List<string> categories = new List<string>();
        string name = NormalizeBlendShapeName(rawName);
        if (name.Contains("mouth") || name.Contains("vrcvaa") || name.Contains("vrcv_aa") ||
            name == "a" || name == "aa" || name == "ah" ||
            ContainsCodePoint(rawName, 0x53e3) ||
            IsSingleKanaMouth(rawName))
        {
            categories.Add("mouth");
        }

        if (name.Contains("smile") || name.Contains("happy") || name.Contains("joy") || name.Contains("fun"))
        {
            categories.Add("happy");
        }

        if (name.Contains("angry") || name.Contains("anger") || name.Contains("annoy") || name.Contains("frown"))
        {
            categories.Add("angry");
        }

        if (name.Contains("sad") || name.Contains("sorrow") || name.Contains("trouble"))
        {
            categories.Add("sad");
        }

        if (name.Contains("blink") || name.Contains("eyeclose") || name.Contains("closeeye") || name.Contains("sleep"))
        {
            categories.Add("blink");
            categories.Add("sleepy");
        }

        if (name.Contains("surprise") || name.Contains("shock"))
        {
            categories.Add("surprised");
        }

        return categories.ToArray();
    }

    private static string NormalizeExpressionName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "neutral";
        }

        string normalized = value.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "talk":
            case "talking":
            case "speak":
            case "speaking":
                return "talk";
            case "think":
            case "thinking":
            case "confused":
                return "thinking";
            case "sleep":
            case "sleepy":
                return "sleeping";
            case "surprise":
                return "surprised";
            default:
                return normalized;
        }
    }

    private static string NormalizeBlendShapeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim()
            .ToLowerInvariant()
            .Replace(" ", string.Empty)
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .Replace(".", string.Empty);
    }

    private static bool ContainsCodePoint(string value, int codePoint)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        char needle = (char)codePoint;
        return value.IndexOf(needle) >= 0;
    }

    private static bool IsSingleKanaMouth(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string normalized = value.Trim();
        if (normalized.Length < 1 || normalized.Length > 2)
        {
            return false;
        }

        int code = normalized[0];
        return code == 0x3042 || code == 0x3044 || code == 0x3046 || code == 0x3048 ||
            code == 0x304a || code == 0x3093 || code == 0x30ef;
    }

    private static string ResolveStreamingPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return Application.streamingAssetsPath;
        }

        if (Path.IsPathRooted(relativePath))
        {
            return Path.GetFullPath(relativePath);
        }

        return Path.Combine(Application.streamingAssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
