using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class SceneSubtitleController : MonoBehaviour
{
    public bool visible = true;
    public int fontSize = 34;
    public int maxCharacters = 108;
    public float maxWidthFraction = 0.78f;
    public float minWidth = 420f;
    public float maxWidth = 1180f;
    public float subtitleHeight = 96f;
    public float bottomOffset = 58f;
    public float horizontalPadding = 28f;
    public float verticalPadding = 8f;
    public float defaultVisibleSeconds = 3.2f;
    public float afterSpeechVisibleSeconds = 0.85f;
    public float mergeWindowSeconds = 0.9f;
    public bool playSegmentsSequentially = true;
    public bool splitLongSubtitleIntoSegments = true;
    public int segmentMaxCharacters = 32;
    public int maxQueuedSegments = 16;
    public float minSegmentVisibleSeconds = 1.25f;
    public float maxSegmentVisibleSeconds = 4.8f;
    public float charactersPerSecond = 10.5f;
    public bool allowIndefiniteSpeechHold = false;
    public float fadeSpeed = 18f;
    public Color textColor = Color.white;
    public Color backgroundColor = new Color(0f, 0f, 0f, 0.58f);
    public Color outlineColor = new Color(0f, 0f, 0f, 0.95f);

    private Canvas _canvas;
    private RectTransform _canvasRect;
    private RectTransform _panelRect;
    private Image _background;
    private Text _text;
    private CanvasGroup _group;
    private Font _font;
    private readonly Queue<string> _queuedSubtitles = new Queue<string>();
    private string _currentText = "";
    private string _lastAcceptedSubtitleText = "";
    private float _hideAt = -1f;
    private float _lastShowAt = -10f;
    private bool _heldBySpeech;
    private bool _speechHoldRequested;
    private float _targetAlpha;

    private void Awake()
    {
        EnsureUi();
        ClearSubtitle(true);
    }

    private void LateUpdate()
    {
        EnsureUi();
        ApplyLayout();
        UpdateFadeAndLifetime();
    }

    public void ShowSubtitle(string message, float seconds = -1f)
    {
        string cleanMessage = NormalizeMessage(message);
        if (string.IsNullOrEmpty(cleanMessage))
        {
            return;
        }

        cleanMessage = ResolveSequentialSubtitleMessage(cleanMessage);
        if (string.IsNullOrEmpty(cleanMessage))
        {
            return;
        }

        EnsureUi();
        if (_canvas == null || _text == null || _group == null)
        {
            return;
        }

        List<string> segments = SplitSubtitleSegments(cleanMessage);
        if (segments.Count > 1)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                ShowSubtitleSegment(segments[i], seconds);
            }

            return;
        }

        ShowSubtitleSegment(cleanMessage, seconds);
    }

    private void ShowSubtitleSegment(string cleanMessage, float seconds)
    {
        if (string.IsNullOrEmpty(cleanMessage))
        {
            return;
        }

        if (!playSegmentsSequentially && ShouldReplaceCurrent(cleanMessage))
        {
            string replacement = cleanMessage.Length >= _currentText.Length ? cleanMessage : _currentText;
            PresentSubtitle(replacement, seconds, true);
            return;
        }

        bool hasActiveSubtitle = !string.IsNullOrEmpty(_currentText)
            && (_heldBySpeech || _targetAlpha > 0f || (_group != null && _group.alpha > 0.001f));
        if (playSegmentsSequentially && hasActiveSubtitle)
        {
            EnqueueSubtitle(cleanMessage);
            if (seconds == 0f)
            {
                _speechHoldRequested = true;
                if (allowIndefiniteSpeechHold)
                {
                    _heldBySpeech = true;
                    _hideAt = -1f;
                }
                else
                {
                    ScheduleCurrentSegmentAdvance();
                }
            }
            else
            {
                ScheduleCurrentSegmentAdvance();
            }
            return;
        }

        PresentSubtitle(cleanMessage, seconds, true);
    }

    private void PresentSubtitle(string cleanMessage, float seconds, bool allowSpeechHold)
    {
        _currentText = cleanMessage;
        _lastShowAt = Time.unscaledTime;
        _text.text = _currentText;
        _text.fontSize = fontSize;
        _text.color = textColor;
        if (_background != null)
        {
            _background.color = backgroundColor;
        }

        if (seconds == 0f)
        {
            _speechHoldRequested = allowSpeechHold;
            bool shouldHold = allowSpeechHold && _queuedSubtitles.Count == 0;
            _heldBySpeech = shouldHold;
            _hideAt = shouldHold && allowIndefiniteSpeechHold
                ? -1f
                : Time.unscaledTime + ResolveSegmentVisibleSeconds(_currentText);
        }
        else
        {
            _heldBySpeech = false;
            float visibleSeconds = seconds > 0f ? seconds : defaultVisibleSeconds;
            _hideAt = Time.unscaledTime + Mathf.Max(0.15f, visibleSeconds);
        }

        _canvas.gameObject.SetActive(true);
        _targetAlpha = visible ? 1f : 0f;
    }

    public void HoldVisibleSubtitle()
    {
        if (string.IsNullOrEmpty(_currentText))
        {
            return;
        }

        _speechHoldRequested = true;
        if (_queuedSubtitles.Count > 0)
        {
            ScheduleCurrentSegmentAdvance();
            return;
        }

        _heldBySpeech = true;
        if (allowIndefiniteSpeechHold)
        {
            _hideAt = -1f;
        }
        else
        {
            float targetHideAt = Time.unscaledTime + ResolveSegmentVisibleSeconds(_currentText) + Mathf.Max(0f, afterSpeechVisibleSeconds);
            if (_hideAt < 0f || _hideAt < targetHideAt)
            {
                _hideAt = targetHideAt;
            }
        }
        _targetAlpha = visible ? 1f : 0f;
    }

    public void ReleaseHeldSubtitle(float seconds = -1f)
    {
        _speechHoldRequested = false;
        if (string.IsNullOrEmpty(_currentText) && _queuedSubtitles.Count == 0)
        {
            return;
        }

        if (_queuedSubtitles.Count > 0)
        {
            ScheduleCurrentSegmentAdvance();
            return;
        }

        _heldBySpeech = false;
        float delay = seconds >= 0f ? seconds : afterSpeechVisibleSeconds;
        _hideAt = Time.unscaledTime + Mathf.Max(0f, delay);
    }

    public void ClearSubtitle(bool immediate = false)
    {
        _queuedSubtitles.Clear();
        _heldBySpeech = false;
        _speechHoldRequested = false;
        _hideAt = -1f;
        _currentText = "";
        _lastAcceptedSubtitleText = "";
        _targetAlpha = 0f;
        if (_text != null)
        {
            _text.text = "";
        }
        if (immediate && _group != null)
        {
            _group.alpha = 0f;
        }
        if (immediate && _canvas != null)
        {
            _canvas.gameObject.SetActive(false);
        }
    }

    private void EnsureUi()
    {
        if (_canvas != null)
        {
            return;
        }

        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null)
        {
            _font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", fontSize);
        }

        GameObject canvasObject = new GameObject("SceneSubtitleCanvas");
        canvasObject.transform.SetParent(transform, false);
        _canvasRect = canvasObject.AddComponent<RectTransform>();

        _canvas = canvasObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 950;

        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        _group = canvasObject.AddComponent<CanvasGroup>();
        _group.alpha = 0f;
        _group.blocksRaycasts = false;
        _group.interactable = false;

        GameObject panelObject = new GameObject("SubtitlePanel");
        panelObject.transform.SetParent(canvasObject.transform, false);
        _panelRect = panelObject.AddComponent<RectTransform>();
        _panelRect.anchorMin = new Vector2(0.5f, 0f);
        _panelRect.anchorMax = new Vector2(0.5f, 0f);
        _panelRect.pivot = new Vector2(0.5f, 0f);

        _background = panelObject.AddComponent<Image>();
        _background.color = backgroundColor;
        _background.raycastTarget = false;

        GameObject textObject = new GameObject("SubtitleText");
        textObject.transform.SetParent(panelObject.transform, false);
        RectTransform textRect = textObject.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(horizontalPadding, verticalPadding);
        textRect.offsetMax = new Vector2(-horizontalPadding, -verticalPadding);

        _text = textObject.AddComponent<Text>();
        _text.font = _font;
        _text.fontSize = fontSize;
        _text.fontStyle = FontStyle.Bold;
        _text.alignment = TextAnchor.MiddleCenter;
        _text.horizontalOverflow = HorizontalWrapMode.Wrap;
        _text.verticalOverflow = VerticalWrapMode.Truncate;
        _text.resizeTextForBestFit = false;
        _text.color = textColor;
        _text.raycastTarget = false;

        Outline outline = textObject.AddComponent<Outline>();
        outline.effectColor = outlineColor;
        outline.effectDistance = new Vector2(2f, -2f);
        outline.useGraphicAlpha = true;

        Shadow shadow = textObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.9f);
        shadow.effectDistance = new Vector2(0f, -3f);
        shadow.useGraphicAlpha = true;

        ApplyLayout();
    }

    private void ApplyLayout()
    {
        if (_panelRect == null)
        {
            return;
        }

        float canvasWidth = 1920f;
        if (_canvasRect != null && _canvasRect.rect.width > 1f)
        {
            canvasWidth = _canvasRect.rect.width;
        }

        float width = Mathf.Clamp(canvasWidth * Mathf.Clamp01(maxWidthFraction), minWidth, maxWidth);
        _panelRect.sizeDelta = new Vector2(width, Mathf.Max(48f, subtitleHeight));
        _panelRect.anchoredPosition = new Vector2(0f, Mathf.Max(0f, bottomOffset));
    }

    private void UpdateFadeAndLifetime()
    {
        if (_group == null || _canvas == null)
        {
            return;
        }

        if (_hideAt >= 0f && Time.unscaledTime >= _hideAt)
        {
            if (ShowNextQueuedSubtitle())
            {
                return;
            }

            if (allowIndefiniteSpeechHold && _speechHoldRequested && !string.IsNullOrEmpty(_currentText))
            {
                _heldBySpeech = true;
                _hideAt = -1f;
                return;
            }

            _hideAt = -1f;
            _currentText = "";
            _lastAcceptedSubtitleText = "";
            if (_text != null)
            {
                _text.text = "";
            }
            _targetAlpha = 0f;
        }

        if (!visible)
        {
            _targetAlpha = 0f;
        }

        float speed = Mathf.Max(1f, fadeSpeed);
        _group.alpha = Mathf.MoveTowards(_group.alpha, _targetAlpha, Time.unscaledDeltaTime * speed);
        if (_group.alpha <= 0.001f && _targetAlpha <= 0f && string.IsNullOrEmpty(_currentText))
        {
            _group.alpha = 0f;
            _canvas.gameObject.SetActive(false);
        }
    }

    private bool ShouldReplaceCurrent(string nextText)
    {
        if (string.IsNullOrEmpty(_currentText))
        {
            return false;
        }

        if (Time.unscaledTime - _lastShowAt > Mathf.Max(0f, mergeWindowSeconds))
        {
            return false;
        }

        if (nextText.Contains(_currentText) || _currentText.Contains(nextText))
        {
            return true;
        }

        return false;
    }

    private string ResolveSequentialSubtitleMessage(string cleanMessage)
    {
        if (!playSegmentsSequentially)
        {
            _lastAcceptedSubtitleText = cleanMessage;
            return cleanMessage;
        }

        if (string.IsNullOrEmpty(_lastAcceptedSubtitleText))
        {
            _lastAcceptedSubtitleText = cleanMessage;
            return cleanMessage;
        }

        if (string.Equals(cleanMessage, _lastAcceptedSubtitleText, StringComparison.Ordinal))
        {
            return "";
        }

        if (cleanMessage.StartsWith(_lastAcceptedSubtitleText, StringComparison.Ordinal))
        {
            string addition = CleanSubtitleDelta(cleanMessage.Substring(_lastAcceptedSubtitleText.Length));
            _lastAcceptedSubtitleText = cleanMessage;
            return addition;
        }

        if (_lastAcceptedSubtitleText.StartsWith(cleanMessage, StringComparison.Ordinal))
        {
            return "";
        }

        _lastAcceptedSubtitleText = cleanMessage;
        return cleanMessage;
    }

    private static string CleanSubtitleDelta(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return value.Trim()
            .TrimStart(',', '.', ';', ':', '!', '?', '\uFF0C', '\u3001', '\u3002', '\uFF1B', '\uFF1A')
            .TrimStart();
    }

    private void EnqueueSubtitle(string text)
    {
        if (string.IsNullOrEmpty(text) || text == _currentText || QueueContains(text))
        {
            return;
        }

        while (_queuedSubtitles.Count >= Mathf.Max(1, maxQueuedSegments))
        {
            _queuedSubtitles.Dequeue();
        }

        _queuedSubtitles.Enqueue(text);
    }

    private bool QueueContains(string text)
    {
        foreach (string queued in _queuedSubtitles)
        {
            if (queued == text)
            {
                return true;
            }
        }

        return false;
    }

    private void ScheduleCurrentSegmentAdvance()
    {
        if (string.IsNullOrEmpty(_currentText))
        {
            return;
        }

        _heldBySpeech = false;
        float targetHideAt = Time.unscaledTime + ResolveSegmentVisibleSeconds(_currentText);
        if (_hideAt < 0f || _hideAt > targetHideAt)
        {
            _hideAt = targetHideAt;
        }
    }

    private bool ShowNextQueuedSubtitle()
    {
        while (_queuedSubtitles.Count > 0)
        {
            string next = _queuedSubtitles.Dequeue();
            if (string.IsNullOrEmpty(next) || next == _currentText)
            {
                continue;
            }

            bool isLastQueuedSpeechSegment = _speechHoldRequested && _queuedSubtitles.Count == 0;
            PresentSubtitle(next, _speechHoldRequested ? 0f : ResolveSegmentVisibleSeconds(next), isLastQueuedSpeechSegment);
            return true;
        }

        return false;
    }

    private float ResolveSegmentVisibleSeconds(string text)
    {
        float cps = Mathf.Max(6f, charactersPerSecond);
        float seconds = string.IsNullOrEmpty(text) ? minSegmentVisibleSeconds : text.Length / cps;
        return Mathf.Clamp(seconds, Mathf.Max(0.4f, minSegmentVisibleSeconds), Mathf.Max(minSegmentVisibleSeconds, maxSegmentVisibleSeconds));
    }

    private static string MergeText(string current, string next)
    {
        if (string.IsNullOrEmpty(current) || next.Contains(current))
        {
            return next;
        }

        if (current.Contains(next))
        {
            return current;
        }

        bool joinsWithoutSpace = EndsWithCjkOrPunctuation(current) || StartsWithCjkOrPunctuation(next);
        return joinsWithoutSpace ? current + next : current + " " + next;
    }

    private static bool EndsWithCjkOrPunctuation(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return IsCjkOrPunctuation(value[value.Length - 1]);
    }

    private static bool StartsWithCjkOrPunctuation(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return IsCjkOrPunctuation(value[0]);
    }

    private static bool IsCjkOrPunctuation(char ch)
    {
        return ch >= 0x2E80 || char.IsPunctuation(ch);
    }

    private string NormalizeMessage(string message)
    {
        string normalized = string.IsNullOrWhiteSpace(message) ? "" : message.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return "";
        }

        normalized = normalized.Replace("\r", " ").Replace("\n", " ");
        while (normalized.Contains("  "))
        {
            normalized = normalized.Replace("  ", " ");
        }

        int segmentLimit = Mathf.Max(12, segmentMaxCharacters) * Mathf.Max(1, maxQueuedSegments);
        int limit = splitLongSubtitleIntoSegments
            ? Mathf.Max(Mathf.Max(12, maxCharacters), segmentLimit)
            : Mathf.Max(12, maxCharacters);
        if (normalized.Length > limit)
        {
            normalized = normalized.Substring(0, limit).TrimEnd() + "...";
        }

        return normalized;
    }

    private List<string> SplitSubtitleSegments(string message)
    {
        List<string> segments = new List<string>();
        if (!splitLongSubtitleIntoSegments || string.IsNullOrEmpty(message))
        {
            segments.Add(message);
            return segments;
        }

        int maxChars = Mathf.Max(12, segmentMaxCharacters);
        string current = "";
        for (int i = 0; i < message.Length; i++)
        {
            char ch = message[i];
            current += ch;
            bool sentenceEnd = IsSentenceTerminator(ch);
            bool softBreak = current.Length >= maxChars && IsSoftBreak(ch);
            bool hardBreak = current.Length >= maxChars + 8;
            if (sentenceEnd || softBreak || hardBreak)
            {
                AddSegment(segments, current);
                current = "";
            }
        }

        AddSegment(segments, current);
        if (segments.Count == 0)
        {
            segments.Add(message);
        }

        return segments;
    }

    private static void AddSegment(List<string> segments, string value)
    {
        string segment = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        if (!string.IsNullOrEmpty(segment))
        {
            segments.Add(segment);
        }
    }

    private static bool IsSentenceTerminator(char ch)
    {
        return ch == '。' || ch == '！' || ch == '？' || ch == '!' || ch == '?' || ch == ';' || ch == '；';
    }

    private static bool IsSoftBreak(char ch)
    {
        return ch == '，' || ch == ',' || ch == '、' || ch == ' ' || ch == '　';
    }
}
