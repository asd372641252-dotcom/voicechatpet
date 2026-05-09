using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class PetMouthController : MonoBehaviour
{
    public PetExpressionController expressionController;
    public AudioSource audioSource;
    public bool driveFromAudio = false;
    public float audioSensitivity = 18f;
    public float smoothSpeed = 18f;
    public float externalHoldSeconds = 0.28f;
    public bool mouthFlapEnabled = true;
    public float mouthFlapOpenWeight = 0.68f;
    public float audioMouthTimeoutSeconds = 0.45f;
    public float minAudioMouthOpen = 0.06f;
    public bool audioMouthUseVolumeVisemes = true;
    public float audioMouthClosedThreshold = 0.035f;
    public float audioMouthPeakThreshold = 0.82f;
    public float audioMouthHoldMinSeconds = 0.12f;
    public float audioMouthHoldMaxSeconds = 0.24f;
    public float mouthVisemeMinHoldSeconds = 0.1f;
    public float mouthVisemeMaxHoldSeconds = 0.2f;
    public int mouthTextQueueMaxChars = 80;

    private readonly float[] _audioSamples = new float[128];
    private float _currentMouth;
    private float _externalMouth = -1f;
    private float _externalMouthUntil;
    private bool _mouthFlapActive;
    private float _mouthVolumeScale = 1f;
    private float _mouthNextVisemeAt;
    private float _lastAudioMouthSignalAt;
    private float _lastAudioMouthOpen;
    private float _smoothedAudioMouthOpen;
    private bool _textMouthActive;
    private float _textMouthUntil;
    private int _mouthVisemeIndex = -1;
    private string _targetMouthExpression = "mouth_closed";
    private float _targetMouthWeight;
    private string _mouthTextQueue = "";
    private string _lastSubtitleText = "";
    private string _lastVolumeVisemeExpression = "";

    private struct MouthViseme
    {
        public readonly string Expression;
        public readonly float Weight;
        public readonly float HoldSeconds;

        public MouthViseme(string expression, float weight, float holdSeconds = -1f)
        {
            Expression = expression;
            Weight = weight;
            HoldSeconds = holdSeconds;
        }
    }

    private static readonly MouthViseme[] DefaultVisemeSequence =
    {
        new MouthViseme("mouth_small", 0.86f),
        new MouthViseme("mouth_round", 0.74f),
        new MouthViseme("mouth_wide", 0.78f),
        new MouthViseme("mouth_open", 0.92f),
        new MouthViseme("mouth_small", 0.68f),
        new MouthViseme("mouth_smirk", 0.55f)
    };

    private static readonly string[] LowAudioVisemes =
    {
        "mouth_small",
        "mouth_round",
        "mouth_closed",
        "mouth_wide"
    };

    private static readonly string[] MidAudioVisemes =
    {
        "mouth_wide",
        "mouth_round",
        "mouth_small",
        "mouth_open"
    };

    private static readonly string[] HighAudioVisemes =
    {
        "mouth_open",
        "mouth_wide",
        "mouth_round",
        "mouth_small"
    };

    private void Reset()
    {
        expressionController = GetComponent<PetExpressionController>();
        audioSource = GetComponent<AudioSource>();
    }

    private void Awake()
    {
        if (expressionController == null)
        {
            expressionController = GetComponent<PetExpressionController>();
        }

        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
        }
    }

    private void Update()
    {
        if (mouthFlapEnabled && _mouthFlapActive)
        {
            UpdateMouthFlap();
            return;
        }

        float target = ResolveMouthTarget();
        _currentMouth = Mathf.Lerp(_currentMouth, target, 1f - Mathf.Exp(-smoothSpeed * Time.deltaTime));

        if (expressionController != null)
        {
            expressionController.SetMouth(_currentMouth);
        }
    }

    public void SetExternalMouth(float open01)
    {
        SetExternalMouth(open01, externalHoldSeconds);
    }

    public void SetExternalMouth(float open01, float holdSeconds)
    {
        StopMouthFlap(false);
        _externalMouth = Mathf.Clamp01(open01);
        _externalMouthUntil = Time.time + Mathf.Max(0.02f, holdSeconds);
    }

    public void ClearExternalMouth()
    {
        _externalMouth = -1f;
        _externalMouthUntil = 0f;
    }

    public void SetAudioVolumeMouth(float open01, bool active)
    {
        if (!active)
        {
            StopMouthFlap(true);
            return;
        }

        _lastAudioMouthSignalAt = Time.unscaledTime;
        _textMouthActive = false;
        _textMouthUntil = 0f;
        _externalMouth = -1f;
        float normalizedOpen = Mathf.Clamp01(open01);
        _lastAudioMouthOpen = normalizedOpen;
        _mouthVolumeScale = Mathf.Clamp(0.42f + normalizedOpen * 0.68f, 0.38f, 0.94f);
        StartMouthFlap();
    }

    public void StartTextMouth(float open01, float holdSeconds)
    {
        _textMouthActive = true;
        _textMouthUntil = Mathf.Max(_textMouthUntil, Time.unscaledTime + Mathf.Max(0.25f, holdSeconds));
        _externalMouth = -1f;
        float normalizedOpen = Mathf.Clamp01(open01);
        _lastAudioMouthOpen = normalizedOpen;
        _smoothedAudioMouthOpen = Mathf.Max(_smoothedAudioMouthOpen, normalizedOpen * 0.6f);
        _mouthVolumeScale = Mathf.Clamp(0.5f + normalizedOpen * 0.56f, 0.48f, 0.96f);
        StartMouthFlap();
    }

    public void QueueMouthText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string incoming = text.Trim();
        string addition = incoming;
        if (!string.IsNullOrEmpty(_lastSubtitleText))
        {
            if (string.Equals(incoming, _lastSubtitleText, StringComparison.Ordinal))
            {
                return;
            }

            if (incoming.StartsWith(_lastSubtitleText, StringComparison.Ordinal))
            {
                addition = incoming.Substring(_lastSubtitleText.Length);
            }
            else if (_lastSubtitleText.StartsWith(incoming, StringComparison.Ordinal))
            {
                return;
            }
        }

        _lastSubtitleText = incoming;
        addition = SanitizeMouthText(addition);
        if (string.IsNullOrWhiteSpace(addition))
        {
            return;
        }

        _mouthTextQueue += addition;
        int maxChars = Mathf.Max(8, mouthTextQueueMaxChars);
        if (_mouthTextQueue.Length > maxChars)
        {
            _mouthTextQueue = _mouthTextQueue.Substring(_mouthTextQueue.Length - maxChars);
        }

        if (_mouthFlapActive)
        {
            _mouthNextVisemeAt = Mathf.Min(_mouthNextVisemeAt, Time.unscaledTime + 0.04f);
        }
    }

    private float ResolveMouthTarget()
    {
        if (_externalMouth >= 0f && Time.time <= _externalMouthUntil)
        {
            return _externalMouth;
        }

        _externalMouth = -1f;

        if (!driveFromAudio || audioSource == null || !audioSource.isPlaying)
        {
            return 0f;
        }

        audioSource.GetOutputData(_audioSamples, 0);
        float sum = 0f;
        for (int i = 0; i < _audioSamples.Length; i++)
        {
            sum += _audioSamples[i] * _audioSamples[i];
        }

        float rms = Mathf.Sqrt(sum / _audioSamples.Length);
        return Mathf.Clamp01(rms * audioSensitivity);
    }

    private void StartMouthFlap()
    {
        if (!mouthFlapEnabled || expressionController == null)
        {
            SetExternalMouth(Mathf.Clamp01(mouthFlapOpenWeight * _mouthVolumeScale), externalHoldSeconds);
            return;
        }

        if (!_mouthFlapActive)
        {
            _mouthFlapActive = true;
            _mouthVisemeIndex = -1;
            _mouthNextVisemeAt = 0f;
            AdvanceMouthViseme();
        }
    }

    private void StopMouthFlap(bool clearQueue)
    {
        _mouthFlapActive = false;
        _mouthNextVisemeAt = 0f;
        _mouthVisemeIndex = -1;
        _mouthVolumeScale = 1f;
        _lastAudioMouthOpen = 0f;
        _smoothedAudioMouthOpen = 0f;
        _textMouthActive = false;
        _textMouthUntil = 0f;
        _lastVolumeVisemeExpression = "";
        string previousExpression = _targetMouthExpression;
        _targetMouthExpression = "mouth_closed";
        _targetMouthWeight = 0f;
        _currentMouth = 0f;
        if (clearQueue)
        {
            _mouthTextQueue = "";
            _lastSubtitleText = "";
        }

        if (expressionController != null)
        {
            if (!string.IsNullOrWhiteSpace(previousExpression))
            {
                expressionController.SetExpressionWeight(previousExpression, 0f);
            }
            expressionController.SetExpressionWeight("mouth_closed", 0f);
        }
    }

    private void UpdateMouthFlap()
    {
        bool textFallbackHolding = _textMouthActive &&
            (!string.IsNullOrEmpty(_mouthTextQueue) || Time.unscaledTime <= _textMouthUntil);

        if (_textMouthActive && !textFallbackHolding)
        {
            StopMouthFlap(true);
            return;
        }

        if (!textFallbackHolding &&
            audioMouthTimeoutSeconds > 0f &&
            Time.unscaledTime - _lastAudioMouthSignalAt > audioMouthTimeoutSeconds)
        {
            StopMouthFlap(true);
            return;
        }

        _smoothedAudioMouthOpen = Mathf.Lerp(
            _smoothedAudioMouthOpen,
            _lastAudioMouthOpen,
            1f - Mathf.Exp(-14f * Time.deltaTime));

        if (Time.unscaledTime >= _mouthNextVisemeAt)
        {
            AdvanceMouthViseme();
        }

        float responseSpeed = _targetMouthWeight >= _currentMouth ? smoothSpeed : smoothSpeed * 0.72f;
        _currentMouth = Mathf.Lerp(_currentMouth, _targetMouthWeight, 1f - Mathf.Exp(-responseSpeed * Time.deltaTime));
        if (expressionController != null)
        {
            expressionController.SetExpressionWeight(_targetMouthExpression, _currentMouth);
        }
    }

    private void AdvanceMouthViseme()
    {
        MouthViseme viseme;
        if (TryTakeTextViseme(out viseme))
        {
            ApplyMouthViseme(viseme);
            return;
        }

        if (_textMouthActive)
        {
            int textStep = UnityEngine.Random.value < 0.25f ? 2 : 1;
            _mouthVisemeIndex = PositiveModulo(_mouthVisemeIndex + textStep, DefaultVisemeSequence.Length);
            ApplyMouthViseme(DefaultVisemeSequence[_mouthVisemeIndex]);
            return;
        }

        if (audioMouthUseVolumeVisemes)
        {
            ApplyMouthViseme(VolumeViseme(_smoothedAudioMouthOpen > 0f ? _smoothedAudioMouthOpen : _lastAudioMouthOpen));
            return;
        }

        if (DefaultVisemeSequence.Length == 0)
        {
            ApplyMouthViseme(new MouthViseme("mouth_open", 1f));
            return;
        }

        int step = UnityEngine.Random.value < 0.28f ? 2 : 1;
        _mouthVisemeIndex = PositiveModulo(_mouthVisemeIndex + step, DefaultVisemeSequence.Length);
        ApplyMouthViseme(DefaultVisemeSequence[_mouthVisemeIndex]);
    }

    private void ApplyMouthViseme(MouthViseme viseme)
    {
        string previousExpression = _targetMouthExpression;
        _targetMouthExpression = string.IsNullOrWhiteSpace(viseme.Expression) ? "mouth_small" : viseme.Expression;
        if (expressionController != null &&
            !string.IsNullOrWhiteSpace(previousExpression) &&
            !string.Equals(previousExpression, _targetMouthExpression, StringComparison.Ordinal))
        {
            expressionController.SetExpressionWeight(previousExpression, 0f);
        }

        _targetMouthWeight = Mathf.Clamp01(viseme.Weight * mouthFlapOpenWeight * _mouthVolumeScale);

        float holdSeconds = viseme.HoldSeconds > 0f
            ? viseme.HoldSeconds
            : UnityEngine.Random.Range(
                Mathf.Max(0.06f, mouthVisemeMinHoldSeconds),
                Mathf.Max(Mathf.Max(0.06f, mouthVisemeMinHoldSeconds), mouthVisemeMaxHoldSeconds));
        _mouthNextVisemeAt = Time.unscaledTime + holdSeconds;
    }

    private MouthViseme VolumeViseme(float energy)
    {
        energy = Mathf.Clamp01(energy);
        float closedThreshold = Mathf.Clamp01(Mathf.Max(minAudioMouthOpen, audioMouthClosedThreshold));
        if (energy <= closedThreshold)
        {
            _lastVolumeVisemeExpression = "mouth_closed";
            return new MouthViseme("mouth_closed", 0f, Mathf.Max(0.12f, audioMouthHoldMinSeconds));
        }

        float shapedEnergy = Mathf.InverseLerp(closedThreshold, 1f, energy);
        if (UnityEngine.Random.value < Mathf.Lerp(0.2f, 0.04f, shapedEnergy) &&
            !string.Equals(_lastVolumeVisemeExpression, "mouth_closed", StringComparison.Ordinal))
        {
            _lastVolumeVisemeExpression = "mouth_closed";
            return new MouthViseme(
                "mouth_closed",
                UnityEngine.Random.Range(0.08f, 0.22f),
                UnityEngine.Random.Range(0.055f, 0.095f));
        }

        string expression;
        if (shapedEnergy < 0.32f)
        {
            expression = PickVolumeViseme(LowAudioVisemes);
        }
        else if (shapedEnergy < Mathf.Clamp01(audioMouthPeakThreshold))
        {
            expression = PickVolumeViseme(MidAudioVisemes);
        }
        else
        {
            expression = PickVolumeViseme(HighAudioVisemes);
        }

        float baseWeight = Mathf.Lerp(0.36f, 0.92f, Mathf.SmoothStep(0f, 1f, shapedEnergy));
        baseWeight *= UnityEngine.Random.Range(0.86f, 1.08f);
        baseWeight *= WeightScaleForViseme(expression);
        float holdMin = Mathf.Max(0.1f, audioMouthHoldMinSeconds);
        float holdMax = Mathf.Max(holdMin + 0.02f, audioMouthHoldMaxSeconds);
        float hold = Mathf.Lerp(holdMax, holdMin, Mathf.Clamp01(shapedEnergy));
        hold *= UnityEngine.Random.Range(0.88f, 1.12f);
        _lastVolumeVisemeExpression = expression;
        return new MouthViseme(expression, Mathf.Clamp01(baseWeight), hold);
    }

    private static float WeightScaleForViseme(string expression)
    {
        switch (expression)
        {
            case "mouth_round":
                return 0.9f;
            case "mouth_wide":
                return 0.82f;
            case "mouth_small":
                return 0.76f;
            case "mouth_smirk":
                return 0.64f;
            case "mouth_closed":
                return 0.35f;
            default:
                return 1f;
        }
    }

    private string PickVolumeViseme(string[] choices)
    {
        if (choices == null || choices.Length == 0)
        {
            return "mouth_small";
        }

        if (choices.Length == 1)
        {
            return choices[0];
        }

        string expression = choices[UnityEngine.Random.Range(0, choices.Length)];
        if (!string.IsNullOrEmpty(_lastVolumeVisemeExpression) &&
            string.Equals(expression, _lastVolumeVisemeExpression, StringComparison.Ordinal))
        {
            int index = Array.IndexOf(choices, expression);
            expression = choices[PositiveModulo(index + 1, choices.Length)];
        }

        return expression;
    }

    private bool TryTakeTextViseme(out MouthViseme viseme)
    {
        while (!string.IsNullOrEmpty(_mouthTextQueue))
        {
            char ch = _mouthTextQueue[0];
            _mouthTextQueue = _mouthTextQueue.Substring(1);
            viseme = MouthVisemeForChar(ch);
            return true;
        }

        viseme = default;
        return false;
    }

    private MouthViseme MouthVisemeForChar(char ch)
    {
        int code = ch;
        if (char.IsWhiteSpace(ch))
        {
            return new MouthViseme("mouth_closed", 0f, 0.12f);
        }

        if (IsSentencePause(code))
        {
            return new MouthViseme("mouth_closed", 0f, UnityEngine.Random.Range(0.22f, 0.42f));
        }

        if (IsShortPause(code))
        {
            return new MouthViseme("mouth_closed", 0.1f, UnityEngine.Random.Range(0.13f, 0.24f));
        }

        char lower = char.ToLowerInvariant(ch);
        if (ContainsChar("a啊哈呀啦まばぱさざたなはらわあかが", lower))
        {
            return new MouthViseme("mouth_open", UnityEngine.Random.Range(0.72f, 0.95f), UnityEngine.Random.Range(0.08f, 0.15f));
        }

        if (ContainsChar("o哦喔噢我过说多おこごそぞとどのほぼぽもよろを", lower))
        {
            return new MouthViseme("mouth_round", UnityEngine.Random.Range(0.68f, 0.92f), UnityEngine.Random.Range(0.08f, 0.16f));
        }

        if (ContainsChar("u呜唔不出住主うくぐすずつづぬふぶぷむゆる", lower))
        {
            return new MouthViseme("mouth_round", UnityEngine.Random.Range(0.5f, 0.76f), UnityEngine.Random.Range(0.07f, 0.14f));
        }

        if (ContainsChar("i你一细轻いきぎしじちぢにひびぴみり", lower))
        {
            return new MouthViseme("mouth_wide", UnityEngine.Random.Range(0.48f, 0.72f), UnityEngine.Random.Range(0.06f, 0.13f));
        }

        if (ContainsChar("e欸诶也这的了えけげせぜてでねへべぺめれ", lower))
        {
            return new MouthViseme("mouth_small", UnityEngine.Random.Range(0.46f, 0.72f), UnityEngine.Random.Range(0.07f, 0.14f));
        }

        if (ContainsChar("嗯唔ん", lower))
        {
            return new MouthViseme("mouth_closed", UnityEngine.Random.Range(0.2f, 0.45f), UnityEngine.Random.Range(0.08f, 0.16f));
        }

        if (IsCjk(code))
        {
            switch (PositiveModulo(code, 6))
            {
                case 0:
                    return new MouthViseme("mouth_small", UnityEngine.Random.Range(0.48f, 0.72f), UnityEngine.Random.Range(0.07f, 0.14f));
                case 1:
                    return new MouthViseme("mouth_wide", UnityEngine.Random.Range(0.42f, 0.66f), UnityEngine.Random.Range(0.07f, 0.14f));
                case 2:
                    return new MouthViseme("mouth_round", UnityEngine.Random.Range(0.42f, 0.68f), UnityEngine.Random.Range(0.08f, 0.15f));
                case 3:
                    return new MouthViseme("mouth_open", UnityEngine.Random.Range(0.52f, 0.78f), UnityEngine.Random.Range(0.08f, 0.15f));
                case 4:
                    return new MouthViseme("mouth_smirk", UnityEngine.Random.Range(0.35f, 0.58f), UnityEngine.Random.Range(0.08f, 0.15f));
                default:
                    return new MouthViseme("mouth_closed", UnityEngine.Random.Range(0.08f, 0.25f), UnityEngine.Random.Range(0.05f, 0.1f));
            }
        }

        switch (PositiveModulo(code, 5))
        {
            case 0:
                return new MouthViseme("mouth_small", 0.6f);
            case 1:
                return new MouthViseme("mouth_wide", 0.55f);
            case 2:
                return new MouthViseme("mouth_round", 0.56f);
            case 3:
                return new MouthViseme("mouth_open", 0.62f);
            default:
                return new MouthViseme("mouth_closed", 0.18f);
        }
    }

    private static string SanitizeMouthText(string text)
    {
        return string.IsNullOrEmpty(text)
            ? ""
            : text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }

    private static bool IsSentencePause(int code)
    {
        return code == '.' || code == '!' || code == '?' || code == 0x3002 || code == 0xff01 || code == 0xff1f || code == 0x2026;
    }

    private static bool IsShortPause(int code)
    {
        return code == ',' || code == ';' || code == ':' || code == 0xff0c || code == 0x3001 || code == 0xff1b || code == 0xff1a;
    }

    private static bool ContainsChar(string choices, char ch)
    {
        return !string.IsNullOrEmpty(choices) && choices.IndexOf(ch) >= 0;
    }

    private static bool IsCjk(int code)
    {
        return (code >= 0x3400 && code <= 0x9fff)
            || (code >= 0x3040 && code <= 0x30ff)
            || (code >= 0xac00 && code <= 0xd7af);
    }

    private static int PositiveModulo(int value, int divisor)
    {
        return ((value % divisor) + divisor) % divisor;
    }
}
