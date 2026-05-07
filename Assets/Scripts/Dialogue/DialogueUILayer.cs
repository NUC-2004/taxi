using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

[ExecuteAlways]
public sealed class DialogueUILayer : MonoBehaviour
{
    private enum ExpressionMood
    {
        Calm,
        Happy,
        Angry
    }

    [Header("Authoring")]
    [SerializeField] private bool buildUiIfMissingOnStart = true;
    [SerializeField] private Canvas authoredCanvas;
    [SerializeField] private bool keepEditableHierarchyInEditMode = true;
    [SerializeField] private bool autoApplyLayout = true;

    [SerializeField] private DialogueSequenceManager dialogueManager;
    [SerializeField] private string windowBackgroundResourcePath = "Level0UI/WindowBackground";
    [SerializeField] private string cabinResourcePath = "Level0UI/car";
    [SerializeField] private string calmExpressionResourcePath = "Level0UI/VeraCalm";
    [SerializeField] private string happyExpressionResourcePath = "Level0UI/VeraHappy";
    [SerializeField] private string angryExpressionResourcePath = "Level0UI/VeraAngry";
    [SerializeField] private bool showMusicNextButtonDebugOverlay = true;

    [Header("Text Layout")]
    [SerializeField] private TextAnchor npcTextAlignment = TextAnchor.UpperLeft;
    [SerializeField] private Vector2 npcTextOffsetMin = new Vector2(12f, 16f);
    [SerializeField] private Vector2 npcTextOffsetMax = new Vector2(-24f, -14f);

    private static readonly Color OptionNormalColor = new Color(0.25f, 0.3f, 0.31f, 1f);
    private static readonly Color OptionSelectedColor = new Color(0.95f, 0.82f, 0.22f, 1f);
    private static readonly Color OptionLockedColor = new Color(0.14f, 0.16f, 0.17f, 0.88f);
    private static readonly Color OptionLockedTextColor = new Color(0.62f, 0.66f, 0.66f, 1f);

    private Text phaseText;
    private Text npcText;
    private Text responseQuestionText;
    private Text responseHintText;
    private Text tutorialText;
    private Text affectionValueText;
    private Text expressionFallbackText;
    private Slider affectionSlider;
    private RectTransform headerRoot;
    private RectTransform dialoguePanelRoot;
    private RectTransform responsePanelRoot;
    private RectTransform tutorialPanelRoot;
    private RectTransform expressionPanelRoot;
    private RectTransform timerBarRoot;
    private RectTransform timerLeftFill;
    private RectTransform timerRightFill;
    private RectTransform optionsRoot;
    private RectTransform musicNextButtonRoot;
    private RectTransform pauseMenuRoot;
    private RectTransform completionMenuRoot;
    private Image windowBackgroundImage;
    private Image cabinImage;
    private Image expressionImage;
    private readonly List<Button> optionButtons = new List<Button>();
    private readonly List<Text> optionButtonLabels = new List<Text>();
    private readonly List<PlayerResponseOption> activeOptions = new List<PlayerResponseOption>();
    private int selectedOptionIndex = -1;
    private bool isPaused;
    private bool isMouseConfirming;
    private bool isFailureMenuActive;
    private string lastNpcDialogueText = string.Empty;
    private string preservedNpcTextBeforeResponse = string.Empty;
    private int currentAffectionValue;
    private bool hasSeenAffectionValue;
    private ExpressionMood currentExpressionMood = ExpressionMood.Calm;

    public void Initialize(DialogueSequenceManager manager)
    {
        dialogueManager = manager;
        if (authoredCanvas == null)
        {
            BuildRuntimeUi();
        }

        ResolveReferencesFromHierarchy();
        MergeResponseControlsIntoDialoguePanel();
        EnsureCompletionMenuExists();
        RefreshCompletionMenuContent();
        ApplyResponsiveLayoutIfEnabled();
        RefreshLevel0Visuals();
        Subscribe();
        SyncResponsePanelVisibility();
    }

    private void Awake()
    {
        ResolveReferencesFromHierarchy();
        MergeResponseControlsIntoDialoguePanel();
        SetResponsePanelVisible(false);
        SetDialoguePanelVisible(false);
    }

    private void Update()
    {
        if (WasPausePressed())
        {
            TogglePauseMenu();
        }

        if (isPaused)
        {
            return;
        }

        SyncResponsePanelVisibility();
        if (TryHandleMusicNextButtonClick())
        {
            return;
        }

        TryHandleDialogueAdvanceClick();

        if (dialogueManager == null || dialogueManager.CurrentPhase != DialoguePhase.PlayerResponse || optionButtons.Count == 0)
        {
            return;
        }

        if (WasUpPressed())
        {
            SelectOption(FindSelectableOptionIndex(selectedOptionIndex, -1));
        }
        else if (WasDownPressed())
        {
            SelectOption(FindSelectableOptionIndex(selectedOptionIndex, 1));
        }
        else if (WasConfirmPressed() &&
                 selectedOptionIndex >= 0 &&
                 selectedOptionIndex < activeOptions.Count &&
            !IsOptionLocked(activeOptions[selectedOptionIndex]))
        {
            dialogueManager.ChooseResponse(activeOptions[selectedOptionIndex]);
        }
    }

    private void Start()
    {
        EnsureEventSystem();
        EnsureSceneCamera();

        if (dialogueManager == null)
        {
            dialogueManager = GetComponent<DialogueSequenceManager>();
        }

        if (NeedsUiRebuildAtRuntime())
        {
            BuildRuntimeUi();
        }

        ResolveReferencesFromHierarchy();
        MergeResponseControlsIntoDialoguePanel();
        EnsureCompletionMenuExists();
        RefreshCompletionMenuContent();
        ApplyResponsiveLayoutIfEnabled();
        RefreshLevel0Visuals();
        AutoConfigurePauseButtonsByLabel();
        RebindPauseButtonsInHierarchy();
        ValidatePauseBindings();
        Subscribe();
        SyncResponsePanelVisibility();
    }

    [ContextMenu("Rebuild Editable Hierarchy")]
    public void RebuildEditableHierarchy()
    {
        BuildRuntimeUi();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (Application.isPlaying || !keepEditableHierarchyInEditMode)
        {
            return;
        }

        EditorApplication.delayCall -= EnsureEditableHierarchyInEditor;
        EditorApplication.delayCall += EnsureEditableHierarchyInEditor;
    }

    private void EnsureEditableHierarchyInEditor()
    {
        if (this == null || Application.isPlaying || !keepEditableHierarchyInEditMode)
        {
            return;
        }

        if (!gameObject.scene.IsValid() || string.IsNullOrEmpty(gameObject.scene.path))
        {
            return;
        }

        if (NeedsUiRebuildInEditor())
        {
            BuildRuntimeUi();
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
        }
        else
        {
            ResolveReferencesFromHierarchy();
            MergeResponseControlsIntoDialoguePanel();
            ApplyResponsiveLayoutIfEnabled();
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
        }
    }
#endif

    private bool NeedsUiRebuildAtRuntime()
    {
        if (!buildUiIfMissingOnStart)
        {
            return false;
        }

        return NeedsUiRebuildCore();
    }

    private bool NeedsUiRebuildInEditor()
    {
        if (!keepEditableHierarchyInEditMode)
        {
            return false;
        }

        return NeedsUiRebuildCore();
    }

    private bool NeedsUiRebuildCore()
    {
        Transform canvasTransform = transform.Find("Level0 Dialogue Canvas");
        if (canvasTransform == null)
        {
            return true;
        }

        if (canvasTransform.Find("Cabin Background") == null)
        {
            return true;
        }

        if (canvasTransform.Find("Window Background") == null)
        {
            return true;
        }

        if (canvasTransform.Find("Expression Area/Expression Image") == null)
        {
            return true;
        }

        if (canvasTransform.Find("NPC Dialogue Area/Portrait Area") != null)
        {
            return true;
        }

        return false;
    }

    private void OnDisable()
    {
        if (dialogueManager == null)
        {
            return;
        }

        dialogueManager.PhaseChanged -= HandlePhaseChanged;
        dialogueManager.BlockChanged -= HandleBlockChanged;
        dialogueManager.NpcTextChanged -= HandleNpcTextChanged;
        dialogueManager.ResponseStarted -= HandleResponseStarted;
        dialogueManager.ResponseTimerChanged -= HandleResponseTimerChanged;
        dialogueManager.AffectionChanged -= HandleAffectionChanged;
        dialogueManager.ResponseResolved -= HandleResponseResolved;
        dialogueManager.SequenceFailed -= HandleSequenceFailed;
        dialogueManager.SequenceCompleted -= HandleSequenceCompleted;
    }

    public void SetTutorialText(string text)
    {
        if (tutorialText != null)
        {
            tutorialText.text = text;
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
        dialogueManager.NpcTextChanged -= HandleNpcTextChanged;
        dialogueManager.ResponseStarted -= HandleResponseStarted;
        dialogueManager.ResponseTimerChanged -= HandleResponseTimerChanged;
        dialogueManager.AffectionChanged -= HandleAffectionChanged;
        dialogueManager.ResponseResolved -= HandleResponseResolved;
        dialogueManager.SequenceFailed -= HandleSequenceFailed;
        dialogueManager.SequenceCompleted -= HandleSequenceCompleted;

        dialogueManager.PhaseChanged += HandlePhaseChanged;
        dialogueManager.BlockChanged += HandleBlockChanged;
        dialogueManager.NpcTextChanged += HandleNpcTextChanged;
        dialogueManager.ResponseStarted += HandleResponseStarted;
        dialogueManager.ResponseTimerChanged += HandleResponseTimerChanged;
        dialogueManager.AffectionChanged += HandleAffectionChanged;
        dialogueManager.ResponseResolved += HandleResponseResolved;
        dialogueManager.SequenceFailed += HandleSequenceFailed;
        dialogueManager.SequenceCompleted += HandleSequenceCompleted;
    }

    private void BuildRuntimeUi()
    {
        EnsureEventSystem();
        EnsureSceneCamera();
        RemoveExistingRuntimeCanvas();

        Canvas canvas = new GameObject("Level0 Dialogue Canvas").AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.transform.SetParent(transform, false);
        authoredCanvas = canvas;

        CanvasScaler scaler = canvas.gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        canvas.gameObject.AddComponent<GraphicRaycaster>();

        RectTransform canvasRect = canvas.GetComponent<RectTransform>();

        Image background = CreateImage("Background", canvasRect, new Color(0.03f, 0.04f, 0.05f, 1f));
        Stretch(background.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        windowBackgroundImage = CreateImage("Window Background", canvasRect, Color.white);
        windowBackgroundImage.preserveAspect = false;
        Stretch(windowBackgroundImage.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        cabinImage = CreateImage("Cabin Background", canvasRect, Color.white);
        cabinImage.preserveAspect = false;
        Stretch(cabinImage.rectTransform, new Vector2(0f, -0.02f), new Vector2(1f, 0.72f), Vector2.zero, Vector2.zero);

        musicNextButtonRoot = CreateMusicNextButton(canvasRect).GetComponent<RectTransform>();

        RectTransform header = CreatePanel("Header", canvasRect, new Color(0.08f, 0.1f, 0.12f, 0.82f));
        Stretch(header, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(36f, -126f), new Vector2(620f, -28f));

        phaseText = CreateText("Phase Text", header, "Phase: -", 28, TextAnchor.MiddleLeft);
        Stretch(phaseText.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.32f, 0.5f), new Vector2(20f, -30f), new Vector2(-12f, 30f));

        affectionSlider = CreateSlider("Affection Slider", header);
        Stretch(affectionSlider.GetComponent<RectTransform>(), new Vector2(0.36f, 0.5f), new Vector2(0.72f, 0.5f), new Vector2(0f, -14f), new Vector2(0f, 14f));

        affectionValueText = CreateText("Affection Value", header, "Engagement: -", 24, TextAnchor.MiddleLeft);
        Stretch(affectionValueText.rectTransform, new Vector2(0.75f, 0.5f), new Vector2(1f, 0.5f), new Vector2(10f, -30f), new Vector2(-18f, 30f));

        RectTransform dialoguePanel = CreatePanel("NPC Dialogue Area", canvasRect, new Color(0.07f, 0.09f, 0.11f, 0.8f));
        Stretch(dialoguePanel, new Vector2(0.04f, 0f), new Vector2(0.74f, 0.30f), Vector2.zero, Vector2.zero);

        RectTransform expressionPanel = CreatePanel("Expression Area", canvasRect, new Color(0f, 0f, 0f, 0f));
        Stretch(expressionPanel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-630f, -320f), new Vector2(-34f, -24f));

        expressionImage = CreateImage("Expression Image", expressionPanel, Color.white);
        expressionImage.preserveAspect = true;
        Stretch(expressionImage.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        expressionFallbackText = CreateText("Expression Fallback", expressionPanel, "Expression importing...", 24, TextAnchor.MiddleCenter);
        Stretch(expressionFallbackText.rectTransform, Vector2.zero, Vector2.one, new Vector2(20f, 20f), new Vector2(-20f, -20f));

        npcText = CreateText("NPC Text", dialoguePanel, string.Empty, 34, npcTextAlignment);
        npcText.horizontalOverflow = HorizontalWrapMode.Wrap;
        npcText.verticalOverflow = VerticalWrapMode.Overflow;
        ApplyNpcTextLayout();

        responsePanelRoot = dialoguePanel;

        responseQuestionText = CreateText("Response Question Text", dialoguePanel, string.Empty, 30, TextAnchor.MiddleLeft);
        responseQuestionText.horizontalOverflow = HorizontalWrapMode.Wrap;
        responseQuestionText.verticalOverflow = VerticalWrapMode.Truncate;
        responseQuestionText.color = new Color(0.96f, 0.97f, 0.95f, 1f);
        Stretch(responseQuestionText.rectTransform, new Vector2(0f, 0.74f), new Vector2(1f, 1f), new Vector2(24f, 0f), new Vector2(-24f, -8f));

        responseHintText = CreateText("Response Hint Text", dialoguePanel, "Wait for your turn", 26, TextAnchor.MiddleLeft);
        Stretch(responseHintText.rectTransform, new Vector2(0f, 0.61f), new Vector2(1f, 0.73f), new Vector2(24f, 0f), new Vector2(-24f, 0f));

        timerBarRoot = CreatePanel("Response Timer Bar", dialoguePanel, new Color(0.05f, 0.06f, 0.06f, 0.94f));
        Stretch(timerBarRoot, new Vector2(0.2f, 0.52f), new Vector2(0.8f, 0.60f), Vector2.zero, Vector2.zero);

        Image centerMarker = CreateImage("Center Marker", timerBarRoot, new Color(1f, 1f, 1f, 0.35f));
        Stretch(centerMarker.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(-2f, 0f), new Vector2(2f, 0f));

        timerLeftFill = CreateImage("Left Timer Fill", timerBarRoot, new Color(0.75f, 0.86f, 0.45f, 1f)).rectTransform;
        timerRightFill = CreateImage("Right Timer Fill", timerBarRoot, new Color(0.75f, 0.86f, 0.45f, 1f)).rectTransform;
        SetTimerBar(0f);
        timerBarRoot.gameObject.SetActive(false);

        optionsRoot = CreatePanel("Options Root", dialoguePanel, new Color(0f, 0f, 0f, 0f));
        Stretch(optionsRoot, Vector2.zero, new Vector2(1f, 0.48f), new Vector2(20f, 16f), new Vector2(-20f, -8f));

        RectTransform tutorialPanel = CreatePanel("Tutorial Area", canvasRect, new Color(0.09f, 0.08f, 0.06f, 0.78f));
        Stretch(tutorialPanel, new Vector2(0.04f, 0.30f), new Vector2(0.74f, 0.36f), Vector2.zero, Vector2.zero);

        tutorialText = CreateText("Tutorial Text", tutorialPanel, string.Empty, 24, TextAnchor.MiddleLeft);
        Stretch(tutorialText.rectTransform, Vector2.zero, Vector2.one, new Vector2(24f, 8f), new Vector2(-24f, -8f));

        pauseMenuRoot = BuildPauseMenu(canvasRect);
        completionMenuRoot = BuildCompletionMenu(canvasRect);

        HideOptions();
        RefreshLevel0Visuals();
        ResolveReferencesFromHierarchy();
        AutoConfigurePauseButtonsByLabel();
        RebindPauseButtonsInHierarchy();
        ValidatePauseBindings();
    }

    private void RemoveExistingRuntimeCanvas()
    {
        authoredCanvas = null;
        Transform existingCanvas = transform.Find("Level0 Dialogue Canvas");
        if (existingCanvas == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(existingCanvas.gameObject);
        }
        else
        {
            DestroyImmediate(existingCanvas.gameObject);
        }
    }

    private void ResolveReferencesFromHierarchy()
    {
        if (authoredCanvas == null)
        {
            authoredCanvas = GetComponentInChildren<Canvas>(true);
        }

        headerRoot = FindRect("Level0 Dialogue Canvas/Header");
        dialoguePanelRoot = FindRect("Level0 Dialogue Canvas/NPC Dialogue Area");
        responsePanelRoot = FindRect("Level0 Dialogue Canvas/NPC Dialogue Area");
        tutorialPanelRoot = FindRect("Level0 Dialogue Canvas/Tutorial Area");
        expressionPanelRoot = FindRect("Level0 Dialogue Canvas/Expression Area");
        phaseText = FindText("Level0 Dialogue Canvas/Header/Phase Text");
        affectionSlider = FindRect("Level0 Dialogue Canvas/Header/Affection Slider")?.GetComponent<Slider>();
        affectionValueText = FindText("Level0 Dialogue Canvas/Header/Affection Value");
        windowBackgroundImage = FindRect("Level0 Dialogue Canvas/Window Background")?.GetComponent<Image>();
        cabinImage = FindRect("Level0 Dialogue Canvas/Cabin Background")?.GetComponent<Image>();
        npcText = FindText("Level0 Dialogue Canvas/NPC Dialogue Area/NPC Text");
        responseQuestionText = FindTextAny(
            "Level0 Dialogue Canvas/NPC Dialogue Area/Response Question Text",
            "Level0 Dialogue Canvas/Player Response Area/Response Question Text");
        responseHintText = FindTextAny(
            "Level0 Dialogue Canvas/NPC Dialogue Area/Response Hint Text",
            "Level0 Dialogue Canvas/Player Response Area/Response Hint Text");
        timerBarRoot = FindRectAny(
            "Level0 Dialogue Canvas/NPC Dialogue Area/Response Timer Bar",
            "Level0 Dialogue Canvas/Player Response Area/Response Timer Bar");
        timerLeftFill = FindRectAny(
            "Level0 Dialogue Canvas/NPC Dialogue Area/Response Timer Bar/Left Timer Fill",
            "Level0 Dialogue Canvas/Player Response Area/Response Timer Bar/Left Timer Fill");
        timerRightFill = FindRectAny(
            "Level0 Dialogue Canvas/NPC Dialogue Area/Response Timer Bar/Right Timer Fill",
            "Level0 Dialogue Canvas/Player Response Area/Response Timer Bar/Right Timer Fill");
        optionsRoot = FindRectAny(
            "Level0 Dialogue Canvas/NPC Dialogue Area/Options Root",
            "Level0 Dialogue Canvas/Player Response Area/Options Root");
        musicNextButtonRoot = FindRect("Level0 Dialogue Canvas/Music Next Button");
        tutorialText = FindText("Level0 Dialogue Canvas/Tutorial Area/Tutorial Text");
        pauseMenuRoot = FindRect("Level0 Dialogue Canvas/Pause Overlay");
        completionMenuRoot = FindRect("Level0 Dialogue Canvas/Completion Overlay");
        expressionImage = FindRect("Level0 Dialogue Canvas/Expression Area/Expression Image")?.GetComponent<Image>();
        expressionFallbackText = FindText("Level0 Dialogue Canvas/Expression Area/Expression Fallback");
    }

    private void EnsureCompletionMenuExists()
    {
        if (completionMenuRoot != null)
        {
            RefreshCompletionMenuContent();
            return;
        }

        if (authoredCanvas == null)
        {
            return;
        }

        RectTransform canvasRect = authoredCanvas.GetComponent<RectTransform>();
        if (canvasRect == null)
        {
            return;
        }

        completionMenuRoot = BuildCompletionMenu(canvasRect);
        RefreshCompletionMenuContent();
        AutoConfigurePauseButtonsByLabel();
        RebindPauseButtonsInHierarchy();
    }

    private void OnRectTransformDimensionsChange()
    {
        if (!isActiveAndEnabled)
        {
            return;
        }

        ApplyResponsiveLayoutIfEnabled();
    }

    private void ApplyResponsiveLayout()
    {
        if (windowBackgroundImage != null)
        {
            Stretch(windowBackgroundImage.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        }

        if (cabinImage != null)
        {
            Stretch(cabinImage.rectTransform, new Vector2(0f, -0.02f), new Vector2(1f, 0.72f), Vector2.zero, Vector2.zero);
        }

        if (headerRoot != null)
        {
            Stretch(headerRoot, new Vector2(0.012f, 0.91f), new Vector2(0.988f, 0.985f), Vector2.zero, Vector2.zero);
        }

        if (dialoguePanelRoot != null)
        {
            Stretch(dialoguePanelRoot, new Vector2(0.04f, 0f), new Vector2(0.74f, 0.30f), Vector2.zero, Vector2.zero);
        }

        if (expressionPanelRoot != null)
        {
            Stretch(expressionPanelRoot, new Vector2(0.69f, 0.55f), new Vector2(0.98f, 0.98f), Vector2.zero, Vector2.zero);
        }

        if (responsePanelRoot != null)
        {
            Stretch(responsePanelRoot, new Vector2(0.04f, 0f), new Vector2(0.74f, 0.30f), Vector2.zero, Vector2.zero);
        }

        ApplyResponsePanelLayout();

        if (responseQuestionText == null)
        {
            EnsureResponseQuestionTextExists();
            ApplyResponsePanelLayout();
        }

        ApplyNpcTextLayout();

        if (tutorialPanelRoot != null)
        {
            Stretch(tutorialPanelRoot, new Vector2(0.04f, 0.30f), new Vector2(0.74f, 0.36f), Vector2.zero, Vector2.zero);
        }

        if (musicNextButtonRoot == null)
        {
            EnsureMusicNextButtonExists();
        }

        if (musicNextButtonRoot != null)
        {
            EnsureMusicNextButtonOnTop();
            Stretch(musicNextButtonRoot, new Vector2(0.862f, 0.066f), new Vector2(0.916f, 0.132f), Vector2.zero, Vector2.zero);
            RefreshMusicNextButtonDebugVisual();
            musicNextButtonRoot.SetAsLastSibling();
        }
    }

    private void ApplyResponsiveLayoutIfEnabled()
    {
        if (!autoApplyLayout)
        {
            if (musicNextButtonRoot == null)
            {
                EnsureMusicNextButtonExists();
            }

            if (musicNextButtonRoot != null)
            {
                EnsureMusicNextButtonOnTop();
                RefreshMusicNextButtonDebugVisual();
                musicNextButtonRoot.SetAsLastSibling();
            }

            return;
        }

        ApplyResponsiveLayout();
    }

    private void ApplyResponsePanelLayout()
    {
        if (responsePanelRoot == null)
        {
            return;
        }

        if (responseQuestionText != null)
        {
            responseQuestionText.horizontalOverflow = HorizontalWrapMode.Wrap;
            responseQuestionText.verticalOverflow = VerticalWrapMode.Truncate;
            responseQuestionText.resizeTextForBestFit = true;
            responseQuestionText.resizeTextMinSize = 16;
            responseQuestionText.resizeTextMaxSize = 30;
            Stretch(responseQuestionText.rectTransform, new Vector2(0f, 0.74f), new Vector2(1f, 1f), new Vector2(24f, 0f), new Vector2(-24f, -8f));
        }

        if (responseHintText != null)
        {
            responseHintText.resizeTextForBestFit = true;
            responseHintText.resizeTextMinSize = 12;
            responseHintText.resizeTextMaxSize = 22;
            Stretch(responseHintText.rectTransform, new Vector2(0f, 0.61f), new Vector2(1f, 0.73f), new Vector2(24f, 0f), new Vector2(-24f, 0f));
        }

        if (timerBarRoot != null)
        {
            Stretch(timerBarRoot, new Vector2(0.2f, 0.52f), new Vector2(0.8f, 0.60f), Vector2.zero, Vector2.zero);
        }

        if (optionsRoot != null)
        {
            Stretch(optionsRoot, Vector2.zero, new Vector2(1f, 0.48f), new Vector2(20f, 16f), new Vector2(-20f, -8f));
        }
    }

    private void ApplyNpcTextLayout()
    {
        if (npcText == null)
        {
            return;
        }

        npcText.alignment = npcTextAlignment;
        npcText.horizontalOverflow = HorizontalWrapMode.Wrap;
        npcText.verticalOverflow = VerticalWrapMode.Overflow;
        Stretch(npcText.rectTransform, Vector2.zero, Vector2.one, npcTextOffsetMin, npcTextOffsetMax);
    }

    private void EnsureResponseQuestionTextExists()
    {
        if (responseQuestionText != null || responsePanelRoot == null)
        {
            return;
        }

        responseQuestionText = CreateText("Response Question Text", responsePanelRoot, string.Empty, 30, TextAnchor.MiddleLeft);
        responseQuestionText.color = new Color(0.96f, 0.97f, 0.95f, 1f);
        responseQuestionText.gameObject.SetActive(false);
    }

    private void EnsureMusicNextButtonExists()
    {
        if (musicNextButtonRoot != null)
        {
            EnsureMusicNextButtonOnTop();
            BindMusicNextButton(musicNextButtonRoot.GetComponent<Button>());
            RefreshMusicNextButtonDebugVisual();
            musicNextButtonRoot.SetAsLastSibling();
            return;
        }

        Canvas canvas = authoredCanvas != null ? authoredCanvas : GetComponentInChildren<Canvas>(true);
        if (canvas == null)
        {
            return;
        }

        musicNextButtonRoot = CreateMusicNextButton(canvas.GetComponent<RectTransform>()).GetComponent<RectTransform>();
        EnsureMusicNextButtonOnTop();
        RefreshMusicNextButtonDebugVisual();
        musicNextButtonRoot.SetAsLastSibling();
    }

    private void EnsureMusicNextButtonOnTop()
    {
        if (musicNextButtonRoot == null)
        {
            return;
        }

        Canvas canvas = authoredCanvas != null ? authoredCanvas : GetComponentInChildren<Canvas>(true);
        if (canvas == null)
        {
            return;
        }

        RectTransform canvasRect = canvas.GetComponent<RectTransform>();
        if (canvasRect != null && musicNextButtonRoot.parent != canvasRect)
        {
            musicNextButtonRoot.SetParent(canvasRect, false);
        }
    }

    private Button CreateMusicNextButton(Transform parent)
    {
        Image image = CreateImage("Music Next Button", parent, new Color(0.2f, 0.9f, 1f, 0.18f));
        image.raycastTarget = true;

        Button button = image.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        BindMusicNextButton(button);
        return button;
    }

    private void BindMusicNextButton(Button button)
    {
        if (button == null)
        {
            return;
        }

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(HandleMusicNextButtonClicked);
    }

    private void HandleMusicNextButtonClicked()
    {
        Debug.Log("[DialogueUILayer] Music next button clicked.");
        SceneMusicController.PlayNextTrack();
    }

    private bool TryHandleMusicNextButtonClick()
    {
        if (musicNextButtonRoot == null || !WasPointerPressedThisFrame())
        {
            return false;
        }

        Vector2 pointerPosition = GetPointerScreenPosition();
        if (!RectTransformUtility.RectangleContainsScreenPoint(musicNextButtonRoot, pointerPosition, null))
        {
            return false;
        }

        Debug.Log("[DialogueUILayer] Music next button click detected by fallback hit test.");
        HandleMusicNextButtonClicked();
        return true;
    }

    private void TryHandleDialogueAdvanceClick()
    {
        if (dialogueManager == null ||
            dialogueManager.CurrentPhase != DialoguePhase.NpcSpeaking ||
            dialoguePanelRoot == null ||
            !WasPointerPressedThisFrame())
        {
            return;
        }

        Vector2 pointerPosition = GetPointerScreenPosition();
        if (!RectTransformUtility.RectangleContainsScreenPoint(dialoguePanelRoot, pointerPosition, null))
        {
            return;
        }

        dialogueManager.RequestNpcAdvance();
    }

    private void RefreshMusicNextButtonDebugVisual()
    {
        if (musicNextButtonRoot == null)
        {
            return;
        }

        Image image = musicNextButtonRoot.GetComponent<Image>();
        if (image == null)
        {
            return;
        }

        image.color = showMusicNextButtonDebugOverlay
            ? new Color(0.2f, 0.9f, 1f, 0.18f)
            : new Color(1f, 1f, 1f, 0.001f);
    }

    private void HideResponseQuestion()
    {
        if (responseQuestionText == null)
        {
            return;
        }

        responseQuestionText.text = string.Empty;
        responseQuestionText.gameObject.SetActive(false);
    }

    private void AutoConfigurePauseButtonsByLabel()
    {
        Button[] buttons = GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            Text label = buttons[i].GetComponentInChildren<Text>(true);
            if (label == null)
            {
                continue;
            }

            PauseMenuButtonAction.ActionType? actionType = GetPauseActionForLabel(label.text);
            if (!actionType.HasValue)
            {
                continue;
            }

            PauseMenuButtonAction action = buttons[i].GetComponent<PauseMenuButtonAction>();
            if (action == null)
            {
                action = buttons[i].gameObject.AddComponent<PauseMenuButtonAction>();
            }

            action.SetAction(actionType.Value);
        }
    }

    private void RebindPauseButtonsInHierarchy()
    {
        PauseMenuButtonAction[] buttons = GetComponentsInChildren<PauseMenuButtonAction>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Rebind();
        }
    }

    private static PauseMenuButtonAction.ActionType? GetPauseActionForLabel(string label)
    {
        switch (label.Trim())
        {
            case "Continue":
                return PauseMenuButtonAction.ActionType.Resume;
            case "Return to Main Menu":
            case "End Shift":
            case "回到主菜单":
                return PauseMenuButtonAction.ActionType.ReturnToMainMenu;
            case "Continue Game":
            case "Retry":
            case "继续游戏":
                return PauseMenuButtonAction.ActionType.ContinueGame;
            default:
                return null;
        }
    }

    private static void SetPauseButtonAction(Button button, PauseMenuButtonAction.ActionType actionType)
    {
        if (button == null)
        {
            return;
        }

        PauseMenuButtonAction action = button.GetComponent<PauseMenuButtonAction>();
        if (action == null)
        {
            action = button.gameObject.AddComponent<PauseMenuButtonAction>();
        }

        action.SetAction(actionType);
        action.Rebind();
    }

    private RectTransform FindRect(string path)
    {
        Transform found = transform.Find(path);
        return found as RectTransform;
    }

    private RectTransform FindRectAny(params string[] paths)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            RectTransform found = FindRect(paths[i]);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private Text FindText(string path)
    {
        Transform found = transform.Find(path);
        return found != null ? found.GetComponent<Text>() : null;
    }

    private Text FindTextAny(params string[] paths)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            Text found = FindText(paths[i]);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private void MergeResponseControlsIntoDialoguePanel()
    {
        if (dialoguePanelRoot == null)
        {
            return;
        }

        RectTransform legacyResponsePanel = FindRect("Level0 Dialogue Canvas/Player Response Area");
        if (legacyResponsePanel != null)
        {
            MoveChildIfPresent(legacyResponsePanel, dialoguePanelRoot, "Response Question Text");
            MoveChildIfPresent(legacyResponsePanel, dialoguePanelRoot, "Response Hint Text");
            MoveChildIfPresent(legacyResponsePanel, dialoguePanelRoot, "Response Timer Bar");
            MoveChildIfPresent(legacyResponsePanel, dialoguePanelRoot, "Options Root");
            legacyResponsePanel.gameObject.SetActive(false);
        }

        responsePanelRoot = dialoguePanelRoot;
        responseQuestionText = FindText("Level0 Dialogue Canvas/NPC Dialogue Area/Response Question Text");
        responseHintText = FindText("Level0 Dialogue Canvas/NPC Dialogue Area/Response Hint Text");
        timerBarRoot = FindRect("Level0 Dialogue Canvas/NPC Dialogue Area/Response Timer Bar");
        timerLeftFill = FindRect("Level0 Dialogue Canvas/NPC Dialogue Area/Response Timer Bar/Left Timer Fill");
        timerRightFill = FindRect("Level0 Dialogue Canvas/NPC Dialogue Area/Response Timer Bar/Right Timer Fill");
        optionsRoot = FindRect("Level0 Dialogue Canvas/NPC Dialogue Area/Options Root");
    }

    private static void MoveChildIfPresent(RectTransform oldParent, RectTransform newParent, string childName)
    {
        Transform child = oldParent.Find(childName);
        if (child != null && child.parent != newParent)
        {
            child.SetParent(newParent, false);
        }
    }

    private void HandlePhaseChanged(DialoguePhase phase)
    {
        if (phaseText != null)
        {
            phaseText.text = "Phase: " + phase;
        }

        if (phase == DialoguePhase.NpcSpeaking || phase == DialoguePhase.Complete || phase == DialoguePhase.Failed)
        {
            HideOptions();
            SetTimerVisible(false);
            HideResponseQuestion();
            if (responseHintText != null)
            {
                responseHintText.text = phase == DialoguePhase.Complete
                    ? "Complete"
                    : phase == DialoguePhase.Failed ? "Conversation ended" : "NPC speaking";
            }
        }

        SyncDialoguePanelVisibility();
        UpdatePortraitPhaseTint(phase);
    }

    private void HandleBlockChanged(NpcSpeakingBlock block, int index, int total)
    {
        if (phaseText != null)
        {
            phaseText.text = "Phase: NPC Speaking  Block " + (index + 1) + "/" + total;
        }
    }

    private void HandleNpcTextChanged(string text)
    {
        if (npcText != null)
        {
            npcText.text = text;
        }

        if (IsQuotedDialogue(text))
        {
            lastNpcDialogueText = text;
        }
    }

    private void HandleResponseStarted(IReadOnlyList<PlayerResponseOption> options)
    {
        if (npcText != null && string.IsNullOrWhiteSpace(npcText.text) && !string.IsNullOrWhiteSpace(lastNpcDialogueText))
        {
            npcText.text = lastNpcDialogueText;
        }

        if (npcText != null)
        {
            preservedNpcTextBeforeResponse = npcText.text;
            npcText.text = string.Empty;
        }

        ShowOptions(options);
    }

    private void HandleResponseTimerChanged(float remaining, float duration)
    {
        SetTimerVisible(duration > 0f);
        SetTimerBar(duration <= 0f ? 0f : remaining / duration);
    }

    private void HandleAffectionChanged(int value, int min, int max)
    {
        int previousAffectionValue = currentAffectionValue;
        currentAffectionValue = value;

        if (!hasSeenAffectionValue)
        {
            hasSeenAffectionValue = true;
            currentExpressionMood = ExpressionMood.Calm;
        }
        else if (value > previousAffectionValue)
        {
            currentExpressionMood = ExpressionMood.Happy;
        }
        else if (value < previousAffectionValue)
        {
            currentExpressionMood = ExpressionMood.Angry;
        }
        else
        {
            currentExpressionMood = ExpressionMood.Calm;
        }

        if (affectionSlider != null)
        {
            affectionSlider.minValue = min;
            affectionSlider.maxValue = max;
            affectionSlider.value = value;
        }

        if (affectionValueText != null)
        {
            affectionValueText.text = "Engagement: " + value + "/" + max;
        }

        UpdateExpressionSprite();
    }

    private void HandleResponseResolved(ResponseResult result)
    {
        HideOptions();
        SetTimerVisible(false);
        if (responseHintText != null)
        {
            responseHintText.text = result.WasChosen ? "Response recorded" : "No Response";
        }
    }

    private void HandleSequenceCompleted()
    {
        isFailureMenuActive = false;
        HideOptions();
        SetTimerVisible(false);
        if (phaseText != null)
        {
            phaseText.text = "Phase: Complete";
        }

        string sceneName = SceneManager.GetActiveScene().name;
        if (sceneName == "Level0" || sceneName == "Level1" || sceneName == "Level2")
        {
            ShowCompletionMenu();
        }
    }

    private void HandleSequenceFailed()
    {
        isFailureMenuActive = true;
        HideOptions(true);
        SetTimerVisible(false);
        SetDialoguePanelVisible(true);
        SetNpcTextVisible(true);

        if (phaseText != null)
        {
            phaseText.text = "Phase: Failed";
        }

        if (responseHintText != null)
        {
            responseHintText.text = "Conversation ended";
        }

        ShowCompletionMenu();
    }

    private void ShowOptions(IReadOnlyList<PlayerResponseOption> options)
    {
        HideOptions(true);
        EnsureResponseQuestionTextExists();
        ApplyResponsePanelLayout();
        SetDialoguePanelVisible(false);
        SetResponsePanelVisible(true);

        optionsRoot.gameObject.SetActive(true);
        activeOptions.Clear();
        activeOptions.AddRange(options);
        selectedOptionIndex = FindFirstSelectableOptionIndex(options);
        bool useTimer = dialogueManager != null && dialogueManager.CurrentResponseDurationSeconds > 0f;
        SetTimerVisible(useTimer);
        SetTimerBar(useTimer ? 1f : 0f);

        if (responseHintText != null)
        {
            responseHintText.text = "Use Up/Down to choose, Space or Enter to confirm";
        }

        if (responseQuestionText != null)
        {
            string questionText = !string.IsNullOrWhiteSpace(lastNpcDialogueText)
                ? lastNpcDialogueText
                : preservedNpcTextBeforeResponse;
            responseQuestionText.text = questionText;
            responseQuestionText.gameObject.SetActive(!string.IsNullOrWhiteSpace(questionText));
        }

        for (int i = 0; i < options.Count; i++)
        {
            int optionIndex = i;
            PlayerResponseOption option = options[i];
            Button button = CreateButton("Option " + (i + 1), optionsRoot, option.placeholderText);
            button.interactable = !IsOptionLocked(option);
            RectTransform rect = button.GetComponent<RectTransform>();
            const float spacing = 0.025f;
            float buttonHeight = (1f - spacing * (options.Count - 1)) / options.Count;
            float top = 1f - i * (buttonHeight + spacing);
            float bottom = top - buttonHeight;
            Stretch(rect, new Vector2(0f, bottom), new Vector2(1f, top), Vector2.zero, Vector2.zero);
            button.onClick.AddListener(() => HandleMouseOptionClicked(optionIndex));
            Text label = EnsureButtonLabel(button, GetOptionDisplayText(option), options.Count);
            optionButtons.Add(button);
            optionButtonLabels.Add(label);
        }

        RefreshOptionSelection();
    }

    private void HideOptions(bool preserveQuestionContext = false)
    {
        SetResponsePanelVisible(false);

        if (optionsRoot != null)
        {
            optionsRoot.gameObject.SetActive(false);
        }

        for (int i = optionButtons.Count - 1; i >= 0; i--)
        {
            if (optionButtons[i] != null)
            {
                Destroy(optionButtons[i].gameObject);
            }
        }

        optionButtons.Clear();
        optionButtonLabels.Clear();
        activeOptions.Clear();
        selectedOptionIndex = -1;
        isMouseConfirming = false;
        HideResponseQuestion();
        if (!preserveQuestionContext)
        {
            preservedNpcTextBeforeResponse = string.Empty;
        }
    }

    private void SyncResponsePanelVisibility()
    {
        SyncDialoguePanelVisibility();
    }

    private void SyncDialoguePanelVisibility()
    {
        DialoguePhase phase = dialogueManager != null ? dialogueManager.CurrentPhase : DialoguePhase.None;
        SetDialoguePanelVisible(phase == DialoguePhase.NpcSpeaking || phase == DialoguePhase.PlayerResponse || phase == DialoguePhase.Failed);
        SetNpcTextVisible(phase == DialoguePhase.NpcSpeaking || phase == DialoguePhase.Failed);
        SetResponsePanelVisible(phase == DialoguePhase.PlayerResponse);
    }

    private void SetDialoguePanelVisible(bool visible)
    {
        if (dialoguePanelRoot != null && dialoguePanelRoot.gameObject.activeSelf != visible)
        {
            dialoguePanelRoot.gameObject.SetActive(visible);
        }
    }

    private void SetNpcTextVisible(bool visible)
    {
        if (npcText != null && npcText.gameObject.activeSelf != visible)
        {
            npcText.gameObject.SetActive(visible);
        }
    }

    private void SetResponsePanelVisible(bool visible)
    {
        SetResponseElementVisible(responseQuestionText, visible && !string.IsNullOrWhiteSpace(responseQuestionText != null ? responseQuestionText.text : string.Empty));
        SetResponseElementVisible(responseHintText, visible);

        if (!visible && timerBarRoot != null && timerBarRoot.gameObject.activeSelf)
        {
            timerBarRoot.gameObject.SetActive(false);
        }

        if (optionsRoot != null && optionsRoot.gameObject.activeSelf != visible)
        {
            optionsRoot.gameObject.SetActive(visible);
        }
    }

    private static void SetResponseElementVisible(Graphic graphic, bool visible)
    {
        if (graphic != null && graphic.gameObject.activeSelf != visible)
        {
            graphic.gameObject.SetActive(visible);
        }
    }

    private void HandleMouseOptionClicked(int optionIndex)
    {
        if (isMouseConfirming ||
            dialogueManager == null ||
            dialogueManager.CurrentPhase != DialoguePhase.PlayerResponse ||
            optionIndex < 0 ||
            optionIndex >= activeOptions.Count ||
            IsOptionLocked(activeOptions[optionIndex]))
        {
            return;
        }

        StartCoroutine(ConfirmMouseOptionAfterHighlight(optionIndex));
    }

    private IEnumerator ConfirmMouseOptionAfterHighlight(int optionIndex)
    {
        isMouseConfirming = true;
        SelectOption(optionIndex);

        for (int i = 0; i < optionButtons.Count; i++)
        {
            if (optionButtons[i] != null)
            {
                optionButtons[i].interactable = false;
            }
        }

        yield return new WaitForSecondsRealtime(0.12f);

        if (dialogueManager != null &&
            dialogueManager.CurrentPhase == DialoguePhase.PlayerResponse &&
            optionIndex >= 0 &&
            optionIndex < activeOptions.Count &&
            !IsOptionLocked(activeOptions[optionIndex]))
        {
            dialogueManager.ChooseResponse(activeOptions[optionIndex]);
        }

        isMouseConfirming = false;
    }

    private RectTransform BuildPauseMenu(RectTransform canvasRect)
    {
        RectTransform overlay = CreatePanel("Pause Overlay", canvasRect, new Color(0f, 0f, 0f, 0.62f));
        Stretch(overlay, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        RectTransform panel = CreatePanel("Pause Panel", overlay, new Color(0.13f, 0.15f, 0.16f, 0.98f));
        Stretch(panel, new Vector2(0.34f, 0.32f), new Vector2(0.66f, 0.68f), Vector2.zero, Vector2.zero);

        Text title = CreateText("Pause Title", panel, "Paused", 40, TextAnchor.UpperCenter);
        Stretch(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -56f), new Vector2(0f, -8f));

        Text desc = CreateText("Pause Text", panel, "The cab is idling. Choose what happens next.", 24, TextAnchor.UpperCenter);
        desc.horizontalOverflow = HorizontalWrapMode.Wrap;
        desc.verticalOverflow = VerticalWrapMode.Overflow;
        desc.color = new Color(0.85f, 0.87f, 0.88f, 1f);
        Stretch(desc.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(32f, -128f), new Vector2(-32f, -72f));

        Button continueButton = CreatePauseButton("Continue Button", panel, "Continue", PauseMenuButtonAction.ActionType.Resume);
        Stretch(continueButton.GetComponent<RectTransform>(), new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(36f, -10f), new Vector2(-36f, 58f));

        Button mainMenuButton = CreatePauseButton("Main Menu Button", panel, "Return to Main Menu", PauseMenuButtonAction.ActionType.ReturnToMainMenu);
        Stretch(mainMenuButton.GetComponent<RectTransform>(), new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(36f, -102f), new Vector2(-36f, -34f));

        overlay.gameObject.SetActive(false);
        return overlay;
    }

    private RectTransform BuildCompletionMenu(RectTransform canvasRect)
    {
        RectTransform overlay = CreatePanel("Completion Overlay", canvasRect, new Color(0f, 0f, 0f, 0.58f));
        Stretch(overlay, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        RectTransform panel = CreatePanel("Completion Panel", overlay, new Color(0.12f, 0.14f, 0.15f, 0.98f));
        Stretch(panel, new Vector2(0.33f, 0.32f), new Vector2(0.67f, 0.68f), Vector2.zero, Vector2.zero);

        Text title = CreateText("Completion Title", panel, "Complete", 38, TextAnchor.UpperCenter);
        Stretch(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -58f), new Vector2(0f, -10f));

        Text desc = CreateText("Completion Text", panel, "Choose what happens next.", 24, TextAnchor.UpperCenter);
        desc.horizontalOverflow = HorizontalWrapMode.Wrap;
        desc.verticalOverflow = VerticalWrapMode.Overflow;
        desc.color = new Color(0.86f, 0.88f, 0.88f, 1f);
        Stretch(desc.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(32f, -132f), new Vector2(-32f, -74f));

        Button continueButton = CreatePauseButton("Continue Game Button", panel, "Continue Game", PauseMenuButtonAction.ActionType.ContinueGame);
        Stretch(continueButton.GetComponent<RectTransform>(), new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(36f, -10f), new Vector2(-36f, 58f));

        Button mainMenuButton = CreatePauseButton("Completion Main Menu Button", panel, "Return to Main Menu", PauseMenuButtonAction.ActionType.ReturnToMainMenu);
        Stretch(mainMenuButton.GetComponent<RectTransform>(), new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(36f, -102f), new Vector2(-36f, -34f));

        overlay.gameObject.SetActive(false);
        return overlay;
    }

    private void ShowCompletionMenu()
    {
        if (completionMenuRoot == null)
        {
            EnsureCompletionMenuExists();
        }

        if (completionMenuRoot != null)
        {
            RefreshCompletionMenuContent();
            completionMenuRoot.gameObject.SetActive(true);
        }
    }

    private void RefreshCompletionMenuContent()
    {
        if (completionMenuRoot == null)
        {
            return;
        }

        Text title = FindText("Level0 Dialogue Canvas/Completion Overlay/Completion Panel/Completion Title");
        if (title != null)
        {
            title.text = isFailureMenuActive ? "Shift Ended" : GetCompletionTitle();
        }

        Text desc = FindText("Level0 Dialogue Canvas/Completion Overlay/Completion Panel/Completion Text");
        if (desc != null)
        {
            desc.text = isFailureMenuActive
                ? GetFailureDescription()
                : GetCompletionDescription();
        }

        Text continueLabel = FindText("Level0 Dialogue Canvas/Completion Overlay/Completion Panel/Continue Game Button/Label");
        if (continueLabel != null)
        {
            continueLabel.text = isFailureMenuActive ? "Retry" : GetContinueButtonLabel();
            SetPauseButtonAction(continueLabel.GetComponentInParent<Button>(), PauseMenuButtonAction.ActionType.ContinueGame);
        }

        Text mainMenuLabel = FindText("Level0 Dialogue Canvas/Completion Overlay/Completion Panel/Completion Main Menu Button/Label");
        if (mainMenuLabel != null)
        {
            mainMenuLabel.text = isFailureMenuActive ? "End Shift" : "Return to Main Menu";
            SetPauseButtonAction(mainMenuLabel.GetComponentInParent<Button>(), PauseMenuButtonAction.ActionType.ReturnToMainMenu);
        }
    }

    private string GetCompletionTitle()
    {
        switch (SceneManager.GetActiveScene().name)
        {
            case "Level2":
                return "Level 2 Complete";
            case "Level1":
                return "Level 1 Complete";
            case "Level0":
            default:
                return "Level 0 Complete";
        }
    }

    private string GetCompletionDescription()
    {
        switch (SceneManager.GetActiveScene().name)
        {
            case "Level2":
                return "Daniel is gone. The shift can return to the route table.";
            case "Level1":
                return "Daniel is gone. The next route is ready.";
            case "Level0":
            default:
                return "The first ride is over. Choose your next stop.";
        }
    }

    private string GetContinueButtonLabel()
    {
        switch (SceneManager.GetActiveScene().name)
        {
            case "Level2":
                return "Return to Main Menu";
            case "Level1":
                return "Continue to Level 2";
            case "Level0":
            default:
                return "Continue to Level 1";
        }
    }

    private string GetFailureDescription()
    {
        return dialogueManager != null && !string.IsNullOrWhiteSpace(dialogueManager.FailureMessage)
            ? dialogueManager.FailureMessage
            : "Seems the passenger does not want to talk anymore.";
    }

    private void TogglePauseMenu()
    {
        if (pauseMenuRoot == null)
        {
            return;
        }

        if (isPaused)
        {
            ResumeFromPauseMenu();
            return;
        }

        isPaused = true;
        Time.timeScale = 0f;
        pauseMenuRoot.gameObject.SetActive(true);
    }

    public void ResumeFromPauseMenu()
    {
        isPaused = false;
        Time.timeScale = 1f;
        if (pauseMenuRoot != null)
        {
            pauseMenuRoot.gameObject.SetActive(false);
        }
    }

    public void ReturnToMainMenu()
    {
        Time.timeScale = 1f;
        SceneManager.LoadScene("SampleScene");
    }

    public void ContinueGame()
    {
        Time.timeScale = 1f;

        if (isFailureMenuActive)
        {
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            return;
        }

        switch (SceneManager.GetActiveScene().name)
        {
            case "Level0":
                SceneManager.LoadScene("Level1");
                break;
            case "Level1":
                SceneManager.LoadScene("Level2");
                break;
            case "Level2":
                SceneManager.LoadScene("SampleScene");
                break;
            default:
                SceneManager.LoadScene("SampleScene");
                break;
        }
    }

    private void ValidatePauseBindings()
    {
        Button[] buttons = GetComponentsInChildren<Button>(true);
        bool hasContinue = false;
        bool hasReturn = false;

        for (int i = 0; i < buttons.Length; i++)
        {
            Text label = buttons[i].GetComponentInChildren<Text>(true);
            if (label == null)
            {
                continue;
            }

            if (label.text == "Continue")
            {
                hasContinue = buttons[i].GetComponent<PauseMenuButtonAction>() != null;
            }
            else if (label.text == "Return to Main Menu")
            {
                hasReturn = buttons[i].GetComponent<PauseMenuButtonAction>() != null;
            }
        }

        if (!hasContinue || !hasReturn)
        {
            Debug.LogWarning("Pause menu binding self-check failed. Rebuild the Level0 editable hierarchy.");
        }
    }

    private void SelectOption(int index)
    {
        if (optionButtons.Count == 0)
        {
            selectedOptionIndex = -1;
            return;
        }

        selectedOptionIndex = Mathf.Clamp(index, 0, optionButtons.Count - 1);
        RefreshOptionSelection();
    }

    private void RefreshOptionSelection()
    {
        for (int i = 0; i < optionButtons.Count; i++)
        {
            bool isLocked = i < activeOptions.Count && IsOptionLocked(activeOptions[i]);
            Image image = optionButtons[i].GetComponent<Image>();
            if (image != null)
            {
                if (isLocked)
                {
                    image.color = OptionLockedColor;
                }
                else
                {
                    image.color = i == selectedOptionIndex ? OptionSelectedColor : OptionNormalColor;
                }
            }

            if (i < optionButtonLabels.Count && optionButtonLabels[i] != null)
            {
                if (i < activeOptions.Count)
                {
                    optionButtonLabels[i].text = GetOptionDisplayText(activeOptions[i]);
                }

                if (isLocked)
                {
                    optionButtonLabels[i].color = OptionLockedTextColor;
                }
                else
                {
                    optionButtonLabels[i].color = i == selectedOptionIndex
                        ? new Color(0.12f, 0.1f, 0.02f, 1f)
                        : Color.white;
                }
                optionButtonLabels[i].enabled = true;
                optionButtonLabels[i].transform.SetAsLastSibling();
                optionButtonLabels[i].gameObject.SetActive(true);
            }
        }
    }

    private int FindFirstSelectableOptionIndex(IReadOnlyList<PlayerResponseOption> options)
    {
        if (options == null)
        {
            return -1;
        }

        for (int i = 0; i < options.Count; i++)
        {
            if (!IsOptionLocked(options[i]))
            {
                return i;
            }
        }

        return options.Count > 0 ? 0 : -1;
    }

    private int FindSelectableOptionIndex(int startIndex, int direction)
    {
        if (activeOptions.Count == 0)
        {
            return -1;
        }

        int step = direction < 0 ? -1 : 1;
        int index = startIndex;
        for (int i = 0; i < activeOptions.Count; i++)
        {
            index += step;
            if (index < 0)
            {
                index = activeOptions.Count - 1;
            }
            else if (index >= activeOptions.Count)
            {
                index = 0;
            }

            if (!IsOptionLocked(activeOptions[index]))
            {
                return index;
            }
        }

        return startIndex;
    }

    private static Text EnsureButtonLabel(Button button, string labelText, int optionCount = 3)
    {
        Text label = button.GetComponentInChildren<Text>(true);
        if (label == null)
        {
            label = CreateText("Label", button.transform, labelText, 26, TextAnchor.MiddleCenter);
        }

        int maxSize = optionCount >= 5 ? 20 : optionCount >= 4 ? 22 : 26;
        int minSize = optionCount >= 5 ? 10 : 12;

        label.text = labelText;
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = maxSize;
        label.resizeTextForBestFit = true;
        label.resizeTextMinSize = minSize;
        label.resizeTextMaxSize = maxSize;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        label.alignment = TextAnchor.MiddleCenter;
        label.raycastTarget = false;
        label.enabled = true;
        label.color = Color.white;
        Stretch(label.rectTransform, Vector2.zero, Vector2.one, new Vector2(8f, 0f), new Vector2(-8f, 0f));
        label.transform.SetAsLastSibling();
        return label;
    }

    private bool IsOptionLocked(PlayerResponseOption option)
    {
        return dialogueManager != null
            ? dialogueManager.IsOptionLockedForCurrentState(option)
            : DialogueSequenceManager.IsOptionLocked(option);
    }

    private string GetOptionDisplayText(PlayerResponseOption option)
    {
        if (option == null)
        {
            return string.Empty;
        }

        bool isSpecial = option.isKeywordOption || option.isUnlockedOption || option.requiredAffection > 0;
        if (isSpecial)
        {
            string lockReason = dialogueManager != null
                ? dialogueManager.GetOptionLockReason(option)
                : DialogueSequenceManager.IsOptionLocked(option) ? "earlier choice required" : null;
            string suffix = !string.IsNullOrWhiteSpace(lockReason) ? " (locked: " + lockReason + ")" : string.Empty;
            return option.placeholderText + suffix + " [!]";
        }

        return option.placeholderText;
    }

    private void RefreshLevel0Visuals()
    {
        RefreshWindowBackground();
        RefreshCabinBackground();
        UpdateExpressionSprite();
    }

    private void RefreshWindowBackground()
    {
        if (windowBackgroundImage == null)
        {
            return;
        }

        Sprite sprite = LoadSpriteFromResourcePath(windowBackgroundResourcePath);
        windowBackgroundImage.sprite = sprite;
        windowBackgroundImage.enabled = sprite != null;
    }

    private void RefreshCabinBackground()
    {
        if (cabinImage == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(cabinResourcePath))
        {
            cabinImage.enabled = cabinImage.sprite != null;
            return;
        }

        Sprite sprite = LoadSpriteFromResourcePath(cabinResourcePath);
        cabinImage.sprite = sprite;
        cabinImage.enabled = sprite != null;
    }

    private void UpdateExpressionSprite()
    {
        if (expressionImage == null)
        {
            return;
        }

        string path = calmExpressionResourcePath;
        switch (currentExpressionMood)
        {
            case ExpressionMood.Happy:
                path = happyExpressionResourcePath;
                break;
            case ExpressionMood.Angry:
                path = angryExpressionResourcePath;
                break;
            default:
                path = calmExpressionResourcePath;
                break;
        }

        Sprite sprite = LoadSpriteFromResourcePath(path);
        expressionImage.sprite = sprite;
        expressionImage.enabled = sprite != null;
        expressionImage.color = Color.white;

        if (expressionFallbackText != null)
        {
            expressionFallbackText.gameObject.SetActive(sprite == null);
            if (sprite == null)
            {
                expressionFallbackText.text = "Expression importing...\nIf it stays blank, set the PNG import type to Sprite (2D and UI).";
            }
        }
    }

    private Sprite LoadSpriteFromResourcePath(string resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            return null;
        }

        Sprite directSprite = Resources.Load<Sprite>(resourcePath);
        if (directSprite != null)
        {
            return directSprite;
        }

        Sprite[] sprites = Resources.LoadAll<Sprite>(resourcePath);
        if (sprites != null && sprites.Length > 0)
        {
            Sprite bestSprite = sprites[0];
            float bestArea = bestSprite.rect.width * bestSprite.rect.height;
            for (int i = 1; i < sprites.Length; i++)
            {
                float area = sprites[i].rect.width * sprites[i].rect.height;
                if (area > bestArea)
                {
                    bestSprite = sprites[i];
                    bestArea = area;
                }
            }

            return bestSprite;
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

    private void UpdatePortraitPhaseTint(DialoguePhase phase)
    {
        if (expressionImage == null || expressionImage.sprite == null)
        {
            return;
        }

        switch (phase)
        {
            case DialoguePhase.NpcSpeaking:
                expressionImage.color = Color.white;
                break;
            case DialoguePhase.PlayerResponse:
                expressionImage.color = new Color(1f, 0.96f, 0.9f, 1f);
                break;
            case DialoguePhase.Complete:
                expressionImage.color = new Color(0.9f, 0.9f, 0.9f, 1f);
                break;
            default:
                expressionImage.color = Color.white;
                break;
        }
    }

    private void SetTimerVisible(bool visible)
    {
        if (timerBarRoot != null)
        {
            timerBarRoot.gameObject.SetActive(visible);
        }
    }

    private void SetTimerBar(float normalizedRemaining)
    {
        float amount = Mathf.Clamp01(normalizedRemaining);
        if (timerLeftFill != null)
        {
            Stretch(timerLeftFill, new Vector2(0.5f - 0.5f * amount, 0f), new Vector2(0.5f, 1f), Vector2.zero, Vector2.zero);
        }

        if (timerRightFill != null)
        {
            Stretch(timerRightFill, new Vector2(0.5f, 0f), new Vector2(0.5f + 0.5f * amount, 1f), Vector2.zero, Vector2.zero);
        }
    }

    private static bool WasUpPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.upArrowKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.UpArrow);
#endif
    }

    private static bool WasDownPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.downArrowKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.DownArrow);
#endif
    }

    private static bool WasConfirmPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null &&
               (Keyboard.current.spaceKey.wasPressedThisFrame ||
                Keyboard.current.enterKey.wasPressedThisFrame ||
                Keyboard.current.numpadEnterKey.wasPressedThisFrame);
#else
        return Input.GetKeyDown(KeyCode.Space) ||
               Input.GetKeyDown(KeyCode.Return) ||
               Input.GetKeyDown(KeyCode.KeypadEnter);
#endif
    }

    private static bool WasPausePressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.Escape);
#endif
    }

    private static bool WasPointerPressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
#else
        return Input.GetMouseButtonDown(0);
#endif
    }

    private static Vector2 GetPointerScreenPosition()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
        return Input.mousePosition;
#endif
    }

    private static bool IsQuotedDialogue(string text)
    {
        return !string.IsNullOrWhiteSpace(text) &&
               text.Length >= 2 &&
               text[0] == '"' &&
               text[text.Length - 1] == '"';
    }

    private static void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null)
        {
            return;
        }

        GameObject eventSystem = new GameObject("EventSystem");
        eventSystem.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        eventSystem.AddComponent<InputSystemUIInputModule>();
#else
        eventSystem.AddComponent<StandaloneInputModule>();
#endif
    }

    private static void EnsureSceneCamera()
    {
        Camera existingCamera = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
        if (existingCamera != null)
        {
            if (existingCamera.GetComponent<AudioListener>() == null)
            {
                existingCamera.gameObject.AddComponent<AudioListener>();
            }
            return;
        }

        GameObject cameraObject = new GameObject("Main Camera");
        Camera cameraComponent = cameraObject.AddComponent<Camera>();
        cameraComponent.clearFlags = CameraClearFlags.SolidColor;
        cameraComponent.backgroundColor = new Color(0.08f, 0.09f, 0.1f, 1f);
        cameraComponent.orthographic = true;
        cameraComponent.nearClipPlane = 0.3f;
        cameraComponent.farClipPlane = 1000f;

        cameraObject.tag = "MainCamera";
        cameraObject.AddComponent<AudioListener>();
        cameraObject.transform.position = new Vector3(0f, 0f, -10f);
    }

    private static RectTransform CreatePanel(string name, Transform parent, Color color)
    {
        Image image = CreateImage(name, parent, color);
        return image.rectTransform;
    }

    private static Image CreateImage(string name, Transform parent, Color color)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Image image = go.AddComponent<Image>();
        image.color = color;
        return image;
    }

    private static Text CreateText(string name, Transform parent, string text, int fontSize, TextAnchor alignment)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Text label = go.AddComponent<Text>();
        label.text = text;
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = fontSize;
        label.alignment = alignment;
        label.color = Color.white;
        return label;
    }

    private static Button CreateButton(string name, Transform parent, string labelText)
    {
        Image image = CreateImage(name, parent, OptionNormalColor);
        Button button = image.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.34f, 0.4f, 0.41f, 1f);
        colors.pressedColor = new Color(0.18f, 0.22f, 0.23f, 1f);
        colors.selectedColor = Color.white;
        button.colors = colors;

        Text label = CreateText("Label", image.transform, labelText, 26, TextAnchor.MiddleCenter);
        Stretch(label.rectTransform, Vector2.zero, Vector2.one, new Vector2(12f, 4f), new Vector2(-12f, -4f));
        return button;
    }

    private static Button CreatePauseButton(string name, Transform parent, string labelText, PauseMenuButtonAction.ActionType actionType)
    {
        Image image = CreateImage(name, parent, new Color(0.24f, 0.28f, 0.29f, 1f));
        Button button = image.gameObject.AddComponent<Button>();

        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.9f, 0.82f, 0.34f, 1f);
        colors.pressedColor = new Color(0.66f, 0.58f, 0.24f, 1f);
        colors.selectedColor = new Color(0.9f, 0.82f, 0.34f, 1f);
        button.colors = colors;

        PauseMenuButtonAction action = image.gameObject.AddComponent<PauseMenuButtonAction>();
        action.SetAction(actionType);

        Text label = CreateText("Label", image.transform, labelText, 28, TextAnchor.MiddleCenter);
        label.color = new Color(0.95f, 0.96f, 0.96f, 1f);
        Stretch(label.rectTransform, Vector2.zero, Vector2.one, new Vector2(16f, 6f), new Vector2(-16f, -6f));
        return button;
    }

    private static Slider CreateSlider(string name, Transform parent)
    {
        GameObject root = new GameObject(name);
        root.transform.SetParent(parent, false);
        Slider slider = root.AddComponent<Slider>();
        slider.interactable = false;

        Image background = CreateImage("Background", root.transform, new Color(0.08f, 0.08f, 0.08f, 1f));
        Stretch(background.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        RectTransform fillArea = CreatePanel("Fill Area", root.transform, new Color(0f, 0f, 0f, 0f));
        Stretch(fillArea, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -4f));

        Image fill = CreateImage("Fill", fillArea, new Color(0.53f, 0.78f, 0.42f, 1f));
        Stretch(fill.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        slider.fillRect = fill.rectTransform;
        slider.targetGraphic = background;
        return slider;
    }

    private static void Stretch(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }
}

public sealed class PauseMenuButtonAction : MonoBehaviour
{
    public enum ActionType
    {
        Resume,
        ReturnToMainMenu,
        ContinueGame
    }

    [SerializeField] private ActionType actionType;

    private Button button;

    private void Awake()
    {
        button = GetComponent<Button>();
        Rebind();
    }

    private void OnEnable()
    {
        Rebind();
    }

    public void SetAction(ActionType type)
    {
        actionType = type;
    }

    public void Rebind()
    {
        if (button == null)
        {
            button = GetComponent<Button>();
        }

        if (button == null)
        {
            return;
        }

        button.onClick.RemoveListener(HandleClick);
        button.onClick.AddListener(HandleClick);
    }

    private void HandleClick()
    {
        DialogueUILayer uiLayer = GetComponentInParent<DialogueUILayer>();
        if (uiLayer == null)
        {
            return;
        }

        string label = GetLabelText();
        if (label == "Retry")
        {
            uiLayer.ContinueGame();
            return;
        }

        if (label == "End Shift" || label == "Return to Main Menu" || label == "回到主菜单")
        {
            uiLayer.ReturnToMainMenu();
            return;
        }

        switch (actionType)
        {
            case ActionType.Resume:
                uiLayer.ResumeFromPauseMenu();
                break;
            case ActionType.ReturnToMainMenu:
                uiLayer.ReturnToMainMenu();
                break;
            case ActionType.ContinueGame:
                uiLayer.ContinueGame();
                break;
        }
    }

    private string GetLabelText()
    {
        Text label = GetComponentInChildren<Text>(true);
        return label != null ? label.text.Trim() : string.Empty;
    }
}
