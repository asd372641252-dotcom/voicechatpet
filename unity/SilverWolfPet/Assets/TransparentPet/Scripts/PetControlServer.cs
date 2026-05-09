using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[Serializable]
public sealed class PetControlCommand
{
    public string state;
    public string emotion;
    public string action;
    public string text;
    public string bubble_text;
    public float mouth = -1f;
    public float mouth_open = -1f;
    public bool audio_active;
    public bool clear_bubble;
    public bool focus_pet;
    public bool bring_pet_to_camera;
    public string voice_runtime;
    public string screen_vision;
    public string camera_video;
    public string face_tracking;
    public string voice_route;
    public bool quit_app;
    public int companion_interval_sec;
    public int duration_ms;
    public int priority;

    [NonSerialized] public bool hasMouth;
    [NonSerialized] public bool hasMouthOpen;
    [NonSerialized] public bool hasMouthMode;
    [NonSerialized] public bool hasAudioActive;
    [NonSerialized] public bool hasFocusPet;
    [NonSerialized] public bool hasBringPetToCamera;
    [NonSerialized] public bool hasVoiceRuntime;
    [NonSerialized] public bool hasScreenVision;
    [NonSerialized] public bool hasCameraVideo;
    [NonSerialized] public bool hasFaceTracking;
    [NonSerialized] public bool hasVoiceRoute;
    [NonSerialized] public bool hasQuitApp;
    [NonSerialized] public bool hasCompanionIntervalSec;
    [NonSerialized] public bool hasDurationMs;
    [NonSerialized] public string mouthMode;
}

[DisallowMultipleComponent]
public sealed class PetControlServer : MonoBehaviour
{
    private const int ListenBacklog = 32;
    private const int PortBindAttempts = 40;

    public PetStateController stateController;
    public TransparentPetPlacementController placementController;
    public TransparentPetVoiceRuntimeLauncher voiceLauncher;
    public string host = "127.0.0.1";
    public int port = 17861;
    public bool startOnPlay = true;
    public bool logCommands;
    public bool enableVoiceRuntimeCommands = true;

    private readonly ConcurrentQueue<string> _pendingJson = new ConcurrentQueue<string>();
    private TcpListener _listener;
    private Thread _listenerThread;
    private volatile bool _running;
    private volatile bool _starting;
    private volatile bool _listenerActive;
    private float _nextServerRetryAt;
    private int _serverGeneration;

    private void OnEnable()
    {
        if (Application.isPlaying && startOnPlay)
        {
            EnsureServerStarted();
        }
    }

    private void Reset()
    {
        stateController = GetComponent<PetStateController>();
    }

    private void Awake()
    {
        if (stateController == null)
        {
            stateController = GetComponent<PetStateController>();
        }

        if (placementController == null)
        {
            placementController = FindAnyObjectByType<TransparentPetPlacementController>();
        }

        if (voiceLauncher == null)
        {
            voiceLauncher = FindAnyObjectByType<TransparentPetVoiceRuntimeLauncher>();
        }
    }

    private void Start()
    {
        if (Application.isPlaying && startOnPlay)
        {
            EnsureServerStarted();
        }
    }

    private void Update()
    {
        if (Application.isPlaying && startOnPlay && Time.unscaledTime >= _nextServerRetryAt)
        {
            if (_starting && IsListenerThreadAlive())
            {
                _nextServerRetryAt = Time.unscaledTime + 1f;
            }
            else if (!IsServerHealthy())
            {
                StopServer();
                EnsureServerStarted();
            }
        }

        while (_pendingJson.TryDequeue(out string json))
        {
            ApplyJsonOnMainThread(json);
        }
    }

    private void OnDestroy()
    {
        StopServer();
    }

    public void StartServer()
    {
        if ((_running || _starting) && IsListenerThreadAlive())
        {
            return;
        }

        StopServer();
        if (!TryCreateListener(IPAddress.Parse(host), port, out TcpListener listener, out int boundPort, out string error))
        {
            Debug.LogWarning("PetControlServer failed to listen: " + error);
            _nextServerRetryAt = Time.unscaledTime + 1.5f;
            return;
        }

        if (boundPort != port)
        {
            Debug.LogWarning("PetControlServer port " + port.ToString()
                + " is busy; switched control port to " + boundPort.ToString() + ".");
            port = boundPort;
        }

        int generation = Interlocked.Increment(ref _serverGeneration);
        _listener = listener;
        _starting = false;
        _running = true;
        _listenerActive = true;
        Debug.Log("PetControlServer listening on " + host + ":" + port);
        _listenerThread = new Thread(() => ListenLoop(generation, listener))
        {
            IsBackground = true,
            Name = "PetControlServer"
        };
        _listenerThread.Start();
    }

    public void StopServer()
    {
        _running = false;
        _starting = false;
        _listenerActive = false;
        Interlocked.Increment(ref _serverGeneration);
        TcpListener listener = _listener;
        Thread listenerThread = _listenerThread;
        _listener = null;
        _listenerThread = null;

        try
        {
            listener?.Stop();
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        if (listenerThread != null && listenerThread.IsAlive)
        {
            try
            {
                listenerThread.Join(500);
            }
            catch
            {
            }
        }
    }

    private void EnsureServerStarted()
    {
        if (IsServerHealthy() || (_starting && IsListenerThreadAlive()))
        {
            return;
        }

        _nextServerRetryAt = Time.unscaledTime + 1f;
        StartServer();
    }

    private bool IsServerHealthy()
    {
        return _running &&
            _listenerActive &&
            !_starting &&
            _listener != null &&
            IsListenerThreadAlive();
    }

    private bool IsListenerThreadAlive()
    {
        return _listenerThread != null && _listenerThread.IsAlive;
    }

    private bool IsCurrentServerGeneration(int generation)
    {
        return generation == Interlocked.CompareExchange(ref _serverGeneration, 0, 0);
    }

    private void ListenLoop(int generation, TcpListener listener)
    {
        try
        {
            while (_running && IsCurrentServerGeneration(generation))
            {
                TcpClient client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
        }
        catch (SocketException ex)
        {
            if (_running)
            {
                Debug.LogWarning("PetControlServer socket stopped unexpectedly: " + ex.Message);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (ThreadAbortException)
        {
        }
        catch (Exception ex)
        {
            Debug.LogWarning("PetControlServer failed: " + ex.Message);
        }
        finally
        {
            if (IsCurrentServerGeneration(generation))
            {
                _listenerActive = false;
                _starting = false;
                _running = false;
                _listener = null;
            }
        }
    }

    private static bool TryCreateListener(IPAddress address, int requestedPort, out TcpListener listener, out int boundPort, out string error)
    {
        listener = null;
        boundPort = 0;
        error = "";
        int firstPort = Mathf.Clamp(requestedPort, 1024, 65535);
        List<int> candidatePorts = new List<int> { firstPort };
        for (int fallbackPort = 17880; fallbackPort <= 17920 && candidatePorts.Count < PortBindAttempts; fallbackPort++)
        {
            if (!candidatePorts.Contains(fallbackPort))
            {
                candidatePorts.Add(fallbackPort);
            }
        }

        for (int offset = 1; candidatePorts.Count < PortBindAttempts; offset++)
        {
            int candidatePort = firstPort + offset;
            if (candidatePort > 65535)
            {
                break;
            }

            if (candidatePort == 17862 || candidatePort == 17863 || candidatePort == 17865)
            {
                continue;
            }

            if (!candidatePorts.Contains(candidatePort))
            {
                candidatePorts.Add(candidatePort);
            }
        }

        foreach (int candidatePort in candidatePorts)
        {
            TcpListener candidate = null;
            try
            {
                candidate = new TcpListener(address, candidatePort);
                try
                {
                    candidate.Server.ExclusiveAddressUse = true;
                    candidate.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                }
                catch
                {
                    // Keep the normal listener path if this runtime rejects socket tuning.
                }

                candidate.Start(ListenBacklog);
                listener = candidate;
                boundPort = candidatePort;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                try
                {
                    candidate?.Stop();
                }
                catch
                {
                }

                // Windows can report a stale Unity listener as AccessDenied instead of AddressAlreadyInUse.
                // Keep walking fallback ports so the voice bridge can still reach a fresh control socket.
            }
        }

        return false;
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 2000;
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false))
                {
                    string payload = reader.ReadToEnd();
                    string[] messages = SplitJsonMessages(payload);
                    for (int i = 0; i < messages.Length; i++)
                    {
                        _pendingJson.Enqueue(messages[i]);
                    }

                    byte[] response = Encoding.UTF8.GetBytes("{\"ok\":true}\n");
                    stream.Write(response, 0, response.Length);
                }
            }
            catch (IOException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogWarning("PetControlServer client error: " + ex.Message);
            }
        }
    }

    private void ApplyJsonOnMainThread(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            PetControlCommand command = ParseCommand(json);
            if (command == null)
            {
                return;
            }

            if (logCommands)
            {
                Debug.Log("PetControlServer command: " + json);
            }

            if (command.hasFocusPet && command.focus_pet)
            {
                FocusPet();
            }

            if (command.hasBringPetToCamera && command.bring_pet_to_camera)
            {
                BringPetToCamera();
            }

            ApplyVoiceRuntimeCommand(command);

            if (command.hasQuitApp && command.quit_app)
            {
                QuitApplication();
                return;
            }

            if (stateController != null)
            {
                stateController.ApplyCommand(command);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("PetControlServer could not parse JSON: " + ex.Message + "\n" + json);
        }
    }

    private static PetControlCommand ParseCommand(string json)
    {
        Dictionary<string, object> root = TransparentPetJson.AsObject(TransparentPetJson.Parse(json));
        if (root == null)
        {
            return null;
        }

        PetControlCommand command = new PetControlCommand
        {
            state = TransparentPetJson.GetString(root, "state", ""),
            emotion = TransparentPetJson.GetString(root, "emotion", ""),
            action = TransparentPetJson.GetString(root, "action", ""),
            text = TransparentPetJson.GetString(root, "text", ""),
            bubble_text = TransparentPetJson.GetString(root, "bubble_text", ""),
            audio_active = TransparentPetJson.GetBool(root, "audio_active", false),
            clear_bubble = TransparentPetJson.GetBool(root, "clear_bubble", false),
            focus_pet = TransparentPetJson.GetBool(root, "focus_pet", false),
            bring_pet_to_camera = TransparentPetJson.GetBool(root, "bring_pet_to_camera", false),
            voice_runtime = TransparentPetJson.GetString(root, "voice_runtime", ""),
            screen_vision = TransparentPetJson.GetString(root, "screen_vision", ""),
            camera_video = TransparentPetJson.GetString(root, "camera_video", ""),
            face_tracking = TransparentPetJson.GetString(root, "face_tracking", ""),
            voice_route = TransparentPetJson.GetString(root, "voice_route", ""),
            quit_app = TransparentPetJson.GetBool(root, "quit_app", false),
            companion_interval_sec = TransparentPetJson.GetInt(root, "companion_interval_sec", 0),
            duration_ms = TransparentPetJson.GetInt(root, "duration_ms", 0),
            priority = TransparentPetJson.GetInt(root, "priority", 0),
            mouth = -1f,
            mouth_open = -1f,
            mouthMode = ""
        };

        if (root.TryGetValue("mouth", out object mouthValue) && mouthValue != null)
        {
            command.hasMouth = true;
            if (mouthValue is string mouthText)
            {
                command.mouthMode = mouthText.Trim().ToLowerInvariant();
                command.hasMouthMode = !string.IsNullOrEmpty(command.mouthMode);
            }
            else
            {
                command.mouth = Mathf.Clamp01(TransparentPetJson.ToFloat(mouthValue, -1f));
            }
        }

        if (root.TryGetValue("mouth_open", out object mouthOpenValue))
        {
            command.hasMouthOpen = true;
            command.mouth_open = Mathf.Clamp01(TransparentPetJson.ToFloat(mouthOpenValue, 0f));
        }

        command.hasAudioActive = root.ContainsKey("audio_active");
        command.hasFocusPet = root.ContainsKey("focus_pet");
        command.hasBringPetToCamera = root.ContainsKey("bring_pet_to_camera");
        command.hasVoiceRuntime = root.ContainsKey("voice_runtime");
        command.hasScreenVision = root.ContainsKey("screen_vision");
        command.hasCameraVideo = root.ContainsKey("camera_video");
        command.hasFaceTracking = root.ContainsKey("face_tracking");
        command.hasVoiceRoute = root.ContainsKey("voice_route");
        command.hasQuitApp = root.ContainsKey("quit_app");
        command.hasCompanionIntervalSec = root.ContainsKey("companion_interval_sec");
        command.hasDurationMs = root.ContainsKey("duration_ms");
        return command;
    }

    private void FocusPet()
    {
        if (placementController == null)
        {
            placementController = FindAnyObjectByType<TransparentPetPlacementController>();
        }

        if (placementController != null)
        {
            placementController.FocusPet();
        }
    }

    private void BringPetToCamera()
    {
        if (placementController == null)
        {
            placementController = FindAnyObjectByType<TransparentPetPlacementController>();
        }

        if (placementController != null)
        {
            placementController.BringPetToCameraView();
        }
    }

    private void ApplyVoiceRuntimeCommand(PetControlCommand command)
    {
        bool hasVoiceCommand = command.hasVoiceRuntime ||
            command.hasScreenVision ||
            command.hasCameraVideo ||
            command.hasVoiceRoute ||
            command.hasCompanionIntervalSec;
        if (command.hasFaceTracking)
        {
            ApplyFaceTrackingCommand(command.face_tracking);
        }

        if (!enableVoiceRuntimeCommands || !hasVoiceCommand)
        {
            return;
        }

        if (voiceLauncher == null)
        {
            voiceLauncher = FindAnyObjectByType<TransparentPetVoiceRuntimeLauncher>();
        }

        if (voiceLauncher == null)
        {
            Debug.LogWarning("PetControlServer voice command ignored: TransparentPetVoiceRuntimeLauncher missing.");
            return;
        }

        if (command.hasVoiceRoute && !string.IsNullOrWhiteSpace(command.voice_route))
        {
            voiceLauncher.SelectRoute(command.voice_route);
        }

        if (command.hasCompanionIntervalSec && command.companion_interval_sec > 0)
        {
            voiceLauncher.SetCompanionPollingInterval(command.companion_interval_sec);
        }

        ApplyVoiceToggle(command.voice_runtime, () => voiceLauncher.StartVoiceRuntime(), () => voiceLauncher.StopVoiceRuntime());
        ApplyVoiceToggle(command.screen_vision, () => voiceLauncher.StartScreenVisionRuntime(command.voice_route), () => voiceLauncher.StopScreenVisionRuntime());
        ApplyVoiceToggle(command.camera_video, () => voiceLauncher.StartCameraVideoRuntime(), () => voiceLauncher.StopCameraVideoRuntime());
    }

    private void ApplyFaceTrackingCommand(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        TransparentPetSceneFaceTracker tracker = FindAnyObjectByType<TransparentPetSceneFaceTracker>();
        if (tracker == null)
        {
            Debug.LogWarning("PetControlServer face tracking command ignored: TransparentPetSceneFaceTracker missing.");
            return;
        }

        string normalized = value.Trim().ToLowerInvariant();
        if (normalized == "status" || normalized == "debug")
        {
            tracker.LogRuntimeStatus("control " + normalized);
            return;
        }

        if (normalized == "stop" || normalized == "off" || normalized == "false" || normalized == "0")
        {
            tracker.StopCamera();
            tracker.LogRuntimeStatus("control stop");
            return;
        }

        if (normalized == "bridge" || normalized == "shared")
        {
            tracker.SetTrackingEnabled(true);
            tracker.StartStandaloneLocalMediaPipe();
            tracker.LogRuntimeStatus("control bridge ignored, standalone tracking kept");
            return;
        }

        if (normalized == "restart" || normalized == "reset" || normalized == "hardreset")
        {
            tracker.StopCamera();
            tracker.SetTrackingEnabled(true);
            tracker.StartStandaloneLocalMediaPipe();
            tracker.LogRuntimeStatus("control " + normalized);
            return;
        }

        if (normalized == "start" || normalized == "on" || normalized == "standalone" || normalized == "exclusive" || normalized == "true" || normalized == "1")
        {
            if (voiceLauncher == null)
            {
                voiceLauncher = FindAnyObjectByType<TransparentPetVoiceRuntimeLauncher>();
            }

            if (voiceLauncher != null && voiceLauncher.CameraVideoActive)
            {
                voiceLauncher.StopCameraVideoRuntime(false);
            }

            tracker.StartStandaloneLocalMediaPipe();
            tracker.LogRuntimeStatus("control standalone");
        }
    }

    private static void ApplyVoiceToggle(string value, Action start, Action stop)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string normalized = value.Trim().ToLowerInvariant();
        if (normalized == "start" || normalized == "on" || normalized == "true" || normalized == "1")
        {
            start?.Invoke();
        }
        else if (normalized == "stop" || normalized == "off" || normalized == "false" || normalized == "0")
        {
            stop?.Invoke();
        }
    }

    private static string[] SplitJsonMessages(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new string[0];
        }

        string normalized = payload.Replace("\r\n", "\n");
        string[] lines = normalized.Split('\n');
        if (lines.Length <= 1)
        {
            return new[] { payload.Trim() };
        }

        return Array.FindAll(lines, line => !string.IsNullOrWhiteSpace(line));
    }

    private static void QuitApplication()
    {
#if UNITY_EDITOR
        EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
