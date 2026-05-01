using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class SceneMusicController : MonoBehaviour
{
    private const string SampleSceneName = "SampleScene";
    private const string Level0SceneName = "Level0";
    private const string Level1SceneName = "Level1";
    private static readonly string[] MusicPlaylist =
    {
        "Audio/SampleSceneBgm",
        "Audio/Level0Bgm",
        "Audio/CityOfLove",
        "Audio/ThisHeavyMetal",
        "Audio/WaltzAFlatMajorOp69No1",
        "Audio/Hajimi"
    };

    private static SceneMusicController instance;

    private AudioSource musicSource;
    private int currentTrackIndex;
    private bool manualPlaylistMode;

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
        if (manualPlaylistMode)
        {
            if (musicSource.clip != null && !musicSource.isPlaying)
            {
                musicSource.Play();
            }
            return;
        }

        int sceneTrackIndex = GetSceneDefaultTrackIndex(scene.name);
        if (sceneTrackIndex < 0)
        {
            musicSource.Stop();
            musicSource.clip = null;
            return;
        }

        PlayTrack(sceneTrackIndex);
    }

    public static void PlayNextTrack()
    {
        if (instance == null)
        {
            Debug.LogWarning("[SceneMusicController] PlayNextTrack called, but controller instance is missing.");
            return;
        }

        instance.manualPlaylistMode = true;
        int nextIndex = (instance.currentTrackIndex + 1) % MusicPlaylist.Length;
        Debug.Log($"[SceneMusicController] Manual next track requested. Switching to index {nextIndex}.");
        instance.PlayTrack(nextIndex);
    }

    private void PlayTrack(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= MusicPlaylist.Length)
        {
            return;
        }

        string resourcePath = MusicPlaylist[trackIndex];
        AudioClip clip = Resources.Load<AudioClip>(resourcePath);
        if (clip == null)
        {
            Debug.LogWarning($"Scene music clip not found at Resources/{resourcePath}.");
            musicSource.Stop();
            musicSource.clip = null;
            return;
        }

        if (musicSource.clip != clip)
        {
            currentTrackIndex = trackIndex;
            musicSource.clip = clip;
            musicSource.Play();
            Debug.Log($"[SceneMusicController] Now playing: {clip.name} (index {trackIndex}).");
            return;
        }

        currentTrackIndex = trackIndex;
        if (!musicSource.isPlaying)
        {
            musicSource.Play();
            Debug.Log($"[SceneMusicController] Resumed: {clip.name} (index {trackIndex}).");
        }
    }

    private static int GetSceneDefaultTrackIndex(string sceneName)
    {
        switch (sceneName)
        {
            case SampleSceneName:
                return 0;
            case Level0SceneName:
            case Level1SceneName:
                return 1;
            default:
                return -1;
        }
    }
}
