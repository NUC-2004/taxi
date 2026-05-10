using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public sealed class DrivingMinigameController : MonoBehaviour
{
    private const string Level0SceneName = "Level0";
    private const string Level1SceneName = "Level1";
    private const string Level2SceneName = "Level2";
    private const string Level3SceneName = "Level3";
    private const string FailureMessage = "(The ride breaks off before the conversation can continue.)";
    private const string PlayerCarResourcePath = "Level2UI/PlayerCarBlueUp";
    private const string ObstacleCarResourcePath = "Level2UI/ObstacleCarGreenUp";
    private const int CrashAffectionPenalty = -1;
    private const float InitialObstacleSpeed = 120.6f;
    private const float ObstacleSpeedIncreasePerStep = 13.4f;
    private const float MaxObstacleSpeed = 214.4f;
    private const float InitialSpawnInterval = 10.4f;
    private const float SpawnIntervalDecreasePerStep = 0.4f;
    private const float MinSpawnInterval = 6.8f;
    private const float DifficultyStepSeconds = 20f;

    private sealed class Obstacle
    {
        public RectTransform Rect;
        public int LaneIndex;
        public float Y;
    }

    private readonly List<Obstacle> obstacles = new List<Obstacle>();
    private DialogueSequenceManager dialogueManager;
    private RectTransform root;
    private RectTransform playerCar;
    private RectTransform obstacleRoot;
    private Sprite playerCarSprite;
    private Sprite obstacleCarSprite;
    private int currentLane = 1;
    private float elapsedSeconds;
    private float nextSpawnSeconds;
    private bool isRunning;

    private IEnumerator Start()
    {
        if (!IsDrivingMinigameScene(SceneManager.GetActiveScene().name))
        {
            Destroy(this);
            yield break;
        }

        dialogueManager = GetComponent<DialogueSequenceManager>();
        if (dialogueManager == null)
        {
            dialogueManager = FindFirstObjectByType<DialogueSequenceManager>();
        }

        if (dialogueManager == null)
        {
            yield break;
        }

        dialogueManager.SequenceCompleted += HandleSequenceEnded;
        dialogueManager.SequenceFailed += HandleSequenceEnded;

        Canvas canvas = null;
        for (int i = 0; i < 4 && canvas == null; i++)
        {
            canvas = GetComponentInChildren<Canvas>(true);
            if (canvas == null)
            {
                yield return null;
            }
        }

        if (canvas == null)
        {
            Debug.LogWarning("[DrivingMinigameController] Could not find a dialogue canvas for driving minigame.");
            yield break;
        }

        LoadCarSprites();
        BuildUi(canvas.GetComponent<RectTransform>());
        isRunning = true;
        nextSpawnSeconds = InitialSpawnInterval;
    }

    private void OnDestroy()
    {
        if (dialogueManager != null)
        {
            dialogueManager.SequenceCompleted -= HandleSequenceEnded;
            dialogueManager.SequenceFailed -= HandleSequenceEnded;
        }
    }

    private void Update()
    {
        if (!isRunning || dialogueManager == null || root == null)
        {
            return;
        }

        HandleLaneInput();
        UpdateDifficultyAndSpawning();
        MoveObstacles();
        CheckCollision();
    }

    private void BuildUi(RectTransform canvasRect)
    {
        root = CreatePanel("Driving Minigame Area", canvasRect, new Color(0.025f, 0.03f, 0.032f, 0.88f));
        Stretch(root, new Vector2(0.80f, 0.18f), new Vector2(0.98f, 0.48f), Vector2.zero, Vector2.zero);

        Image roadEdge = CreateImage("Road Edge", root, new Color(0.72f, 0.63f, 0.39f, 0.55f));
        Stretch(roadEdge.rectTransform, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -4f));

        Image road = CreateImage("Road", root, new Color(0.07f, 0.08f, 0.085f, 0.96f));
        Stretch(road.rectTransform, Vector2.zero, Vector2.one, new Vector2(8f, 8f), new Vector2(-8f, -8f));

        Text controlHint = CreateText("Control Hint", root, "A: move left    D: move right", 18, TextAnchor.UpperCenter);
        Stretch(controlHint.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(8f, -30f), new Vector2(-8f, -6f));

        CreateLaneLine("Left Lane Line", 1f / 3f);
        CreateLaneLine("Right Lane Line", 2f / 3f);

        obstacleRoot = CreatePanel("Obstacles", root, new Color(0f, 0f, 0f, 0f));
        Stretch(obstacleRoot, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        Image playerCarImage = CreateImage("Player Car", root, playerCarSprite == null ? new Color(0.36f, 0.78f, 0.95f, 1f) : Color.white);
        playerCarImage.sprite = playerCarSprite;
        playerCarImage.preserveAspect = true;
        playerCar = playerCarImage.rectTransform;
        playerCar.anchorMin = new Vector2(0.5f, 0.5f);
        playerCar.anchorMax = new Vector2(0.5f, 0.5f);
        playerCar.pivot = new Vector2(0.5f, 0.5f);
        RefreshCarLayout();
    }

    private void CreateLaneLine(string objectName, float normalizedX)
    {
        Image line = CreateImage(objectName, root, new Color(0.88f, 0.86f, 0.72f, 0.5f));
        line.rectTransform.anchorMin = new Vector2(normalizedX, 0f);
        line.rectTransform.anchorMax = new Vector2(normalizedX, 1f);
        line.rectTransform.offsetMin = new Vector2(-2f, 12f);
        line.rectTransform.offsetMax = new Vector2(2f, -12f);
    }

    private void HandleLaneInput()
    {
        if (WasLeftPressed())
        {
            currentLane = Mathf.Max(0, currentLane - 1);
            RefreshCarLayout();
        }
        else if (WasRightPressed())
        {
            currentLane = Mathf.Min(2, currentLane + 1);
            RefreshCarLayout();
        }
    }

    private void UpdateDifficultyAndSpawning()
    {
        elapsedSeconds += Time.deltaTime;
        nextSpawnSeconds -= Time.deltaTime;

        if (nextSpawnSeconds > 0f)
        {
            return;
        }

        SpawnObstacle();
        nextSpawnSeconds = GetCurrentSpawnInterval();
    }

    private void MoveObstacles()
    {
        float speed = GetCurrentObstacleSpeed();
        float panelHeight = root.rect.height;
        for (int i = obstacles.Count - 1; i >= 0; i--)
        {
            Obstacle obstacle = obstacles[i];
            obstacle.Y -= speed * Time.deltaTime;
            obstacle.Rect.anchoredPosition = new Vector2(GetLaneX(obstacle.LaneIndex), obstacle.Y);

            if (obstacle.Y < -panelHeight * 0.62f)
            {
                Destroy(obstacle.Rect.gameObject);
                obstacles.RemoveAt(i);
            }
        }
    }

    private void CheckCollision()
    {
        float carHeight = GetCarSize().y;
        float playerY = GetPlayerY();
        for (int i = 0; i < obstacles.Count; i++)
        {
            Obstacle obstacle = obstacles[i];
            if (obstacle.LaneIndex == currentLane && Mathf.Abs(obstacle.Y - playerY) < carHeight * 0.85f)
            {
                Destroy(obstacle.Rect.gameObject);
                obstacles.RemoveAt(i);
                dialogueManager.ChangeAffection(CrashAffectionPenalty, FailureMessage);
                return;
            }
        }
    }

    private void SpawnObstacle()
    {
        if (root.rect.width <= 0f || root.rect.height <= 0f)
        {
            return;
        }

        int lane = Random.Range(0, 3);
        Image obstacleImage = CreateImage("Obstacle Car", obstacleRoot, obstacleCarSprite == null ? new Color(0.46f, 0.62f, 0.34f, 1f) : Color.white);
        obstacleImage.sprite = obstacleCarSprite;
        obstacleImage.preserveAspect = true;
        RectTransform obstacleRect = obstacleImage.rectTransform;
        obstacleRect.anchorMin = new Vector2(0.5f, 0.5f);
        obstacleRect.anchorMax = new Vector2(0.5f, 0.5f);
        obstacleRect.pivot = new Vector2(0.5f, 0.5f);
        obstacleRect.sizeDelta = GetCarSize();

        Obstacle obstacle = new Obstacle
        {
            Rect = obstacleRect,
            LaneIndex = lane,
            Y = root.rect.height * 0.56f
        };
        obstacle.Rect.anchoredPosition = new Vector2(GetLaneX(lane), obstacle.Y);
        obstacles.Add(obstacle);
    }

    private void RefreshCarLayout()
    {
        if (playerCar == null || root == null)
        {
            return;
        }

        playerCar.sizeDelta = GetCarSize();
        playerCar.anchoredPosition = new Vector2(GetLaneX(currentLane), GetPlayerY());
    }

    private Vector2 GetCarSize()
    {
        float laneWidth = Mathf.Max(1f, root.rect.width / 3f);
        float height = Mathf.Clamp(root.rect.height * 0.2f, 48f, 76f);
        float aspect = playerCarSprite != null && playerCarSprite.rect.height > 0f
            ? playerCarSprite.rect.width / playerCarSprite.rect.height
            : 0.48f;
        float width = Mathf.Min(laneWidth * 0.58f, height * aspect);
        return new Vector2(width, height);
    }

    private void LoadCarSprites()
    {
        playerCarSprite = LoadSpriteFromResourcePath(PlayerCarResourcePath);
        obstacleCarSprite = LoadSpriteFromResourcePath(ObstacleCarResourcePath);
    }

    private static Sprite LoadSpriteFromResourcePath(string resourcePath)
    {
        Sprite directSprite = Resources.Load<Sprite>(resourcePath);
        if (directSprite != null)
        {
            return directSprite;
        }

        Texture2D texture = Resources.Load<Texture2D>(resourcePath);
        if (texture == null)
        {
            return null;
        }

        texture.filterMode = FilterMode.Point;
        return Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            100f);
    }

    private float GetLaneX(int laneIndex)
    {
        float laneWidth = root.rect.width / 3f;
        return (laneIndex - 1) * laneWidth;
    }

    private float GetPlayerY()
    {
        return -root.rect.height * 0.32f;
    }

    private float GetCurrentObstacleSpeed()
    {
        float steps = Mathf.Floor(elapsedSeconds / DifficultyStepSeconds);
        return Mathf.Min(MaxObstacleSpeed, InitialObstacleSpeed + steps * ObstacleSpeedIncreasePerStep);
    }

    private float GetCurrentSpawnInterval()
    {
        float steps = Mathf.Floor(elapsedSeconds / DifficultyStepSeconds);
        return Mathf.Max(MinSpawnInterval, InitialSpawnInterval - steps * SpawnIntervalDecreasePerStep);
    }

    private void HandleSequenceEnded()
    {
        isRunning = false;
        if (root != null)
        {
            root.gameObject.SetActive(false);
        }
    }

    private static bool WasLeftPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.aKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.A);
#endif
    }

    private static bool WasRightPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.dKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.D);
#endif
    }

    private static bool IsDrivingMinigameScene(string sceneName)
    {
        return string.Equals(sceneName, Level0SceneName, System.StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sceneName, Level1SceneName, System.StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sceneName, Level2SceneName, System.StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sceneName, Level3SceneName, System.StringComparison.OrdinalIgnoreCase);
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
        label.color = new Color(0.9f, 0.88f, 0.72f, 0.95f);
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Truncate;
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
