using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class TransparentPetEnvironmentLightingBridge : MonoBehaviour
{
    public Transform targetRoot;
    public Transform probeAnchor;
    public Light fillLight;

    public bool applyOnEnable = true;
    public bool includeInactiveRenderers = true;
    public bool tuneLilToonMaterials = true;
    public bool receiveSceneShadows = true;
    public bool castSceneShadows = true;
    public bool refreshLightingDuringPlay = true;
    public bool enforceSceneLightingMinimums = true;
    public bool applyAfterPlayStart = true;

    [Range(0f, 1f)]
    public float lilToonAsUnlit = 0.03f;
    [Range(0f, 2f)]
    public float lilToonGiIntensity = 1f;
    [Range(0f, 1f)]
    public float lilToonLightMinLimit = 0.2f;
    [Range(0.1f, 2f)]
    public float lilToonLightMaxLimit = 1.45f;
    [Range(0f, 1f)]
    public float lilToonShadowReceive = 1f;
    [Range(0f, 1f)]
    public float lilToonSecondShadowReceive = 0.72f;
    [Range(0f, 1f)]
    public float lilToonThirdShadowReceive = 0.32f;
    [Range(0f, 1f)]
    public float lilToonShadowStrength = 0.72f;
    [Range(0f, 1f)]
    public float fillLightIntensity = 0.12f;
    [Min(0.1f)]
    public float playRefreshIntervalSeconds = 0.75f;
    [Range(1, 12)]
    public int playStartupRefreshFrames = 4;

    private readonly HashSet<Material> _touchedMaterials = new HashSet<Material>();
    private Coroutine _startupRefreshRoutine;
    private float _nextPlayRefreshTime;
    private int _lastAppliedRendererCount;

    private void Reset()
    {
        targetRoot = transform;
        probeAnchor = transform;
        fillLight = FindFillLight();
    }

    private void OnEnable()
    {
        if (targetRoot == null)
        {
            targetRoot = transform;
        }

        if (probeAnchor == null)
        {
            probeAnchor = targetRoot;
        }

        if (fillLight == null)
        {
            fillLight = FindFillLight();
        }

        if (applyOnEnable)
        {
            ApplyLightingSettings();
        }

        ScheduleStartupLightingRefresh();
    }

    private void Start()
    {
        if (applyOnEnable)
        {
            ApplyLightingSettings();
        }

        ScheduleStartupLightingRefresh();
    }

    private void OnDisable()
    {
        if (_startupRefreshRoutine != null)
        {
            StopCoroutine(_startupRefreshRoutine);
            _startupRefreshRoutine = null;
        }
    }

    private void OnTransformChildrenChanged()
    {
        ScheduleStartupLightingRefresh();
    }

    private void LateUpdate()
    {
        if (!Application.isPlaying || !refreshLightingDuringPlay)
        {
            return;
        }

        if (Time.unscaledTime < _nextPlayRefreshTime)
        {
            return;
        }

        _nextPlayRefreshTime = Time.unscaledTime + Mathf.Max(0.1f, playRefreshIntervalSeconds);
        ApplyLightingSettings();
    }

    private void OnValidate()
    {
        if (targetRoot == null)
        {
            targetRoot = transform;
        }

        if (probeAnchor == null)
        {
            probeAnchor = targetRoot;
        }

        if (fillLight == null)
        {
            fillLight = FindFillLight();
        }

        if (applyOnEnable)
        {
            ApplyLightingSettings();
        }
    }

    [ContextMenu("Apply Environment Lighting")]
    public void ApplyLightingSettings()
    {
        NormalizeLightingValues();

        Transform root = targetRoot != null ? targetRoot : transform;
        Transform anchor = probeAnchor != null ? probeAnchor : root;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactiveRenderers);
        _lastAppliedRendererCount = renderers.Length;
        _touchedMaterials.Clear();

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }

            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbesAndSkybox;
            renderer.probeAnchor = anchor;
            renderer.receiveShadows = receiveSceneShadows;
            renderer.shadowCastingMode = castSceneShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.renderingLayerMask = 1u;

            if (tuneLilToonMaterials)
            {
                TuneMaterials(renderer.sharedMaterials);
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                EditorUtility.SetDirty(renderer);
            }
#endif
        }

        if (fillLight != null)
        {
            fillLight.intensity = fillLightIntensity;
            fillLight.renderMode = LightRenderMode.Auto;
            fillLight.shadows = LightShadows.None;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                EditorUtility.SetDirty(fillLight);
            }
#endif
        }
    }

    private void NormalizeLightingValues()
    {
        if (!enforceSceneLightingMinimums)
        {
            return;
        }

        lilToonGiIntensity = Mathf.Max(lilToonGiIntensity, 1f);
        lilToonLightMinLimit = Mathf.Max(lilToonLightMinLimit, 0.2f);
        lilToonLightMaxLimit = Mathf.Max(lilToonLightMaxLimit, 1.45f);
        lilToonSecondShadowReceive = Mathf.Max(lilToonSecondShadowReceive, 0.72f);
        lilToonThirdShadowReceive = Mathf.Max(lilToonThirdShadowReceive, 0.32f);
        lilToonShadowStrength = Mathf.Max(lilToonShadowStrength, 0.72f);
        fillLightIntensity = Mathf.Max(fillLightIntensity, 0.12f);
        playRefreshIntervalSeconds = Mathf.Max(0.1f, playRefreshIntervalSeconds);
        playStartupRefreshFrames = Mathf.Clamp(playStartupRefreshFrames, 1, 12);
    }

    private void ScheduleStartupLightingRefresh()
    {
        if (!Application.isPlaying || !applyAfterPlayStart || !isActiveAndEnabled)
        {
            return;
        }

        if (_startupRefreshRoutine != null)
        {
            StopCoroutine(_startupRefreshRoutine);
        }

        _startupRefreshRoutine = StartCoroutine(RefreshLightingAfterStartup());
    }

    private IEnumerator RefreshLightingAfterStartup()
    {
        int frameCount = Mathf.Clamp(playStartupRefreshFrames, 1, 12);
        for (int i = 0; i < frameCount; i++)
        {
            yield return null;
            ApplyLightingSettings();
        }

        _startupRefreshRoutine = null;
    }

    private void TuneMaterials(Material[] materials)
    {
        if (materials == null)
        {
            return;
        }

        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null || !_touchedMaterials.Add(material) || !IsLilToon(material))
            {
                continue;
            }

            SetFloatIfPresent(material, "_AsUnlit", lilToonAsUnlit);
            SetFloatIfPresent(material, "_GI_Intensity", lilToonGiIntensity);
            SetFloatIfPresent(material, "_LightMinLimit", lilToonLightMinLimit);
            SetFloatIfPresent(material, "_LightMaxLimit", lilToonLightMaxLimit);
            SetFloatIfPresent(material, "_VertexLightStrength", 1f);
            SetFloatIfPresent(material, "_lilDirectionalLightStrength", 1f);
            SetFloatIfPresent(material, "_MonochromeLighting", 0f);
            SetFloatIfPresent(material, "_UseShadow", receiveSceneShadows ? 1f : 0f);
            SetFloatIfPresent(material, "_ReceiveShadows", receiveSceneShadows ? 1f : 0f);
            SetFloatIfPresent(material, "_ShadowReceive", receiveSceneShadows ? lilToonShadowReceive : 0f);
            SetFloatIfPresent(material, "_Shadow2ndReceive", receiveSceneShadows ? lilToonSecondShadowReceive : 0f);
            SetFloatIfPresent(material, "_Shadow3rdReceive", receiveSceneShadows ? lilToonThirdShadowReceive : 0f);
            SetFloatIfPresent(material, "_ShadowStrength", receiveSceneShadows ? lilToonShadowStrength : 0f);
            SetFloatIfPresent(material, "_Set_SystemShadowsToBase", receiveSceneShadows ? 1f : 0f);
            SetFloatIfPresent(material, "_Is_Filter_HiCutPointLightColor", 0f);

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                EditorUtility.SetDirty(material);
            }
#endif
        }
    }

    private static bool IsLilToon(Material material)
    {
        return material.shader != null && material.shader.name.IndexOf("lilToon", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void SetFloatIfPresent(Material material, string propertyName, float value)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetFloat(propertyName, value);
        }
    }

    private Light FindFillLight()
    {
        Transform root = transform;
        Transform found = root.Find("TransparentPet Key Light");
        if (found != null && found.TryGetComponent(out Light light))
        {
            return light;
        }

        return GetComponentInChildren<Light>(true);
    }
}
