using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public sealed class TrafficSignalPromptController : MonoBehaviour
{
    private enum TrafficCueType
    {
        None,
        Red,
        Green
    }

    private const string Level2SceneName = "Level2";
    private const string Level3SceneName = "Level3";
    private const string RedSignalResourcePath = "Level3UI/TrafficSignalRed";
    private const string GreenSignalResourcePath = "Level3UI/TrafficSignalGreen";
    private const float PromptDurationSeconds = 5f;
    private const int MissPenalty = -1;
    private const string FailureMessage = "(You miss the traffic cue. The ride breaks off before the conversation can continue.)";

    private static readonly Dictionary<string, TrafficCueType> FixedCueByBlockId = new Dictionary<string, TrafficCueType>(StringComparer.Ordinal)
    {
        { "L2_B1_SETUP", TrafficCueType.Green },
        { "L2_B2_SETUP", TrafficCueType.Red },
        { "L2_B3_SETUP", TrafficCueType.Green },
        { "L2_B4_SETUP", TrafficCueType.Red },
        { "L2_B5_SETUP", TrafficCueType.Green },
        { "L2_B6_SETUP", TrafficCueType.Red },
        { "L3_B1_SETUP", TrafficCueType.Green },
        { "L3_B2_SETUP", TrafficCueType.Red },
        { "L3_B3_SETUP", TrafficCueType.Green },
        { "L3_B4_SETUP", TrafficCueType.Red },
        { "L3_B5_SETUP", TrafficCueType.Green }
    };

    private DialogueSequenceManager dialogueManager;
    private RectTransform promptRoot;
    private Image signalImage;
    private Image instructionBackdrop;
    private Text instructionText;
    private Text countdownText;
    private Text keyHintText;
    private Sprite redSignalSprite;
    private Sprite greenSignalSprite;

    private string currentBlockId = string.Empty;
    private string queuedBlockId = string.Empty;
    private TrafficCueType queuedCueType = TrafficCueType.None;
    private TrafficCueType activeCueType = TrafficCueType.None;
    private bool hasQueuedCue;
    private bool promptActive;
    private float promptRemainingSeconds;

    private void Awake()
    {
        dialogueManager = GetComponent<DialogueSequenceManager>();
        LoadSprites();
        Subscribe();
    }

    private void Start()
    {
        TryBuildUi();
        HidePromptUi();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    private void Update()
    {
        if (!IsSupportedScene())
        {
            return;
        }

        if (promptRoot == null)
        {
            TryBuildUi();
        }

        if (!promptActive || Time.timeScale <= 0f)
        {
            return;
        }

        if (WasRequiredKeyPressed())
        {
            ResolvePromptSuccess();
            return;
        }

        RefreshCountdownVisual();
        promptRemainingSeconds -= Time.deltaTime;
        if (promptRemainingSeconds <= 0f)
        {
            ResolvePromptFailure();
        }
    }

    private void Subscribe()
    {
        if (dialogueManager == null)
        {
            return;
        }

        dialogueManager.PhaseChanged -= HandlePhaseChanged;
        dialogueManager.BlockChanged -= HandleBlockChanged;
        dialogueManager.SequenceFailed -= HandleSequenceEnded;
        dialogueManager.SequenceCompleted -= HandleSequenceEnded;

        dialogueManager.PhaseChanged += HandlePhaseChanged;
        dialogueManager.BlockChanged += HandleBlockChanged;
        dialogueManager.SequenceFailed += HandleSequenceEnded;
        dialogueManager.SequenceCompleted += HandleSequenceEnded;
    }

    private void Unsubscribe()
    {
        if (dialogueManager == null)
        {
            return;
        }

        dialogueManager.PhaseChanged -= HandlePhaseChanged;
        dialogueManager.BlockChanged -= HandleBlockChanged;
        dialogueManager.SequenceFailed -= HandleSequenceEnded;
        dialogueManager.SequenceCompleted -= HandleSequenceEnded;
    }

    private void HandleBlockChanged(NpcSpeakingBlock block, int index, int total)
    {
        currentBlockId = block != null ? block.blockId : string.Empty;
        ClearPrompt(clearQueue: false);

        hasQueuedCue = false;
        queuedCueType = TrafficCueType.None;
        queuedBlockId = currentBlockId;

        queuedCueType = GetFixedCueType(block);
        if (queuedCueType == TrafficCueType.None)
        {
            HidePromptUi();
            return;
        }

        queuedBlockId = currentBlockId;
        hasQueuedCue = true;
    }

    private void HandlePhaseChanged(DialoguePhase phase)
    {
        if (phase == DialoguePhase.NpcSpeaking)
        {
            ActivateQueuedCueIfNeeded();
            return;
        }

        ClearPrompt();
    }

    private void HandleSequenceEnded()
    {
        ClearPrompt();
    }

    private void ActivateQueuedCueIfNeeded()
    {
        if (!hasQueuedCue || !string.Equals(queuedBlockId, currentBlockId, StringComparison.Ordinal))
        {
            HidePromptUi();
            return;
        }

        hasQueuedCue = false;
        if (queuedCueType == TrafficCueType.None)
        {
            HidePromptUi();
            return;
        }

        activeCueType = queuedCueType;
        promptRemainingSeconds = PromptDurationSeconds;
        promptActive = true;
        RefreshPromptUi();
        RefreshCountdownVisual();
    }

    private void ResolvePromptSuccess()
    {
        ClearPrompt();
    }

    private void ResolvePromptFailure()
    {
        ClearPrompt();
        if (dialogueManager != null)
        {
            dialogueManager.ChangeAffection(MissPenalty, FailureMessage);
        }
    }

    private void ClearPrompt(bool clearQueue = true)
    {
        promptActive = false;
        promptRemainingSeconds = 0f;
        activeCueType = TrafficCueType.None;
        if (clearQueue)
        {
            hasQueuedCue = false;
            queuedCueType = TrafficCueType.None;
            queuedBlockId = string.Empty;
        }

        HidePromptUi();
    }

    private bool TryBuildUi()
    {
        if (promptRoot != null)
        {
            return true;
        }

        Canvas canvas = GetComponentInChildren<Canvas>(true);
        if (canvas == null)
        {
            return false;
        }

        promptRoot = CreatePanel("Traffic Signal Prompt", canvas.transform, new Color(0f, 0f, 0f, 0f));
        Stretch(promptRoot, new Vector2(0.38f, 0.34f), new Vector2(0.62f, 0.9f), Vector2.zero, Vector2.zero);
        UpdatePromptLayer();
        promptRoot.gameObject.SetActive(false);

        signalImage = CreateImage("Signal Image", promptRoot, Color.white);
        signalImage.preserveAspect = true;
        Stretch(signalImage.rectTransform, new Vector2(0.1f, 0.4f), new Vector2(0.9f, 0.94f), Vector2.zero, Vector2.zero);

        instructionBackdrop = CreateImage("Instruction Backdrop", promptRoot, new Color(0.25f, 0.3f, 0.31f, 1f));
        DialogueUILayer.ApplyOptionButtonVisualStyle(instructionBackdrop);
        Stretch(instructionBackdrop.rectTransform, new Vector2(0.08f, 0.215f), new Vector2(0.92f, 0.335f), Vector2.zero, Vector2.zero);

        instructionText = CreateText("Instruction Text", promptRoot, string.Empty, 28, TextAnchor.MiddleCenter);
        DialogueUILayer.ApplyOptionButtonLabelStyle(instructionText);
        Stretch(instructionText.rectTransform, new Vector2(0.08f, 0.215f), new Vector2(0.92f, 0.335f), new Vector2(12f, 4f), new Vector2(-12f, -4f));

        countdownText = CreateText("Countdown Text", promptRoot, "5", 126, TextAnchor.MiddleCenter);
        countdownText.color = new Color(1f, 0.97f, 0.9f, 0.95f);
        countdownText.raycastTarget = false;
        Stretch(countdownText.rectTransform, new Vector2(0.12f, 0.01f), new Vector2(0.88f, 0.22f), Vector2.zero, Vector2.zero);
        Shadow countdownShadow = countdownText.gameObject.AddComponent<Shadow>();
        countdownShadow.effectColor = new Color(0f, 0f, 0f, 0.72f);
        countdownShadow.effectDistance = new Vector2(3f, -3f);

        keyHintText = CreateText("Key Hint Text", promptRoot, string.Empty, 22, TextAnchor.LowerCenter);
        keyHintText.color = Color.clear;
        Stretch(keyHintText.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0.1f), new Vector2(10f, 0f), new Vector2(-10f, 12f));
        keyHintText.gameObject.SetActive(false);

        if (promptActive)
        {
            RefreshPromptUi();
            RefreshCountdownVisual();
        }

        return true;
    }

    private void RefreshPromptUi()
    {
        if (promptRoot == null || signalImage == null || instructionText == null || keyHintText == null)
        {
            return;
        }

        UpdatePromptLayer();
        promptRoot.gameObject.SetActive(promptActive);
        if (!promptActive)
        {
            return;
        }

        bool isRed = activeCueType == TrafficCueType.Red;
        signalImage.sprite = isRed ? redSignalSprite : greenSignalSprite;
        signalImage.enabled = signalImage.sprite != null;

        instructionText.text = isRed ? "Red light - press S within 5s" : "Green light - press W within 5s";
        instructionText.color = Color.white;
        keyHintText.text = string.Empty;
        keyHintText.color = Color.clear;
    }

    private void RefreshCountdownVisual()
    {
        if (countdownText == null)
        {
            return;
        }

        int number = Mathf.Clamp(Mathf.CeilToInt(promptRemainingSeconds), 1, Mathf.CeilToInt(PromptDurationSeconds));
        float progress = Mathf.Clamp01(number - promptRemainingSeconds);
        countdownText.text = number.ToString();
        countdownText.rectTransform.localScale = Vector3.one * Mathf.Lerp(1f, 0.4f, progress);

        Color color = countdownText.color;
        color.a = Mathf.Lerp(0.98f, 0.12f, progress);
        countdownText.color = color;
    }

    private void HidePromptUi()
    {
        if (promptRoot != null)
        {
            promptRoot.gameObject.SetActive(false);
        }
    }

    private void LoadSprites()
    {
        redSignalSprite = LoadSpriteFromResourcePath(RedSignalResourcePath);
        greenSignalSprite = LoadSpriteFromResourcePath(GreenSignalResourcePath);
    }

    private static Sprite LoadSpriteFromResourcePath(string resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            return null;
        }

        Sprite sprite = Resources.Load<Sprite>(resourcePath);
        if (sprite != null)
        {
            return sprite;
        }

        Texture2D texture = Resources.Load<Texture2D>(resourcePath);
        if (texture == null)
        {
            return null;
        }

        return Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            100f);
    }

    private static TrafficCueType GetFixedCueType(NpcSpeakingBlock block)
    {
        if (block == null || string.IsNullOrWhiteSpace(block.blockId))
        {
            return TrafficCueType.None;
        }

        return FixedCueByBlockId.TryGetValue(block.blockId, out TrafficCueType cueType)
            ? cueType
            : TrafficCueType.None;
    }

    private static bool IsSupportedScene()
    {
        string sceneName = SceneManager.GetActiveScene().name;
        return string.Equals(sceneName, Level2SceneName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sceneName, Level3SceneName, StringComparison.OrdinalIgnoreCase);
    }

    private bool WasRequiredKeyPressed()
    {
        switch (activeCueType)
        {
            case TrafficCueType.Red:
                return WasSPressed();
            case TrafficCueType.Green:
                return WasWPressed();
            default:
                return false;
        }
    }

    private static bool WasWPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.wKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.W);
#endif
    }

    private static bool WasSPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.sKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.S);
#endif
    }

    private void UpdatePromptLayer()
    {
        if (promptRoot == null)
        {
            return;
        }

        Transform canvasTransform = promptRoot.parent;
        if (canvasTransform == null)
        {
            return;
        }

        Transform expressionArea = canvasTransform.Find("Expression Area");
        if (expressionArea != null)
        {
            promptRoot.SetSiblingIndex(expressionArea.GetSiblingIndex());
            return;
        }

        promptRoot.SetAsLastSibling();
    }

    private static RectTransform CreatePanel(string objectName, Transform parent, Color color)
    {
        Image image = CreateImage(objectName, parent, color);
        return image.rectTransform;
    }

    private static Image CreateImage(string objectName, Transform parent, Color color)
    {
        GameObject gameObject = new GameObject(objectName);
        gameObject.transform.SetParent(parent, false);
        Image image = gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static Text CreateText(string objectName, Transform parent, string text, int fontSize, TextAnchor alignment)
    {
        GameObject gameObject = new GameObject(objectName);
        gameObject.transform.SetParent(parent, false);
        Text label = gameObject.AddComponent<Text>();
        label.text = text;
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = fontSize;
        label.alignment = alignment;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        label.raycastTarget = false;
        return label;
    }

    private static void Stretch(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }
}
