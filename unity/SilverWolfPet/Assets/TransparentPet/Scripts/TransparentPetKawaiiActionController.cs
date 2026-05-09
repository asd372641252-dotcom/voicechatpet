using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class TransparentPetKawaiiActionController : MonoBehaviour
{
    public struct ActionMenuEntry
    {
        public string Name;
        public string DisplayName;
        public string PrimaryCategory;
        public string FilePath;
    }

    public Transform modelRoot;
    public string actionBundleDirectory = "GodotFinal/assets/action_import/kawaii100";
    public string actionManifestFile = "KawaiiUnity/official_actions.txt";
    public string defaultActionName = "KA_Idle01_breathing";
    public bool autoPlay = true;
    public bool onlyIdleActions = true;
    public bool useAnimatorController = true;
    public bool randomAutoSwitch = true;
    public bool useProductRandomActionWhitelist = true;
    public string randomActionWhitelist =
        "KA_Idle02_LookLeftAndRight,KA_Idle03_LookAtHands,KA_Idle04_LookAtFeet,KA_Idle05_Stretch," +
        "KA_Idle08_ComeUpWithAnIdea,KA_Idle09_Waiting,KA_Idle11_LookingBack,KA_Idle12_LeaningForward," +
        "KA_Idle16_WaveHands,KA_Idle18_Shy,KA_Idle19_ShyRefusal,KA_Idle25_Cheers,KA_Idle27_Angry," +
        "KA_Idle28_Laugh,KA_Idle29_Surprised,KA_Idle35_FingerSnap,KA_Idle36_Yay,KA_Idle37_Tsundere," +
        "KA_Idle39_CuteArmUp,KA_Idle41_CuteShyPose,KA_Idle43_HandOnHip,KA_Idle44_GreetingBow," +
        "KA_Idle45_WaveHandSlightly,KA_Idle50_StandingTalk1_1,KA_Idle51_StandingTalk1_2,KA_Idle52_Curtsy";
    public bool menuOnly = true;
    public bool applyBundleBoneRotations;
    public bool applyHandFingerRotations;
    public bool allowAnimatorToMoveModelRoot;
    public string idleActionName = "KA_Idle01_breathing";
    public float randomActionIntervalSeconds = 8f;
    public float transitionSeconds = 0.62f;
    public float playbackSpeed = 1f;
    public string rotationConversion = "current";

    private readonly List<ActionMenuEntry> _entries = new List<ActionMenuEntry>();
    private readonly Dictionary<string, float> _animatorClipLengths = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Transform> _transformByName = new Dictionary<string, Transform>(StringComparer.Ordinal);
    private readonly Dictionary<string, Transform> _transformByNormalizedName = new Dictionary<string, Transform>(StringComparer.Ordinal);
    private readonly Dictionary<Transform, Quaternion> _runtimeRestRotations = new Dictionary<Transform, Quaternion>();
    private readonly Dictionary<Transform, Quaternion> _transitionFromRotations = new Dictionary<Transform, Quaternion>();
    private Animator _animator;
    private ActionClip _clip;
    private float _elapsedSeconds;
    private float _transitionElapsed;
    private float _idleElapsedSeconds;
    private float _animatorActionElapsedSeconds;
    private float _animatorActionDurationSeconds;
    private bool _animatorActionIsIdle = true;
    private bool _playing;
    private string _currentActionName = string.Empty;
    private bool _hasModelRootRestTransform;
    private Vector3 _modelRootRestLocalPosition;
    private Quaternion _modelRootRestLocalRotation;
    private Vector3 _modelRootRestLocalScale;

    private static readonly int[] ExcludedIdleActionNumbers = { 7, 13, 30, 33, 34, 57, 58 };
    private static readonly string[] CategoryOrder =
    {
        "\u5168\u90e8",
        "\u57fa\u7840\u5f85\u673a",
        "\u8bed\u97f3\u72b6\u6001",
        "\u60c5\u7eea\u53cd\u5e94",
        "\u5de5\u4f5c\u72b6\u6001",
        "\u7528\u6237\u4ea4\u4e92",
        "\u684c\u5ba0\u59ff\u6001",
        "\u59ff\u6001\u8fc7\u6e21",
        "\u7279\u6b8a\u5c55\u793a",
        "\u5176\u4ed6",
    };

    public string CurrentActionName => _currentActionName;
    public bool IsPlaying => _playing;

    private void Awake()
    {
        if (modelRoot == null)
        {
            modelRoot = transform;
        }

        BuildTransformLookup();
        CaptureModelRootRestTransform();
        BuildAnimatorClipLookup();
        LoadActionEntries();
        if (!autoPlay)
        {
            _currentActionName = HasAction(defaultActionName) ? defaultActionName : (_entries.Count > 0 ? _entries[0].Name : string.Empty);
        }

        if (autoPlay)
        {
            PlayAction(defaultActionName);
        }
    }

    private void LateUpdate()
    {
        if (useAnimatorController && _clip == null)
        {
            UpdateAnimatorSchedule();
            RestoreModelRootTransform();
            return;
        }

        if (!_playing || _clip == null || _clip.Bones.Count == 0)
        {
            RestoreModelRootTransform();
            return;
        }

        _elapsedSeconds += Time.unscaledDeltaTime * Mathf.Max(0.01f, playbackSpeed);
        _transitionElapsed += Time.unscaledDeltaTime;
        if (_elapsedSeconds > _clip.LengthSeconds)
        {
            if (_clip.Loop)
            {
                _elapsedSeconds %= Mathf.Max(0.001f, _clip.LengthSeconds);
                _transitionElapsed = transitionSeconds;
            }
            else
            {
                _elapsedSeconds = _clip.LengthSeconds;
                _playing = false;
            }
        }

        ApplyClipAtTime(_elapsedSeconds);
        RestoreModelRootTransform();
    }

    public ActionMenuEntry[] GetActionEntries()
    {
        return _entries.ToArray();
    }

    public string[] GetCategoryOrder()
    {
        return CategoryOrder;
    }

    public void TogglePlayback()
    {
        _playing = !_playing;
        if (useAnimatorController && _animator != null)
        {
            _animator.speed = _playing ? Mathf.Max(0.01f, playbackSpeed) : 0f;
        }
    }

    public bool PlayAction(string actionName)
    {
        if (string.IsNullOrWhiteSpace(actionName))
        {
            actionName = defaultActionName;
        }

        ActionMenuEntry entry = default;
        bool found = false;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Name, actionName, StringComparison.OrdinalIgnoreCase))
            {
                entry = _entries[i];
                found = true;
                break;
            }
        }

        if (!found)
        {
            return false;
        }

        if (string.Equals(_currentActionName, entry.Name, StringComparison.OrdinalIgnoreCase) &&
            _playing &&
            (useAnimatorController || _clip == null))
        {
            return true;
        }

        _currentActionName = entry.Name;
        _elapsedSeconds = 0f;
        _transitionElapsed = 0f;
        _playing = true;

        if (useAnimatorController && TryPlayAnimatorAction(entry.Name))
        {
            return true;
        }

        if (menuOnly || !applyBundleBoneRotations)
        {
            _clip = null;
            return true;
        }

        ActionClip loaded = LoadActionClip(entry.FilePath);
        if (loaded == null || loaded.Bones.Count == 0)
        {
            _clip = null;
            _playing = false;
            Debug.LogWarning($"Kawaii action could not be loaded or matched: {entry.Name} ({entry.FilePath})");
            return false;
        }

        CaptureTransitionStart();
        _clip = loaded;
        ApplyClipAtTime(0f);
        Debug.Log($"Kawaii action loaded: {entry.Name}, bones={loaded.Bones.Count}");
        return true;
    }

    public void RebindModelRoot(Transform nextModelRoot)
    {
        RuntimeAnimatorController previousController = _animator != null ? _animator.runtimeAnimatorController : null;
        string actionToReplay = string.IsNullOrWhiteSpace(_currentActionName) ? defaultActionName : _currentActionName;
        bool shouldReplay = _playing || autoPlay;

        modelRoot = nextModelRoot != null ? nextModelRoot : transform;
        Animator nextAnimator = modelRoot != null ? modelRoot.GetComponentInChildren<Animator>(true) : null;
        if (nextAnimator != null)
        {
            if (nextAnimator.runtimeAnimatorController == null && previousController != null)
            {
                nextAnimator.runtimeAnimatorController = previousController;
            }

            nextAnimator.applyRootMotion = false;
        }

        _animator = nextAnimator;
        BuildTransformLookup();
        CaptureModelRootRestTransform();
        BuildAnimatorClipLookup();

        _clip = null;
        _playing = false;
        _currentActionName = string.Empty;
        if (shouldReplay)
        {
            PlayAction(actionToReplay);
        }
    }

    private bool TryPlayAnimatorAction(string actionName)
    {
        if (_animator == null)
        {
            _animator = modelRoot != null ? modelRoot.GetComponentInChildren<Animator>(true) : GetComponentInChildren<Animator>(true);
            BuildAnimatorClipLookup();
        }

        if (_animator == null || _animator.runtimeAnimatorController == null)
        {
            return false;
        }

        _animator.applyRootMotion = false;
        _clip = null;
        _animator.speed = Mathf.Max(0.01f, playbackSpeed);
        _animatorActionElapsedSeconds = 0f;
        _animatorActionDurationSeconds = AnimatorActionDuration(actionName);
        _animatorActionIsIdle = IsIdleAction(actionName);
        if (_animatorActionIsIdle)
        {
            _idleElapsedSeconds = 0f;
        }

        _animator.CrossFadeInFixedTime(actionName, Mathf.Clamp(transitionSeconds, 0.18f, 1.0f), 0, 0f);
        RestoreModelRootTransform();
        Debug.Log($"Kawaii animator action: {actionName}");
        return true;
    }

    private void OnAnimatorMove()
    {
        RestoreModelRootTransform();
    }

    private void UpdateAnimatorSchedule()
    {
        if (!_playing || _animator == null || _animator.runtimeAnimatorController == null)
        {
            return;
        }

        float delta = Time.unscaledDeltaTime * Mathf.Max(0.01f, playbackSpeed);
        if (_animatorActionIsIdle)
        {
            if (!randomAutoSwitch || _entries.Count == 0)
            {
                return;
            }

            _idleElapsedSeconds += delta;
            if (_idleElapsedSeconds >= Mathf.Max(0.1f, randomActionIntervalSeconds))
            {
                PlayRandomAnimatorAction();
            }

            return;
        }

        _animatorActionElapsedSeconds += delta;
        if (_animatorActionElapsedSeconds >= Mathf.Max(0.1f, _animatorActionDurationSeconds))
        {
            PlayAction(IdleActionName());
        }
    }

    private void PlayRandomAnimatorAction()
    {
        List<ActionMenuEntry> candidates = new List<ActionMenuEntry>();
        string idle = IdleActionName();
        for (int i = 0; i < _entries.Count; i++)
        {
            string actionName = _entries[i].Name;
            if (string.Equals(actionName, idle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionName, _currentActionName, StringComparison.OrdinalIgnoreCase) ||
                !IsRandomActionAllowedForProduct(actionName) ||
                !_animatorClipLengths.ContainsKey(actionName))
            {
                continue;
            }

            candidates.Add(_entries[i]);
        }

        if (candidates.Count == 0)
        {
            _idleElapsedSeconds = 0f;
            return;
        }

        int index = UnityEngine.Random.Range(0, candidates.Count);
        PlayAction(candidates[index].Name);
    }

    private float AnimatorActionDuration(string actionName)
    {
        return _animatorClipLengths.TryGetValue(actionName, out float length)
            ? Mathf.Max(0.1f, length)
            : 1f;
    }

    private bool IsIdleAction(string actionName)
    {
        return string.Equals(actionName, IdleActionName(), StringComparison.OrdinalIgnoreCase);
    }

    public bool IsRandomActionAllowedForProduct(string actionName)
    {
        if (!useProductRandomActionWhitelist)
        {
            return true;
        }

        string normalizedAction = NormalizeActionId(actionName);
        if (string.IsNullOrEmpty(normalizedAction))
        {
            return false;
        }

        string[] allowed = SplitCsv(randomActionWhitelist);
        if (allowed.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < allowed.Length; i++)
        {
            if (string.Equals(NormalizeActionId(allowed[i]), normalizedAction, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string IdleActionName()
    {
        return string.IsNullOrWhiteSpace(idleActionName) ? defaultActionName : idleActionName;
    }

    public string CategoryForAction(string actionName)
    {
        string[] categories = CategoriesForAction(actionName);
        return categories.Length > 0 ? categories[0] : "\u5176\u4ed6";
    }

    public string DisplayNameForAction(string actionName)
    {
        string display = actionName ?? string.Empty;
        display = display.Replace("KA_", string.Empty).Replace("_action_bundle", string.Empty);
        foreach (KeyValuePair<string, string> pair in DisplayReplacements)
        {
            display = display.Replace(pair.Key, pair.Value);
        }

        return display.Replace("_", " ").Trim();
    }

    public bool ActionMatchesCategory(string actionName, string category)
    {
        if (string.IsNullOrEmpty(category) || category == "\u5168\u90e8")
        {
            return true;
        }

        string[] categories = CategoriesForAction(actionName);
        for (int i = 0; i < categories.Length; i++)
        {
            if (categories[i] == category)
            {
                return true;
            }
        }

        return false;
    }

    private void BuildTransformLookup()
    {
        _transformByName.Clear();
        _transformByNormalizedName.Clear();
        _runtimeRestRotations.Clear();
        _animator = modelRoot.GetComponentInChildren<Animator>(true);
        if (_animator != null)
        {
            _animator.applyRootMotion = false;
        }

        Transform[] transforms = modelRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform transform = transforms[i];
            if (!_transformByName.ContainsKey(transform.name))
            {
                _transformByName.Add(transform.name, transform);
            }

            string normalized = NormalizeBoneName(transform.name);
            if (!string.IsNullOrEmpty(normalized) && !_transformByNormalizedName.ContainsKey(normalized))
            {
                _transformByNormalizedName.Add(normalized, transform);
            }

            _runtimeRestRotations[transform] = transform.localRotation;
        }
    }

    private void CaptureModelRootRestTransform()
    {
        if (modelRoot == null)
        {
            _hasModelRootRestTransform = false;
            return;
        }

        _modelRootRestLocalPosition = modelRoot.localPosition;
        _modelRootRestLocalRotation = modelRoot.localRotation;
        _modelRootRestLocalScale = modelRoot.localScale;
        _hasModelRootRestTransform = true;
    }

    private void RestoreModelRootTransform()
    {
        if (allowAnimatorToMoveModelRoot || !_hasModelRootRestTransform || modelRoot == null)
        {
            return;
        }

        modelRoot.localPosition = _modelRootRestLocalPosition;
        modelRoot.localRotation = _modelRootRestLocalRotation;
        modelRoot.localScale = _modelRootRestLocalScale;
    }

    private void BuildAnimatorClipLookup()
    {
        _animatorClipLengths.Clear();
        if (_animator == null || _animator.runtimeAnimatorController == null)
        {
            return;
        }

        AnimationClip[] clips = _animator.runtimeAnimatorController.animationClips;
        for (int i = 0; i < clips.Length; i++)
        {
            AnimationClip clip = clips[i];
            if (clip == null || string.IsNullOrEmpty(clip.name))
            {
                continue;
            }

            string actionName = clip.name.StartsWith("@", StringComparison.Ordinal) ? clip.name.Substring(1) : clip.name;
            _animatorClipLengths[actionName] = clip.length;
        }

        Debug.Log($"Kawaii animator clips indexed: {_animatorClipLengths.Count}");
    }

    private void LoadActionEntries()
    {
        _entries.Clear();
        if (!string.IsNullOrWhiteSpace(actionManifestFile))
        {
            LoadActionEntriesFromManifest(ResolveStreamingPath(actionManifestFile));
        }

        if (_entries.Count > 0 || string.IsNullOrWhiteSpace(actionBundleDirectory))
        {
            return;
        }

        string directory = ResolveStreamingPath(actionBundleDirectory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        string[] files = Directory.GetFiles(directory, "*_action_bundle.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Length; i++)
        {
            string name = Path.GetFileNameWithoutExtension(files[i]).Replace("_action_bundle", string.Empty);
            AddActionEntry(name, files[i]);
        }
    }

    private void LoadActionEntriesFromManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return;
        }

        string[] lines = File.ReadAllLines(manifestPath, Encoding.UTF8);
        for (int i = 0; i < lines.Length; i++)
        {
            string assetPath = lines[i].Trim().TrimStart('\uFEFF');
            if (assetPath.Length == 0 || assetPath.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string actionName = ActionNameFromAssetPath(assetPath);
            AddActionEntry(actionName, ResolveActionFilePath(actionName, assetPath));
        }
    }

    private string ResolveActionFilePath(string actionName, string manifestPath)
    {
        if (!string.IsNullOrWhiteSpace(actionBundleDirectory))
        {
            string directory = actionBundleDirectory.TrimEnd('/', '\\');
            string bundlePath = ResolveStreamingPath(directory + "/" + actionName + "_action_bundle.json");
            if (File.Exists(bundlePath))
            {
                return bundlePath;
            }
        }

        if (!string.IsNullOrWhiteSpace(manifestPath) && File.Exists(manifestPath))
        {
            return manifestPath;
        }

        string streamingPath = ResolveStreamingPath(manifestPath ?? string.Empty);
        return File.Exists(streamingPath) ? streamingPath : (manifestPath ?? string.Empty);
    }

    private void AddActionEntry(string actionName, string filePath)
    {
        if (string.IsNullOrWhiteSpace(actionName) || !IsActionAllowed(actionName))
        {
            return;
        }

        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Name, actionName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        _entries.Add(new ActionMenuEntry
        {
            Name = actionName,
            DisplayName = DisplayNameForAction(actionName),
            PrimaryCategory = CategoryForAction(actionName),
            FilePath = filePath
        });
    }

    private static string ActionNameFromAssetPath(string assetPath)
    {
        string normalized = (assetPath ?? string.Empty).Replace('\\', '/');
        int slashIndex = normalized.LastIndexOf('/');
        string fileName = slashIndex >= 0 ? normalized.Substring(slashIndex + 1) : normalized;
        int extensionIndex = fileName.LastIndexOf('.');
        if (extensionIndex > 0)
        {
            fileName = fileName.Substring(0, extensionIndex);
        }

        return fileName.StartsWith("@", StringComparison.Ordinal) ? fileName.Substring(1) : fileName;
    }

    private bool HasAction(string actionName)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Name, actionName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private ActionClip LoadActionClip(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        string json = File.ReadAllText(filePath, Encoding.UTF8);
        object parsed = MiniJson.Parse(json);
        if (!(parsed is Dictionary<string, object> data))
        {
            return null;
        }

        ActionClip clip = new ActionClip
        {
            Name = GetString(data, "action_name", Path.GetFileNameWithoutExtension(filePath).Replace("_action_bundle", string.Empty)),
            LengthSeconds = Mathf.Max(0.001f, GetFloat(data, "length_sec", 1f)),
            SampleRate = Mathf.Max(1f, GetFloat(data, "sample_rate", 30f)),
            FrameCount = Mathf.Max(1, GetInt(data, "frame_count", 1)),
            Loop = GetBool(data, "loop", true)
        };

        Dictionary<string, Quaternion> importedRestRotations = ParseImportedRestRotations(data);
        if (!data.TryGetValue("actions", out object actionsObj) ||
            !(actionsObj is List<object> actions) ||
            actions.Count == 0 ||
            !(actions[0] is Dictionary<string, object> action) ||
            !action.TryGetValue("bones", out object bonesObj) ||
            !(bonesObj is List<object> bones))
        {
            return null;
        }

        for (int i = 0; i < bones.Count; i++)
        {
            if (!(bones[i] is Dictionary<string, object> boneData))
            {
                continue;
            }

            if (!boneData.TryGetValue("local_rotations", out object rotationsObj) ||
                !(rotationsObj is List<object> rotationValues))
            {
                continue;
            }

            Quaternion[] rotations = ParseQuaternionFrames(rotationValues);
            if (rotations.Length == 0)
            {
                continue;
            }

            string importedName = GetString(boneData, "name", string.Empty);
            Transform transform = ResolveTransform(importedName);
            if (transform == null)
            {
                continue;
            }

            if (!importedRestRotations.TryGetValue(importedName, out Quaternion importedRest))
            {
                importedRest = rotations[0];
            }

            clip.Bones.Add(new ActionBone
            {
                ImportedName = importedName,
                Transform = transform,
                ImportedRestRotation = importedRest,
                RuntimeRestRotation = _runtimeRestRotations.TryGetValue(transform, out Quaternion runtimeRest) ? runtimeRest : transform.localRotation,
                Rotations = rotations
            });
        }

        return clip;
    }

    private Dictionary<string, Quaternion> ParseImportedRestRotations(Dictionary<string, object> data)
    {
        Dictionary<string, Quaternion> output = new Dictionary<string, Quaternion>(StringComparer.Ordinal);
        if (!data.TryGetValue("rest_pose", out object restPoseObj) ||
            !(restPoseObj is Dictionary<string, object> restPose) ||
            !restPose.TryGetValue("bones", out object bonesObj) ||
            !(bonesObj is List<object> bones))
        {
            return output;
        }

        for (int i = 0; i < bones.Count; i++)
        {
            if (!(bones[i] is Dictionary<string, object> boneData))
            {
                continue;
            }

            string name = GetString(boneData, "name", string.Empty);
            if (string.IsNullOrEmpty(name) ||
                !boneData.TryGetValue("local_rotation", out object rotationObj) ||
                !(rotationObj is List<object> rotationArray))
            {
                continue;
            }

            output[name] = ParseQuaternion(rotationArray);
        }

        return output;
    }

    private void CaptureTransitionStart()
    {
        _transitionFromRotations.Clear();
        foreach (KeyValuePair<Transform, Quaternion> pair in _runtimeRestRotations)
        {
            if (pair.Key != null)
            {
                _transitionFromRotations[pair.Key] = pair.Key.localRotation;
            }
        }
    }

    private void ApplyClipAtTime(float timeSeconds)
    {
        if (_clip == null)
        {
            return;
        }

        float rawFrame = Mathf.Clamp(timeSeconds * _clip.SampleRate, 0f, Mathf.Max(0f, _clip.FrameCount - 1f));
        int frameA = Mathf.FloorToInt(rawFrame);
        int frameB = Mathf.Min(frameA + 1, _clip.FrameCount - 1);
        float frameWeight = rawFrame - frameA;
        float transitionWeight = transitionSeconds <= 0.001f ? 1f : Mathf.Clamp01(_transitionElapsed / transitionSeconds);
        transitionWeight = transitionWeight * transitionWeight * (3f - 2f * transitionWeight);

        for (int i = 0; i < _clip.Bones.Count; i++)
        {
            ActionBone bone = _clip.Bones[i];
            if (bone.Transform == null || bone.Rotations.Length == 0)
            {
                continue;
            }

            Quaternion poseA = bone.Rotations[Mathf.Min(frameA, bone.Rotations.Length - 1)];
            Quaternion poseB = bone.Rotations[Mathf.Min(frameB, bone.Rotations.Length - 1)];
            Quaternion importedPose = Quaternion.Slerp(poseA, poseB, frameWeight);
            Quaternion delta = Quaternion.Inverse(ConvertImportedRotation(bone.ImportedRestRotation)) * ConvertImportedRotation(importedPose);
            Quaternion target = NormalizeQuaternion(bone.RuntimeRestRotation * delta);

            if (transitionWeight < 1f && _transitionFromRotations.TryGetValue(bone.Transform, out Quaternion from))
            {
                target = Quaternion.Slerp(from, target, transitionWeight);
            }

            bone.Transform.localRotation = target;
        }
    }

    private Transform ResolveTransform(string importedName)
    {
        if (string.IsNullOrEmpty(importedName))
        {
            return null;
        }

        if (!applyHandFingerRotations && IsMmdHandBone(importedName))
        {
            return null;
        }

        if (_transformByName.TryGetValue(importedName, out Transform direct))
        {
            return direct;
        }

        Transform humanoid = ResolveHumanoidTransform(importedName);
        if (humanoid != null)
        {
            return humanoid;
        }

        string normalized = NormalizeBoneName(importedName);
        return !string.IsNullOrEmpty(normalized) && _transformByNormalizedName.TryGetValue(normalized, out Transform normalizedTransform)
            ? normalizedTransform
            : null;
    }

    private Transform ResolveHumanoidTransform(string importedName)
    {
        if (_animator == null || !_animator.isHuman || !TryMapMmdBone(importedName, out HumanBodyBones bone))
        {
            return null;
        }

        return _animator.GetBoneTransform(bone);
    }

    private static bool TryMapMmdBone(string importedName, out HumanBodyBones bone)
    {
        bone = HumanBodyBones.LastBone;
        string name = NormalizeMmdBoneName(importedName);
        switch (name)
        {
            case "腰":
                bone = HumanBodyBones.Hips;
                return true;
            case "上半身":
                bone = HumanBodyBones.Chest;
                return true;
            case "上半身2":
                bone = HumanBodyBones.UpperChest;
                return true;
            case "首":
                bone = HumanBodyBones.Neck;
                return true;
            case "頭":
                bone = HumanBodyBones.Head;
                return true;
        }

        bool left = name.EndsWith(".L", StringComparison.Ordinal);
        bool right = name.EndsWith(".R", StringComparison.Ordinal);
        if (!left && !right)
        {
            return false;
        }

        string stem = name.Substring(0, name.Length - 2);
        switch (stem)
        {
            case "肩":
                bone = left ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder;
                return true;
            case "腕":
                bone = left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm;
                return true;
            case "ひじ":
                bone = left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm;
                return true;
            case "手首":
                bone = left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
                return true;
            case "足D":
                bone = left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg;
                return true;
            case "ひざD":
                bone = left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg;
                return true;
            case "足首D":
                bone = left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot;
                return true;
            case "足先EX":
                bone = left ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes;
                return true;
            case "中指1":
                bone = left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal;
                return true;
            case "中指2":
                bone = left ? HumanBodyBones.LeftMiddleIntermediate : HumanBodyBones.RightMiddleIntermediate;
                return true;
            case "中指3":
                bone = left ? HumanBodyBones.LeftMiddleDistal : HumanBodyBones.RightMiddleDistal;
                return true;
            case "人指1":
                bone = left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal;
                return true;
            case "人指2":
                bone = left ? HumanBodyBones.LeftIndexIntermediate : HumanBodyBones.RightIndexIntermediate;
                return true;
            case "人指3":
                bone = left ? HumanBodyBones.LeftIndexDistal : HumanBodyBones.RightIndexDistal;
                return true;
            case "小指1":
                bone = left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal;
                return true;
            case "小指2":
                bone = left ? HumanBodyBones.LeftLittleIntermediate : HumanBodyBones.RightLittleIntermediate;
                return true;
            case "小指3":
                bone = left ? HumanBodyBones.LeftLittleDistal : HumanBodyBones.RightLittleDistal;
                return true;
            case "薬指1":
                bone = left ? HumanBodyBones.LeftRingProximal : HumanBodyBones.RightRingProximal;
                return true;
            case "薬指2":
                bone = left ? HumanBodyBones.LeftRingIntermediate : HumanBodyBones.RightRingIntermediate;
                return true;
            case "薬指3":
                bone = left ? HumanBodyBones.LeftRingDistal : HumanBodyBones.RightRingDistal;
                return true;
            case "親指0":
                bone = left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal;
                return true;
            case "親指1":
                bone = left ? HumanBodyBones.LeftThumbIntermediate : HumanBodyBones.RightThumbIntermediate;
                return true;
            case "親指2":
                bone = left ? HumanBodyBones.LeftThumbDistal : HumanBodyBones.RightThumbDistal;
                return true;
        }

        return false;
    }

    private Quaternion ConvertImportedRotation(Quaternion rotation)
    {
        switch (rotationConversion)
        {
            case "raw":
                return NormalizeQuaternion(rotation);
            case "handed_z":
                return NormalizeQuaternion(new Quaternion(-rotation.x, -rotation.y, rotation.z, rotation.w));
            case "handed_y":
                return NormalizeQuaternion(new Quaternion(-rotation.x, rotation.y, -rotation.z, rotation.w));
            default:
                return NormalizeQuaternion(new Quaternion(rotation.x, -rotation.y, -rotation.z, rotation.w));
        }
    }

    private bool IsActionAllowed(string actionName)
    {
        if (!onlyIdleActions)
        {
            return true;
        }

        int number = IdleActionNumber(actionName);
        if (number < 0)
        {
            return false;
        }

        for (int i = 0; i < ExcludedIdleActionNumbers.Length; i++)
        {
            if (ExcludedIdleActionNumbers[i] == number)
            {
                return false;
            }
        }

        return true;
    }

    private string[] CategoriesForAction(string actionName)
    {
        List<string> categories = new List<string>();
        int idleNumber = IdleActionNumber(actionName);
        AddIf(categories, idleNumber, new[] { 1, 3, 4, 5, 9, 10, 11, 12, 15, 40, 46, 53 }, "\u57fa\u7840\u5f85\u673a");
        AddIf(categories, idleNumber, new[] { 2, 8, 27, 28, 29, 42, 50, 51 }, "\u8bed\u97f3\u72b6\u6001");
        AddIf(categories, idleNumber, new[] { 18, 19, 25, 26, 27, 28, 29, 36, 37, 38, 41, 42, 43, 47 }, "\u60c5\u7eea\u53cd\u5e94");
        AddIf(categories, idleNumber, new[] { 2, 3, 8, 9, 19, 35, 36 }, "\u5de5\u4f5c\u72b6\u6001");
        AddIf(categories, idleNumber, new[] { 2, 12, 21, 22, 39, 43, 44, 45, 52, 61, 62 }, "\u7528\u6237\u4ea4\u4e92");
        AddIf(categories, idleNumber, new[] { 1, 10, 40, 46, 53 }, "\u684c\u5ba0\u59ff\u6001");
        AddIf(categories, idleNumber, new[] { 54 }, "\u59ff\u6001\u8fc7\u6e21");
        AddIf(categories, idleNumber, new[] { 14, 23, 24, 35, 36, 41, 43, 54, 55, 56, 59, 60, 61, 62 }, "\u7279\u6b8a\u5c55\u793a");

        if (categories.Count == 0)
        {
            categories.Add(LegacyCategoryForAction(actionName));
        }

        return categories.ToArray();
    }

    private static void AddIf(List<string> categories, int value, int[] values, string category)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (value == values[i])
            {
                categories.Add(category);
                return;
            }
        }
    }

    private static int IdleActionNumber(string actionName)
    {
        if (string.IsNullOrEmpty(actionName) || !actionName.StartsWith("KA_Idle", StringComparison.Ordinal))
        {
            return -1;
        }

        int start = "KA_Idle".Length;
        int end = start;
        while (end < actionName.Length && char.IsDigit(actionName[end]))
        {
            end++;
        }

        return end > start && int.TryParse(actionName.Substring(start, end - start), out int value) ? value : -1;
    }

    private static string LegacyCategoryForAction(string actionName)
    {
        string lower = (actionName ?? string.Empty).ToLowerInvariant();
        if (lower.Contains("_idle")) return "\u57fa\u7840\u5f85\u673a";
        if (lower.Contains("_sit")) return "\u684c\u5ba0\u59ff\u6001";
        if (lower.Contains("_jump") || lower.Contains("_run") || lower.Contains("_combat")) return "\u7279\u6b8a\u5c55\u793a";
        return "\u5176\u4ed6";
    }

    private static string NormalizeActionId(string value)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.EndsWith(".FBX", StringComparison.OrdinalIgnoreCase))
        {
            text = Path.GetFileNameWithoutExtension(text);
        }

        if (text.StartsWith("@", StringComparison.Ordinal))
        {
            text = text.Substring(1);
        }

        if (text.EndsWith("_action_bundle", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(0, text.Length - "_action_bundle".Length);
        }

        return text.Trim();
    }

    private static string[] SplitCsv(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        string[] raw = value.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        List<string> output = new List<string>();
        for (int i = 0; i < raw.Length; i++)
        {
            string item = raw[i].Trim();
            if (!string.IsNullOrEmpty(item))
            {
                output.Add(item);
            }
        }

        return output.ToArray();
    }

    private static Quaternion[] ParseQuaternionFrames(List<object> values)
    {
        Quaternion[] frames = new Quaternion[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            frames[i] = values[i] is List<object> frame ? ParseQuaternion(frame) : Quaternion.identity;
        }

        return frames;
    }

    private static Quaternion ParseQuaternion(List<object> values)
    {
        if (values.Count < 4)
        {
            return Quaternion.identity;
        }

        return NormalizeQuaternion(new Quaternion(ToFloat(values[0]), ToFloat(values[1]), ToFloat(values[2]), ToFloat(values[3])));
    }

    private static string ResolveStreamingPath(string relativePath)
    {
        return Path.Combine(Application.streamingAssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string NormalizeBoneName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(".", string.Empty).ToLowerInvariant();
    }

    private static string NormalizeMmdBoneName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("０", "0")
            .Replace("１", "1")
            .Replace("２", "2")
            .Replace("３", "3")
            .Replace("４", "4")
            .Replace("５", "5")
            .Replace("６", "6")
            .Replace("７", "7")
            .Replace("８", "8")
            .Replace("９", "9");
    }

    private static bool IsMmdHandBone(string importedName)
    {
        string name = NormalizeMmdBoneName(importedName);
        if (name == "手首.L" || name == "手首.R")
        {
            return true;
        }

        bool left = name.EndsWith(".L", StringComparison.Ordinal);
        bool right = name.EndsWith(".R", StringComparison.Ordinal);
        if (!left && !right)
        {
            return false;
        }

        string stem = name.Substring(0, name.Length - 2);
        switch (stem)
        {
            case "中指1":
            case "中指2":
            case "中指3":
            case "人指1":
            case "人指2":
            case "人指3":
            case "小指1":
            case "小指2":
            case "小指3":
            case "薬指1":
            case "薬指2":
            case "薬指3":
            case "親指0":
            case "親指1":
            case "親指2":
                return true;
            default:
                return false;
        }
    }

    private static string GetString(Dictionary<string, object> data, string key, string fallback)
    {
        return data.TryGetValue(key, out object value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : fallback;
    }

    private static float GetFloat(Dictionary<string, object> data, string key, float fallback)
    {
        return data.TryGetValue(key, out object value) ? ToFloat(value, fallback) : fallback;
    }

    private static int GetInt(Dictionary<string, object> data, string key, int fallback)
    {
        return data.TryGetValue(key, out object value) ? Mathf.RoundToInt(ToFloat(value, fallback)) : fallback;
    }

    private static bool GetBool(Dictionary<string, object> data, string key, bool fallback)
    {
        return data.TryGetValue(key, out object value) && value is bool boolValue ? boolValue : fallback;
    }

    private static float ToFloat(object value, float fallback = 0f)
    {
        if (value == null)
        {
            return fallback;
        }

        if (value is float f) return f;
        if (value is double d) return (float)d;
        if (value is long l) return l;
        if (value is int i) return i;
        return float.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : fallback;
    }

    private static Quaternion NormalizeQuaternion(Quaternion rotation)
    {
        float magnitude = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
        if (magnitude <= 0.000001f)
        {
            return Quaternion.identity;
        }

        float inverse = 1f / magnitude;
        return new Quaternion(rotation.x * inverse, rotation.y * inverse, rotation.z * inverse, rotation.w * inverse);
    }

    private sealed class ActionClip
    {
        public string Name;
        public float LengthSeconds;
        public float SampleRate;
        public int FrameCount;
        public bool Loop;
        public readonly List<ActionBone> Bones = new List<ActionBone>();
    }

    private sealed class ActionBone
    {
        public string ImportedName;
        public Transform Transform;
        public Quaternion ImportedRestRotation;
        public Quaternion RuntimeRestRotation;
        public Quaternion[] Rotations;
    }

    private static readonly Dictionary<string, string> DisplayReplacements = new Dictionary<string, string>
    {
        { "Idle", "\u5f85\u673a" },
        { "breathing", "\u547c\u5438" },
        { "LookLeftAndRight", "\u5de6\u53f3\u770b" },
        { "LookAtHands", "\u770b\u624b" },
        { "LookAtFeet", "\u770b\u811a" },
        { "Stretch", "\u4f38\u61d2\u8170" },
        { "JumpAround", "\u8e66\u8df3" },
        { "ComeUpWithAnIdea", "\u60f3\u5230\u70b9\u5b50" },
        { "Waiting", "\u7b49\u5f85" },
        { "Sit", "\u5750\u4e0b" },
        { "LookingBack", "\u56de\u5934" },
        { "LeaningForward", "\u524d\u503e" },
        { "Dance", "\u8df3\u821e" },
        { "TieShoelaces", "\u7cfb\u978b\u5e26" },
        { "WaveHands", "\u6325\u53cc\u624b" },
        { "WaveHandSlightly", "\u8f7b\u8f7b\u6325\u624b" },
        { "StumbleAndFall", "\u8dcc\u5012" },
        { "ShyRefusal", "\u5bb3\u7f9e\u62d2\u7edd" },
        { "Shy", "\u5bb3\u7f9e" },
        { "TriplePose", "\u4e09\u8fde\u59ff\u52bf" },
        { "HighFive", "\u51fb\u638c" },
        { "Cheers", "\u6b22\u547c" },
        { "Shout", "\u547c\u558a" },
        { "Angry", "\u751f\u6c14" },
        { "Laugh", "\u5927\u7b11" },
        { "Surprised", "\u60ca\u8bb6" },
        { "PickUp", "\u6361\u8d77" },
        { "LeanAgainst", "\u501a\u9760" },
        { "FingerSnap", "\u54cd\u6307" },
        { "Yay", "\u5f00\u5fc3" },
        { "Tsundere", "\u50b2\u5a07" },
        { "Cry", "\u54ed\u6ce3" },
        { "CuteArmUp", "\u53ef\u7231\u4e3e\u624b" },
        { "CrossLegs", "\u4ea4\u53c9\u817f" },
        { "CuteShyPose", "\u53ef\u7231\u5bb3\u7f9e" },
        { "Taunt", "\u6311\u8845" },
        { "HandOnHip", "\u53c9\u8170" },
        { "GreetingBow", "\u97a0\u8eac" },
        { "Scaring", "\u5413\u4eba" },
        { "StandingTalk", "\u7ad9\u7acb\u8bf4\u8bdd" },
        { "Curtsy", "\u5c48\u819d\u793c" },
        { "Seiza", "\u6b63\u5750" },
        { "CartwheelAndBackHandspring", "\u4fa7\u624b\u7ffb" },
        { "Backflip", "\u540e\u7a7a\u7ffb" },
        { "Handstand", "\u5012\u7acb" },
        { "Kiss", "\u4eb2\u543b" },
        { "RockPaperScissors", "\u731c\u62f3" },
    };

    private static class MiniJson
    {
        public static object Parse(string json)
        {
            return new Parser(json).ParseValue();
        }

        private sealed class Parser
        {
            private readonly string _json;
            private int _index;

            public Parser(string json)
            {
                _json = json ?? string.Empty;
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (_index >= _json.Length)
                {
                    return null;
                }

                char c = _json[_index];
                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '"') return ParseString();
                if (c == 't') { _index += 4; return true; }
                if (c == 'f') { _index += 5; return false; }
                if (c == 'n') { _index += 4; return null; }
                return ParseNumber();
            }

            private Dictionary<string, object> ParseObject()
            {
                Dictionary<string, object> table = new Dictionary<string, object>(StringComparer.Ordinal);
                _index++;
                while (true)
                {
                    SkipWhitespace();
                    if (_index >= _json.Length)
                    {
                        return table;
                    }

                    if (_json[_index] == '}')
                    {
                        _index++;
                        return table;
                    }

                    string key = ParseString();
                    SkipWhitespace();
                    if (_index < _json.Length && _json[_index] == ':')
                    {
                        _index++;
                    }

                    table[key] = ParseValue();
                    SkipWhitespace();
                    if (_index < _json.Length && _json[_index] == ',')
                    {
                        _index++;
                    }
                }
            }

            private List<object> ParseArray()
            {
                List<object> array = new List<object>();
                _index++;
                while (true)
                {
                    SkipWhitespace();
                    if (_index >= _json.Length)
                    {
                        return array;
                    }

                    if (_json[_index] == ']')
                    {
                        _index++;
                        return array;
                    }

                    array.Add(ParseValue());
                    SkipWhitespace();
                    if (_index < _json.Length && _json[_index] == ',')
                    {
                        _index++;
                    }
                }
            }

            private string ParseString()
            {
                StringBuilder builder = new StringBuilder();
                _index++;
                while (_index < _json.Length)
                {
                    char c = _json[_index++];
                    if (c == '"')
                    {
                        break;
                    }

                    if (c == '\\' && _index < _json.Length)
                    {
                        char escape = _json[_index++];
                        switch (escape)
                        {
                            case '"': builder.Append('"'); break;
                            case '\\': builder.Append('\\'); break;
                            case '/': builder.Append('/'); break;
                            case 'b': builder.Append('\b'); break;
                            case 'f': builder.Append('\f'); break;
                            case 'n': builder.Append('\n'); break;
                            case 'r': builder.Append('\r'); break;
                            case 't': builder.Append('\t'); break;
                            case 'u':
                                if (_index + 4 <= _json.Length &&
                                    ushort.TryParse(_json.Substring(_index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                                {
                                    builder.Append((char)code);
                                    _index += 4;
                                }
                                break;
                            default:
                                builder.Append(escape);
                                break;
                        }
                    }
                    else
                    {
                        builder.Append(c);
                    }
                }

                return builder.ToString();
            }

            private object ParseNumber()
            {
                int start = _index;
                while (_index < _json.Length && "-+0123456789.eE".IndexOf(_json[_index]) >= 0)
                {
                    _index++;
                }

                string token = _json.Substring(start, _index - start);
                if (token.IndexOf('.') < 0 && token.IndexOf('e') < 0 && token.IndexOf('E') < 0 &&
                    long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
                {
                    return integer;
                }

                return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0d;
            }

            private void SkipWhitespace()
            {
                while (_index < _json.Length && char.IsWhiteSpace(_json[_index]))
                {
                    _index++;
                }
            }
        }
    }
}
