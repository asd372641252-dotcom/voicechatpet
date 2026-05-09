using UnityEngine;

[DisallowMultipleComponent]
[DefaultExecutionOrder(12000)]
public sealed class TransparentPetHeadLookAt : MonoBehaviour
{
    public Animator animator;
    public Camera targetCamera;
    public Transform modelRoot;
    public bool lookAtEnabled = true;
    public bool useAnimatorLookAtIk = true;
    public bool directBoneFallback = true;
    public bool persistRuntimeSetting = true;
    public string settingsKey = "TransparentPet.HeadLookAt.v1";

    [Range(0f, 30f)]
    public float deadZoneDegrees = 4f;
    [Range(0.03f, 0.8f)]
    public float smoothTime = 0.16f;
    [Range(5f, 80f)]
    public float maxYawDegrees = 38f;
    [Range(5f, 60f)]
    public float maxPitchUpDegrees = 18f;
    [Range(5f, 60f)]
    public float maxPitchDownDegrees = 22f;

    [Range(0f, 1f)]
    public float ikBodyWeight = 0.02f;
    [Range(0f, 1f)]
    public float ikHeadWeight = 0.82f;
    [Range(0f, 1f)]
    public float ikEyesWeight;
    [Range(0f, 1f)]
    public float ikClampWeight = 0.72f;

    [Range(0f, 1f)]
    public float fallbackNeckWeight = 0.18f;
    [Range(0f, 1f)]
    public float fallbackHeadWeight = 0.56f;

    private Transform _head;
    private Transform _neck;
    private float _currentYaw;
    private float _currentPitch;
    private float _yawVelocity;
    private float _pitchVelocity;
    private int _lastIkFrame = -1;
    private bool _settingsLoaded;
    private bool _hasExternalAdditiveAngles;
    private Vector2 _externalAdditiveAngles;

    public bool LookAtEnabled => lookAtEnabled;
    public float DeadZoneDegrees => deadZoneDegrees;
    public float SmoothTime => smoothTime;

    public static TransparentPetHeadLookAt EnsureForRuntimeControls(TransparentPetRuntimeControls controls, Camera camera)
    {
        if (controls == null)
        {
            return null;
        }

        Animator foundAnimator = controls.GetComponent<Animator>();
        if (foundAnimator == null)
        {
            foundAnimator = controls.GetComponentInChildren<Animator>();
        }

        GameObject host = foundAnimator != null ? foundAnimator.gameObject : controls.gameObject;
        TransparentPetHeadLookAt headLookAt = host.GetComponent<TransparentPetHeadLookAt>();
        if (headLookAt == null)
        {
            headLookAt = host.AddComponent<TransparentPetHeadLookAt>();
        }

        headLookAt.animator = foundAnimator;
        headLookAt.targetCamera = camera != null ? camera : (Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>());
        headLookAt.modelRoot = controls.modelRoot != null ? controls.modelRoot : controls.transform;
        return headLookAt;
    }

    public void Rebind(Animator nextAnimator, Transform nextModelRoot, Camera nextCamera)
    {
        animator = nextAnimator;
        modelRoot = nextModelRoot != null ? nextModelRoot : (animator != null ? animator.transform : transform);
        if (nextCamera != null)
        {
            targetCamera = nextCamera;
        }

        _head = null;
        _neck = null;
        _lastIkFrame = -1;
        _currentYaw = 0f;
        _currentPitch = 0f;
        _yawVelocity = 0f;
        _pitchVelocity = 0f;
        ResolveReferences();
    }

    private void Awake()
    {
        ResolveReferences();
        LoadSettings();
    }

    private void OnEnable()
    {
        ResolveReferences();
        LoadSettings();
    }

    private void OnValidate()
    {
        NormalizeValues();
    }

    private void Update()
    {
        ResolveReferences();
        UpdateSmoothedAngles();
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (!CanUseAnimatorIk())
        {
            return;
        }

        if (!lookAtEnabled || _head == null)
        {
            animator.SetLookAtWeight(0f);
            return;
        }

        animator.SetLookAtWeight(1f, ikBodyWeight, ikHeadWeight, ikEyesWeight, ikClampWeight);
        animator.SetLookAtPosition(BuildLookAtPosition());
        _lastIkFrame = Time.frameCount;
    }

    private void LateUpdate()
    {
        if (!lookAtEnabled || !directBoneFallback || _head == null)
        {
            return;
        }

        if (useAnimatorLookAtIk && _lastIkFrame == Time.frameCount)
        {
            return;
        }

        Quaternion offset = BuildWorldOffsetRotation();
        if (_neck != null && fallbackNeckWeight > 0f)
        {
            _neck.rotation = Quaternion.Slerp(Quaternion.identity, offset, fallbackNeckWeight) * _neck.rotation;
        }

        if (fallbackHeadWeight > 0f)
        {
            _head.rotation = Quaternion.Slerp(Quaternion.identity, offset, fallbackHeadWeight) * _head.rotation;
        }
    }

    public void SetLookAtEnabled(bool enabled)
    {
        lookAtEnabled = enabled;
        if (!enabled)
        {
            _currentYaw = 0f;
            _currentPitch = 0f;
            _yawVelocity = 0f;
            _pitchVelocity = 0f;
        }

        SaveSettings();
    }

    public void SetDeadZoneDegrees(float value)
    {
        deadZoneDegrees = Mathf.Clamp(value, 0f, 30f);
        SaveSettings();
    }

    public void SetSmoothTime(float value)
    {
        smoothTime = Mathf.Clamp(value, 0.03f, 0.8f);
        SaveSettings();
    }

    public void SetExternalAdditiveLookAngles(Vector2 yawPitchDegrees)
    {
        _externalAdditiveAngles = new Vector2(
            Mathf.Clamp(yawPitchDegrees.x, -maxYawDegrees, maxYawDegrees),
            Mathf.Clamp(yawPitchDegrees.y, -maxPitchDownDegrees, maxPitchUpDegrees));
        _hasExternalAdditiveAngles = true;
    }

    public void ClearExternalAdditiveLookAngles()
    {
        _hasExternalAdditiveAngles = false;
        _externalAdditiveAngles = Vector2.zero;
    }

    private void ResolveReferences()
    {
        if (animator == null)
        {
            animator = GetComponent<Animator>();
        }

        if (animator == null)
        {
            animator = GetComponentInChildren<Animator>();
        }

        if (targetCamera == null)
        {
            targetCamera = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();
        }

        if (modelRoot == null)
        {
            modelRoot = animator != null ? animator.transform : transform;
        }

        if (animator != null)
        {
            if (_head == null)
            {
                _head = animator.GetBoneTransform(HumanBodyBones.Head);
            }

            if (_neck == null)
            {
                _neck = animator.GetBoneTransform(HumanBodyBones.Neck);
            }
        }
    }

    private void UpdateSmoothedAngles()
    {
        Vector2 targetAngles = lookAtEnabled ? ComputeTargetAngles() : Vector2.zero;
        float deltaTime = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        _currentYaw = Mathf.SmoothDampAngle(_currentYaw, targetAngles.x, ref _yawVelocity, smoothTime, Mathf.Infinity, deltaTime);
        _currentPitch = Mathf.SmoothDampAngle(_currentPitch, targetAngles.y, ref _pitchVelocity, smoothTime, Mathf.Infinity, deltaTime);
        _currentYaw = Mathf.Clamp(_currentYaw, -maxYawDegrees, maxYawDegrees);
        _currentPitch = Mathf.Clamp(_currentPitch, -maxPitchDownDegrees, maxPitchUpDegrees);
    }

    private Vector2 ComputeTargetAngles()
    {
        if (_head == null || targetCamera == null)
        {
            return Vector2.zero;
        }

        Transform reference = modelRoot != null ? modelRoot : transform;
        Vector3 toCamera = targetCamera.transform.position - _head.position;
        if (toCamera.sqrMagnitude <= 0.0001f)
        {
            return Vector2.zero;
        }

        Vector3 localDirection = reference.InverseTransformDirection(toCamera.normalized);
        float yaw = Mathf.Atan2(localDirection.x, localDirection.z) * Mathf.Rad2Deg;
        float horizontal = new Vector2(localDirection.x, localDirection.z).magnitude;
        float pitch = Mathf.Atan2(localDirection.y, horizontal) * Mathf.Rad2Deg;

        yaw = ApplyDeadZone(yaw, deadZoneDegrees);
        pitch = ApplyDeadZone(pitch, deadZoneDegrees);

        if (_hasExternalAdditiveAngles)
        {
            yaw += _externalAdditiveAngles.x;
            pitch += _externalAdditiveAngles.y;
        }

        return new Vector2(
            Mathf.Clamp(yaw, -maxYawDegrees, maxYawDegrees),
            Mathf.Clamp(pitch, -maxPitchDownDegrees, maxPitchUpDegrees));
    }

    private Vector3 BuildLookAtPosition()
    {
        float distance = targetCamera != null && _head != null
            ? Mathf.Max(1f, Vector3.Distance(targetCamera.transform.position, _head.position))
            : 5f;

        return _head.position + BuildLookDirection() * distance;
    }

    private Vector3 BuildLookDirection()
    {
        Transform reference = modelRoot != null ? modelRoot : transform;
        return (BuildWorldOffsetRotation() * reference.forward).normalized;
    }

    private Quaternion BuildWorldOffsetRotation()
    {
        Transform reference = modelRoot != null ? modelRoot : transform;
        Quaternion yaw = Quaternion.AngleAxis(_currentYaw, reference.up);
        Quaternion pitch = Quaternion.AngleAxis(-_currentPitch, reference.right);
        return yaw * pitch;
    }

    private bool CanUseAnimatorIk()
    {
        return useAnimatorLookAtIk
            && animator != null
            && animator.isActiveAndEnabled
            && animator.isHuman;
    }

    private static float ApplyDeadZone(float angle, float deadZone)
    {
        float magnitude = Mathf.Abs(angle);
        if (magnitude <= deadZone)
        {
            return 0f;
        }

        return Mathf.Sign(angle) * (magnitude - deadZone);
    }

    private void NormalizeValues()
    {
        deadZoneDegrees = Mathf.Clamp(deadZoneDegrees, 0f, 30f);
        smoothTime = Mathf.Clamp(smoothTime, 0.03f, 0.8f);
        maxYawDegrees = Mathf.Clamp(maxYawDegrees, 5f, 80f);
        maxPitchUpDegrees = Mathf.Clamp(maxPitchUpDegrees, 5f, 60f);
        maxPitchDownDegrees = Mathf.Clamp(maxPitchDownDegrees, 5f, 60f);
        ikBodyWeight = Mathf.Clamp01(ikBodyWeight);
        ikHeadWeight = Mathf.Clamp01(ikHeadWeight);
        ikEyesWeight = Mathf.Clamp01(ikEyesWeight);
        ikClampWeight = Mathf.Clamp01(ikClampWeight);
        fallbackNeckWeight = Mathf.Clamp01(fallbackNeckWeight);
        fallbackHeadWeight = Mathf.Clamp01(fallbackHeadWeight);
    }

    private void LoadSettings()
    {
        if (_settingsLoaded || !persistRuntimeSetting || string.IsNullOrWhiteSpace(settingsKey) || !PlayerPrefs.HasKey(settingsKey))
        {
            _settingsLoaded = true;
            return;
        }

        try
        {
            HeadLookAtSettings settings = JsonUtility.FromJson<HeadLookAtSettings>(PlayerPrefs.GetString(settingsKey));
            lookAtEnabled = settings.enabled;
            deadZoneDegrees = settings.deadZoneDegrees > 0f ? settings.deadZoneDegrees : deadZoneDegrees;
            smoothTime = settings.smoothTime > 0f ? settings.smoothTime : smoothTime;
            NormalizeValues();
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning("Failed to load head look-at settings: " + exception.Message);
        }

        _settingsLoaded = true;
    }

    private void SaveSettings()
    {
        NormalizeValues();
        if (!persistRuntimeSetting || string.IsNullOrWhiteSpace(settingsKey))
        {
            return;
        }

        HeadLookAtSettings settings = new HeadLookAtSettings
        {
            enabled = lookAtEnabled,
            deadZoneDegrees = deadZoneDegrees,
            smoothTime = smoothTime
        };
        PlayerPrefs.SetString(settingsKey, JsonUtility.ToJson(settings));
        PlayerPrefs.Save();
    }

    [System.Serializable]
    private sealed class HeadLookAtSettings
    {
        public bool enabled = true;
        public float deadZoneDegrees = 4f;
        public float smoothTime = 0.16f;
    }
}
