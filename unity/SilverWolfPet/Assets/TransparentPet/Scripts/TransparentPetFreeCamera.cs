using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_STANDALONE_WIN
using System.Runtime.InteropServices;
#endif

[DisallowMultipleComponent]
public sealed class TransparentPetFreeCamera : MonoBehaviour
{
    private const int CurrentCameraStateVersion = 5;

    public TransparentWindowController windowController;
    public TransparentPetContextMenu contextMenu;
    public Camera targetCamera;
    public bool enabledInput = true;
    public Vector3 target = new Vector3(0f, 0.75f, 0f);
    public float distance = 0.31f;
    public float yawDegrees;
    public float pitchDegrees;
    public float minPitchDegrees = -55f;
    public float maxPitchDegrees = 65f;
    public float rotateSensitivity = 0.22f;
    public float panSensitivity = 0.0018f;
    public float orthographicZoomStep = 0.18f;
    public float minOrthographicSize = 0.75f;
    public float maxOrthographicSize = 6f;
    public float rightDragThresholdPixels = 8f;
    public bool allowEditorFreeCameraInput = true;
    public bool allowWholeScreenCameraInput;
    public bool requirePetHitForInput = true;
    public bool freeSceneInput;
    public Volume postProcessVolume;
    public bool depthOfFieldEnabled = true;
    public float focusDistance = 0.31f;
    public float aperture = 5.6f;
    [Range(0f, 1f)]
    public float depthOfFieldBlurAmount = 0.45f;
    public bool lockDepthOfFieldToPet = true;
    public string runtimeVolumeName = "TransparentPet Runtime Camera Volume";
    public float runtimeVolumePriority = 250f;
    public float minDepthOfFieldAperture = 1.2f;
    public float maxDepthOfFieldAperture = 16f;
    public float minDepthOfFieldFocalLength = 35f;
    public float maxDepthOfFieldFocalLength = 140f;
    public float focalLength = 35f;
    public float minFocalLength = 14f;
    public float maxFocalLength = 85f;
    public float focalLengthStep = 3f;
    public float focusDistanceStep = 0.35f;
    public float apertureStep = 0.35f;
    public bool keyboardMouseControls = true;
    public float keyboardPanSpeed = 1.2f;
    public float keyboardOrbitSpeed = 95f;
    public KeyCode resetViewKey = KeyCode.R;
    public KeyCode toggleDepthOfFieldKey = KeyCode.B;
    public bool followPlacementTarget = true;
    public bool persistCameraState = true;
    public bool useSavedCameraInEditor;
    public bool saveCameraInEditor;
    public string cameraSaveKey = "TransparentPet.FreeCamera.v1";
    [Min(0.05f)]
    public float cameraSaveInterval = 0.35f;

    private bool _rightPressed;
    private bool _rightRotating;
    private bool _rightIgnoredUntilRelease;
    private bool _middlePanning;
    private bool _middleIgnoredUntilRelease;
    private bool _cameraSavePending;
    private bool _suppressCameraSave;
    private bool _hasLockedFocusTarget;
    private bool _hasExternalOrbitOffset;
    private bool _hasExternalTargetOffset;
    private bool _hasExternalCameraOffset;
    private bool _externalTargetControlsFocus;
    private Vector2 _rightPressPosition;
    private Vector2 _lastCursorPosition;
    private float _nextCameraSaveTime;
    private float _externalYawOffsetDegrees;
    private float _externalPitchOffsetDegrees;
    private float _defaultOrthographicSize;
    private Vector3 _defaultTarget;
    private float _defaultDistance;
    private float _defaultFocalLength;
    private float _defaultFocusDistance;
    private float _defaultAperture;
    private Vector3 _lockedFocusTarget;
    private Vector3 _manualTargetOffset;
    private Vector3 _externalTargetOffset;
    private Vector3 _externalCameraOffset;

#if UNITY_STANDALONE_WIN
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
#endif

    public bool HasSavedCamera => !string.IsNullOrWhiteSpace(cameraSaveKey) && PlayerPrefs.HasKey(cameraSaveKey);
    private string ManualCameraSaveMarkerKey => string.IsNullOrWhiteSpace(cameraSaveKey) ? "" : cameraSaveKey + ".ManualUserSave";
    private bool HasManualCameraSave => !string.IsNullOrWhiteSpace(ManualCameraSaveMarkerKey) && PlayerPrefs.GetInt(ManualCameraSaveMarkerKey, 0) == 1;

    private void Awake()
    {
        ResolveMissingReferences();

        _defaultOrthographicSize = targetCamera != null ? targetCamera.orthographicSize : 1.65f;
        _defaultTarget = target;
        _defaultDistance = distance;
        _defaultFocalLength = targetCamera != null ? targetCamera.focalLength : focalLength;
        _defaultFocusDistance = focusDistance;
        _defaultAperture = aperture;
        ResolveVolume();
        _suppressCameraSave = true;
        if (ShouldUseSavedCamera())
        {
            TryLoadCameraState();
        }

        EnforceInputScope();
        if (freeSceneInput)
        {
            requirePetHitForInput = false;
        }

        ApplyOptics();
        ApplyCameraTransform();
        _suppressCameraSave = false;
    }

    private void Update()
    {
        ResolveMissingReferences();
        if (!enabledInput || targetCamera == null)
        {
            ResetDragState();
            return;
        }

        if (contextMenu != null && contextMenu.IsVisible)
        {
            ResetDragState();
            return;
        }

        UpdateKeyboardCamera();

        if (windowController == null)
        {
            UpdateZoom(true);
            ResetDragState();
            return;
        }

        if (!windowController.TryGetCursorPositionInWindow(out Vector2 cursor))
        {
            UpdateZoom(freeSceneInput || !requirePetHitForInput);
            ResetDragState();
            return;
        }

        bool inputHit = freeSceneInput || !requirePetHitForInput || windowController.IsPetVisualHitAt(cursor);
        bool cameraInputHit = inputHit || ShouldAllowEditorFreeCameraInput();
        UpdateZoom(cameraInputHit);
        UpdateRightDrag(cursor, cameraInputHit);
        UpdateMiddlePan(cursor, cameraInputHit);
        _lastCursorPosition = cursor;
    }

    private void LateUpdate()
    {
        FlushPendingCameraSave(false);
    }

    private void OnDisable()
    {
        FlushPendingCameraSave(true);
    }

    private void OnApplicationQuit()
    {
        FlushPendingCameraSave(true);
    }

    private void ResolveMissingReferences()
    {
        if (targetCamera == null)
        {
            targetCamera = GetComponent<Camera>();
        }

        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        if (windowController == null)
        {
            windowController = FindAnyObjectByType<TransparentWindowController>();
        }

        if (contextMenu == null)
        {
            contextMenu = FindAnyObjectByType<TransparentPetContextMenu>();
        }
    }

    public void ResetView()
    {
        followPlacementTarget = true;
        target = _defaultTarget;
        _manualTargetOffset = Vector3.zero;
        distance = _defaultDistance;
        yawDegrees = 0f;
        pitchDegrees = 0f;
        if (targetCamera != null)
        {
            targetCamera.orthographicSize = _defaultOrthographicSize;
            targetCamera.focalLength = _defaultFocalLength;
        }

        focusDistance = _defaultFocusDistance;
        aperture = _defaultAperture;
        ApplyOptics();
        ApplyCameraTransform();
        MarkCameraDirty();
    }

    public void SaveUserCameraNow()
    {
        MarkManualCameraSave(true);
        SaveCameraState();
        _cameraSavePending = false;
        _nextCameraSaveTime = 0f;
    }

    public void ClearSavedCamera()
    {
        if (!string.IsNullOrWhiteSpace(cameraSaveKey))
        {
            PlayerPrefs.DeleteKey(cameraSaveKey);
            if (!string.IsNullOrWhiteSpace(ManualCameraSaveMarkerKey))
            {
                PlayerPrefs.DeleteKey(ManualCameraSaveMarkerKey);
            }

            PlayerPrefs.Save();
        }

        _cameraSavePending = false;
        _nextCameraSaveTime = 0f;
    }

    public void ResetToFactoryDefault()
    {
        bool previousSuppress = _suppressCameraSave;
        _suppressCameraSave = true;
        ClearSavedCamera();
        ResetView();
        _suppressCameraSave = previousSuppress;
    }

    public void FocusOn(Vector3 worldTarget, float preferredDistance, float preferredOrthographicSize)
    {
        Vector3 previousForward = targetCamera != null ? targetCamera.transform.forward : transform.forward;
        FocusOnFromDirection(worldTarget, -previousForward, preferredDistance, preferredOrthographicSize);
    }

    public void FocusOnFromDirection(Vector3 worldTarget, Vector3 cameraDirectionFromTarget, float preferredDistance, float preferredOrthographicSize)
    {
        FocusOnFromDirection(worldTarget, cameraDirectionFromTarget, preferredDistance, preferredOrthographicSize, false);
    }

    public void FocusOnFromDirection(Vector3 worldTarget, Vector3 cameraDirectionFromTarget, float preferredDistance, float preferredOrthographicSize, bool keepPlacementTargetLocked)
    {
        Transform cameraTransform = ResolveCameraTransform();
        if (cameraDirectionFromTarget.sqrMagnitude <= 0.0001f)
        {
            cameraDirectionFromTarget = Vector3.back;
        }

        cameraDirectionFromTarget.Normalize();
        followPlacementTarget = keepPlacementTargetLocked;
        target = worldTarget;
        _manualTargetOffset = Vector3.zero;
        distance = Mathf.Clamp(preferredDistance, 0.25f, 12f);
        if (targetCamera != null)
        {
            if (targetCamera.orthographic)
            {
                targetCamera.orthographicSize = Mathf.Clamp(preferredOrthographicSize, minOrthographicSize, maxOrthographicSize);
            }

            targetCamera.usePhysicalProperties = true;
            focalLength = Mathf.Clamp(targetCamera.focalLength > 0f ? targetCamera.focalLength : focalLength, minFocalLength, maxFocalLength);
            targetCamera.focalLength = focalLength;
        }

        focusDistance = distance;
        ApplyOptics();
        Vector3 cameraPosition = target + cameraDirectionFromTarget * distance;
        Vector3 cameraForward = target - cameraPosition;
        if (cameraForward.sqrMagnitude > 0.0001f)
        {
            cameraTransform.SetPositionAndRotation(cameraPosition, Quaternion.LookRotation(cameraForward.normalized, Vector3.up));
            SyncRigTransformFromCamera();
            CaptureOrbitFromForward(cameraTransform.forward);
        }
        else
        {
            CaptureOrbitFromForward(-cameraDirectionFromTarget);
            ApplyCameraTransform();
        }

        ResetDragState();
        MarkCameraDirty();
    }

    public void SetFreeSceneInput(bool enabled)
    {
        freeSceneInput = enabled && allowWholeScreenCameraInput;
        requirePetHitForInput = !freeSceneInput;
        enabledInput = true;
        ResetDragState();
        MarkCameraDirty();
    }

    public void OrbitSteps(float yawStep, float pitchStep)
    {
        yawDegrees += yawStep;
        pitchDegrees = Mathf.Clamp(pitchDegrees + pitchStep, minPitchDegrees, maxPitchDegrees);
        ApplyCameraTransform();
        MarkCameraDirty();
    }

    public void PanLocal(Vector3 localDelta)
    {
        Transform cameraTransform = ResolveCameraTransform();
        PanWorld(cameraTransform.right * localDelta.x + cameraTransform.up * localDelta.y + cameraTransform.forward * localDelta.z);
    }

    public void SetExternalTarget(Vector3 nextTarget)
    {
        _lockedFocusTarget = nextTarget;
        _hasLockedFocusTarget = true;
        _externalTargetControlsFocus = false;

        target = nextTarget;
        ApplyCameraTransform();
        ApplyOptics();
    }

    public void SetExternalTargetOffset(Vector3 worldOffset)
    {
        _hasExternalTargetOffset = true;
        _externalTargetOffset = worldOffset;
        _lockedFocusTarget = GetDrivenTarget();
        _hasLockedFocusTarget = true;
        _externalTargetControlsFocus = true;
        ApplyCameraTransform();
        ApplyOptics();
    }

    public void SetExternalCameraOffset(Vector3 worldOffset)
    {
        _hasExternalCameraOffset = true;
        _externalCameraOffset = worldOffset;
        ApplyCameraTransform();
        ApplyOptics();
    }

    public void ClearExternalCameraOffset()
    {
        if (!_hasExternalCameraOffset)
        {
            return;
        }

        _hasExternalCameraOffset = false;
        _externalCameraOffset = Vector3.zero;
        ApplyCameraTransform();
        ApplyOptics();
    }

    public void ClearExternalTargetOffset()
    {
        if (!_hasExternalTargetOffset)
        {
            return;
        }

        _hasExternalTargetOffset = false;
        _externalTargetOffset = Vector3.zero;
        if (_externalTargetControlsFocus)
        {
            _hasLockedFocusTarget = false;
            _externalTargetControlsFocus = false;
        }

        ApplyCameraTransform();
        ApplyOptics();
    }

    public void SetExternalOrbitOffset(float yawOffsetDegrees, float pitchOffsetDegrees)
    {
        _hasExternalOrbitOffset = true;
        _externalYawOffsetDegrees = yawOffsetDegrees;
        _externalPitchOffsetDegrees = pitchOffsetDegrees;
        ApplyCameraTransform();
        ApplyOptics();
    }

    public void ClearExternalOrbitOffset()
    {
        if (!_hasExternalOrbitOffset)
        {
            return;
        }

        _hasExternalOrbitOffset = false;
        _externalYawOffsetDegrees = 0f;
        _externalPitchOffsetDegrees = 0f;
        ApplyCameraTransform();
        ApplyOptics();
    }

    public void SetFollowPlacementTarget(bool enabled)
    {
        if (!enabled && followPlacementTarget)
        {
            target = GetEffectiveTarget();
            _manualTargetOffset = Vector3.zero;
        }

        followPlacementTarget = enabled;
        ApplyCameraTransform();
        MarkCameraDirty();
    }

    public void FocalLengthSteps(int steps)
    {
        if (targetCamera == null)
        {
            return;
        }

        targetCamera.usePhysicalProperties = true;
        focalLength = Mathf.Clamp((targetCamera.focalLength > 0f ? targetCamera.focalLength : focalLength) + steps * focalLengthStep, minFocalLength, maxFocalLength);
        targetCamera.focalLength = focalLength;
        ApplyOptics();
        MarkCameraDirty();
    }

    public void FocusDistanceSteps(int steps)
    {
        focusDistance = Mathf.Clamp(focusDistance + steps * focusDistanceStep, 0.15f, 30f);
        ApplyOptics();
        MarkCameraDirty();
    }

    public void ApertureSteps(int steps)
    {
        aperture = Mathf.Clamp(aperture + steps * apertureStep, 1.2f, 16f);
        ApplyOptics();
        MarkCameraDirty();
    }

    public void SetDepthOfFieldEnabled(bool enabled)
    {
        depthOfFieldEnabled = enabled;
        ApplyOptics();
        MarkCameraDirty();
    }

    public void SetDepthOfFieldFocusLock(bool enabled)
    {
        lockDepthOfFieldToPet = enabled;
        ApplyOptics();
        MarkCameraDirty();
    }

    public void SetDepthOfFieldBlurAmount(float value)
    {
        depthOfFieldBlurAmount = Mathf.Clamp01(value);
        if (depthOfFieldBlurAmount > 0.001f)
        {
            depthOfFieldEnabled = true;
        }

        ApplyOptics();
        MarkCameraDirty();
    }

    public void ZoomSteps(int steps)
    {
        if (targetCamera == null)
        {
            return;
        }

        if (targetCamera.orthographic)
        {
            targetCamera.orthographicSize = Mathf.Clamp(
                targetCamera.orthographicSize - orthographicZoomStep * steps,
                minOrthographicSize,
                maxOrthographicSize);
        }
        else
        {
            distance = Mathf.Clamp(distance - steps * 0.28f, 0.25f, 9f);
        }

        ApplyCameraTransform();
        MarkCameraDirty();
    }

    private void UpdateZoom(bool inputHit)
    {
        float scroll = ReadScrollDelta();
        if (Mathf.Abs(scroll) <= 0.01f)
        {
            return;
        }

        if (!inputHit)
        {
            return;
        }

        if (IsCtrlHeld())
        {
            FocalLengthSteps(scroll > 0f ? 1 : -1);
            return;
        }

        if (IsAltHeld())
        {
            SetDepthOfFieldEnabled(true);
            FocusDistanceSteps(scroll > 0f ? 1 : -1);
            return;
        }

        if (IsShiftHeld())
        {
            SetDepthOfFieldEnabled(true);
            ApertureSteps(scroll > 0f ? -1 : 1);
            return;
        }

        ZoomSteps(scroll > 0f ? 1 : -1);
    }

    private static float ReadScrollDelta()
    {
        return TransparentPetRuntimeInput.ScrollY();
    }

    private void UpdateRightDrag(Vector2 cursor, bool inputHit)
    {
        bool rightDown = IsRightMouseDown();
        if (!rightDown)
        {
            _rightPressed = false;
            _rightRotating = false;
            _rightIgnoredUntilRelease = false;
            return;
        }

        if (_rightIgnoredUntilRelease)
        {
            return;
        }

        if (rightDown && !_rightPressed)
        {
            if (!inputHit)
            {
                _rightIgnoredUntilRelease = true;
                return;
            }

            _rightPressed = true;
            _rightRotating = false;
            _rightPressPosition = cursor;
            _lastCursorPosition = cursor;
        }

        if (_rightPressed)
        {
            if (!_rightRotating && Vector2.Distance(_rightPressPosition, cursor) > rightDragThresholdPixels)
            {
                _rightRotating = true;
            }

            if (_rightRotating)
            {
                Vector2 delta = cursor - _lastCursorPosition;
                yawDegrees += delta.x * rotateSensitivity;
                pitchDegrees = Mathf.Clamp(pitchDegrees - delta.y * rotateSensitivity, minPitchDegrees, maxPitchDegrees);
                ApplyCameraTransform();
                MarkCameraDirty();
            }
        }
    }

    private void UpdateMiddlePan(Vector2 cursor, bool inputHit)
    {
        Transform cameraTransform = ResolveCameraTransform();
        bool middleDown = IsMiddleMouseDown();
        if (!middleDown)
        {
            _middlePanning = false;
            _middleIgnoredUntilRelease = false;
            return;
        }

        if (_middleIgnoredUntilRelease)
        {
            return;
        }

        if (middleDown && !_middlePanning)
        {
            if (!inputHit)
            {
                _middleIgnoredUntilRelease = true;
                return;
            }

            _middlePanning = true;
            _lastCursorPosition = cursor;
        }

        if (_middlePanning)
        {
            Vector2 delta = cursor - _lastCursorPosition;
            float panScale = panSensitivity * (targetCamera.orthographic ? targetCamera.orthographicSize : distance);
            PanWorld((-cameraTransform.right * delta.x + cameraTransform.up * delta.y) * panScale);
        }
    }

    private void UpdateKeyboardCamera()
    {
        if (!keyboardMouseControls)
        {
            return;
        }

        if (IsCtrlHeld() && IsKeyDown(resetViewKey))
        {
            ResetView();
        }

        if (IsKeyDown(toggleDepthOfFieldKey))
        {
            SetDepthOfFieldEnabled(!depthOfFieldEnabled);
        }

        float speed = keyboardPanSpeed * (IsShiftHeld() ? 3f : 1f);
        Vector3 pan = Vector3.zero;
        if (IsKeyHeld(KeyCode.A)) pan.x -= 1f;
        if (IsKeyHeld(KeyCode.D)) pan.x += 1f;
        if (IsKeyHeld(KeyCode.Q)) pan.y -= 1f;
        if (IsKeyHeld(KeyCode.E)) pan.y += 1f;
        if (IsKeyHeld(KeyCode.S)) pan.z -= 1f;
        if (IsKeyHeld(KeyCode.W)) pan.z += 1f;
        if (pan.sqrMagnitude > 0.0001f)
        {
            PanLocal(pan.normalized * speed * Time.unscaledDeltaTime);
        }

        float yaw = 0f;
        float pitch = 0f;
        if (IsKeyHeld(KeyCode.LeftArrow)) yaw -= 1f;
        if (IsKeyHeld(KeyCode.RightArrow)) yaw += 1f;
        if (IsKeyHeld(KeyCode.DownArrow)) pitch -= 1f;
        if (IsKeyHeld(KeyCode.UpArrow)) pitch += 1f;
        if (Mathf.Abs(yaw) > 0.001f || Mathf.Abs(pitch) > 0.001f)
        {
            OrbitSteps(yaw * keyboardOrbitSpeed * Time.unscaledDeltaTime, pitch * keyboardOrbitSpeed * Time.unscaledDeltaTime);
        }

        if (IsKeyDown(KeyCode.LeftBracket))
        {
            FocalLengthSteps(-1);
        }

        if (IsKeyDown(KeyCode.RightBracket))
        {
            FocalLengthSteps(1);
        }

        if (IsKeyDown(KeyCode.Semicolon))
        {
            SetDepthOfFieldEnabled(true);
            FocusDistanceSteps(-1);
        }

        if (IsKeyDown(KeyCode.Quote))
        {
            SetDepthOfFieldEnabled(true);
            FocusDistanceSteps(1);
        }
    }

    private void ApplyCameraTransform()
    {
        if (targetCamera == null)
        {
            return;
        }

        Transform cameraTransform = ResolveCameraTransform();
        Vector3 effectiveTarget = GetEffectiveTarget();
        Quaternion orbit = BuildCameraRotation();
        Vector3 cameraOffset = _hasExternalCameraOffset ? _externalCameraOffset : Vector3.zero;
        cameraTransform.position = effectiveTarget + orbit * new Vector3(0f, 0f, -distance) + cameraOffset;
        cameraTransform.LookAt(effectiveTarget, Vector3.up);
        SyncRigTransformFromCamera();
    }

    private void ApplyCameraRotationOnly()
    {
        if (targetCamera == null)
        {
            return;
        }

        Transform cameraTransform = ResolveCameraTransform();
        cameraTransform.rotation = BuildCameraRotation();
        target = cameraTransform.position + cameraTransform.forward * distance - _manualTargetOffset - (_hasExternalTargetOffset ? _externalTargetOffset : Vector3.zero);
        SyncRigTransformFromCamera();
    }

    private Quaternion BuildCameraRotation()
    {
        float externalYaw = _hasExternalOrbitOffset ? _externalYawOffsetDegrees : 0f;
        float externalPitch = _hasExternalOrbitOffset ? _externalPitchOffsetDegrees : 0f;
        Quaternion yaw = Quaternion.AngleAxis(yawDegrees + externalYaw, Vector3.up);
        Quaternion pitch = Quaternion.AngleAxis(Mathf.Clamp(pitchDegrees + externalPitch, minPitchDegrees, maxPitchDegrees), Vector3.right);
        return yaw * pitch;
    }

    private void CaptureOrbitFromForward(Vector3 forward)
    {
        if (forward.sqrMagnitude <= 0.0001f)
        {
            return;
        }

        forward.Normalize();
        Vector3 flatForward = new Vector3(forward.x, 0f, forward.z);
        if (flatForward.sqrMagnitude > 0.0001f)
        {
            yawDegrees = Mathf.Atan2(flatForward.x, flatForward.z) * Mathf.Rad2Deg;
        }

        pitchDegrees = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(forward.y, -0.98f, 0.98f)) * Mathf.Rad2Deg, minPitchDegrees, maxPitchDegrees);
    }

    private void ApplyOptics()
    {
        if (targetCamera == null)
        {
            return;
        }

        depthOfFieldBlurAmount = Mathf.Clamp01(depthOfFieldBlurAmount);
        UpdateLockedDepthOfFieldFocus();

        if (targetCamera.usePhysicalProperties)
        {
            targetCamera.focalLength = Mathf.Clamp(focalLength, minFocalLength, maxFocalLength);
        }

        UniversalAdditionalCameraData cameraData = targetCamera.GetUniversalAdditionalCameraData();
        if (cameraData != null)
        {
            cameraData.renderPostProcessing = true;
        }

        if (!TryGetDepthOfField(out DepthOfField depthOfField))
        {
            return;
        }

        depthOfField.active = depthOfFieldEnabled;
        depthOfField.mode.Override(DepthOfFieldMode.Bokeh);
        depthOfField.focusDistance.Override(focusDistance);
        depthOfField.aperture.Override(Mathf.Lerp(maxDepthOfFieldAperture, minDepthOfFieldAperture, depthOfFieldBlurAmount));
        depthOfField.focalLength.Override(Mathf.Lerp(minDepthOfFieldFocalLength, maxDepthOfFieldFocalLength, depthOfFieldBlurAmount));
        depthOfField.highQualitySampling.Override(true);
    }

    private void UpdateLockedDepthOfFieldFocus()
    {
        if (!lockDepthOfFieldToPet || targetCamera == null)
        {
            return;
        }

        Vector3 focusPoint = _hasLockedFocusTarget ? _lockedFocusTarget : GetEffectiveTarget();
        Vector3 toFocus = focusPoint - targetCamera.transform.position;
        float projectedDistance = Vector3.Dot(toFocus, targetCamera.transform.forward);
        if (projectedDistance <= 0.05f)
        {
            projectedDistance = toFocus.magnitude;
        }

        focusDistance = Mathf.Clamp(projectedDistance, 0.15f, 30f);
    }

    private bool TryGetDepthOfField(out DepthOfField depthOfField)
    {
        depthOfField = null;
        ResolveVolume();
        if (postProcessVolume == null)
        {
            return false;
        }

        VolumeProfile profile = postProcessVolume.profile;
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            postProcessVolume.profile = profile;
        }

        if (!profile.TryGet(out depthOfField))
        {
            depthOfField = profile.Add<DepthOfField>(true);
        }

        return depthOfField != null;
    }

    private Vector3 GetEffectiveTarget()
    {
        return target + _manualTargetOffset + (_hasExternalTargetOffset ? _externalTargetOffset : Vector3.zero);
    }

    public Vector3 EffectiveTarget => GetEffectiveTarget();
    public Vector3 ManualTargetOffset => _manualTargetOffset;
    public Vector3 CameraWorldPosition => ResolveCameraTransform().position;
    public Vector3 CameraWorldForward => ResolveCameraTransform().forward;
    public float CameraYawDegrees => yawDegrees + (_hasExternalOrbitOffset ? _externalYawOffsetDegrees : 0f);
    public float CameraPitchDegrees => Mathf.Clamp(pitchDegrees + (_hasExternalOrbitOffset ? _externalPitchOffsetDegrees : 0f), minPitchDegrees, maxPitchDegrees);
    public bool HasExternalOrbitOffset => _hasExternalOrbitOffset;
    public Vector2 ExternalOrbitOffset => new Vector2(_externalYawOffsetDegrees, _externalPitchOffsetDegrees);
    public bool HasExternalCameraOffset => _hasExternalCameraOffset;
    public Vector3 ExternalCameraOffset => _externalCameraOffset;

    private Transform ResolveCameraTransform()
    {
        return targetCamera != null ? targetCamera.transform : transform;
    }

    private Vector3 GetDrivenTarget()
    {
        return target + (_hasExternalTargetOffset ? _externalTargetOffset : Vector3.zero);
    }

    private void PanWorld(Vector3 worldDelta)
    {
        if (followPlacementTarget)
        {
            _manualTargetOffset += worldDelta;
        }
        else
        {
            target += worldDelta;
        }

        ApplyCameraTransform();
        MarkCameraDirty();
    }

    private void SyncRigTransformFromCamera()
    {
        if (targetCamera == null || targetCamera.transform == transform)
        {
            return;
        }

        transform.SetPositionAndRotation(targetCamera.transform.position, targetCamera.transform.rotation);
    }

    private void ResolveVolume()
    {
        if (postProcessVolume != null)
        {
            ConfigureRuntimeVolume(postProcessVolume);
            return;
        }

        if (!string.IsNullOrWhiteSpace(runtimeVolumeName))
        {
            GameObject existing = GameObject.Find(runtimeVolumeName);
            if (existing != null)
            {
                postProcessVolume = existing.GetComponent<Volume>();
                if (postProcessVolume == null)
                {
                    postProcessVolume = existing.AddComponent<Volume>();
                }

                ConfigureRuntimeVolume(postProcessVolume);
                return;
            }
        }

        GameObject volumeObject = new GameObject(string.IsNullOrWhiteSpace(runtimeVolumeName) ? "TransparentPet Runtime Camera Volume" : runtimeVolumeName);
        postProcessVolume = volumeObject.AddComponent<Volume>();
        ConfigureRuntimeVolume(postProcessVolume);
    }

    private void ConfigureRuntimeVolume(Volume volume)
    {
        if (volume == null)
        {
            return;
        }

        volume.isGlobal = true;
        volume.priority = runtimeVolumePriority;
        volume.weight = 1f;
        if (volume.profile == null)
        {
            volume.profile = ScriptableObject.CreateInstance<VolumeProfile>();
        }
    }

    private void MarkCameraDirty()
    {
        if (!ShouldSaveCamera() || _suppressCameraSave)
        {
            return;
        }

        _cameraSavePending = true;
        _nextCameraSaveTime = Time.unscaledTime + cameraSaveInterval;
    }

    private void FlushPendingCameraSave(bool force)
    {
        if (!ShouldSaveCamera() || string.IsNullOrWhiteSpace(cameraSaveKey))
        {
            return;
        }

        if (!force && (!_cameraSavePending || Time.unscaledTime < _nextCameraSaveTime))
        {
            return;
        }

        SaveCameraState();
        _cameraSavePending = false;
        _nextCameraSaveTime = 0f;
    }

    private void SaveCameraState()
    {
        PersistedCameraState state = new PersistedCameraState
        {
            version = CurrentCameraStateVersion,
            target = target,
            distance = distance,
            yawDegrees = yawDegrees,
            pitchDegrees = pitchDegrees,
            focusDistance = focusDistance,
            aperture = aperture,
            depthOfFieldBlurAmount = depthOfFieldBlurAmount,
            lockDepthOfFieldToPet = lockDepthOfFieldToPet,
            focalLength = focalLength,
            depthOfFieldEnabled = depthOfFieldEnabled,
            manualTargetOffset = _manualTargetOffset,
            followPlacementTarget = followPlacementTarget,
            allowWholeScreenCameraInput = allowWholeScreenCameraInput,
            freeSceneInput = freeSceneInput,
            requirePetHitForInput = requirePetHitForInput,
            cameraPosition = transform.position,
            cameraRotation = transform.rotation,
            cameraOrthographic = targetCamera != null && targetCamera.orthographic,
            orthographicSize = targetCamera != null ? targetCamera.orthographicSize : _defaultOrthographicSize
        };

        PlayerPrefs.SetString(cameraSaveKey, JsonUtility.ToJson(state));
        PlayerPrefs.Save();
    }

    private bool TryLoadCameraState()
    {
        if (string.IsNullOrWhiteSpace(cameraSaveKey) || !PlayerPrefs.HasKey(cameraSaveKey))
        {
            return false;
        }

        try
        {
            PersistedCameraState state = JsonUtility.FromJson<PersistedCameraState>(PlayerPrefs.GetString(cameraSaveKey));
            if (state == null || state.version < 3 || state.distance <= 0.001f)
            {
                return false;
            }

            target = state.target;
            distance = Mathf.Clamp(state.distance, 0.25f, 12f);
            yawDegrees = state.yawDegrees;
            pitchDegrees = Mathf.Clamp(state.pitchDegrees, minPitchDegrees, maxPitchDegrees);
            focusDistance = Mathf.Clamp(state.focusDistance, 0.15f, 30f);
            aperture = Mathf.Clamp(state.aperture, 1.2f, 16f);
            if (state.depthOfFieldBlurAmount > 0f || state.version >= 2)
            {
                depthOfFieldBlurAmount = Mathf.Clamp01(state.depthOfFieldBlurAmount);
            }

            if (state.version >= 2)
            {
                lockDepthOfFieldToPet = state.lockDepthOfFieldToPet;
            }

            focalLength = Mathf.Clamp(state.focalLength > 0f ? state.focalLength : focalLength, minFocalLength, maxFocalLength);
            depthOfFieldEnabled = state.version >= 4 ? state.depthOfFieldEnabled : true;
            _manualTargetOffset = state.version >= 5 ? state.manualTargetOffset : Vector3.zero;
            followPlacementTarget = state.version >= 2 ? state.followPlacementTarget : true;
            if (state.version >= 2)
            {
                allowWholeScreenCameraInput = state.allowWholeScreenCameraInput;
                freeSceneInput = state.freeSceneInput;
                requirePetHitForInput = state.requirePetHitForInput;
            }
            else
            {
                freeSceneInput = false;
                requirePetHitForInput = true;
            }

            EnforceInputScope();

            if (targetCamera != null)
            {
                targetCamera.orthographic = state.cameraOrthographic;
                targetCamera.orthographicSize = Mathf.Clamp(state.orthographicSize > 0f ? state.orthographicSize : targetCamera.orthographicSize, minOrthographicSize, maxOrthographicSize);
                targetCamera.usePhysicalProperties = true;
                targetCamera.focalLength = focalLength;
            }

            if (!followPlacementTarget && IsUsableQuaternion(state.cameraRotation))
            {
                transform.SetPositionAndRotation(state.cameraPosition, NormalizeQuaternion(state.cameraRotation));
                CaptureOrbitFromForward(transform.forward);
                target = transform.position + transform.forward * distance;
                _manualTargetOffset = Vector3.zero;
            }

            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning("Failed to load pet camera state: " + exception.Message);
            return false;
        }
    }

    private void ResetDragState()
    {
        _rightPressed = false;
        _rightRotating = false;
        _rightIgnoredUntilRelease = false;
        _middlePanning = false;
        _middleIgnoredUntilRelease = false;
    }

    private bool ShouldAllowEditorFreeCameraInput()
    {
#if UNITY_EDITOR
        return allowEditorFreeCameraInput;
#else
        return false;
#endif
    }

    private bool ShouldUseSavedCamera()
    {
        if (!Application.isPlaying || !persistCameraState)
        {
            return false;
        }

#if UNITY_EDITOR
        return useSavedCameraInEditor || HasManualCameraSave;
#else
        return true;
#endif
    }

    private bool ShouldSaveCamera()
    {
        if (!Application.isPlaying || !persistCameraState)
        {
            return false;
        }

#if UNITY_EDITOR
        return saveCameraInEditor;
#else
        return true;
#endif
    }

    private void EnforceInputScope()
    {
        if (!allowWholeScreenCameraInput)
        {
            freeSceneInput = false;
            requirePetHitForInput = true;
        }
    }

    private void MarkManualCameraSave(bool enabled)
    {
        if (string.IsNullOrWhiteSpace(ManualCameraSaveMarkerKey))
        {
            return;
        }

        if (enabled)
        {
            PlayerPrefs.SetInt(ManualCameraSaveMarkerKey, 1);
        }
        else
        {
            PlayerPrefs.DeleteKey(ManualCameraSaveMarkerKey);
        }
    }

    private static bool IsRightMouseDown()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        return (GetAsyncKeyState(VkRButton) & unchecked((short)0x8000)) != 0;
#else
        return TransparentPetRuntimeInput.MouseButtonHeld(1);
#endif
    }

    private static bool IsMiddleMouseDown()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        return (GetAsyncKeyState(VkMButton) & unchecked((short)0x8000)) != 0;
#else
        return TransparentPetRuntimeInput.MouseButtonHeld(2);
#endif
    }

    private static bool IsKeyDown(KeyCode keyCode)
    {
        return TransparentPetRuntimeInput.KeyDown(keyCode);
    }

    private static bool IsKeyHeld(KeyCode keyCode)
    {
        return TransparentPetRuntimeInput.KeyHeld(keyCode);
    }

    private static bool IsShiftHeld()
    {
        return IsKeyHeld(KeyCode.LeftShift) || IsKeyHeld(KeyCode.RightShift);
    }

    private static bool IsCtrlHeld()
    {
        return IsKeyHeld(KeyCode.LeftControl) || IsKeyHeld(KeyCode.RightControl);
    }

    private static bool IsAltHeld()
    {
        return IsKeyHeld(KeyCode.LeftAlt) || IsKeyHeld(KeyCode.RightAlt);
    }

    private static bool IsUsableQuaternion(Quaternion value)
    {
        return value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w > 0.0001f;
    }

    private static Quaternion NormalizeQuaternion(Quaternion value)
    {
        float length = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
        if (length <= 0.0001f)
        {
            return Quaternion.identity;
        }

        return new Quaternion(value.x / length, value.y / length, value.z / length, value.w / length);
    }

    [System.Serializable]
    private sealed class PersistedCameraState
    {
        public int version;
        public Vector3 target;
        public float distance;
        public float yawDegrees;
        public float pitchDegrees;
        public float focusDistance;
        public float aperture;
        public float depthOfFieldBlurAmount;
        public bool lockDepthOfFieldToPet;
        public float focalLength;
        public bool depthOfFieldEnabled;
        public Vector3 manualTargetOffset;
        public bool followPlacementTarget;
        public bool allowWholeScreenCameraInput;
        public bool freeSceneInput;
        public bool requirePetHitForInput;
        public Vector3 cameraPosition;
        public Quaternion cameraRotation;
        public bool cameraOrthographic;
        public float orthographicSize;
    }
}
