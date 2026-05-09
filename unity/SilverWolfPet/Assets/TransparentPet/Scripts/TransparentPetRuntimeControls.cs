using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class TransparentPetRuntimeControls : MonoBehaviour
{
    public enum PetForm
    {
        Base = 0,
        Transform = 1,
        Custom = 2
    }

    public Transform modelRoot;
    public TransparentPetSkeletonHitMask skeletonHitMask;
    public Light keyLight;
    public bool defaultGlassesVisible;
    public bool defaultWingsVisible;
    public PetForm defaultForm = PetForm.Base;
    public string currentRenderPreset = "DesktopPetSoft";
    public int lightMode;

    private readonly List<Renderer> _headGlassesRenderers = new List<Renderer>();
    private readonly List<Renderer> _faceVisorRenderers = new List<Renderer>();
    private readonly List<Renderer> _lightWingRenderers = new List<Renderer>();
    private readonly List<Renderer> _wingBoneRenderers = new List<Renderer>();
    private readonly List<Renderer> _smallLightWingRenderers = new List<Renderer>();
    private readonly List<Renderer> _largeLightWingRenderers = new List<Renderer>();
    private readonly List<Renderer> _smallWingBoneRenderers = new List<Renderer>();
    private readonly List<Renderer> _largeWingBoneRenderers = new List<Renderer>();
    private readonly List<AccessoryMaterialSlot> _headGlassesMaterialSlots = new List<AccessoryMaterialSlot>();
    private readonly List<AccessoryMaterialSlot> _faceVisorMaterialSlots = new List<AccessoryMaterialSlot>();
    private readonly List<AccessoryMaterialSlot> _lightWingMaterialSlots = new List<AccessoryMaterialSlot>();
    private readonly List<AccessoryMaterialSlot> _wingBoneMaterialSlots = new List<AccessoryMaterialSlot>();
    private readonly List<AccessoryMaterialSlot> _smallLightWingMaterialSlots = new List<AccessoryMaterialSlot>();
    private readonly List<AccessoryMaterialSlot> _largeLightWingMaterialSlots = new List<AccessoryMaterialSlot>();
    private readonly List<AccessoryMaterialSlot> _smallWingBoneMaterialSlots = new List<AccessoryMaterialSlot>();
    private readonly List<AccessoryMaterialSlot> _largeWingBoneMaterialSlots = new List<AccessoryMaterialSlot>();
    private readonly List<Material> _materials = new List<Material>();
    private readonly Dictionary<Material, MaterialDefaults> _materialDefaults = new Dictionary<Material, MaterialDefaults>();
    private Material _hiddenMaterial;
    private bool _hitMaskDebugVisible;
    private bool _headGlassesVisible;
    private bool _faceVisorVisible;
    private bool _smallLightBladeVisible;
    private bool _largeLightWingsVisible;
    private bool _smallWingBonesVisible;
    private bool _largeWingBonesVisible;
    private bool _applyingForm;
    private PetForm _currentForm = PetForm.Base;
    private Color _defaultAmbientLight;
    private float _defaultKeyIntensity;

    public bool HitMaskDebugVisible => _hitMaskDebugVisible;
    public bool GlassesVisible => _headGlassesVisible || _faceVisorVisible;
    public bool HeadGlassesVisible => _headGlassesVisible;
    public bool FaceVisorVisible => _faceVisorVisible;
    public bool WingsVisible => LightWingsVisible || WingBonesVisible;
    public bool LightWingsVisible => _smallLightBladeVisible || _largeLightWingsVisible;
    public bool WingBonesVisible => _smallWingBonesVisible || _largeWingBonesVisible;
    public bool SmallLightBladeVisible => _smallLightBladeVisible;
    public bool LargeLightWingsVisible => _largeLightWingsVisible;
    public bool SmallWingBonesVisible => _smallWingBonesVisible;
    public bool LargeWingBonesVisible => _largeWingBonesVisible;
    public PetForm CurrentForm => _currentForm;
    public string CurrentRenderPreset => currentRenderPreset;
    public int LightMode => lightMode;

    private void Awake()
    {
        ApplyConfiguredDefaults();
    }

    public void ApplyConfiguredDefaults()
    {
        if (modelRoot == null)
        {
            modelRoot = transform;
        }

        if (keyLight == null)
        {
            keyLight = FindAnyObjectByType<Light>();
        }

        _defaultAmbientLight = RenderSettings.ambientLight;
        _defaultKeyIntensity = keyLight != null ? keyLight.intensity : 0.58f;
        CollectRenderers();
        ApplyForm(defaultForm);
        SetLightMode(lightMode);
    }

    public void SetHitMaskDebugVisible(bool visible)
    {
        _hitMaskDebugVisible = visible;
        if (skeletonHitMask != null)
        {
            skeletonHitMask.debugDraw = visible;
        }
    }

    public void SetGlassesVisible(bool visible)
    {
        SetHeadGlassesVisible(visible);
        SetFaceVisorVisible(visible);
    }

    public void SetWingsVisible(bool visible)
    {
        SetLightWingsVisible(visible);
        SetWingBonesVisible(visible);
    }

    public void ApplyBaseForm()
    {
        ApplyForm(PetForm.Base);
    }

    public void ApplyTransformForm()
    {
        ApplyForm(PetForm.Transform);
    }

    public void SetHeadGlassesVisible(bool visible)
    {
        SetHeadGlassesVisible(visible, true);
    }

    public void SetFaceVisorVisible(bool visible)
    {
        SetFaceVisorVisible(visible, true);
    }

    public void SetLightWingsVisible(bool visible)
    {
        SetLightWingsVisible(visible, true);
    }

    public void SetWingBonesVisible(bool visible)
    {
        SetWingBonesVisible(visible, true);
    }

    public void SetSmallLightBladeVisible(bool visible)
    {
        SetSmallLightBladeVisible(visible, true);
    }

    public void SetLargeLightWingsVisible(bool visible)
    {
        SetLargeLightWingsVisible(visible, true);
    }

    public void SetSmallWingBonesVisible(bool visible)
    {
        SetSmallWingBonesVisible(visible, true);
    }

    public void SetLargeWingBonesVisible(bool visible)
    {
        SetLargeWingBonesVisible(visible, true);
    }

    public void SetRenderPreset(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
        {
            presetName = "DesktopPetSoft";
        }

        currentRenderPreset = presetName;
        for (int i = 0; i < _materials.Count; i++)
        {
            Material material = _materials[i];
            if (material == null)
            {
                continue;
            }

            ApplyMaterialPreset(material, presetName);
        }
    }

    public void SetLightMode(int mode)
    {
        lightMode = Mathf.Clamp(mode, 0, 3);
        if (keyLight == null)
        {
            return;
        }

        switch (lightMode)
        {
            case 1:
                keyLight.intensity = 0.28f;
                RenderSettings.ambientLight = new Color(0.48f, 0.5f, 0.56f, 1f);
                break;
            case 2:
                keyLight.intensity = 0.42f;
                RenderSettings.ambientLight = new Color(0.54f, 0.56f, 0.62f, 1f);
                break;
            case 3:
                keyLight.intensity = 0.18f;
                RenderSettings.ambientLight = new Color(0.34f, 0.36f, 0.42f, 1f);
                break;
            default:
                keyLight.intensity = _defaultKeyIntensity;
                RenderSettings.ambientLight = _defaultAmbientLight;
                break;
        }
    }

    private void CollectRenderers()
    {
        _headGlassesRenderers.Clear();
        _faceVisorRenderers.Clear();
        _lightWingRenderers.Clear();
        _wingBoneRenderers.Clear();
        _smallLightWingRenderers.Clear();
        _largeLightWingRenderers.Clear();
        _smallWingBoneRenderers.Clear();
        _largeWingBoneRenderers.Clear();
        _headGlassesMaterialSlots.Clear();
        _faceVisorMaterialSlots.Clear();
        _lightWingMaterialSlots.Clear();
        _wingBoneMaterialSlots.Clear();
        _smallLightWingMaterialSlots.Clear();
        _largeLightWingMaterialSlots.Clear();
        _smallWingBoneMaterialSlots.Clear();
        _largeWingBoneMaterialSlots.Clear();
        _materials.Clear();

        if (modelRoot == null)
        {
            return;
        }

        Renderer[] renderers = modelRoot.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            string searchableName = BuildSearchableRendererName(renderer);
            if (CanToggleRendererAsAccessory(renderer))
            {
                if (ContainsFaceVisorToken(searchableName))
                {
                    _faceVisorRenderers.Add(renderer);
                }
                else if (ContainsHeadGlassesToken(searchableName))
                {
                    _headGlassesRenderers.Add(renderer);
                }

                if (ContainsSmallLightWingToken(searchableName))
                {
                    _smallLightWingRenderers.Add(renderer);
                    _lightWingRenderers.Add(renderer);
                }
                else if (ContainsLightWingToken(searchableName))
                {
                    _largeLightWingRenderers.Add(renderer);
                    _lightWingRenderers.Add(renderer);
                }

                if (ContainsSmallWingBoneToken(searchableName))
                {
                    _smallWingBoneRenderers.Add(renderer);
                    _wingBoneRenderers.Add(renderer);
                }
                else if (ContainsWingBoneToken(searchableName))
                {
                    _largeWingBoneRenderers.Add(renderer);
                    _wingBoneRenderers.Add(renderer);
                }
                else if (ContainsWingToken(searchableName) && !ContainsLightWingToken(searchableName))
                {
                    _largeLightWingRenderers.Add(renderer);
                    _lightWingRenderers.Add(renderer);
                }
            }

            Material[] sharedMaterials = renderer.sharedMaterials;
            for (int j = 0; j < sharedMaterials.Length; j++)
            {
                Material material = sharedMaterials[j];
                string materialName = material != null ? material.name : string.Empty;
                if (ContainsFaceVisorToken(materialName))
                {
                    _faceVisorMaterialSlots.Add(new AccessoryMaterialSlot(renderer, j, material));
                }
                else if (ContainsHeadGlassesToken(materialName))
                {
                    _headGlassesMaterialSlots.Add(new AccessoryMaterialSlot(renderer, j, material));
                }

                if (ContainsSmallLightWingToken(materialName))
                {
                    _smallLightWingMaterialSlots.Add(new AccessoryMaterialSlot(renderer, j, material));
                    _lightWingMaterialSlots.Add(new AccessoryMaterialSlot(renderer, j, material));
                }
                else if (ContainsLightWingToken(materialName))
                {
                    _largeLightWingMaterialSlots.Add(new AccessoryMaterialSlot(renderer, j, material));
                    _lightWingMaterialSlots.Add(new AccessoryMaterialSlot(renderer, j, material));
                }

                if (ContainsSmallWingBoneToken(materialName))
                {
                    _smallWingBoneMaterialSlots.Add(new AccessoryMaterialSlot(renderer, j, material));
                    _wingBoneMaterialSlots.Add(new AccessoryMaterialSlot(renderer, j, material));
                }
                else if (ContainsWingBoneToken(materialName))
                {
                    _largeWingBoneMaterialSlots.Add(new AccessoryMaterialSlot(renderer, j, material));
                    _wingBoneMaterialSlots.Add(new AccessoryMaterialSlot(renderer, j, material));
                }
                else if (ContainsWingToken(materialName) && !ContainsLightWingToken(materialName))
                {
                    _largeLightWingMaterialSlots.Add(new AccessoryMaterialSlot(renderer, j, material));
                    _lightWingMaterialSlots.Add(new AccessoryMaterialSlot(renderer, j, material));
                }

                if (material != null && !_materials.Contains(material))
                {
                    _materials.Add(material);
                    CaptureMaterialDefaults(material);
                }
            }
        }
    }

    private void ApplyForm(PetForm form)
    {
        _applyingForm = true;
        switch (form)
        {
            case PetForm.Transform:
                SetHeadGlassesVisible(false, false);
                SetFaceVisorVisible(true, false);
                SetSmallWingBonesVisible(true, false);
                SetSmallLightBladeVisible(true, false);
                SetLargeWingBonesVisible(true, false);
                SetLargeLightWingsVisible(true, false);
                _currentForm = PetForm.Transform;
                break;
            default:
                SetHeadGlassesVisible(true, false);
                SetFaceVisorVisible(false, false);
                SetSmallWingBonesVisible(true, false);
                SetSmallLightBladeVisible(true, false);
                SetLargeWingBonesVisible(false, false);
                SetLargeLightWingsVisible(false, false);
                _currentForm = PetForm.Base;
                break;
        }

        _applyingForm = false;
    }

    private void SetHeadGlassesVisible(bool visible, bool markCustom)
    {
        _headGlassesVisible = visible;
        SetRendererListVisible(_headGlassesRenderers, visible);
        SetMaterialSlotsVisible(_headGlassesMaterialSlots, visible);
        MarkCustomForm(markCustom);
    }

    private void SetFaceVisorVisible(bool visible, bool markCustom)
    {
        _faceVisorVisible = visible;
        SetRendererListVisible(_faceVisorRenderers, visible);
        SetMaterialSlotsVisible(_faceVisorMaterialSlots, visible);
        MarkCustomForm(markCustom);
    }

    private void SetLightWingsVisible(bool visible, bool markCustom)
    {
        SetSmallLightBladeVisible(visible, false);
        SetLargeLightWingsVisible(visible, false);
        MarkCustomForm(markCustom);
    }

    private void SetWingBonesVisible(bool visible, bool markCustom)
    {
        SetSmallWingBonesVisible(visible, false);
        SetLargeWingBonesVisible(visible, false);
        MarkCustomForm(markCustom);
    }

    private void SetSmallLightBladeVisible(bool visible, bool markCustom)
    {
        _smallLightBladeVisible = visible;
        SetRendererListVisible(_smallLightWingRenderers, visible);
        SetMaterialSlotsVisible(_smallLightWingMaterialSlots, visible);
        MarkCustomForm(markCustom);
    }

    private void SetLargeLightWingsVisible(bool visible, bool markCustom)
    {
        _largeLightWingsVisible = visible;
        SetRendererListVisible(_largeLightWingRenderers, visible);
        SetMaterialSlotsVisible(_largeLightWingMaterialSlots, visible);
        MarkCustomForm(markCustom);
    }

    private void SetSmallWingBonesVisible(bool visible, bool markCustom)
    {
        _smallWingBonesVisible = visible;
        SetRendererListVisible(_smallWingBoneRenderers, visible);
        SetMaterialSlotsVisible(_smallWingBoneMaterialSlots, visible);
        MarkCustomForm(markCustom);
    }

    private void SetLargeWingBonesVisible(bool visible, bool markCustom)
    {
        _largeWingBonesVisible = visible;
        SetRendererListVisible(_largeWingBoneRenderers, visible);
        SetMaterialSlotsVisible(_largeWingBoneMaterialSlots, visible);
        MarkCustomForm(markCustom);
    }

    private void MarkCustomForm(bool markCustom)
    {
        if (markCustom && !_applyingForm)
        {
            _currentForm = PetForm.Custom;
        }
    }

    private static bool CanToggleRendererAsAccessory(Renderer renderer)
    {
        if (renderer == null)
        {
            return false;
        }

        Material[] materials = renderer.sharedMaterials;
        if (renderer is SkinnedMeshRenderer && materials != null && materials.Length > 6)
        {
            return false;
        }

        return true;
    }

    private static string BuildSearchableRendererName(Renderer renderer)
    {
        string value = renderer.name;
        Material[] materials = renderer.sharedMaterials;
        for (int i = 0; i < materials.Length; i++)
        {
            if (materials[i] != null)
            {
                value += " " + materials[i].name;
            }
        }

        return value;
    }

    private static bool ContainsHeadGlassesToken(string value)
    {
        return Contains(value, "\u93e1\u6846") ||
            Contains(value, "\u93e1\u7247") ||
            Contains(value, "\u773c\u93e1") ||
            Contains(value, "\u773c\u955c") ||
            Contains(value, "head_glasses") ||
            Contains(value, "headglasses");
    }

    private static bool ContainsFaceVisorToken(string value)
    {
        return Contains(value, "\u76ee\u93e1") ||
            Contains(value, "\u76ee\u955c") ||
            Contains(value, "\u8b77\u76ee\u93e1") ||
            Contains(value, "\u62a4\u76ee\u955c") ||
            Contains(value, "visor") ||
            Contains(value, "goggle");
    }

    private static bool ContainsLightWingToken(string value)
    {
        return Contains(value, "\u5149\u7ffc") ||
            Contains(value, "glow_wing") ||
            Contains(value, "lightwing") ||
            Contains(value, "light_wing");
    }

    private static bool ContainsSmallLightWingToken(string value)
    {
        return Contains(value, "\u5c0f\u5149\u7ffc") ||
            Contains(value, "small_lightwing") ||
            Contains(value, "small_light_wing");
    }

    private static bool ContainsWingBoneToken(string value)
    {
        return Contains(value, "\u7ffc\u9aa8") ||
            Contains(value, "wingbone") ||
            Contains(value, "wing_bone");
    }

    private static bool ContainsSmallWingBoneToken(string value)
    {
        return Contains(value, "\u5c0f\u7ffc\u9aa8") ||
            Contains(value, "small_wingbone") ||
            Contains(value, "small_wing_bone");
    }

    private static bool ContainsWingToken(string value)
    {
        return Contains(value, "\u7ffc") ||
            Contains(value, "\u5149\u7ffc") ||
            Contains(value, "wing");
    }

    private static bool Contains(string value, string token)
    {
        return value != null && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void SetRendererListVisible(List<Renderer> renderers, bool visible)
    {
        for (int i = 0; i < renderers.Count; i++)
        {
            if (renderers[i] != null)
            {
                renderers[i].enabled = visible;
            }
        }
    }

    private void SetMaterialSlotsVisible(List<AccessoryMaterialSlot> slots, bool visible)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            AccessoryMaterialSlot slot = slots[i];
            if (slot.Renderer == null || slot.MaterialIndex < 0)
            {
                continue;
            }

            Material[] materials = slot.Renderer.sharedMaterials;
            if (slot.MaterialIndex >= materials.Length)
            {
                continue;
            }

            materials[slot.MaterialIndex] = visible ? slot.OriginalMaterial : GetHiddenMaterial();
            slot.Renderer.sharedMaterials = materials;
        }
    }

    private Material GetHiddenMaterial()
    {
        if (_hiddenMaterial != null)
        {
            return _hiddenMaterial;
        }

        Shader shader = Shader.Find("DesktopPet/SilverWolfSimpleToon");
        if (shader == null)
        {
            shader = Shader.Find("Unlit/Transparent");
        }
        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }

        if (shader == null)
        {
            shader = Shader.Find("Standard");
        }

        _hiddenMaterial = new Material(shader)
        {
            name = "TransparentPetHiddenAccessory"
        };
        if (_hiddenMaterial.HasProperty("_Color"))
        {
            _hiddenMaterial.SetColor("_Color", new Color(0f, 0f, 0f, 0f));
        }

        if (_hiddenMaterial.HasProperty("_Alpha"))
        {
            _hiddenMaterial.SetFloat("_Alpha", 0f);
        }

        _hiddenMaterial.renderQueue = 3000;
        return _hiddenMaterial;
    }

    private void ApplyMaterialPreset(Material material, string presetName)
    {
        switch (presetName)
        {
            case "CelAnime":
                SetFloatIfExists(material, "_BaseColor_Step", 0.72f);
                SetFloatIfExists(material, "_BaseShade_Feather", 0.025f);
                SetFloatIfExists(material, "_1st_ShadeColor_Feather", 0.025f);
                SetFloatIfExists(material, "_Outline_Width", 0.008f);
                break;
            case "FlatLive2DLike":
                SetFloatIfExists(material, "_BaseColor_Step", 0.92f);
                SetFloatIfExists(material, "_BaseShade_Feather", 0.18f);
                SetFloatIfExists(material, "_1st_ShadeColor_Feather", 0.16f);
                SetFloatIfExists(material, "_Outline_Width", 0.004f);
                break;
            default:
                RestoreMaterialDefaults(material);
                break;
        }
    }

    private static void SetFloatIfExists(Material material, string propertyName, float value)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetFloat(propertyName, value);
        }
    }

    private void CaptureMaterialDefaults(Material material)
    {
        if (material == null || _materialDefaults.ContainsKey(material))
        {
            return;
        }

        _materialDefaults.Add(material, new MaterialDefaults
        {
            HasBaseColorStep = material.HasProperty("_BaseColor_Step"),
            BaseColorStep = GetFloatOrDefault(material, "_BaseColor_Step"),
            HasBaseShadeFeather = material.HasProperty("_BaseShade_Feather"),
            BaseShadeFeather = GetFloatOrDefault(material, "_BaseShade_Feather"),
            HasFirstShadeFeather = material.HasProperty("_1st_ShadeColor_Feather"),
            FirstShadeFeather = GetFloatOrDefault(material, "_1st_ShadeColor_Feather"),
            HasOutlineWidth = material.HasProperty("_Outline_Width"),
            OutlineWidth = GetFloatOrDefault(material, "_Outline_Width")
        });
    }

    private static float GetFloatOrDefault(Material material, string propertyName)
    {
        return material.HasProperty(propertyName) ? material.GetFloat(propertyName) : 0f;
    }

    private void RestoreMaterialDefaults(Material material)
    {
        if (material == null || !_materialDefaults.TryGetValue(material, out MaterialDefaults defaults))
        {
            return;
        }

        if (defaults.HasBaseColorStep)
        {
            material.SetFloat("_BaseColor_Step", defaults.BaseColorStep);
        }

        if (defaults.HasBaseShadeFeather)
        {
            material.SetFloat("_BaseShade_Feather", defaults.BaseShadeFeather);
        }

        if (defaults.HasFirstShadeFeather)
        {
            material.SetFloat("_1st_ShadeColor_Feather", defaults.FirstShadeFeather);
        }

        if (defaults.HasOutlineWidth)
        {
            material.SetFloat("_Outline_Width", defaults.OutlineWidth);
        }
    }

    private struct MaterialDefaults
    {
        public bool HasBaseColorStep;
        public float BaseColorStep;
        public bool HasBaseShadeFeather;
        public float BaseShadeFeather;
        public bool HasFirstShadeFeather;
        public float FirstShadeFeather;
        public bool HasOutlineWidth;
        public float OutlineWidth;
    }

    private readonly struct AccessoryMaterialSlot
    {
        public readonly Renderer Renderer;
        public readonly int MaterialIndex;
        public readonly Material OriginalMaterial;

        public AccessoryMaterialSlot(Renderer renderer, int materialIndex, Material originalMaterial)
        {
            Renderer = renderer;
            MaterialIndex = materialIndex;
            OriginalMaterial = originalMaterial;
        }
    }
}
