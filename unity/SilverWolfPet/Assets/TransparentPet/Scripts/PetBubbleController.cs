using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class PetBubbleController : MonoBehaviour
{
    public Transform searchRoot;
    public Transform followTarget;
    public Animator characterAnimator;
    public Camera worldCamera;
    public Vector3 worldOffset = new Vector3(0f, 0.5f, 0f);
    public float defaultVisibleSeconds = 4.2f;
    public int maxVisibleMessages = 3;
    public Vector2 bubbleSize = new Vector2(210f, 48f);
    public float minBubbleWidth = 54f;
    public float minBubbleHeight = 26f;
    public float horizontalPadding = 10f;
    public float verticalPadding = 5f;
    public float bubbleSpacing = 5f;
    public int fontSize = 15;
    public int maxMessageCharacters = 96;
    public float mergeWindowSeconds = 1.4f;
    public float afterSpeechVisibleSeconds = 1.0f;
    public float canvasScale = 0.0039f;
    public float moveSmoothSpeed = 18f;
    public float fadeSmoothSpeed = 14f;

    private Canvas _canvas;
    private RectTransform _canvasRect;
    private Font _font;
    private readonly List<BubbleView> _bubbles = new List<BubbleView>(3);
    private readonly List<BubbleView> _removeBuffer = new List<BubbleView>(3);
    private static Sprite _bubbleSprite;

    private sealed class BubbleView
    {
        public GameObject Root;
        public RectTransform Rect;
        public RectTransform TextRect;
        public Text Text;
        public CanvasGroup Group;
        public string Message = "";
        public float CreatedAt;
        public float ExpiresAt;
        public bool Persistent;
        public bool Exiting;
        public Vector2 TargetPosition;
        public float TargetAlpha;
    }

    private void Awake()
    {
        EnsureBubbleCanvas();
        HideText();
    }

    private void LateUpdate()
    {
        EnsureBubbleCanvas();
        if (_canvas == null)
        {
            return;
        }

        FollowTarget();
        UpdateBubbleAnimations();
    }

    public void ShowText(string message, float seconds = -1f)
    {
        string cleanMessage = NormalizeMessage(message);
        if (string.IsNullOrEmpty(cleanMessage))
        {
            return;
        }

        EnsureBubbleCanvas();
        if (_canvas == null)
        {
            return;
        }

        _canvas.gameObject.SetActive(true);

        BubbleView latest = _bubbles.Count > 0 ? _bubbles[0] : null;
        if (ShouldMergeLatest(latest, cleanMessage))
        {
            string merged = cleanMessage.Length >= latest.Message.Length ? cleanMessage : latest.Message;
            ApplyBubbleContent(latest, merged);
            RefreshLifetime(latest, seconds);
            LayoutBubbles();
            return;
        }

        while (_bubbles.Count >= Mathf.Max(1, maxVisibleMessages))
        {
            RemoveBubble(_bubbles[_bubbles.Count - 1]);
        }

        BubbleView bubble = CreateBubbleView();
        bubble.CreatedAt = Time.time;
        bubble.Group.alpha = 0f;
        bubble.TargetAlpha = 1f;
        bubble.Rect.anchoredPosition = new Vector2(0f, -12f);
        ApplyBubbleContent(bubble, cleanMessage);
        RefreshLifetime(bubble, seconds);
        _bubbles.Insert(0, bubble);
        LayoutBubbles();
    }

    public void HideText()
    {
        for (int i = _bubbles.Count - 1; i >= 0; i--)
        {
            RemoveBubble(_bubbles[i]);
        }

        _bubbles.Clear();
        _hideCanvasIfEmpty();
    }

    public void HoldVisibleText()
    {
        for (int i = 0; i < _bubbles.Count; i++)
        {
            BubbleView bubble = _bubbles[i];
            if (bubble.Exiting)
            {
                continue;
            }

            bubble.Persistent = true;
            bubble.ExpiresAt = -1f;
        }

        LayoutBubbles();
    }

    public void ReleaseHeldText(float seconds = -1f)
    {
        float delay = seconds >= 0f ? seconds : afterSpeechVisibleSeconds;
        delay = Mathf.Max(0f, delay);
        bool changed = false;

        for (int i = 0; i < _bubbles.Count; i++)
        {
            BubbleView bubble = _bubbles[i];
            if (bubble.Exiting || !bubble.Persistent)
            {
                continue;
            }

            bubble.Persistent = false;
            bubble.ExpiresAt = Time.time + delay;
            changed = true;
        }

        if (changed)
        {
            LayoutBubbles();
        }
    }

    private void EnsureBubbleCanvas()
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

        GameObject canvasObject = new GameObject("PetBubbleCanvas");
        canvasObject.transform.SetParent(transform, false);
        canvasObject.transform.localScale = Vector3.one * canvasScale;

        _canvasRect = canvasObject.AddComponent<RectTransform>();
        _canvasRect.sizeDelta = new Vector2(bubbleSize.x + 32f, bubbleSize.y * 3f + bubbleSpacing * 2f + 24f);
        _canvasRect.pivot = new Vector2(0.5f, 0f);

        _canvas = canvasObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        _canvas.worldCamera = worldCamera != null ? worldCamera : Camera.main;

        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 16f;
        canvasObject.AddComponent<GraphicRaycaster>().enabled = false;
    }

    private BubbleView CreateBubbleView()
    {
        GameObject root = new GameObject("BubbleItem");
        root.transform.SetParent(_canvas.transform, false);

        RectTransform rect = root.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0f);
        rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);

        Image background = root.AddComponent<Image>();
        background.sprite = GetBubbleSprite();
        background.type = Image.Type.Sliced;
        background.color = new Color(1f, 0.96f, 0.98f, 0.92f); // 可爱粉白
        background.raycastTarget = false;

        Shadow shadow = root.AddComponent<Shadow>();
        shadow.effectColor = new Color(0.92f, 0.55f, 0.72f, 0.22f); // 粉色淡影
        shadow.effectDistance = new Vector2(0f, -2f);
        shadow.useGraphicAlpha = true;

        CanvasGroup group = root.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;

        GameObject textObject = new GameObject("Text");
        textObject.transform.SetParent(root.transform, false);
        RectTransform textRect = textObject.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;

        Text text = textObject.AddComponent<Text>();
        text.alignment = TextAnchor.MiddleLeft;
        text.color = new Color(0.35f, 0.12f, 0.32f, 1f); // 深紫可爱文字
        text.fontSize = fontSize;
        text.resizeTextForBestFit = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.raycastTarget = false;
        text.font = _font;

        return new BubbleView
        {
            Root = root,
            Rect = rect,
            TextRect = textRect,
            Text = text,
            Group = group
        };
    }

    private void ApplyBubbleContent(BubbleView bubble, string message)
    {
        bubble.Message = message;
        bubble.Text.text = message;
        bubble.Text.fontSize = fontSize;

        Vector2 size = CalculateBubbleSize(bubble.Text, message);
        bubble.Rect.sizeDelta = size;
        bubble.TextRect.offsetMin = new Vector2(horizontalPadding, verticalPadding);
        bubble.TextRect.offsetMax = new Vector2(-horizontalPadding, -verticalPadding);
    }

    private Vector2 CalculateBubbleSize(Text text, string message)
    {
        float maxWidth = Mathf.Max(minBubbleWidth, bubbleSize.x);
        float maxTextWidth = Mathf.Max(1f, maxWidth - horizontalPadding * 2f);
        float pixelsPerUnit = Mathf.Max(1f, text.pixelsPerUnit);

        TextGenerationSettings widthSettings = text.GetGenerationSettings(new Vector2(maxTextWidth, 0f));
        float preferredWidth = text.cachedTextGeneratorForLayout.GetPreferredWidth(message, widthSettings) / pixelsPerUnit;
        float width = Mathf.Clamp(preferredWidth + horizontalPadding * 2f, minBubbleWidth, maxWidth);

        float textWidth = Mathf.Max(1f, width - horizontalPadding * 2f);
        TextGenerationSettings heightSettings = text.GetGenerationSettings(new Vector2(textWidth, 0f));
        float preferredHeight = text.cachedTextGeneratorForLayout.GetPreferredHeight(message, heightSettings) / pixelsPerUnit;
        float height = Mathf.Clamp(preferredHeight + verticalPadding * 2f, minBubbleHeight, bubbleSize.y);

        return new Vector2(Mathf.Ceil(width), Mathf.Ceil(height));
    }

    private void RefreshLifetime(BubbleView bubble, float seconds)
    {
        bubble.Persistent = seconds == 0f;
        bubble.ExpiresAt = bubble.Persistent
            ? -1f
            : Time.time + (seconds > 0f ? seconds : defaultVisibleSeconds);
        bubble.Exiting = false;
    }

    private bool ShouldMergeLatest(BubbleView latest, string message)
    {
        if (latest == null || latest.Exiting)
        {
            return false;
        }

        if (Time.time - latest.CreatedAt > mergeWindowSeconds)
        {
            return false;
        }

        if (latest.Message == message)
        {
            return true;
        }

        return message.StartsWith(latest.Message) || latest.Message.StartsWith(message);
    }

    private void LayoutBubbles()
    {
        float y = 0f;
        int visibleCount = Mathf.Max(1, maxVisibleMessages);
        for (int i = 0; i < _bubbles.Count; i++)
        {
            BubbleView bubble = _bubbles[i];
            bubble.TargetPosition = new Vector2(0f, y);
            bubble.TargetAlpha = bubble.Exiting ? 0f : AgeAlpha(i, visibleCount);
            y += bubble.Rect.sizeDelta.y + bubbleSpacing;
        }
    }

    private float AgeAlpha(int index, int visibleCount)
    {
        if (visibleCount <= 1)
        {
            return 1f;
        }

        float t = Mathf.Clamp01(index / (float)(visibleCount - 1));
        return Mathf.Lerp(1f, 0.42f, t);
    }

    private void UpdateBubbleAnimations()
    {
        if (_bubbles.Count == 0)
        {
            _hideCanvasIfEmpty();
            return;
        }

        _removeBuffer.Clear();
        float moveT = 1f - Mathf.Exp(-moveSmoothSpeed * Time.deltaTime);
        float fadeT = 1f - Mathf.Exp(-fadeSmoothSpeed * Time.deltaTime);

        for (int i = 0; i < _bubbles.Count; i++)
        {
            BubbleView bubble = _bubbles[i];
            if (!bubble.Persistent && !bubble.Exiting && Time.time >= bubble.ExpiresAt)
            {
                bubble.Exiting = true;
                bubble.TargetAlpha = 0f;
                bubble.TargetPosition += new Vector2(0f, 16f);
            }

            bubble.Rect.anchoredPosition = Vector2.Lerp(bubble.Rect.anchoredPosition, bubble.TargetPosition, moveT);
            bubble.Group.alpha = Mathf.Lerp(bubble.Group.alpha, bubble.TargetAlpha, fadeT);

            if (bubble.Exiting && bubble.Group.alpha <= 0.02f)
            {
                _removeBuffer.Add(bubble);
            }
        }

        if (_removeBuffer.Count > 0)
        {
            for (int i = 0; i < _removeBuffer.Count; i++)
            {
                RemoveBubble(_removeBuffer[i]);
            }

            LayoutBubbles();
        }
    }

    private void RemoveBubble(BubbleView bubble)
    {
        if (bubble == null)
        {
            return;
        }

        _bubbles.Remove(bubble);
        if (bubble.Root != null)
        {
            Destroy(bubble.Root);
        }
    }

    private void FollowTarget()
    {
        if (followTarget == null)
        {
            followTarget = FindFollowTarget();
        }

        Transform target = followTarget != null ? followTarget : (searchRoot != null ? searchRoot : transform);
        _canvas.transform.position = target.position + worldOffset;

        Camera cameraToFace = worldCamera != null ? worldCamera : Camera.main;
        if (cameraToFace != null)
        {
            Vector3 direction = _canvas.transform.position - cameraToFace.transform.position;
            if (direction.sqrMagnitude > 0.0001f)
            {
                _canvas.transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            }
        }
    }

    private Transform FindFollowTarget()
    {
        if (characterAnimator != null)
        {
            Transform head = characterAnimator.GetBoneTransform(HumanBodyBones.Head);
            if (head != null)
            {
                return head;
            }
        }

        Transform root = searchRoot != null ? searchRoot : transform;
        Transform[] children = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            string lowerName = children[i].name.ToLowerInvariant();
            if (lowerName.Contains("head") || lowerName.Contains("neck"))
            {
                return children[i];
            }
        }

        return root;
    }

    private string NormalizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        string normalized = message.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        while (normalized.Contains("  "))
        {
            normalized = normalized.Replace("  ", " ");
        }

        if (maxMessageCharacters > 3 && normalized.Length > maxMessageCharacters)
        {
            normalized = normalized.Substring(0, maxMessageCharacters - 3).TrimEnd() + "...";
        }

        return normalized;
    }

    private void _hideCanvasIfEmpty()
    {
        if (_canvas != null && _bubbles.Count == 0)
        {
            _canvas.gameObject.SetActive(false);
        }
    }

    private static Sprite GetBubbleSprite()
    {
        if (_bubbleSprite != null)
        {
            return _bubbleSprite;
        }

        const int width = 128;
        const int height = 64;
        const float radius = 18f;
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.name = "PetChatBubbleGradient";
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        Color top = new Color(1f, 0.97f, 0.99f, 0.96f);   // 浅粉
        Color bottom = new Color(1f, 0.9f, 0.95f, 0.96f); // 粉紫
        Vector2 center = new Vector2(width * 0.5f, height * 0.5f);
        Vector2 half = new Vector2(width * 0.5f - 1f, height * 0.5f - 1f);

        for (int y = 0; y < height; y++)
        {
            float t = y / (float)(height - 1);
            Color color = Color.Lerp(bottom, top, t);
            for (int x = 0; x < width; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f) - center;
                Vector2 q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - (half - Vector2.one * radius);
                float outside = new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude - radius;
                float alpha = Mathf.Clamp01(1.2f - outside);
                color.a = 0.96f * alpha;
                texture.SetPixel(x, y, color);
            }
        }

        texture.Apply(false, true);
        _bubbleSprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, width, height),
            new Vector2(0.5f, 0.5f),
            100f,
            1,
            SpriteMeshType.FullRect,
            new Vector4(radius, radius, radius, radius));
        _bubbleSprite.name = "PetChatBubbleGradientSprite";
        return _bubbleSprite;
    }
}
