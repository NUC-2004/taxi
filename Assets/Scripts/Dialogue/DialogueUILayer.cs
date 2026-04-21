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
    [Header("Authoring")]
    [SerializeField] private bool buildUiIfMissingOnStart = true;
    [SerializeField] private Canvas authoredCanvas;
    [SerializeField] private bool keepEditableHierarchyInEditMode = true;

    [SerializeField] private DialogueSequenceManager dialogueManager;
    [SerializeField] private string portraitResourcePath = "Portraits/Female Sprite by Sutemo";

    private static readonly Color OptionNormalColor = new Color(0.25f, 0.3f, 0.31f, 1f);
    private static readonly Color OptionSelectedColor = new Color(0.95f, 0.82f, 0.22f, 1f);

    private Text phaseText;
    private Text npcText;
    private Text responseHintText;
    private Text tutorialText;
    private Text affectionValueText;
    private Text portraitFallbackText;
    private Slider affectionSlider;
    private RectTransform timerBarRoot;
    private RectTransform timerLeftFill;
    private RectTransform timerRightFill;
    private RectTransform optionsRoot;
    private RectTransform pauseMenuRoot;
    private Image portraitImage;
    private readonly List<Button> optionButtons = new List<Button>();
    private readonly List<Text> optionButtonLabels = new List<Text>();
    private readonly List<PlayerResponseOption> activeOptions = new List<PlayerResponseOption>();
    private int selectedOptionIndex = -1;
    private bool isPaused;

    public void Initialize(DialogueSequenceManager manager)
    {
        dialogueManager = manager;
        if (authoredCanvas == null)
        {
            BuildRuntimeUi();
        }

        ResolveReferencesFromHierarchy();
        Subscribe();
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

        if (dialogueManager == null || dialogueManager.CurrentPhase != DialoguePhase.PlayerResponse || optionButtons.Count == 0)
        {
            return;
        }

        if (WasUpPressed())
        {
            SelectOption(selectedOptionIndex <= 0 ? optionButtons.Count - 1 : selectedOptionIndex - 1);
        }
        else if (WasDownPressed())
        {
            SelectOption(selectedOptionIndex >= optionButtons.Count - 1 ? 0 : selectedOptionIndex + 1);
        }
        else if (WasConfirmPressed() && selectedOptionIndex >= 0 && selectedOptionIndex < activeOptions.Count)
        {
            dialogueManager.ChooseResponse(activeOptions[selectedOptionIndex]);
        }
    }

    private void Start()
    {
        if (dialogueManager == null)
        {
            dialogueManager = GetComponent<DialogueSequenceManager>();
        }

        if (authoredCanvas == null && buildUiIfMissingOnStart)
        {
            BuildRuntimeUi();
        }

        ResolveReferencesFromHierarchy();
        AutoConfigurePauseButtonsByLabel();
        RebindPauseButtonsInHierarchy();
        Subscribe();
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

        if (authoredCanvas != null || transform.Find("Level0 Dialogue Canvas") != null)
        {
            return;
        }

        BuildRuntimeUi();
        EditorSceneManager.MarkSceneDirty(gameObject.scene);
    }
#endif

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
        dialogueManager.SequenceCompleted -= HandleSequenceCompleted;

        dialogueManager.PhaseChanged += HandlePhaseChanged;
        dialogueManager.BlockChanged += HandleBlockChanged;
        dialogueManager.NpcTextChanged += HandleNpcTextChanged;
        dialogueManager.ResponseStarted += HandleResponseStarted;
        dialogueManager.ResponseTimerChanged += HandleResponseTimerChanged;
        dialogueManager.AffectionChanged += HandleAffectionChanged;
        dialogueManager.ResponseResolved += HandleResponseResolved;
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

        Image background = CreateImage("Background", canvasRect, new Color(0.08f, 0.09f, 0.1f, 1f));
        Stretch(background.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        RectTransform header = CreatePanel("Header", canvasRect, new Color(0.13f, 0.16f, 0.17f, 0.95f));
        Stretch(header, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(48f, -118f), new Vector2(-48f, -32f));

        phaseText = CreateText("Phase Text", header, "Phase: -", 28, TextAnchor.MiddleLeft);
        Stretch(phaseText.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.38f, 0.5f), new Vector2(24f, -34f), new Vector2(-12f, 34f));

        affectionSlider = CreateSlider("Affection Slider", header);
        Stretch(affectionSlider.GetComponent<RectTransform>(), new Vector2(0.42f, 0.5f), new Vector2(0.78f, 0.5f), new Vector2(0f, -16f), new Vector2(0f, 16f));

        affectionValueText = CreateText("Affection Value", header, "Affection: -", 24, TextAnchor.MiddleLeft);
        Stretch(affectionValueText.rectTransform, new Vector2(0.8f, 0.5f), new Vector2(1f, 0.5f), new Vector2(10f, -34f), new Vector2(-24f, 34f));

        RectTransform dialoguePanel = CreatePanel("NPC Dialogue Area", canvasRect, new Color(0.16f, 0.17f, 0.17f, 0.96f));
        Stretch(dialoguePanel, new Vector2(0f, 0.48f), new Vector2(1f, 0.82f), new Vector2(80f, -20f), new Vector2(-80f, -20f));

        RectTransform portraitPanel = CreatePanel("Portrait Area", dialoguePanel, new Color(0.13f, 0.14f, 0.15f, 1f));
        Stretch(portraitPanel, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(24f, 24f), new Vector2(344f, -24f));

        portraitImage = CreateImage("Portrait Image", portraitPanel, Color.white);
        portraitImage.preserveAspect = true;
        Stretch(portraitImage.rectTransform, Vector2.zero, Vector2.one, new Vector2(16f, 16f), new Vector2(-16f, -16f));

        portraitFallbackText = CreateText("Portrait Fallback", portraitPanel, "Portrait importing...", 24, TextAnchor.MiddleCenter);
        Stretch(portraitFallbackText.rectTransform, Vector2.zero, Vector2.one, new Vector2(20f, 20f), new Vector2(-20f, -20f));

        npcText = CreateText("NPC Text", dialoguePanel, string.Empty, 34, TextAnchor.MiddleLeft);
        npcText.horizontalOverflow = HorizontalWrapMode.Wrap;
        npcText.verticalOverflow = VerticalWrapMode.Overflow;
        Stretch(npcText.rectTransform, Vector2.zero, Vector2.one, new Vector2(380f, 28f), new Vector2(-36f, -28f));

        RectTransform responsePanel = CreatePanel("Player Response Area", canvasRect, new Color(0.11f, 0.12f, 0.13f, 0.96f));
        Stretch(responsePanel, new Vector2(0f, 0.13f), new Vector2(1f, 0.44f), new Vector2(80f, -8f), new Vector2(-80f, -8f));

        responseHintText = CreateText("Response Hint Text", responsePanel, "Wait for your turn", 26, TextAnchor.MiddleLeft);
        Stretch(responseHintText.rectTransform, new Vector2(0f, 0.82f), new Vector2(1f, 1f), new Vector2(26f, 0f), new Vector2(-26f, -6f));

        timerBarRoot = CreatePanel("Response Timer Bar", responsePanel, new Color(0.05f, 0.06f, 0.06f, 1f));
        Stretch(timerBarRoot, new Vector2(0f, 0.72f), new Vector2(1f, 0.82f), new Vector2(26f, 4f), new Vector2(-26f, -4f));

        Image centerMarker = CreateImage("Center Marker", timerBarRoot, new Color(1f, 1f, 1f, 0.35f));
        Stretch(centerMarker.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(-2f, 0f), new Vector2(2f, 0f));

        timerLeftFill = CreateImage("Left Timer Fill", timerBarRoot, new Color(0.75f, 0.86f, 0.45f, 1f)).rectTransform;
        timerRightFill = CreateImage("Right Timer Fill", timerBarRoot, new Color(0.75f, 0.86f, 0.45f, 1f)).rectTransform;
        SetTimerBar(0f);
        timerBarRoot.gameObject.SetActive(false);

        optionsRoot = CreatePanel("Options Root", responsePanel, new Color(0f, 0f, 0f, 0f));
        Stretch(optionsRoot, Vector2.zero, new Vector2(1f, 0.7f), new Vector2(24f, 18f), new Vector2(-24f, -4f));

        RectTransform tutorialPanel = CreatePanel("Tutorial Area", canvasRect, new Color(0.18f, 0.18f, 0.15f, 0.95f));
        Stretch(tutorialPanel, Vector2.zero, new Vector2(1f, 0.1f), new Vector2(80f, 32f), new Vector2(-80f, -8f));

        tutorialText = CreateText("Tutorial Text", tutorialPanel, string.Empty, 24, TextAnchor.MiddleLeft);
        Stretch(tutorialText.rectTransform, Vector2.zero, Vector2.one, new Vector2(24f, 8f), new Vector2(-24f, -8f));

        pauseMenuRoot = BuildPauseMenu(canvasRect);

        HideOptions();
        RefreshPortrait();
        ResolveReferencesFromHierarchy();
        AutoConfigurePauseButtonsByLabel();
        RebindPauseButtonsInHierarchy();
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

        phaseText = FindText("Level0 Dialogue Canvas/Header/Phase Text");
        affectionSlider = FindRect("Level0 Dialogue Canvas/Header/Affection Slider")?.GetComponent<Slider>();
        affectionValueText = FindText("Level0 Dialogue Canvas/Header/Affection Value");
        npcText = FindText("Level0 Dialogue Canvas/NPC Dialogue Area/NPC Text");
        responseHintText = FindText("Level0 Dialogue Canvas/Player Response Area/Response Hint Text");
        timerBarRoot = FindRect("Level0 Dialogue Canvas/Player Response Area/Response Timer Bar");
        timerLeftFill = FindRect("Level0 Dialogue Canvas/Player Response Area/Response Timer Bar/Left Timer Fill");
        timerRightFill = FindRect("Level0 Dialogue Canvas/Player Response Area/Response Timer Bar/Right Timer Fill");
        optionsRoot = FindRect("Level0 Dialogue Canvas/Player Response Area/Options Root");
        tutorialText = FindText("Level0 Dialogue Canvas/Tutorial Area/Tutorial Text");
        pauseMenuRoot = FindRect("Level0 Dialogue Canvas/Pause Overlay");
        portraitImage = FindRect("Level0 Dialogue Canvas/NPC Dialogue Area/Portrait Area/Portrait Image")?.GetComponent<Image>();
        portraitFallbackText = FindText("Level0 Dialogue Canvas/NPC Dialogue Area/Portrait Area/Portrait Fallback");
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
                return PauseMenuButtonAction.ActionType.ReturnToMainMenu;
            default:
                return null;
        }
    }

    private RectTransform FindRect(string path)
    {
        Transform found = transform.Find(path);
        return found as RectTransform;
    }

    private Text FindText(string path)
    {
        Transform found = transform.Find(path);
        return found != null ? found.GetComponent<Text>() : null;
    }

    private void HandlePhaseChanged(DialoguePhase phase)
    {
        if (phaseText != null)
        {
            phaseText.text = "Phase: " + phase;
        }

        if (phase == DialoguePhase.NpcSpeaking || phase == DialoguePhase.Complete)
        {
            HideOptions();
            SetTimerVisible(false);
            if (responseHintText != null)
            {
                responseHintText.text = phase == DialoguePhase.Complete ? "Complete" : "NPC speaking";
            }
        }

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
    }

    private void HandleResponseStarted(IReadOnlyList<PlayerResponseOption> options)
    {
        ShowOptions(options);
    }

    private void HandleResponseTimerChanged(float remaining, float duration)
    {
        SetTimerBar(duration <= 0f ? 0f : remaining / duration);
    }

    private void HandleAffectionChanged(int value, int min, int max)
    {
        if (affectionSlider != null)
        {
            affectionSlider.minValue = min;
            affectionSlider.maxValue = max;
            affectionSlider.value = value;
        }

        if (affectionValueText != null)
        {
            affectionValueText.text = "Affection: " + value + "/" + max;
        }
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
        HideOptions();
        SetTimerVisible(false);
        if (phaseText != null)
        {
            phaseText.text = "Phase: Complete";
        }
    }

    private void ShowOptions(IReadOnlyList<PlayerResponseOption> options)
    {
        HideOptions();
        optionsRoot.gameObject.SetActive(true);
        activeOptions.Clear();
        activeOptions.AddRange(options);
        selectedOptionIndex = 0;
        SetTimerVisible(true);
        SetTimerBar(1f);

        if (responseHintText != null)
        {
            responseHintText.text = "Use Up/Down to choose, Space to confirm";
        }

        for (int i = 0; i < options.Count; i++)
        {
            PlayerResponseOption option = options[i];
            Button button = CreateButton("Option " + (i + 1), optionsRoot, option.placeholderText);
            RectTransform rect = button.GetComponent<RectTransform>();
            const float spacing = 0.025f;
            float buttonHeight = (1f - spacing * (options.Count - 1)) / options.Count;
            float top = 1f - i * (buttonHeight + spacing);
            float bottom = top - buttonHeight;
            Stretch(rect, new Vector2(0f, bottom), new Vector2(1f, top), Vector2.zero, Vector2.zero);
            button.onClick.AddListener(() => dialogueManager.ChooseResponse(option));
            optionButtons.Add(button);
            optionButtonLabels.Add(button.GetComponentInChildren<Text>());
        }

        RefreshOptionSelection();
    }

    private void HideOptions()
    {
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

        Button continueButton = CreatePauseButton("Continue Button", panel, "Continue");
        Stretch(continueButton.GetComponent<RectTransform>(), new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(36f, -10f), new Vector2(-36f, 58f));
        continueButton.onClick.AddListener(ResumeFromPauseMenu);

        Button mainMenuButton = CreatePauseButton("Main Menu Button", panel, "Return to Main Menu");
        Stretch(mainMenuButton.GetComponent<RectTransform>(), new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(36f, -102f), new Vector2(-36f, -34f));
        mainMenuButton.onClick.AddListener(ReturnToMainMenu);

        overlay.gameObject.SetActive(false);
        return overlay;
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

    private void ResumeFromPauseMenu()
    {
        isPaused = false;
        Time.timeScale = 1f;
        if (pauseMenuRoot != null)
        {
            pauseMenuRoot.gameObject.SetActive(false);
        }
    }

    private void ReturnToMainMenu()
    {
        Time.timeScale = 1f;
        SceneManager.LoadScene("SampleScene");
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
            Image image = optionButtons[i].GetComponent<Image>();
            if (image != null)
            {
                image.color = i == selectedOptionIndex ? OptionSelectedColor : OptionNormalColor;
            }

            if (i < optionButtonLabels.Count && optionButtonLabels[i] != null)
            {
                optionButtonLabels[i].color = i == selectedOptionIndex
                    ? new Color(0.12f, 0.1f, 0.02f, 1f)
                    : Color.white;
            }
        }
    }

    private void RefreshPortrait()
    {
        if (portraitImage == null)
        {
            return;
        }

        Sprite portraitSprite = LoadPortraitSprite();
        portraitImage.sprite = portraitSprite;
        portraitImage.enabled = portraitSprite != null;

        if (portraitFallbackText != null)
        {
            portraitFallbackText.gameObject.SetActive(portraitSprite == null);
            if (portraitSprite == null)
            {
                portraitFallbackText.text = "Importing portrait...\nIf it stays like this, reselect the PSD as Sprite (2D and UI).";
            }
        }
    }

    private Sprite LoadPortraitSprite()
    {
        Sprite[] sprites = Resources.LoadAll<Sprite>(portraitResourcePath);
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

        Texture2D texture = Resources.Load<Texture2D>(portraitResourcePath);
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
        if (portraitImage == null || portraitImage.sprite == null)
        {
            return;
        }

        switch (phase)
        {
            case DialoguePhase.NpcSpeaking:
                portraitImage.color = Color.white;
                break;
            case DialoguePhase.PlayerResponse:
                portraitImage.color = new Color(1f, 0.94f, 0.86f, 1f);
                break;
            case DialoguePhase.Complete:
                portraitImage.color = new Color(0.82f, 0.82f, 0.82f, 1f);
                break;
            default:
                portraitImage.color = Color.white;
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
        return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.Space);
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
        if (Camera.main != null || FindFirstObjectByType<Camera>() != null)
        {
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

    private static Button CreatePauseButton(string name, Transform parent, string labelText)
    {
        Image image = CreateImage(name, parent, new Color(0.24f, 0.28f, 0.29f, 1f));
        Button button = image.gameObject.AddComponent<Button>();

        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.9f, 0.82f, 0.34f, 1f);
        colors.pressedColor = new Color(0.66f, 0.58f, 0.24f, 1f);
        colors.selectedColor = new Color(0.9f, 0.82f, 0.34f, 1f);
        button.colors = colors;

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
        ReturnToMainMenu
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

        switch (actionType)
        {
            case ActionType.Resume:
                uiLayer.SendMessage("ResumeFromPauseMenu", SendMessageOptions.DontRequireReceiver);
                break;
            case ActionType.ReturnToMainMenu:
                uiLayer.SendMessage("ReturnToMainMenu", SendMessageOptions.DontRequireReceiver);
                break;
        }
    }
}
