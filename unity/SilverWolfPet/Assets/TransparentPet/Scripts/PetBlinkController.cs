using UnityEngine;

[DisallowMultipleComponent]
public sealed class PetBlinkController : MonoBehaviour
{
    public Transform scanRoot;
    public PetExpressionController expressionController;
    public string blinkExpressionName = "blink";
    public bool blinkOnStart = true;
    public Vector2 blinkIntervalRange = new Vector2(2.8f, 6.2f);
    public float closeSeconds = 0.055f;
    public float holdSeconds = 0.025f;
    public float openSeconds = 0.085f;
    public Vector3 fallbackClosedEyeScale = new Vector3(1.08f, 0.12f, 1f);

    private Transform _leftEye;
    private Transform _rightEye;
    private Vector3 _leftEyeOpenScale;
    private Vector3 _rightEyeOpenScale;
    private float _nextBlinkTime;
    private float _blinkStartTime = -100f;
    private bool _blinkActive;
    private bool _hasBlendShapeBlink;

    private void Awake()
    {
        ResolveReferences();
        Rebind(scanRoot != null ? scanRoot : transform);
    }

    private void OnEnable()
    {
        ScheduleNextBlink(blinkOnStart ? 0.35f : RandomInterval());
    }

    private void Update()
    {
        if (!_blinkActive && Time.unscaledTime >= _nextBlinkTime)
        {
            StartBlink();
        }

        if (_blinkActive)
        {
            UpdateBlink();
        }
    }

    public void Rebind(Transform nextScanRoot)
    {
        scanRoot = nextScanRoot != null ? nextScanRoot : transform;
        ResolveReferences();
        _hasBlendShapeBlink = expressionController != null && expressionController.HasExpressionTargets(blinkExpressionName);
        ResolveFallbackEyes();
        ApplyBlinkWeight(0f);
    }

    private void ResolveReferences()
    {
        if (expressionController == null)
        {
            expressionController = GetComponent<PetExpressionController>();
        }

        if (expressionController == null)
        {
            expressionController = GetComponentInChildren<PetExpressionController>(true);
        }
    }

    private void ResolveFallbackEyes()
    {
        _leftEye = FindChildByName(scanRoot, "LeftEye");
        _rightEye = FindChildByName(scanRoot, "RightEye");
        if (_leftEye != null)
        {
            _leftEyeOpenScale = _leftEye.localScale;
        }

        if (_rightEye != null)
        {
            _rightEyeOpenScale = _rightEye.localScale;
        }
    }

    private void StartBlink()
    {
        _blinkActive = true;
        _blinkStartTime = Time.unscaledTime;
    }

    private void UpdateBlink()
    {
        float elapsed = Time.unscaledTime - _blinkStartTime;
        float weight;
        if (elapsed < closeSeconds)
        {
            weight = closeSeconds <= 0.0001f ? 1f : elapsed / closeSeconds;
        }
        else if (elapsed < closeSeconds + holdSeconds)
        {
            weight = 1f;
        }
        else if (elapsed < closeSeconds + holdSeconds + openSeconds)
        {
            float openElapsed = elapsed - closeSeconds - holdSeconds;
            weight = 1f - (openSeconds <= 0.0001f ? 1f : openElapsed / openSeconds);
        }
        else
        {
            weight = 0f;
            _blinkActive = false;
            ScheduleNextBlink(RandomInterval());
        }

        ApplyBlinkWeight(Mathf.Clamp01(weight));
    }

    private void ApplyBlinkWeight(float weight)
    {
        if (_hasBlendShapeBlink && expressionController != null)
        {
            expressionController.SetExpressionWeight(blinkExpressionName, weight);
            return;
        }

        ApplyFallbackEyeScale(_leftEye, _leftEyeOpenScale, weight);
        ApplyFallbackEyeScale(_rightEye, _rightEyeOpenScale, weight);
    }

    private void ApplyFallbackEyeScale(Transform eye, Vector3 openScale, float weight)
    {
        if (eye == null)
        {
            return;
        }

        Vector3 closedScale = new Vector3(
            openScale.x * fallbackClosedEyeScale.x,
            openScale.y * fallbackClosedEyeScale.y,
            openScale.z * fallbackClosedEyeScale.z);
        eye.localScale = Vector3.Lerp(openScale, closedScale, weight);
    }

    private void ScheduleNextBlink(float delaySeconds)
    {
        _nextBlinkTime = Time.unscaledTime + Mathf.Max(0.05f, delaySeconds);
    }

    private float RandomInterval()
    {
        float min = Mathf.Max(0.2f, Mathf.Min(blinkIntervalRange.x, blinkIntervalRange.y));
        float max = Mathf.Max(min, Mathf.Max(blinkIntervalRange.x, blinkIntervalRange.y));
        return Random.Range(min, max);
    }

    private static Transform FindChildByName(Transform root, string childName)
    {
        if (root == null || string.IsNullOrWhiteSpace(childName))
        {
            return null;
        }

        Transform[] children = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] != null && string.Equals(children[i].name, childName, System.StringComparison.OrdinalIgnoreCase))
            {
                return children[i];
            }
        }

        return null;
    }
}
