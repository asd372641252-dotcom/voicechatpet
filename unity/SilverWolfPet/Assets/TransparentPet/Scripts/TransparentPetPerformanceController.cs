using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[DisallowMultipleComponent]
[DefaultExecutionOrder(-900)]
public sealed class TransparentPetPerformanceController : MonoBehaviour
{
    public string settingsKey = "TransparentPet.Performance.v1";
    public bool persistRuntimeSettings = true;
    public bool limitFrameRate = true;
    [Range(15, 144)]
    public int targetFrameRate = 60;
    public bool verticalSync;
    public int msaaSamples = 4;
    public bool overrideRenderScale;
    [Range(0.5f, 1.25f)]
    public float renderScale = 1f;
    public Camera[] targetCameras;

    private bool _settingsLoaded;

    public bool LimitFrameRate => limitFrameRate;
    public int TargetFrameRate => targetFrameRate;
    public bool VerticalSync => verticalSync;
    public int MsaaSamples => NormalizeMsaa(msaaSamples);
    public bool OverrideRenderScale => overrideRenderScale;
    public float RenderScale => renderScale;

    private void Awake()
    {
        LoadSettings();
        ApplySettings();
    }

    private void OnEnable()
    {
        LoadSettings();
        ApplySettings();
    }

    private void OnValidate()
    {
        NormalizeValues();
    }

    public void SetLimitFrameRate(bool value)
    {
        limitFrameRate = value;
        SaveAndApply();
    }

    public void SetTargetFrameRate(float value)
    {
        targetFrameRate = Mathf.RoundToInt(value);
        SaveAndApply();
    }

    public void SetVerticalSync(bool value)
    {
        verticalSync = value;
        SaveAndApply();
    }

    public void SetMsaaSamples(int value)
    {
        msaaSamples = NormalizeMsaa(value);
        SaveAndApply();
    }

    public void SetOverrideRenderScale(bool value)
    {
        overrideRenderScale = value;
        SaveAndApply();
    }

    public void SetRenderScale(float value)
    {
        renderScale = value;
        SaveAndApply();
    }

    public void ApplySettings()
    {
        NormalizeValues();
        QualitySettings.vSyncCount = verticalSync ? 1 : 0;
        Application.targetFrameRate = !verticalSync && limitFrameRate ? targetFrameRate : -1;
        QualitySettings.antiAliasing = MsaaSamples;
        ApplyCameraMsaa();
        ApplyRenderScale();
    }

    private void SaveAndApply()
    {
        ApplySettings();
        SaveSettings();
    }

    private void ApplyCameraMsaa()
    {
        Camera[] cameras = targetCameras;
        if (cameras == null || cameras.Length == 0)
        {
            cameras = Camera.allCameras;
        }

        bool allow = MsaaSamples > 0;
        for (int i = 0; i < cameras.Length; i++)
        {
            if (cameras[i] != null)
            {
                cameras[i].allowMSAA = allow;
            }
        }
    }

    private void ApplyRenderScale()
    {
        if (!overrideRenderScale)
        {
            return;
        }

        UniversalRenderPipelineAsset urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urpAsset == null)
        {
            urpAsset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
        }

        if (urpAsset != null)
        {
            urpAsset.renderScale = renderScale;
        }
    }

    private void NormalizeValues()
    {
        targetFrameRate = Mathf.Clamp(targetFrameRate, 15, 144);
        msaaSamples = NormalizeMsaa(msaaSamples);
        renderScale = Mathf.Clamp(renderScale, 0.5f, 1.25f);
    }

    private void LoadSettings()
    {
        if (_settingsLoaded || !persistRuntimeSettings || string.IsNullOrWhiteSpace(settingsKey) || !PlayerPrefs.HasKey(settingsKey))
        {
            _settingsLoaded = true;
            return;
        }

        try
        {
            PerformanceSettings settings = JsonUtility.FromJson<PerformanceSettings>(PlayerPrefs.GetString(settingsKey));
            limitFrameRate = settings.limitFrameRate;
            targetFrameRate = settings.targetFrameRate > 0 ? settings.targetFrameRate : targetFrameRate;
            verticalSync = settings.verticalSync;
            msaaSamples = settings.msaaSamples;
            overrideRenderScale = settings.overrideRenderScale;
            renderScale = settings.renderScale > 0f ? settings.renderScale : renderScale;
            NormalizeValues();
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Failed to load transparent pet performance settings: " + exception.Message);
        }

        _settingsLoaded = true;
    }

    private void SaveSettings()
    {
        NormalizeValues();
        if (!persistRuntimeSettings || string.IsNullOrWhiteSpace(settingsKey))
        {
            return;
        }

        PerformanceSettings settings = new PerformanceSettings
        {
            settingsVersion = 1,
            limitFrameRate = limitFrameRate,
            targetFrameRate = targetFrameRate,
            verticalSync = verticalSync,
            msaaSamples = MsaaSamples,
            overrideRenderScale = overrideRenderScale,
            renderScale = renderScale
        };
        PlayerPrefs.SetString(settingsKey, JsonUtility.ToJson(settings));
        PlayerPrefs.Save();
    }

    private static int NormalizeMsaa(int value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value <= 2)
        {
            return 2;
        }

        if (value <= 4)
        {
            return 4;
        }

        return 8;
    }

    [Serializable]
    private sealed class PerformanceSettings
    {
        public int settingsVersion = 1;
        public bool limitFrameRate = true;
        public int targetFrameRate = 60;
        public bool verticalSync;
        public int msaaSamples = 4;
        public bool overrideRenderScale;
        public float renderScale = 1f;
    }
}
