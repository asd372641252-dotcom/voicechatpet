using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public sealed class StateAnimationBinding
{
    public string state;
    public string animatorState;
    public string fallbackAnimatorState = "KA_Idle01_breathing";
    public float crossFadeSeconds = 0.36f;
}

[DisallowMultipleComponent]
public sealed class PetStateController : MonoBehaviour
{
    public Animator animator;
    public TransparentPetKawaiiActionController actionController;
    public PetExpressionController expressionController;
    public PetMouthController mouthController;
    public PetBubbleController bubbleController;
    public SceneSubtitleController screenSubtitleController;

    public string initialState = "idle";
    public string idleActionName = "KA_Idle01_breathing";
    public string speakingActionName = "KA_Idle50_StandingTalk1_1";
    public List<string> speakingActionPool = new List<string>
    {
        "KA_Idle50_StandingTalk1_1",
        "KA_Idle51_StandingTalk1_2",
        "KA_Idle12_LeaningForward",
        "KA_Idle16_WaveHands",
        "KA_Idle43_HandOnHip",
        "KA_Idle45_WaveHandSlightly"
    };
    public string thinkingActionName = "KA_Idle08_ComeUpWithAnIdea";
    public string listeningActionName = "KA_Idle02_LookLeftAndRight";
    public string happyActionName = "KA_Idle28_Laugh";
    public string angryActionName = "KA_Idle27_Angry";
    public string surprisedActionName = "KA_Idle29_Surprised";
    public string sleepyActionName = "KA_Idle09_Waiting";
    public bool queueBubbleTextForMouth = true;
    public bool pauseRandomActionsDuringVoice = true;
    public bool enableSceneScreenSubtitles = true;
    public bool screenSubtitlesOnlyInSceneHost = true;
    public float maxThinkingVisualSeconds = 12f;
    public List<StateAnimationBinding> stateBindings = new List<StateAnimationBinding>();

    public string CurrentState { get; private set; } = "idle";

    private Coroutine _returnToIdleCoroutine;
    private bool _savedRandomAutoSwitch;
    private bool _hasSavedRandomAutoSwitch;
    private bool _hasAppliedState;
    private string _lastSpeakingActionName = "";
    private float _stateEnteredAt;

    private static readonly string[] DefaultStates =
    {
        "idle",
        "listening",
        "speaking",
        "thinking",
        "happy",
        "angry",
        "sleepy",
        "surprised",
        "clicked",
        "interrupted"
    };

    private void Reset()
    {
        animator = GetComponentInChildren<Animator>();
        actionController = GetComponent<TransparentPetKawaiiActionController>();
        expressionController = GetComponent<PetExpressionController>();
        mouthController = GetComponent<PetMouthController>();
        bubbleController = GetComponent<PetBubbleController>();
        screenSubtitleController = GetComponent<SceneSubtitleController>();
        EnsureDefaultSpeakingActionPool();
        EnsureDefaultBindings();
    }

    private void Awake()
    {
        if (animator == null)
        {
            animator = GetComponentInChildren<Animator>();
        }

        if (actionController == null)
        {
            actionController = GetComponent<TransparentPetKawaiiActionController>();
        }

        if (expressionController == null)
        {
            expressionController = GetComponent<PetExpressionController>();
        }

        if (mouthController == null)
        {
            mouthController = GetComponent<PetMouthController>();
        }

        if (bubbleController == null)
        {
            bubbleController = GetComponent<PetBubbleController>();
        }

        if (screenSubtitleController == null)
        {
            screenSubtitleController = GetComponent<SceneSubtitleController>();
        }

        EnsureDefaultSpeakingActionPool();
        EnsureDefaultBindings();
        EnsureScreenSubtitleController();
        if (animator != null)
        {
            animator.applyRootMotion = false;
        }
    }

    private void Start()
    {
        SetState(initialState, null);
    }

    private void Update()
    {
        if (maxThinkingVisualSeconds <= 0f || NormalizeState(CurrentState) != "thinking")
        {
            return;
        }

        float elapsed = Time.unscaledTime - _stateEnteredAt;
        if (elapsed >= maxThinkingVisualSeconds)
        {
            SetState("idle", null);
        }
    }

    public void ApplyCommand(PetControlCommand command)
    {
        if (command == null)
        {
            return;
        }

        string bubbleText = !string.IsNullOrEmpty(command.text) ? command.text : command.bubble_text;

        if (!string.IsNullOrWhiteSpace(command.state) || !string.IsNullOrWhiteSpace(command.action))
        {
            SetState(command.state, command.action);
        }

        if (!string.IsNullOrWhiteSpace(command.emotion) && expressionController != null)
        {
            expressionController.SetEmotion(command.emotion);
        }

        bool shouldQueueMouthText = queueBubbleTextForMouth || IsSpeechBubbleCommand(command);
        bool queuedMouthText = false;
        if (shouldQueueMouthText && mouthController != null && !string.IsNullOrEmpty(bubbleText))
        {
            mouthController.QueueMouthText(bubbleText);
            queuedMouthText = true;
        }

        if (mouthController != null && command.hasMouthMode && IsAudioMouthMode(command.mouthMode))
        {
            bool hasExplicitMouthSignal = command.hasAudioActive || command.hasMouthOpen;
            if (hasExplicitMouthSignal)
            {
                bool active = command.hasAudioActive
                    ? command.audio_active
                    : command.hasMouthOpen && command.mouth_open > 0.01f;
                float open = command.hasMouthOpen ? command.mouth_open : 0.58f;
                mouthController.SetAudioVolumeMouth(open, active);
            }
            else if (queuedMouthText && NormalizeState(command.state) == "speaking")
            {
                mouthController.StartTextMouth(0.58f, EstimateTextMouthSeconds(bubbleText));
            }
            else if (HasSpeechEndState(command))
            {
                mouthController.SetAudioVolumeMouth(0f, false);
            }
        }
        else if (command.hasMouthOpen && mouthController != null)
        {
            mouthController.SetExternalMouth(command.mouth_open);
        }
        else if (command.hasMouth && mouthController != null)
        {
            mouthController.SetExternalMouth(command.mouth);
        }
        else if (command.hasAudioActive && mouthController != null)
        {
            mouthController.SetAudioVolumeMouth(0.55f, command.audio_active);
        }
        else if (mouthController != null && HasSpeechEndState(command))
        {
            mouthController.SetAudioVolumeMouth(0f, false);
        }

        if (bubbleController != null && command.clear_bubble)
        {
            bubbleController.HideText();
        }

        if (screenSubtitleController != null && command.clear_bubble)
        {
            screenSubtitleController.ClearSubtitle();
        }

        if (!string.IsNullOrEmpty(bubbleText) && bubbleController != null)
        {
            float visibleSeconds = ResolveBubbleVisibleSeconds(command);
            bubbleController.ShowText(bubbleText, visibleSeconds);
        }

        if (!string.IsNullOrEmpty(bubbleText) && screenSubtitleController != null)
        {
            float visibleSeconds = ResolveScreenSubtitleVisibleSeconds(command, bubbleText);
            screenSubtitleController.ShowSubtitle(bubbleText, visibleSeconds);
        }

        UpdateBubbleSpeechLifetime(command, bubbleText);
        UpdateScreenSubtitleSpeechLifetime(command, bubbleText);
    }

    private float ResolveBubbleVisibleSeconds(PetControlCommand command)
    {
        if (command.hasDurationMs)
        {
            return command.duration_ms > 0 ? command.duration_ms / 1000f : 0f;
        }

        return IsSpeechBubbleCommand(command) ? 0f : -1f;
    }

    private float ResolveScreenSubtitleVisibleSeconds(PetControlCommand command, string text)
    {
        if (command.hasAudioActive && command.audio_active)
        {
            return 0f;
        }

        if (command.hasDurationMs && command.duration_ms > 0)
        {
            return command.duration_ms / 1000f;
        }

        if (IsSpeechBubbleCommand(command) || command.hasDurationMs)
        {
            return EstimateScreenSubtitleVisibleSeconds(text);
        }

        return -1f;
    }

    private static float EstimateScreenSubtitleVisibleSeconds(string text)
    {
        string cleanText = string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
        if (string.IsNullOrEmpty(cleanText))
        {
            return 1.8f;
        }

        int visibleChars = 0;
        for (int i = 0; i < cleanText.Length; i++)
        {
            if (!char.IsWhiteSpace(cleanText[i]))
            {
                visibleChars++;
            }
        }

        float seconds = visibleChars / 7.2f + 0.45f;
        return Mathf.Clamp(seconds, 1.8f, 6.8f);
    }

    private static float EstimateTextMouthSeconds(string text)
    {
        string cleanText = string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
        if (string.IsNullOrEmpty(cleanText))
        {
            return 1.2f;
        }

        int visibleChars = 0;
        for (int i = 0; i < cleanText.Length; i++)
        {
            if (!char.IsWhiteSpace(cleanText[i]))
            {
                visibleChars++;
            }
        }

        return Mathf.Clamp(visibleChars / 6.8f + 0.65f, 1.2f, 7.5f);
    }

    private void UpdateBubbleSpeechLifetime(PetControlCommand command, string bubbleText)
    {
        if (bubbleController == null)
        {
            return;
        }

        if (command.hasAudioActive)
        {
            if (command.audio_active)
            {
                bubbleController.HoldVisibleText();
            }
            else
            {
                bubbleController.ReleaseHeldText();
            }

            return;
        }

        if (string.IsNullOrEmpty(bubbleText) && IsSpeechEndState(command.state))
        {
            bubbleController.ReleaseHeldText();
        }
    }

    private void UpdateScreenSubtitleSpeechLifetime(PetControlCommand command, string bubbleText)
    {
        if (screenSubtitleController == null)
        {
            return;
        }

        if (command.hasAudioActive)
        {
            if (command.audio_active)
            {
                screenSubtitleController.HoldVisibleSubtitle();
            }
            else
            {
                screenSubtitleController.ReleaseHeldSubtitle();
            }

            return;
        }

        if (string.IsNullOrEmpty(bubbleText) && IsSpeechEndState(command.state))
        {
            screenSubtitleController.ReleaseHeldSubtitle();
        }
    }

    private void EnsureScreenSubtitleController()
    {
        if (!enableSceneScreenSubtitles || screenSubtitleController != null || !ShouldUseSceneScreenSubtitles())
        {
            return;
        }

        screenSubtitleController = gameObject.AddComponent<SceneSubtitleController>();
    }

    private bool ShouldUseSceneScreenSubtitles()
    {
        if (!screenSubtitlesOnlyInSceneHost)
        {
            return true;
        }

        TransparentWindowController window = GetComponentInParent<TransparentWindowController>();
        return window == null || window.route == TransparentPetRoute.SceneHost;
    }

    private static bool IsSpeechBubbleCommand(PetControlCommand command)
    {
        if (command == null)
        {
            return false;
        }

        if (command.hasAudioActive && command.audio_active)
        {
            return true;
        }

        if (command.hasMouthMode && IsAudioMouthMode(command.mouthMode))
        {
            return true;
        }

        return NormalizeState(command.state) == "speaking";
    }

    private static bool IsSpeechEndState(string state)
    {
        string normalized = NormalizeState(state);
        return normalized == "idle" || normalized == "listening" || normalized == "interrupted";
    }

    private static bool HasSpeechEndState(PetControlCommand command)
    {
        return command != null && !string.IsNullOrWhiteSpace(command.state) && IsSpeechEndState(command.state);
    }

    public void SetState(string requestedState, string requestedAction = null)
    {
        string state = NormalizeState(string.IsNullOrWhiteSpace(requestedState) ? CurrentState : requestedState);
        if (string.IsNullOrWhiteSpace(state))
        {
            state = "idle";
        }

        bool hasRequestedAction = !string.IsNullOrWhiteSpace(requestedAction);
        bool stateChanged = !_hasAppliedState || state != CurrentState;
        bool repeatedStateWithoutAction = _hasAppliedState && !hasRequestedAction && state == CurrentState;
        CurrentState = state;
        if (stateChanged)
        {
            _stateEnteredAt = Time.unscaledTime;
        }
        UpdateRandomActionLock(state);
        if (repeatedStateWithoutAction)
        {
            return;
        }

        PlayActionForState(state, requestedAction);
        ApplyStateExpression(state);
        _hasAppliedState = true;
    }

    public void ReturnToIdle(float delaySeconds = 0f)
    {
        if (_returnToIdleCoroutine != null)
        {
            StopCoroutine(_returnToIdleCoroutine);
        }

        _returnToIdleCoroutine = StartCoroutine(ReturnToIdleRoutine(delaySeconds));
    }

    public void EnsureDefaultBindings()
    {
        if (stateBindings == null)
        {
            stateBindings = new List<StateAnimationBinding>();
        }

        for (int i = 0; i < DefaultStates.Length; i++)
        {
            string state = DefaultStates[i];
            if (stateBindings.Exists(binding => NormalizeState(binding.state) == state))
            {
                continue;
            }

            stateBindings.Add(new StateAnimationBinding
            {
                state = state,
                animatorState = ActionForState(state),
                fallbackAnimatorState = idleActionName,
                crossFadeSeconds = 0.36f
            });
        }
    }

    private static bool IsAudioMouthMode(string value)
    {
        string mode = string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();
        return mode == "audio_volume" || mode == "viseme";
    }

    public static string NormalizeState(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "idle";
        }

        string normalized = value.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "talk":
            case "talking":
            case "speak":
                return "speaking";
            case "think":
                return "thinking";
            case "sleep":
            case "sleeping":
                return "sleepy";
            case "annoyed":
            case "mocking":
                return "angry";
            case "interrupted":
            case "interrupt":
                return "interrupted";
            default:
                return normalized;
        }
    }

    private void PlayActionForState(string state, string requestedAction)
    {
        bool hasRequestedAction = !string.IsNullOrWhiteSpace(requestedAction);
        string targetAction = hasRequestedAction
            ? requestedAction.Trim()
            : SelectActionForState(state);

        if (actionController != null && !string.IsNullOrWhiteSpace(targetAction) && actionController.PlayAction(targetAction))
        {
            return;
        }

        PlayAnimatorFallback(state, targetAction);
    }

    private void PlayAnimatorFallback(string state, string targetAction)
    {
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            return;
        }

        StateAnimationBinding binding = FindBinding(state);
        string stateName = !string.IsNullOrWhiteSpace(targetAction)
            ? targetAction
            : binding != null && !string.IsNullOrWhiteSpace(binding.animatorState)
                ? binding.animatorState
                : idleActionName;
        float fade = binding != null ? Mathf.Max(0.28f, binding.crossFadeSeconds) : 0.36f;

        if (HasAnimatorState(stateName))
        {
            animator.CrossFadeInFixedTime(stateName, fade, 0, 0f);
            return;
        }

        string fallback = binding != null && !string.IsNullOrWhiteSpace(binding.fallbackAnimatorState)
            ? binding.fallbackAnimatorState
            : idleActionName;
        if (HasAnimatorState(fallback))
        {
            animator.CrossFadeInFixedTime(fallback, fade, 0, 0f);
        }
    }

    private string SelectActionForState(string state)
    {
        return NormalizeState(state) == "speaking" ? SelectSpeakingAction() : ActionForState(state);
    }

    private string SelectSpeakingAction()
    {
        EnsureDefaultSpeakingActionPool();
        if (speakingActionPool == null || speakingActionPool.Count == 0)
        {
            _lastSpeakingActionName = speakingActionName;
            return speakingActionName;
        }

        List<string> candidates = new List<string>();
        for (int i = 0; i < speakingActionPool.Count; i++)
        {
            string action = string.IsNullOrWhiteSpace(speakingActionPool[i]) ? "" : speakingActionPool[i].Trim();
            if (string.IsNullOrEmpty(action) || string.Equals(action, _lastSpeakingActionName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!candidates.Contains(action))
            {
                candidates.Add(action);
            }
        }

        if (candidates.Count == 0)
        {
            for (int i = 0; i < speakingActionPool.Count; i++)
            {
                string action = string.IsNullOrWhiteSpace(speakingActionPool[i]) ? "" : speakingActionPool[i].Trim();
                if (!string.IsNullOrEmpty(action) && !candidates.Contains(action))
                {
                    candidates.Add(action);
                }
            }
        }

        string selected = candidates.Count > 0
            ? candidates[UnityEngine.Random.Range(0, candidates.Count)]
            : speakingActionName;
        _lastSpeakingActionName = selected;
        return selected;
    }

    private void EnsureDefaultSpeakingActionPool()
    {
        if (speakingActionPool == null)
        {
            speakingActionPool = new List<string>();
        }

        if (speakingActionPool.Count > 0)
        {
            return;
        }

        speakingActionPool.Add("KA_Idle50_StandingTalk1_1");
        speakingActionPool.Add("KA_Idle51_StandingTalk1_2");
        speakingActionPool.Add("KA_Idle12_LeaningForward");
        speakingActionPool.Add("KA_Idle16_WaveHands");
        speakingActionPool.Add("KA_Idle43_HandOnHip");
        speakingActionPool.Add("KA_Idle45_WaveHandSlightly");
    }

    private string ActionForState(string state)
    {
        switch (NormalizeState(state))
        {
            case "listening":
                return listeningActionName;
            case "speaking":
                return speakingActionName;
            case "thinking":
                return thinkingActionName;
            case "happy":
                return happyActionName;
            case "angry":
                return angryActionName;
            case "sleepy":
                return sleepyActionName;
            case "surprised":
            case "interrupted":
            case "clicked":
                return surprisedActionName;
            case "idle":
            default:
                return idleActionName;
        }
    }

    private void ApplyStateExpression(string state)
    {
        if (expressionController == null)
        {
            return;
        }

        switch (NormalizeState(state))
        {
            case "speaking":
                expressionController.SetEmotion("talk");
                break;
            case "thinking":
                expressionController.SetEmotion("thinking");
                break;
            case "happy":
            case "angry":
            case "sleepy":
            case "surprised":
            case "clicked":
            case "interrupted":
                expressionController.SetEmotion(state);
                break;
            default:
                expressionController.SetEmotion("neutral");
                break;
        }
    }

    private void UpdateRandomActionLock(string state)
    {
        if (!pauseRandomActionsDuringVoice || actionController == null)
        {
            return;
        }

        bool shouldPause = state != "idle" && state != "listening";
        if (shouldPause)
        {
            if (!_hasSavedRandomAutoSwitch)
            {
                _savedRandomAutoSwitch = actionController.randomAutoSwitch;
                _hasSavedRandomAutoSwitch = true;
            }

            actionController.randomAutoSwitch = false;
            return;
        }

        if (_hasSavedRandomAutoSwitch)
        {
            actionController.randomAutoSwitch = _savedRandomAutoSwitch;
            _hasSavedRandomAutoSwitch = false;
        }
    }

    private StateAnimationBinding FindBinding(string state)
    {
        string normalized = NormalizeState(state);
        return stateBindings.Find(binding => NormalizeState(binding.state) == normalized);
    }

    private bool HasAnimatorState(string stateName)
    {
        if (animator == null || string.IsNullOrWhiteSpace(stateName))
        {
            return false;
        }

        int shortHash = Animator.StringToHash(stateName);
        for (int i = 0; i < animator.layerCount; i++)
        {
            int fullHash = Animator.StringToHash(animator.GetLayerName(i) + "." + stateName);
            if (animator.HasState(i, shortHash) || animator.HasState(i, fullHash))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerator ReturnToIdleRoutine(float delaySeconds)
    {
        if (delaySeconds > 0f)
        {
            yield return new WaitForSeconds(delaySeconds);
        }

        SetState("idle", null);
        _returnToIdleCoroutine = null;
    }
}
