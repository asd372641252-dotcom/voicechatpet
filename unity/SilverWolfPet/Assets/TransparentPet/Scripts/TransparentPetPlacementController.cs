using System.Collections;
using UnityEngine;

[ExecuteAlways]
public sealed class TransparentPetPlacementController : MonoBehaviour
{
    [Tooltip("The tuned scene position used as the local zero point for this pet.")]
    public Transform tunedOrigin;

    [Tooltip("Local movement from the tuned origin. X = left/right, Y = up/down, Z = front/back.")]
    public Vector3 offsetFromOrigin = Vector3.zero;

    [Min(0.01f)]
    [Tooltip("Uniform pet scale relative to the tuned size.")]
    public float uniformScale = 1f;

    [Tooltip("Local rotation in degrees. Yaw can be edited freely through 360 degrees.")]
    public Vector3 eulerDegrees = Vector3.zero;

    [Tooltip("Enable to apply the placement fields once. It turns itself off so Unity's transform tools stay authoritative.")]
    public bool applyInspectorValues;

    public TransparentPetFreeCamera freeCamera;
    public Vector3 cameraTargetLocalOffset = new Vector3(0f, 0.75f, 0f);
    public bool lockCameraTargetToPet = true;
    public bool useModelFocusForCameraTarget = true;
    public string frontReferenceName = "PetModelRoot";
    public Vector3 frontCameraDirectionInReferenceSpace = Vector3.forward;
    public bool invertFrontFocusDirection;
    public bool autoFocusCameraOnEditorPlay = true;
    public bool autoChooseUnblockedCameraSide = true;
    [Min(0f)]
    public float autoFocusDelaySeconds = 0.1f;
    public bool captureCurrentAsRuntimeOriginOnPlay = true;
    public bool runtimeKeyboardMouseControls = true;
    public KeyCode placementModeKey = KeyCode.P;
    public KeyCode focusPetKey = KeyCode.F;
    public KeyCode bringPetToCameraKey = KeyCode.G;
    public KeyCode setOriginKey = KeyCode.O;
    public KeyCode resetToOriginKey = KeyCode.Home;
    public float keyboardMoveSpeed = 0.55f;
    public float keyboardFastMultiplier = 3f;
    public float keyboardRotateSpeed = 95f;
    public float wheelScaleSpeed = 0.12f;
    public float mouseRotateSensitivity = 0.22f;
    public bool useFootPivotForRuntimePlacement = true;
    public bool rightDragYawOnly = true;
    [Min(0f)]
    public float footPivotDownOffset = 0.03f;
    public bool persistRuntimePlacement = true;
    public bool useSavedPlacementInEditor;
    public bool savePlacementInEditor;
    public string placementSaveKey = "TransparentPet.Placement.v1";
    [Min(0.05f)]
    public float placementSaveInterval = 0.35f;

    private bool _runtimeOriginCaptured;
    private bool _placementMode;
    private bool _previousFreeCameraInput;
    private bool _rightRotating;
    private bool _placementModeKeyWasHeld;
    private bool _placementSavePending;
    private bool _suppressPlacementSave;
    private float _ignorePlacementHotkeyUntil;
    private float _nextPlacementSaveTime;
    private Vector2 _lastMousePosition;
    private Vector3 _runtimeOriginLocalPosition;
    private Quaternion _runtimeOriginLocalRotation = Quaternion.identity;
    private Vector3 _runtimeOriginLocalScale = Vector3.one;
    private Vector3 _runtimeFootPivotLocal;
    private Vector3 _runtimeFootPivotParentPosition;
    private Coroutine _autoFocusCoroutine;
    private Vector3 _lastSyncedFocusPoint;
    private bool _hasLastSyncedFocusPoint;

    public Vector3 RuntimeOffset => offsetFromOrigin;
    public Vector3 RuntimeEulerDegrees => eulerDegrees;
    public float RuntimeUniformScale => uniformScale;
    public bool PlacementMode => _placementMode;
    public bool CameraTargetLockedToPet => lockCameraTargetToPet;
    public bool CameraFollowsCharacterMotion => useModelFocusForCameraTarget;
    public bool HasSavedPlacement => !string.IsNullOrWhiteSpace(placementSaveKey) && PlayerPrefs.HasKey(placementSaveKey);
    private string ManualPlacementSaveMarkerKey => string.IsNullOrWhiteSpace(placementSaveKey) ? "" : placementSaveKey + ".ManualUserSave";
    private bool HasManualPlacementSave => !string.IsNullOrWhiteSpace(ManualPlacementSaveMarkerKey) && PlayerPrefs.GetInt(ManualPlacementSaveMarkerKey, 0) == 1;

    private void Start()
    {
        _placementMode = false;
        _placementModeKeyWasHeld = IsKeyHeld(placementModeKey) && !IsCtrlHeld();
        _ignorePlacementHotkeyUntil = Time.unscaledTime + 0.15f;
        if (!Application.isPlaying)
        {
            return;
        }

        if (ShouldUseSavedPlacement() && TryLoadRuntimePlacement())
        {
            return;
        }

        if (captureCurrentAsRuntimeOriginOnPlay)
        {
            _suppressPlacementSave = true;
            CaptureRuntimeOriginFromCurrent();
            _suppressPlacementSave = false;
        }

        QueueInitialCameraFocus();
    }

    private void QueueInitialCameraFocus()
    {
#if UNITY_EDITOR
        if (!autoFocusCameraOnEditorPlay)
        {
            return;
        }

        if (HasSavedPlacement || (freeCamera != null && freeCamera.HasSavedCamera))
        {
            return;
        }

        if (_autoFocusCoroutine != null)
        {
            StopCoroutine(_autoFocusCoroutine);
        }

        _autoFocusCoroutine = StartCoroutine(FocusCameraAfterSceneSettles());
#endif
    }

    private IEnumerator FocusCameraAfterSceneSettles()
    {
        yield return null;
        if (autoFocusDelaySeconds > 0f)
        {
            yield return new WaitForSecondsRealtime(autoFocusDelaySeconds);
        }

        FocusPet(false);
        _autoFocusCoroutine = null;
    }

    private void OnValidate()
    {
        uniformScale = Mathf.Max(0.01f, uniformScale);
        if (applyInspectorValues)
        {
            ApplyInspectorValues();
            applyInspectorValues = false;
        }
        else
        {
            CaptureCurrentTransformValues();
        }

        SyncCameraTarget();
    }

    private void LateUpdate()
    {
        if (freeCamera == null)
        {
            freeCamera = FindAnyObjectByType<TransparentPetFreeCamera>();
        }

        if (!Application.isPlaying && !applyInspectorValues)
        {
            CaptureCurrentTransformValues();
        }

        if (Application.isPlaying)
        {
            UpdateRuntimeKeyboardMouseControls();
            FlushPendingPlacementSave(false);
        }

        SyncCameraTarget();
    }

    private void OnDisable()
    {
        if (_autoFocusCoroutine != null)
        {
            StopCoroutine(_autoFocusCoroutine);
            _autoFocusCoroutine = null;
        }

        FlushPendingPlacementSave(true);
    }

    private void OnApplicationQuit()
    {
        FlushPendingPlacementSave(true);
    }

    [ContextMenu("Apply Inspector Values")]
    public void ApplyInspectorValues()
    {
        transform.localPosition = offsetFromOrigin;
        transform.localRotation = Quaternion.Euler(eulerDegrees);
        transform.localScale = Vector3.one * uniformScale;
        SyncCameraTarget();
        MarkPlacementDirty();
    }

    public void NudgeFromRuntimeOrigin(Vector3 localDelta)
    {
        EnsureRuntimeOriginCaptured();
        offsetFromOrigin += localDelta;
        ApplyRuntimePlacement();
        MarkPlacementDirty();
    }

    public void RotateFromRuntimeOrigin(Vector3 eulerDelta)
    {
        EnsureRuntimeOriginCaptured();
        eulerDegrees = NormalizeEuler(eulerDegrees + eulerDelta);
        ApplyRuntimePlacement();
        MarkPlacementDirty();
    }

    public void ScaleFromRuntimeOrigin(float multiplier)
    {
        EnsureRuntimeOriginCaptured();
        uniformScale = Mathf.Clamp(uniformScale * multiplier, 0.05f, 8f);
        ApplyRuntimePlacement();
        MarkPlacementDirty();
    }

    public void ResetRuntimePlacement()
    {
        EnsureRuntimeOriginCaptured();
        offsetFromOrigin = Vector3.zero;
        eulerDegrees = Vector3.zero;
        uniformScale = 1f;
        ApplyRuntimePlacement();
        MarkPlacementDirty();
    }

    public void SaveUserPlacementNow()
    {
        EnsureRuntimeOriginCaptured();
        MarkManualPlacementSave(true);
        SaveRuntimePlacement();
        _placementSavePending = false;
        _nextPlacementSaveTime = 0f;
    }

    public void ClearSavedPlacement()
    {
        if (!string.IsNullOrWhiteSpace(placementSaveKey))
        {
            PlayerPrefs.DeleteKey(placementSaveKey);
            if (!string.IsNullOrWhiteSpace(ManualPlacementSaveMarkerKey))
            {
                PlayerPrefs.DeleteKey(ManualPlacementSaveMarkerKey);
            }

            PlayerPrefs.Save();
        }

        _placementSavePending = false;
        _nextPlacementSaveTime = 0f;
    }

    public void ResetToFactoryDefault()
    {
        bool previousSuppress = _suppressPlacementSave;
        _suppressPlacementSave = true;
        ClearSavedPlacement();
        offsetFromOrigin = Vector3.zero;
        eulerDegrees = Vector3.zero;
        uniformScale = 1f;
        ApplyInspectorValues();
        CaptureRuntimeOriginSnapshot();
        SyncCameraTarget(true);
        _suppressPlacementSave = previousSuppress;
    }

    public void SetPlacementMode(bool enabled)
    {
        if (_placementMode == enabled)
        {
            return;
        }

        _placementMode = enabled;
        _rightRotating = false;
        if (enabled)
        {
            CaptureRuntimeOriginFromCurrent();
        }

        if (freeCamera != null)
        {
            if (enabled)
            {
                _previousFreeCameraInput = freeCamera.enabledInput;
                freeCamera.enabledInput = false;
                freeCamera.SetFollowPlacementTarget(lockCameraTargetToPet);
            }
            else
            {
                freeCamera.enabledInput = _previousFreeCameraInput;
            }
        }

        Debug.Log("Pet placement mode " + (enabled ? "on" : "off"));
    }

    public void SetCameraFollowsCharacterMotion(bool enabled)
    {
        bool changed = useModelFocusForCameraTarget != enabled;
        useModelFocusForCameraTarget = enabled;

        SyncCameraTarget(true);
        if (changed)
        {
            MarkPlacementDirty();
        }
    }

    public void SetCameraTargetLockedToPet(bool enabled)
    {
        bool changed = lockCameraTargetToPet != enabled;
        lockCameraTargetToPet = enabled;
        if (freeCamera != null)
        {
            freeCamera.SetFollowPlacementTarget(enabled);
        }

        SyncCameraTarget(enabled);
        if (changed)
        {
            MarkPlacementDirty();
        }
    }

    public void CaptureRuntimeOriginFromCurrent()
    {
        CaptureRuntimeOriginSnapshot();
        offsetFromOrigin = Vector3.zero;
        eulerDegrees = Vector3.zero;
        uniformScale = 1f;
        SyncCameraTarget();
        MarkPlacementDirty();
    }

    public void ApplyRuntimePlacement()
    {
        EnsureRuntimeOriginCaptured();
        Quaternion nextLocalRotation = _runtimeOriginLocalRotation * Quaternion.Euler(eulerDegrees);
        Vector3 nextLocalScale = new Vector3(
            _runtimeOriginLocalScale.x * uniformScale,
            _runtimeOriginLocalScale.y * uniformScale,
            _runtimeOriginLocalScale.z * uniformScale);
        if (useFootPivotForRuntimePlacement)
        {
            Vector3 desiredPivotParentPosition = _runtimeFootPivotParentPosition + _runtimeOriginLocalRotation * offsetFromOrigin;
            Vector3 scaledPivotLocal = Vector3.Scale(_runtimeFootPivotLocal, nextLocalScale);
            transform.localPosition = desiredPivotParentPosition - nextLocalRotation * scaledPivotLocal;
        }
        else
        {
            transform.localPosition = _runtimeOriginLocalPosition + _runtimeOriginLocalRotation * offsetFromOrigin;
        }

        transform.localRotation = nextLocalRotation;
        transform.localScale = nextLocalScale;
        SyncCameraTarget();
    }

    [ContextMenu("Capture Current Transform")]
    public void CaptureCurrentTransform()
    {
        CaptureCurrentTransformValues();
        SyncCameraTarget();
        MarkPlacementDirty();
    }

    [ContextMenu("Set Current Placement As Tuned Origin")]
    public void SetCurrentPlacementAsTunedOrigin()
    {
        if (tunedOrigin == null)
        {
            CaptureCurrentTransform();
            return;
        }

        Vector3 currentPosition = transform.position;
        Quaternion currentRotation = transform.rotation;
        Vector3 currentScale = transform.localScale;

        if (transform.parent != tunedOrigin)
        {
            transform.SetParent(tunedOrigin, true);
        }

        tunedOrigin.position = currentPosition;
        tunedOrigin.rotation = currentRotation;
        tunedOrigin.localScale = currentScale;

        offsetFromOrigin = Vector3.zero;
        eulerDegrees = Vector3.zero;
        uniformScale = 1f;
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;
        transform.localScale = Vector3.one;
        SyncCameraTarget();
        MarkPlacementDirty();
    }

    [ContextMenu("Reset To Tuned Origin")]
    public void ResetToTunedOrigin()
    {
        offsetFromOrigin = Vector3.zero;
        eulerDegrees = Vector3.zero;
        uniformScale = 1f;
        ApplyInspectorValues();
        MarkPlacementDirty();
    }

    private void SyncCameraTarget(bool force = false)
    {
        if (freeCamera == null)
        {
            return;
        }

        if (Application.isPlaying && !lockCameraTargetToPet)
        {
            return;
        }

        if (Application.isPlaying && lockCameraTargetToPet && !freeCamera.followPlacementTarget)
        {
            freeCamera.SetFollowPlacementTarget(true);
        }

        Vector3 focusPoint = transform.TransformPoint(cameraTargetLocalOffset);
        if (useModelFocusForCameraTarget)
        {
            ResolveFocusTarget(out focusPoint, out _);
        }

        if (!force && _hasLastSyncedFocusPoint && Vector3.Distance(focusPoint, _lastSyncedFocusPoint) < 0.001f)
        {
            return;
        }

        _lastSyncedFocusPoint = focusPoint;
        _hasLastSyncedFocusPoint = true;
        freeCamera.SetExternalTarget(focusPoint);
    }

    private void CaptureCurrentTransformValues()
    {
        offsetFromOrigin = transform.localPosition;
        eulerDegrees = NormalizeEuler(transform.localEulerAngles);
        uniformScale = Mathf.Max(0.01f, transform.localScale.x);
    }

    private void EnsureRuntimeOriginCaptured()
    {
        if (!_runtimeOriginCaptured)
        {
            CaptureRuntimeOriginSnapshot();
        }
    }

    private void MarkPlacementDirty()
    {
        if (!ShouldSavePlacement() || _suppressPlacementSave)
        {
            return;
        }

        _placementSavePending = true;
        _nextPlacementSaveTime = Time.unscaledTime + placementSaveInterval;
    }

    private void FlushPendingPlacementSave(bool force)
    {
        if (!ShouldSavePlacement() || string.IsNullOrWhiteSpace(placementSaveKey))
        {
            return;
        }

        if (!force && (!_placementSavePending || Time.unscaledTime < _nextPlacementSaveTime))
        {
            return;
        }

        SaveRuntimePlacement();
        _placementSavePending = false;
        _nextPlacementSaveTime = 0f;
    }

    private void SaveRuntimePlacement()
    {
        PersistedPlacementState state = new PersistedPlacementState
        {
            version = 5,
            localPosition = transform.localPosition,
            localRotation = transform.localRotation,
            localScale = transform.localScale,
            offsetFromOrigin = offsetFromOrigin,
            eulerDegrees = eulerDegrees,
            uniformScale = uniformScale,
            lockCameraTargetToPet = lockCameraTargetToPet,
            useModelFocusForCameraTarget = useModelFocusForCameraTarget,
            hasRuntimeOrigin = _runtimeOriginCaptured,
            runtimeOriginLocalPosition = _runtimeOriginLocalPosition,
            runtimeOriginLocalRotation = _runtimeOriginLocalRotation,
            runtimeOriginLocalScale = _runtimeOriginLocalScale,
            runtimeFootPivotLocal = _runtimeFootPivotLocal,
            runtimeFootPivotParentPosition = _runtimeFootPivotParentPosition
        };

        PlayerPrefs.SetString(placementSaveKey, JsonUtility.ToJson(state));
        PlayerPrefs.Save();
    }

    private bool TryLoadRuntimePlacement()
    {
        if (string.IsNullOrWhiteSpace(placementSaveKey) || !PlayerPrefs.HasKey(placementSaveKey))
        {
            return false;
        }

        try
        {
            PersistedPlacementState state = JsonUtility.FromJson<PersistedPlacementState>(PlayerPrefs.GetString(placementSaveKey));
            if (state == null || state.version < 3 || state.uniformScale <= 0.001f)
            {
                return false;
            }

            transform.localPosition = state.localPosition;
            transform.localRotation = IsUsableQuaternion(state.localRotation) ? NormalizeQuaternion(state.localRotation) : Quaternion.Euler(state.eulerDegrees);
            transform.localScale = state.localScale.sqrMagnitude > 0.0001f ? state.localScale : Vector3.one * state.uniformScale;
            offsetFromOrigin = state.offsetFromOrigin;
            eulerDegrees = NormalizeEuler(state.eulerDegrees);
            uniformScale = Mathf.Max(0.01f, state.uniformScale);
            lockCameraTargetToPet = state.version >= 5 ? state.lockCameraTargetToPet : true;
            if (state.version >= 4)
            {
                useModelFocusForCameraTarget = state.useModelFocusForCameraTarget;
            }

            if (state.hasRuntimeOrigin && IsUsableQuaternion(state.runtimeOriginLocalRotation))
            {
                _runtimeOriginLocalPosition = state.runtimeOriginLocalPosition;
                _runtimeOriginLocalRotation = NormalizeQuaternion(state.runtimeOriginLocalRotation);
                _runtimeOriginLocalScale = state.runtimeOriginLocalScale.sqrMagnitude > 0.0001f ? state.runtimeOriginLocalScale : transform.localScale;
                _runtimeFootPivotLocal = state.runtimeFootPivotLocal;
                _runtimeFootPivotParentPosition = state.runtimeFootPivotParentPosition;
                _runtimeOriginCaptured = true;
            }
            else
            {
                CaptureRuntimeOriginSnapshot();
            }

            if (freeCamera != null)
            {
                freeCamera.SetFollowPlacementTarget(lockCameraTargetToPet);
            }

            SyncCameraTarget(lockCameraTargetToPet);
            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning("Failed to load pet placement state: " + exception.Message);
            return false;
        }
    }

    private void UpdateRuntimeKeyboardMouseControls()
    {
        if (!runtimeKeyboardMouseControls)
        {
            return;
        }

        bool placementKeyHeld = IsKeyHeld(placementModeKey) && !IsCtrlHeld();
        if (Time.unscaledTime >= _ignorePlacementHotkeyUntil && placementKeyHeld && !_placementModeKeyWasHeld)
        {
            SetPlacementMode(!_placementMode);
        }
        _placementModeKeyWasHeld = placementKeyHeld;

        if (IsCtrlHeld() && (IsKeyDown(focusPetKey) || (!_placementMode && (IsKeyDown(KeyCode.Alpha0) || IsKeyDown(KeyCode.Keypad0)))))
        {
            FocusPet(IsShiftHeld());
            return;
        }

        if (IsCtrlHeld() && IsKeyDown(bringPetToCameraKey))
        {
            BringPetToCameraView();
            return;
        }

        if (!_placementMode)
        {
            return;
        }

        if (IsKeyDown(KeyCode.Escape))
        {
            SetPlacementMode(false);
            return;
        }

        EnsureRuntimeOriginCaptured();

        if (IsCtrlHeld() && IsKeyDown(setOriginKey))
        {
            CaptureRuntimeOriginFromCurrent();
        }

        if (IsCtrlHeld() && (IsKeyDown(resetToOriginKey) || IsKeyDown(KeyCode.Alpha0) || IsKeyDown(KeyCode.Keypad0)))
        {
            ResetRuntimePlacement();
        }

        float speed = keyboardMoveSpeed * (IsShiftHeld() ? keyboardFastMultiplier : 1f);
        Vector3 move = Vector3.zero;
        if (IsKeyHeld(KeyCode.A) || IsKeyHeld(KeyCode.LeftArrow)) move.x -= 1f;
        if (IsKeyHeld(KeyCode.D) || IsKeyHeld(KeyCode.RightArrow)) move.x += 1f;
        if (IsKeyHeld(KeyCode.S)) move.z -= 1f;
        if (IsKeyHeld(KeyCode.W)) move.z += 1f;
        if (IsKeyHeld(KeyCode.Q) || IsKeyHeld(KeyCode.DownArrow)) move.y -= 1f;
        if (IsKeyHeld(KeyCode.E) || IsKeyHeld(KeyCode.UpArrow)) move.y += 1f;
        if (move.sqrMagnitude > 0.0001f)
        {
            NudgeFromRuntimeOrigin(move.normalized * speed * Time.unscaledDeltaTime);
        }

        float rotate = 0f;
        if (IsKeyHeld(KeyCode.Z)) rotate -= 1f;
        if (IsKeyHeld(KeyCode.X)) rotate += 1f;
        if (Mathf.Abs(rotate) > 0.001f)
        {
            RotateFromRuntimeOrigin(new Vector3(0f, rotate * keyboardRotateSpeed * Time.unscaledDeltaTime, 0f));
        }

        float scroll = ReadScrollDelta();
        if (Mathf.Abs(scroll) > 0.01f)
        {
            ScaleFromRuntimeOrigin(1f + scroll * wheelScaleSpeed);
        }

        UpdateMouseRotation();
    }

    private bool ShouldUseSavedPlacement()
    {
        if (!Application.isPlaying || !persistRuntimePlacement)
        {
            return false;
        }

#if UNITY_EDITOR
        return useSavedPlacementInEditor || HasManualPlacementSave;
#else
        return true;
#endif
    }

    private bool ShouldSavePlacement()
    {
        if (!Application.isPlaying || !persistRuntimePlacement)
        {
            return false;
        }

#if UNITY_EDITOR
        return savePlacementInEditor;
#else
        return true;
#endif
    }

    public void FocusPet()
    {
        FocusPet(false);
    }

    public void FocusPet(bool useOppositeSide)
    {
        SetPlacementMode(false);
        EnsureRuntimeOriginCaptured();
        if (freeCamera == null)
        {
            freeCamera = FindAnyObjectByType<TransparentPetFreeCamera>();
        }

        if (freeCamera == null)
        {
            return;
        }

        ResolveFocusTarget(out Vector3 focusPoint, out float radius);
        Vector3 cameraDirection = ResolveFrontCameraDirection(useOppositeSide);
        if (autoChooseUnblockedCameraSide && !useOppositeSide)
        {
            cameraDirection = ResolveBestCameraDirection(focusPoint, radius, cameraDirection);
        }

        Transform focusReference = ResolveFrontReference();
        freeCamera.enabledInput = true;
        freeCamera.FocusOnFromDirection(focusPoint, cameraDirection, ResolveFocusDistance(radius), Mathf.Clamp(radius * 1.15f, 0.85f, 3.5f), lockCameraTargetToPet);
        freeCamera.SetFollowPlacementTarget(lockCameraTargetToPet);
        SyncCameraTarget(true);
        Debug.Log("Pet front focus reference " + (focusReference != null ? focusReference.name : "<none>") + " pos " + (focusReference != null ? focusReference.position.ToString("F3") : "<none>") + " center " + focusPoint.ToString("F3") + " radius " + radius.ToString("F3") + " cameraDir " + cameraDirection.ToString("F3"));
    }

    public void BringPetToCameraView()
    {
        EnsureRuntimeOriginCaptured();
        if (freeCamera == null)
        {
            freeCamera = FindAnyObjectByType<TransparentPetFreeCamera>();
        }

        Camera camera = freeCamera != null && freeCamera.targetCamera != null ? freeCamera.targetCamera : Camera.main;
        if (camera == null)
        {
            return;
        }

        ResolveFocusTarget(out Vector3 focusPoint, out float radius);
        float distanceFromCamera = Mathf.Clamp(radius * 2.4f, 1.6f, 3.4f);
        Vector3 desiredFocusPoint = camera.transform.position + camera.transform.forward * distanceFromCamera;
        transform.position += desiredFocusPoint - focusPoint;
        CaptureRuntimeOriginFromCurrent();
        ResolveFocusTarget(out focusPoint, out radius);

        if (freeCamera != null)
        {
            freeCamera.enabledInput = true;
            freeCamera.FocusOnFromDirection(focusPoint, ResolveFrontCameraDirection(false), ResolveFocusDistance(radius), Mathf.Clamp(radius * 1.15f, 0.85f, 3.5f), lockCameraTargetToPet);
            freeCamera.SetFollowPlacementTarget(lockCameraTargetToPet);
            SyncCameraTarget(true);
        }

        MarkPlacementDirty();
        Debug.Log("Pet brought to camera view center " + focusPoint.ToString("F3") + " radius " + radius.ToString("F3"));
    }

    private void ResolveFocusTarget(out Vector3 focusPoint, out float radius)
    {
        TryResolveStableModelFocus(out Vector3 stableFocusPoint, out float stableRadius);
        Animator animator = GetComponentInChildren<Animator>(true);
        if (animator != null && animator.isHuman)
        {
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            Transform chest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
            if (chest == null)
            {
                chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            }

            if (head != null)
            {
                ResolveFocusBounds(out Vector3 boundsCenter, out radius);
                Vector3 focus = head.position;
                if (chest != null)
                {
                    focus = Vector3.Lerp(chest.position, head.position, 0.58f);
                }

                focusPoint = new Vector3(focus.x, focus.y, focus.z);
                radius = Mathf.Clamp(radius, 0.7f, 2.6f);
                if (IsFocusPointReasonable(focusPoint, stableFocusPoint, stableRadius))
                {
                    return;
                }
            }
        }

        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        if (TryBuildRendererBounds(renderers, out Bounds bounds))
        {
            Vector3 rendererFocus = new Vector3(bounds.center.x, Mathf.Lerp(bounds.min.y, bounds.max.y, 0.62f), bounds.center.z);
            float rendererRadius = Mathf.Max(0.35f, bounds.extents.magnitude);
            if (IsFocusPointReasonable(rendererFocus, stableFocusPoint, stableRadius))
            {
                focusPoint = rendererFocus;
                radius = rendererRadius;
                return;
            }
        }

        focusPoint = stableFocusPoint;
        radius = stableRadius;
    }

    private static float ResolveFocusDistance(float radius)
    {
        return Mathf.Clamp(radius * 1.05f - 0.84f, 0.25f, 0.55f);
    }

    private Vector3 ResolveFrontCameraDirection(bool useOppositeSide)
    {
        Transform reference = ResolveFrontReference();
        Vector3 localDirection = frontCameraDirectionInReferenceSpace.sqrMagnitude > 0.0001f ? frontCameraDirectionInReferenceSpace.normalized : Vector3.forward;
        Vector3 direction = reference != null ? reference.TransformDirection(localDirection) : transform.TransformDirection(localDirection);
        direction.y = 0f;
        if (direction.sqrMagnitude <= 0.0001f)
        {
            direction = transform.forward;
            direction.y = 0f;
        }

        direction.Normalize();
        if (invertFrontFocusDirection ^ useOppositeSide)
        {
            direction = -direction;
        }

        return direction;
    }

    private Vector3 ResolveBestCameraDirection(Vector3 focusPoint, float radius, Vector3 preferredDirection)
    {
        if (preferredDirection.sqrMagnitude <= 0.0001f)
        {
            preferredDirection = Vector3.back;
        }

        preferredDirection.y = 0f;
        preferredDirection.Normalize();

        Transform reference = ResolveFrontReference();
        Vector3 front = ResolveFrontCameraDirection(false);
        Vector3 right = reference != null ? reference.right : transform.right;
        right.y = 0f;
        if (right.sqrMagnitude <= 0.0001f)
        {
            right = Vector3.Cross(Vector3.up, front);
        }

        right.Normalize();

        Vector3 currentDirection = preferredDirection;
        if (freeCamera != null)
        {
            Vector3 fromFocusToCamera = freeCamera.transform.position - focusPoint;
            fromFocusToCamera.y = 0f;
            if (fromFocusToCamera.sqrMagnitude > 0.0001f)
            {
                currentDirection = fromFocusToCamera.normalized;
            }
        }

        Vector3[] candidates =
        {
            preferredDirection,
            -preferredDirection,
            currentDirection,
            front,
            -front,
            right,
            -right
        };

        Vector3 bestDirection = preferredDirection;
        float bestScore = float.PositiveInfinity;
        for (int i = 0; i < candidates.Length; i++)
        {
            Vector3 candidate = candidates[i];
            candidate.y = 0f;
            if (candidate.sqrMagnitude <= 0.0001f)
            {
                continue;
            }

            candidate.Normalize();
            float score = ScoreCameraDirection(focusPoint, radius, candidate, preferredDirection);
            if (score < bestScore)
            {
                bestScore = score;
                bestDirection = candidate;
            }
        }

        return bestDirection;
    }

    private float ScoreCameraDirection(Vector3 focusPoint, float radius, Vector3 cameraDirection, Vector3 preferredDirection)
    {
        float distanceToCamera = ResolveFocusDistance(radius);
        Vector3 cameraPosition = focusPoint + cameraDirection * distanceToCamera;
        Vector3 rayDirection = focusPoint - cameraPosition;
        float rayLength = rayDirection.magnitude;
        if (rayLength <= 0.0001f)
        {
            return float.PositiveInfinity;
        }

        rayDirection /= rayLength;
        Ray sightRay = new Ray(cameraPosition, rayDirection);
        float score = Vector3.Angle(cameraDirection, preferredDirection) * 0.02f;
        Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Exclude);
        float focusPadding = Mathf.Clamp(radius * 0.2f, 0.12f, 0.45f);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled || renderer.transform.IsChildOf(transform))
            {
                continue;
            }

            Bounds bounds = renderer.bounds;
            if (bounds.extents.sqrMagnitude <= 0.0001f)
            {
                continue;
            }

            if (bounds.SqrDistance(cameraPosition) <= 0.0001f)
            {
                score += 50f;
            }

            if (bounds.IntersectRay(sightRay, out float hitDistance) && hitDistance > 0.03f && hitDistance < rayLength - focusPadding)
            {
                score += 15f + Mathf.Clamp(bounds.extents.magnitude, 0f, 5f);
            }
        }

        return score;
    }

    private Transform ResolveFrontReference()
    {
        string referenceName = string.IsNullOrWhiteSpace(frontReferenceName) ? "PetModelRoot" : frontReferenceName;
        if (!string.IsNullOrWhiteSpace(referenceName))
        {
            Transform[] children = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child != null && child.name == referenceName)
                {
                    return child;
                }
            }
        }

        Animator animator = GetComponentInChildren<Animator>(true);
        return animator != null ? animator.transform : transform;
    }

    private void ResolveFocusBounds(out Vector3 focusPoint, out float radius)
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        if (TryBuildRendererBounds(renderers, out Bounds bounds))
        {
            focusPoint = bounds.center;
            radius = Mathf.Max(0.35f, bounds.extents.magnitude);
            return;
        }

        focusPoint = transform.TransformPoint(cameraTargetLocalOffset);
        radius = Mathf.Max(0.35f, transform.lossyScale.magnitude);
    }

    private bool TryResolveStableModelFocus(out Vector3 focusPoint, out float radius)
    {
        Transform reference = ResolveFrontReference();
        if (reference == null)
        {
            focusPoint = transform.TransformPoint(cameraTargetLocalOffset);
            radius = Mathf.Max(0.75f, transform.lossyScale.magnitude);
            return false;
        }

        float scale = Mathf.Max(reference.lossyScale.x, Mathf.Max(reference.lossyScale.y, reference.lossyScale.z));
        float focusHeight = Mathf.Clamp(scale * 1.45f, 0.38f, 1.25f);
        focusPoint = reference.position + Vector3.up * focusHeight;
        radius = Mathf.Clamp(scale * 3.4f, 0.85f, 2.2f);
        return true;
    }

    private static bool IsFocusPointReasonable(Vector3 focusPoint, Vector3 stableFocusPoint, float stableRadius)
    {
        float maxDistance = Mathf.Max(2.5f, stableRadius * 4.5f);
        return Vector3.Distance(focusPoint, stableFocusPoint) <= maxDistance;
    }

    private static bool TryBuildRendererBounds(Renderer[] renderers, out Bounds bounds)
    {
        bool hasBounds = false;
        bounds = default;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return hasBounds;
    }

    private void UpdateMouseRotation()
    {
        bool rightDown = TransparentPetRuntimeInput.MouseButtonHeld(1);
        if (!rightDown)
        {
            _rightRotating = false;
            return;
        }

        Vector2 mousePosition = TransparentPetRuntimeInput.MousePosition();
        if (!_rightRotating)
        {
            _rightRotating = true;
            _lastMousePosition = mousePosition;
            return;
        }

        Vector2 delta = mousePosition - _lastMousePosition;
        _lastMousePosition = mousePosition;
        if (delta.sqrMagnitude <= 0.0001f)
        {
            return;
        }

        Vector3 rotationDelta = rightDragYawOnly
            ? new Vector3(0f, delta.x * mouseRotateSensitivity, 0f)
            : new Vector3(-delta.y * mouseRotateSensitivity, delta.x * mouseRotateSensitivity, 0f);
        RotateFromRuntimeOrigin(rotationDelta);
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

    private static float ReadScrollDelta()
    {
        return TransparentPetRuntimeInput.ScrollY();
    }

    private void MarkManualPlacementSave(bool enabled)
    {
        if (string.IsNullOrWhiteSpace(ManualPlacementSaveMarkerKey))
        {
            return;
        }

        if (enabled)
        {
            PlayerPrefs.SetInt(ManualPlacementSaveMarkerKey, 1);
        }
        else
        {
            PlayerPrefs.DeleteKey(ManualPlacementSaveMarkerKey);
        }
    }

    private void CaptureRuntimeOriginSnapshot()
    {
        _runtimeOriginLocalPosition = transform.localPosition;
        _runtimeOriginLocalRotation = transform.localRotation;
        _runtimeOriginLocalScale = transform.localScale;
        _runtimeFootPivotLocal = ResolveFootPivotLocal();
        _runtimeFootPivotParentPosition = WorldToParentPoint(transform.TransformPoint(_runtimeFootPivotLocal));
        _runtimeOriginCaptured = true;
    }

    private Vector3 ResolveFootPivotLocal()
    {
        if (TryResolveFootPivotWorld(out Vector3 footPivotWorld))
        {
            return transform.InverseTransformPoint(footPivotWorld);
        }

        return Vector3.zero;
    }

    private bool TryResolveFootPivotWorld(out Vector3 footPivotWorld)
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        bool hasBounds = TryBuildRendererBounds(renderers, out Bounds bounds);
        float footY = hasBounds ? bounds.min.y : transform.position.y;

        Animator animator = GetComponentInChildren<Animator>(true);
        if (animator != null && animator.isHuman)
        {
            Transform leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (leftFoot != null && rightFoot != null)
            {
                Vector3 feetCenter = (leftFoot.position + rightFoot.position) * 0.5f;
                footY = Mathf.Min(leftFoot.position.y, rightFoot.position.y) - footPivotDownOffset;
                footPivotWorld = new Vector3(feetCenter.x, footY, feetCenter.z);
                return true;
            }

            Transform singleFoot = leftFoot != null ? leftFoot : rightFoot;
            if (singleFoot != null)
            {
                footY = singleFoot.position.y - footPivotDownOffset;
                footPivotWorld = new Vector3(singleFoot.position.x, footY, singleFoot.position.z);
                return true;
            }
        }

        if (hasBounds)
        {
            footPivotWorld = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            return true;
        }

        footPivotWorld = transform.position;
        return false;
    }

    private Vector3 WorldToParentPoint(Vector3 worldPoint)
    {
        return transform.parent != null ? transform.parent.InverseTransformPoint(worldPoint) : worldPoint;
    }

    private static Vector3 NormalizeEuler(Vector3 value)
    {
        return new Vector3(
            Mathf.DeltaAngle(0f, value.x),
            Mathf.DeltaAngle(0f, value.y),
            Mathf.DeltaAngle(0f, value.z));
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
    private sealed class PersistedPlacementState
    {
        public int version;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
        public Vector3 offsetFromOrigin;
        public Vector3 eulerDegrees;
        public float uniformScale;
        public bool lockCameraTargetToPet = true;
        public bool useModelFocusForCameraTarget = true;
        public bool hasRuntimeOrigin;
        public Vector3 runtimeOriginLocalPosition;
        public Quaternion runtimeOriginLocalRotation;
        public Vector3 runtimeOriginLocalScale;
        public Vector3 runtimeFootPivotLocal;
        public Vector3 runtimeFootPivotParentPosition;
    }
}
