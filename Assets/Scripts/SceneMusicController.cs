using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class SceneMusicController : MonoBehaviour
{
    private const string SampleSceneName = "SampleScene";
    private const string Level0SceneName = "Level0";
    private const string Level1SceneName = "Level1";
    private const string SampleSceneMusicPath = "Audio/SampleSceneBgm";
    private const string Level0MusicPath = "Audio/Level0Bgm";

    private static SceneMusicController instance;

    private AudioSource musicSource;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (instance != null)
        {
            return;
        }

        GameObject root = new GameObject("Scene Music Controller");
        instance = root.AddComponent<SceneMusicController>();
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);

        musicSource = gameObject.AddComponent<AudioSource>();
        musicSource.loop = true;
        musicSource.playOnAwake = false;
        musicSource.volume = 0.5f;

        SceneManager.sceneLoaded += OnSceneLoaded;
        ApplyMusicForScene(SceneManager.GetActiveScene());
    }

    private void OnDestroy()
    {
        if (instance == this)
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            instance = null;
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode loadSceneMode)
    {
        ApplyMusicForScene(scene);
    }

    private void ApplyMusicForScene(Scene scene)
    {
        string resourcePath = GetMusicPath(scene.name);
        if (string.IsNullOrEmpty(resourcePath))
        {
            musicSource.Stop();
            musicSource.clip = null;
            return;
        }

        AudioClip clip = Resources.Load<AudioClip>(resourcePath);
        if (clip == null)
        {
            Debug.LogWarning($"Scene music clip not found at Resources/{resourcePath} for scene {scene.name}.");
            musicSource.Stop();
            musicSource.clip = null;
            return;
        }

        if (musicSource.clip != clip)
        {
            musicSource.clip = clip;
        }

        if (!musicSource.isPlaying)
        {
            musicSource.Play();
        }
    }

    private static string GetMusicPath(string sceneName)
    {
        switch (sceneName)
        {
            case SampleSceneName:
                return SampleSceneMusicPath;
            case Level0SceneName:
                return Level0MusicPath;
            case Level1SceneName:
                return Level0MusicPath;
            default:
                return null;
        }
    }
}
