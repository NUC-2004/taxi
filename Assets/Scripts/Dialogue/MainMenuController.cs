using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

public sealed class MainMenuController : MonoBehaviour
{
    private static readonly Color NightSky = new Color(0.05f, 0.06f, 0.08f, 1f);
    private static readonly Color CityBlue = new Color(0.12f, 0.16f, 0.2f, 0.9f);
    private static readonly Color DashboardColor = new Color(0.12f, 0.14f, 0.15f, 1f);
    private static readonly Color DashboardEdge = new Color(0.2f, 0.23f, 0.24f, 1f);
    private static readonly Color WarmGold = new Color(0.79f, 0.64f, 0.37f, 1f);
    private static readonly Color SoftIvory = new Color(0.91f, 0.88f, 0.79f, 1f);
    private static readonly Color MeterRed = new Color(0.64f, 0.22f, 0.18f, 1f);

    [Header("Authoring")]
    [SerializeField] private bool buildUiIfMissingOnStart = true;
    [SerializeField] private Canvas authoredCanvas;

    [Header("Bound UI")]
    [SerializeField] private RectTransform levelSelectPanel;
    [SerializeField] private RectTransform settingsPanel;
    [SerializeField] private Text statusText;
    [SerializeField] private Text routeHintText;

    private void Start()
    {
        EnsureEventSystem();

        if (authoredCanvas == null && buildUiIfMissingOnStart)
        {
            BuildUi();
        }

        ResolveReferencesFromHierarchy();
        AutoConfigureButtonsByLabel();
        RebindButtonsInHierarchy();
    }

    [ContextMenu("Rebuild Editable Hierarchy")]
    public void RebuildEditableHierarchy()
    {
        BuildUi();
    }

    public void StartGame()
    {
        StorySessionState.ResetForNewRun();
        Level0DialogueBootstrap.StartLevel0();
        gameObject.SetActive(false);
    }

    public void OpenLevelSelect()
    {
        SetPanelState(levelSelectPanel, true);
        SetPanelState(settingsPanel, false);
        SetStatus("Route table opened. Level 0 is live; the others are waiting for their passengers.");
    }

    public void OpenSettings()
    {
        SetPanelState(settingsPanel, true);
        SetPanelState(levelSelectPanel, false);
        SetStatus("Meter, text, sound, and controls can slot into this panel later.");
    }

    public void ClosePanels()
    {
        SetPanelState(levelSelectPanel, false);
        SetPanelState(settingsPanel, false);
        SetStatus("The meter is running.");
    }

    public void LaunchLevelById(string levelId)
    {
        switch (levelId)
        {
            case "LEVEL_0":
                StartGame();
                break;
            case "ROUTE_1":
                SceneManager.LoadScene("Level1");
                break;
            default:
                SetStatus("That route is reserved for a later passenger.");
                break;
        }
    }

    public void OpenControlSettings()
    {
        OpenSettings();
    }

    public void QuitGame()
    {
        SetStatus("End of shift.");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void BuildUi()
    {
        EnsureEventSystem();
        ClearExistingUiChildren();

        Canvas canvas = new GameObject("Main Menu Canvas").AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.transform.SetParent(transform, false);
        authoredCanvas = canvas;

        CanvasScaler scaler = canvas.gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        canvas.gameObject.AddComponent<GraphicRaycaster>();

        RectTransform canvasRect = canvas.GetComponent<RectTransform>();

        CreateBackground(canvasRect);
        CreateWindshieldView(canvasRect);
        CreateTaxiInterior(canvasRect);
        CreateDashboardUi(canvasRect);
        CreateOverlayPanels(canvasRect);
        ClosePanels();
        ResolveReferencesFromHierarchy();
        AutoConfigureButtonsByLabel();
        RebindButtonsInHierarchy();
    }

    private void ClearExistingUiChildren()
    {
        levelSelectPanel = null;
        settingsPanel = null;
        statusText = null;
        routeHintText = null;
        authoredCanvas = null;

        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            GameObject child = transform.GetChild(i).gameObject;
            if (Application.isPlaying)
            {
                Destroy(child);
            }
            else
            {
                DestroyImmediate(child);
            }
        }
    }

    private void ResolveReferencesFromHierarchy()
    {
        if (authoredCanvas == null)
        {
            authoredCanvas = GetComponentInChildren<Canvas>(true);
        }

        if (levelSelectPanel == null)
        {
            Transform found = transform.Find("Main Menu Canvas/Overlay Root/Level Select");
            if (found != null)
            {
                levelSelectPanel = found as RectTransform;
            }
        }

        if (settingsPanel == null)
        {
            Transform found = transform.Find("Main Menu Canvas/Overlay Root/Settings");
            if (found != null)
            {
                settingsPanel = found as RectTransform;
            }
        }

        if (statusText == null)
        {
            Transform found = transform.Find("Main Menu Canvas/Dashboard/Status Strip/Status Text");
            if (found != null)
            {
                statusText = found.GetComponent<Text>();
            }
        }

        if (routeHintText == null)
        {
            Transform found = transform.Find("Main Menu Canvas/Dashboard/Menu Panel/Route Hint");
            if (found != null)
            {
                routeHintText = found.GetComponent<Text>();
            }
        }
    }

    private void RebindButtonsInHierarchy()
    {
        MainMenuButtonAction[] buttons = GetComponentsInChildren<MainMenuButtonAction>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Rebind();
        }
    }

    private void AutoConfigureButtonsByLabel()
    {
        Button[] buttons = GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            Text label = buttons[i].GetComponentInChildren<Text>(true);
            if (label == null)
            {
                continue;
            }

            MainMenuButtonAction.ActionType? actionType = GetActionForLabel(label.text);
            if (!actionType.HasValue)
            {
                continue;
            }

            MainMenuButtonAction action = buttons[i].GetComponent<MainMenuButtonAction>();
            if (action == null)
            {
                action = buttons[i].gameObject.AddComponent<MainMenuButtonAction>();
            }

            action.SetAction(actionType.Value);
        }
    }

    private static MainMenuButtonAction.ActionType? GetActionForLabel(string label)
    {
        switch (label.Trim())
        {
            case "Start Shift":
            case "Level 0":
            case "开始游戏":
                return MainMenuButtonAction.ActionType.StartGame;
            case "Routes":
            case "选择关卡":
                return MainMenuButtonAction.ActionType.OpenRoutes;
            case "Settings":
            case "设置":
                return MainMenuButtonAction.ActionType.OpenSettings;
            case "End Shift":
            case "退出游戏":
                return MainMenuButtonAction.ActionType.QuitGame;
            case "Level 0  First Pickup":
                return MainMenuButtonAction.ActionType.LaunchLevel0;
            case "Route 1  Reserved":
            case "Level 1":
            case "Level 1  Office Worker":
                return MainMenuButtonAction.ActionType.LaunchRoute1;
            case "Route 2  Reserved":
                return MainMenuButtonAction.ActionType.LaunchRoute2;
            case "Controls Placeholder":
                return MainMenuButtonAction.ActionType.OpenControls;
            case "Close":
                return MainMenuButtonAction.ActionType.ClosePanels;
            default:
                return null;
        }
    }

    private void CreateBackground(RectTransform canvasRect)
    {
        Image bg = CreateImage("Night Background", canvasRect, NightSky);
        Stretch(bg.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        Image cityGlowLeft = CreateImage("City Glow Left", canvasRect, new Color(0.11f, 0.18f, 0.19f, 0.55f));
        Stretch(cityGlowLeft.rectTransform, new Vector2(0f, 0.38f), new Vector2(0.36f, 1f), Vector2.zero, Vector2.zero);

        Image cityGlowRight = CreateImage("City Glow Right", canvasRect, new Color(0.3f, 0.15f, 0.13f, 0.3f));
        Stretch(cityGlowRight.rectTransform, new Vector2(0.66f, 0.26f), Vector2.one, Vector2.zero, Vector2.zero);
    }

    private void CreateWindshieldView(RectTransform canvasRect)
    {
        RectTransform windshield = CreatePanel("Windshield", canvasRect, new Color(0.09f, 0.11f, 0.12f, 0.9f));
        Stretch(windshield, new Vector2(0f, 0.28f), new Vector2(1f, 1f), new Vector2(48f, -24f), new Vector2(-48f, -36f));

        CreateCityStripe(windshield, 0.1f, 0.72f, 420f, new Color(0.88f, 0.66f, 0.29f, 0.15f));
        CreateCityStripe(windshield, 0.28f, 0.68f, 520f, new Color(0.19f, 0.53f, 0.54f, 0.18f));
        CreateCityStripe(windshield, 0.56f, 0.75f, 360f, new Color(0.85f, 0.41f, 0.26f, 0.14f));
        CreateCityStripe(windshield, 0.74f, 0.64f, 460f, new Color(0.77f, 0.73f, 0.46f, 0.12f));

        RectTransform skyline = CreatePanel("Skyline", windshield, new Color(0f, 0f, 0f, 0f));
        Stretch(skyline, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        CreateBuilding(skyline, 0.04f, 0.0f, 0.12f, 0.58f);
        CreateBuilding(skyline, 0.16f, 0.0f, 0.08f, 0.48f);
        CreateBuilding(skyline, 0.28f, 0.0f, 0.13f, 0.68f);
        CreateBuilding(skyline, 0.46f, 0.0f, 0.1f, 0.52f);
        CreateBuilding(skyline, 0.62f, 0.0f, 0.12f, 0.62f);
        CreateBuilding(skyline, 0.8f, 0.0f, 0.1f, 0.56f);

        Image road = CreateImage("Road Glow", windshield, new Color(0.08f, 0.1f, 0.11f, 0.92f));
        Stretch(road.rectTransform, new Vector2(0.33f, 0f), new Vector2(0.67f, 0.42f), new Vector2(-120f, 0f), new Vector2(120f, 0f));

        Image laneLeft = CreateImage("Lane Left", road.rectTransform, new Color(SoftIvory.r, SoftIvory.g, SoftIvory.b, 0.22f));
        Stretch(laneLeft.rectTransform, new Vector2(0.38f, 0f), new Vector2(0.4f, 1f), Vector2.zero, Vector2.zero);

        Image laneRight = CreateImage("Lane Right", road.rectTransform, new Color(SoftIvory.r, SoftIvory.g, SoftIvory.b, 0.22f));
        Stretch(laneRight.rectTransform, new Vector2(0.6f, 0f), new Vector2(0.62f, 1f), Vector2.zero, Vector2.zero);

        CreateRainLine(windshield, 0.12f, 0.84f, 180f);
        CreateRainLine(windshield, 0.18f, 0.96f, 220f);
        CreateRainLine(windshield, 0.34f, 0.9f, 250f);
        CreateRainLine(windshield, 0.49f, 0.83f, 170f);
        CreateRainLine(windshield, 0.67f, 0.95f, 240f);
        CreateRainLine(windshield, 0.78f, 0.88f, 200f);
        CreateRainLine(windshield, 0.92f, 0.92f, 210f);

        RectTransform rearView = CreatePanel("Rearview Mirror", windshield, new Color(0.1f, 0.11f, 0.12f, 0.95f));
        Stretch(rearView, new Vector2(0.37f, 1f), new Vector2(0.63f, 1f), new Vector2(0f, -96f), new Vector2(0f, -24f));

        Image rearViewGlass = CreateImage("Rearview Glass", rearView, new Color(0.16f, 0.19f, 0.2f, 1f));
        Stretch(rearViewGlass.rectTransform, Vector2.zero, Vector2.one, new Vector2(8f, 8f), new Vector2(-8f, -8f));

        Text rearViewText = CreateText("Rearview Text", rearViewGlass.rectTransform, "No passenger yet", 20, TextAnchor.MiddleCenter);
        rearViewText.color = new Color(0.8f, 0.84f, 0.85f, 0.9f);
        Stretch(rearViewText.rectTransform, Vector2.zero, Vector2.one, new Vector2(10f, 4f), new Vector2(-10f, -4f));
    }

    private void CreateTaxiInterior(RectTransform canvasRect)
    {
        Image pillarLeft = CreateImage("Pillar Left", canvasRect, new Color(0.07f, 0.08f, 0.09f, 1f));
        Stretch(pillarLeft.rectTransform, new Vector2(0f, 0.1f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(54f, 0f));

        Image pillarRight = CreateImage("Pillar Right", canvasRect, new Color(0.07f, 0.08f, 0.09f, 1f));
        Stretch(pillarRight.rectTransform, new Vector2(1f, 0.1f), new Vector2(1f, 1f), new Vector2(-54f, 0f), new Vector2(0f, 0f));

        Image steeringWheel = CreateImage("Steering Wheel", canvasRect, new Color(0.09f, 0.1f, 0.11f, 0.98f));
        Stretch(steeringWheel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(110f, 120f), new Vector2(460f, 470f));

        Image steeringInner = CreateImage("Steering Inner", steeringWheel.rectTransform, new Color(0.16f, 0.17f, 0.18f, 1f));
        Stretch(steeringInner.rectTransform, new Vector2(0.22f, 0.22f), new Vector2(0.78f, 0.78f), Vector2.zero, Vector2.zero);

        Image console = CreateImage("Center Console", canvasRect, new Color(0.09f, 0.1f, 0.11f, 1f));
        Stretch(console.rectTransform, new Vector2(0.72f, 0f), new Vector2(0.95f, 0.36f), new Vector2(0f, 0f), new Vector2(0f, 0f));
    }

    private void CreateDashboardUi(RectTransform canvasRect)
    {
        RectTransform dashboard = CreatePanel("Dashboard", canvasRect, DashboardColor);
        Stretch(dashboard, new Vector2(0f, 0f), new Vector2(1f, 0.34f), new Vector2(0f, 0f), Vector2.zero);

        Image dashboardLip = CreateImage("Dashboard Lip", dashboard, DashboardEdge);
        Stretch(dashboardLip.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -14f), Vector2.zero);

        RectTransform titleGroup = CreateGroup("Title Group", dashboard);
        Stretch(titleGroup, new Vector2(0f, 0f), new Vector2(0.43f, 1f), new Vector2(72f, 38f), new Vector2(-20f, -42f));

        Text eyebrow = CreateText("Eyebrow", titleGroup, "Late Shift Prototype", 24, TextAnchor.UpperLeft);
        eyebrow.color = new Color(0.74f, 0.78f, 0.8f, 0.9f);
        Stretch(eyebrow.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -34f), new Vector2(0f, 0f));

        Text title = CreateText("Title", titleGroup, "Last Fare", 74, TextAnchor.UpperLeft);
        title.color = SoftIvory;
        Stretch(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -132f), new Vector2(0f, -30f));

        Text subtitle = CreateText("Subtitle", titleGroup, "One city. One cab. Too many unfinished nights.", 28, TextAnchor.UpperLeft);
        subtitle.color = new Color(0.83f, 0.85f, 0.82f, 0.95f);
        subtitle.horizontalOverflow = HorizontalWrapMode.Wrap;
        subtitle.verticalOverflow = VerticalWrapMode.Overflow;
        Stretch(subtitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -216f), new Vector2(-18f, -132f));

        RectTransform meter = CreatePanel("Taxi Meter", dashboard, new Color(0.18f, 0.11f, 0.09f, 1f));
        Stretch(meter, new Vector2(0.45f, 1f), new Vector2(0.62f, 1f), new Vector2(0f, -124f), new Vector2(0f, -30f));

        Image meterScreen = CreateImage("Meter Screen", meter, new Color(0.86f, 0.49f, 0.23f, 0.92f));
        Stretch(meterScreen.rectTransform, Vector2.zero, Vector2.one, new Vector2(10f, 10f), new Vector2(-10f, -10f));

        Text meterLabel = CreateText("Meter Label", meterScreen.rectTransform, "METER RUNNING", 20, TextAnchor.UpperCenter);
        meterLabel.color = new Color(0.25f, 0.12f, 0.06f, 1f);
        Stretch(meterLabel.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -32f), new Vector2(0f, -6f));

        Text meterValue = CreateText("Meter Value", meterScreen.rectTransform, "23:47", 40, TextAnchor.MiddleCenter);
        meterValue.color = new Color(0.24f, 0.11f, 0.05f, 1f);
        Stretch(meterValue.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 8f), new Vector2(0f, -18f));

        RectTransform menuPanel = CreatePanel("Menu Panel", dashboard, new Color(0.14f, 0.16f, 0.17f, 1f));
        Stretch(menuPanel, new Vector2(0.64f, 0f), new Vector2(1f, 1f), new Vector2(0f, 28f), new Vector2(-72f, -36f));

        Text menuHeader = CreateText("Menu Header", menuPanel, "Dispatch Console", 24, TextAnchor.UpperLeft);
        menuHeader.color = new Color(0.75f, 0.78f, 0.81f, 0.95f);
        Stretch(menuHeader.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(24f, -34f), new Vector2(-24f, 0f));

        CreateMenuButton(menuPanel, "开始游戏", 0, MainMenuButtonAction.ActionType.StartGame);
        CreateMenuButton(menuPanel, "选择关卡", 1, MainMenuButtonAction.ActionType.OpenRoutes);
        CreateMenuButton(menuPanel, "设置", 2, MainMenuButtonAction.ActionType.OpenSettings);
        CreateMenuButton(menuPanel, "退出游戏", 3, MainMenuButtonAction.ActionType.QuitGame);

        routeHintText = CreateText("Route Hint", menuPanel, "A rain-soaked city is waiting outside.", 22, TextAnchor.LowerLeft);
        routeHintText.color = new Color(0.79f, 0.82f, 0.83f, 0.9f);
        Stretch(routeHintText.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(24f, 18f), new Vector2(-24f, 56f));

        RectTransform statusStrip = CreatePanel("Status Strip", dashboard, new Color(0.1f, 0.11f, 0.12f, 1f));
        Stretch(statusStrip, new Vector2(0f, 0f), new Vector2(0.62f, 0f), new Vector2(72f, 18f), new Vector2(-28f, 62f));

        Image statusLight = CreateImage("Status Light", statusStrip, MeterRed);
        Stretch(statusLight.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(18f, -10f), new Vector2(38f, 10f));

        statusText = CreateText("Status Text", statusStrip, "The meter is running.", 22, TextAnchor.MiddleLeft);
        statusText.color = new Color(0.82f, 0.84f, 0.85f, 1f);
        Stretch(statusText.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(56f, 0f), new Vector2(-16f, 0f));
    }

    private void CreateOverlayPanels(RectTransform canvasRect)
    {
        RectTransform overlayRoot = CreateGroup("Overlay Root", canvasRect);
        Stretch(overlayRoot, new Vector2(0.58f, 0.08f), new Vector2(0.98f, 0.74f), Vector2.zero, Vector2.zero);

        levelSelectPanel = CreateOverlayPanel("Level Select", overlayRoot);
        settingsPanel = CreateOverlayPanel("Settings", overlayRoot);

        BuildLevelSelect(levelSelectPanel);
        BuildSettings(settingsPanel);
    }

    private void BuildLevelSelect(RectTransform panel)
    {
        Text title = CreateText("Level Title", panel, "Routes", 34, TextAnchor.UpperLeft);
        title.color = SoftIvory;
        Stretch(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(28f, -54f), new Vector2(-28f, -8f));

        Text desc = CreateText("Level Desc", panel, "Night routes open as passengers arrive. The first office route is now taking fares.", 22, TextAnchor.UpperLeft);
        desc.horizontalOverflow = HorizontalWrapMode.Wrap;
        desc.verticalOverflow = VerticalWrapMode.Overflow;
        desc.color = new Color(0.83f, 0.85f, 0.86f, 1f);
        Stretch(desc.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(28f, -128f), new Vector2(-28f, -62f));

        Button level0Button = CreatePanelButton("Level 0  First Pickup", panel, MainMenuButtonAction.ActionType.LaunchLevel0);
        Stretch(level0Button.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(28f, -242f), new Vector2(-28f, -162f));

        Button futureRouteA = CreatePanelButton("Level 1  Office Worker", panel, MainMenuButtonAction.ActionType.LaunchRoute1);
        Stretch(futureRouteA.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(28f, -334f), new Vector2(-28f, -254f));

        Button futureRouteB = CreatePanelButton("Route 2  Reserved", panel, MainMenuButtonAction.ActionType.LaunchRoute2);
        Stretch(futureRouteB.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(28f, -426f), new Vector2(-28f, -346f));

        Button closeButton = CreatePanelButton("Close", panel, MainMenuButtonAction.ActionType.ClosePanels);
        Stretch(closeButton.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(28f, 22f), new Vector2(-28f, 86f));
    }

    private void BuildSettings(RectTransform panel)
    {
        Text title = CreateText("Settings Title", panel, "Settings", 34, TextAnchor.UpperLeft);
        title.color = SoftIvory;
        Stretch(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(28f, -54f), new Vector2(-28f, -8f));

        Text desc = CreateText(
            "Settings Desc",
            panel,
            "Reserved hooks:\n- Master volume\n- Text speed\n- Input remapping\n- Accessibility options",
            22,
            TextAnchor.UpperLeft);
        desc.horizontalOverflow = HorizontalWrapMode.Wrap;
        desc.verticalOverflow = VerticalWrapMode.Overflow;
        desc.color = new Color(0.83f, 0.85f, 0.86f, 1f);
        Stretch(desc.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(28f, -172f), new Vector2(-28f, -62f));

        Button controlsButton = CreatePanelButton("Controls Placeholder", panel, MainMenuButtonAction.ActionType.OpenControls);
        Stretch(controlsButton.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(28f, -298f), new Vector2(-28f, -218f));

        Button closeButton = CreatePanelButton("Close", panel, MainMenuButtonAction.ActionType.ClosePanels);
        Stretch(closeButton.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(28f, 22f), new Vector2(-28f, 86f));
    }

    private void SetStatus(string text)
    {
        if (statusText != null)
        {
            statusText.text = text;
        }

        if (routeHintText != null)
        {
            routeHintText.text = text;
        }
    }

    private void CreateMenuButton(Transform parent, string label, int index, MainMenuButtonAction.ActionType actionType)
    {
        Button button = CreatePanelButton(label, parent, actionType);
        float top = 0.78f - index * 0.17f;
        Stretch(button.GetComponent<RectTransform>(), new Vector2(0f, top - 0.12f), new Vector2(1f, top), new Vector2(24f, 0f), new Vector2(-24f, 0f));
    }

    private static void SetPanelState(RectTransform panel, bool active)
    {
        if (panel != null)
        {
            panel.gameObject.SetActive(active);
        }
    }

    private static RectTransform CreateOverlayPanel(string name, Transform parent)
    {
        Image panel = CreateImage(name, parent, new Color(0.12f, 0.14f, 0.15f, 0.98f));
        Stretch(panel.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        panel.gameObject.SetActive(false);
        return panel.rectTransform;
    }

    private static RectTransform CreateGroup(string name, Transform parent)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.AddComponent<RectTransform>();
    }

    private static RectTransform CreatePanel(string name, Transform parent, Color color)
    {
        return CreateImage(name, parent, color).rectTransform;
    }

    private static Button CreatePanelButton(string label, Transform parent, MainMenuButtonAction.ActionType actionType)
    {
        Image image = CreateImage(label + " Button", parent, new Color(0.21f, 0.24f, 0.25f, 1f));
        Button button = image.gameObject.AddComponent<Button>();

        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = WarmGold;
        colors.pressedColor = new Color(0.62f, 0.5f, 0.28f, 1f);
        colors.selectedColor = WarmGold;
        button.colors = colors;

        MainMenuButtonAction action = image.gameObject.AddComponent<MainMenuButtonAction>();
        action.SetAction(actionType);

        Text text = CreateText("Label", image.transform, label, 28, TextAnchor.MiddleLeft);
        text.color = SoftIvory;
        Stretch(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(22f, 0f), new Vector2(-22f, 0f));
        return button;
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

    private static void Stretch(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }

    private static void CreateBuilding(RectTransform parent, float xMin, float yMin, float width, float height)
    {
        Image building = CreateImage("Building", parent, new Color(CityBlue.r, CityBlue.g, CityBlue.b, 0.9f));
        Stretch(building.rectTransform, new Vector2(xMin, yMin), new Vector2(xMin + width, height), Vector2.zero, Vector2.zero);
    }

    private static void CreateCityStripe(RectTransform parent, float xMin, float yMin, float width, Color color)
    {
        Image stripe = CreateImage("City Stripe", parent, color);
        Stretch(stripe.rectTransform, new Vector2(xMin, yMin), new Vector2(xMin, yMin), new Vector2(0f, -22f), new Vector2(width, 22f));
    }

    private static void CreateRainLine(RectTransform parent, float x, float y, float height)
    {
        Image line = CreateImage("Rain Line", parent, new Color(0.82f, 0.88f, 0.92f, 0.14f));
        Stretch(line.rectTransform, new Vector2(x, y), new Vector2(x, y), new Vector2(0f, -height), new Vector2(3f, 0f));
        line.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 10f);
    }
}

public sealed class MainMenuButtonAction : MonoBehaviour
{
    public enum ActionType
    {
        StartGame,
        OpenRoutes,
        OpenSettings,
        ClosePanels,
        QuitGame,
        OpenControls,
        LaunchLevel0,
        LaunchRoute1,
        LaunchRoute2
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
        MainMenuController controller = GetComponentInParent<MainMenuController>();
        if (controller == null)
        {
            return;
        }

        switch (actionType)
        {
            case ActionType.StartGame:
                controller.StartGame();
                break;
            case ActionType.OpenRoutes:
                controller.OpenLevelSelect();
                break;
            case ActionType.OpenSettings:
                controller.OpenSettings();
                break;
            case ActionType.ClosePanels:
                controller.ClosePanels();
                break;
            case ActionType.QuitGame:
                controller.QuitGame();
                break;
            case ActionType.OpenControls:
                controller.OpenControlSettings();
                break;
            case ActionType.LaunchLevel0:
                controller.LaunchLevelById("LEVEL_0");
                break;
            case ActionType.LaunchRoute1:
                controller.LaunchLevelById("ROUTE_1");
                break;
            case ActionType.LaunchRoute2:
                controller.LaunchLevelById("ROUTE_2");
                break;
        }
    }
}
