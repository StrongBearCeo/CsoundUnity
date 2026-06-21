using System;
using UnityEngine;

[RequireComponent(typeof(AudioSource))]
[DefaultExecutionOrder(-1)]
public class CsoundUnityMicrophone : MonoBehaviour
{
    // Handle microphone audio sharing with Csound instances
    // This method of copying audio avoids the delay caused by using Update() to SetData() on the destination AudioSource.clip
    // The flow is:
    // 1. OnAudioFilterRead() is called with the source audio data
    // 2. The data is added to a shared static buffer
    // 3. OnAudioFilterRead() is called again with the destination audio data
    // 4. The data is read from the shared static buffer and written directly to the data in OnAudioFilterRead() of the destination AudioSource
    // Setting DefaultExecutionOrder to -1000 ensures that this script runs before the destination AudioSource
    [SerializeField]
    [Tooltip("The name of the source. It is used for logging.")]
    private string m_SourceName = "Microphone";

    [SerializeField]
    [Tooltip("The AudioSource component that will be used to play the Microphone audio. If an audio clip is assigned, it will override the Microphone source.")]
    private AudioSource m_MicrophoneSource;

    [Header("Microphone Settings")]

    [SerializeField]
    [Tooltip("The index of the microphone device to use. If negative, the default microphone will be used.")]
    private int m_MicrophoneDeviceIndex = 0;

    [SerializeField]
    [Tooltip("The name of the microphone device to use. It is set automatically according to the device index.")]
    private string m_MicrophoneDevice;
    public string MicrophoneDevice
    {
        get
        {
            if (m_MicrophoneDeviceIndex >= 0 && m_MicrophoneDeviceIndex < Microphone.devices.Length)
            {
                m_MicrophoneDevice = Microphone.devices[m_MicrophoneDeviceIndex];
            }
            return m_MicrophoneDevice;
        }
    }

    [SerializeField]
    [Tooltip("The length of the Microphone recording buffer in seconds.")]
    private int m_RecordLength = 1;

    [SerializeField]
    [Tooltip("The sample rate of the Microphone audio. If not set, the output sample rate of the AudioSource will be used.")]
    private int m_SampleRate;

    [Header("Control Toggles")]

    [SerializeField]
    [Tooltip("Toggle to log processing information.")]
    private bool m_LogProcessing = false;

    [SerializeField]
    [Tooltip("Toggle to start and stop the Microphone source. When enabled, the Microphone will be active and sending audio data. When disabled, the Microphone will be paused and no audio data will be sent.")]
    private bool m_IsPlaying = true;

    [SerializeField]
    [Tooltip("Toggle to mute the Microphone source")]
    private bool m_IsMuted = true;

    /// <summary>
    /// Refcount of callers that want the native low-latency engine to own the mic.
    /// Multiple native users (LiveFxController, MicInstrument, MicMonitorHub) may
    /// request native mode concurrently and release in any order, so a plain bool
    /// is wrong: a caller that released while another still held the engine could
    /// never clear the flag (its SetNativeMode(false) was gated on the engine being
    /// idle), leaving the mic permanently handed to native &rarr; the Unity mic
    /// pipeline silent for the whole session. Refcounting makes the handoff
    /// last-user-wins and order-independent: the OS mic is released on the first
    /// <c>true</c> (0&rarr;1) and reacquired on the last <c>false</c> (1&rarr;0).
    /// </summary>
    private int m_NativeModeRefs = 0;

    /// <summary>Current native-mode refcount. Lets a caller detect the 0&rarr;1
    /// transition (it is about to release Unity's mic to the native engine) and
    /// the 1&rarr;0 transition (it is about to take the mic back). Read BEFORE
    /// calling <see cref="SetNativeMode"/> to know whether this call is the one
    /// that flips the device.</summary>
    public int NativeModeRefCount => m_NativeModeRefs;

    /// <summary>
    /// Switch between native low-latency mode and the standard Unity mic path.
    /// Refcounted: the FIRST caller to activate releases the OS mic to the native
    /// engine; the LAST caller to deactivate reacquires it for Unity. Intermediate
    /// calls are no-ops on the device but always adjust the ref so callers can
    /// pair <c>true</c>/<c>false</c> regardless of engine state or leave order.
    /// </summary>
    /// <param name="active">True to add a native-mode ref (release Unity mic if first),
    /// false to drop a ref (reacquire Unity mic if last).</param>
    public void SetNativeMode(bool active)
    {
        if (active)
        {
            if (m_NativeModeRefs++ == 0)
            {
                // FULLY RELEASE the OS microphone. Pausing the AudioSource is not enough:
                // Microphone.Start keeps the capture device open, so the native Oboe/WASAPI
                // input (low-latency) can't acquire the mic and captures silence.
                // We must Microphone.End() to hand the device to the native engine.
                if (m_MicrophoneSource != null)
                {
                    m_MicrophoneSource.Stop();
                    m_MicrophoneSource.clip = null;
                }
                try
                {
                    var dev = MicrophoneDevice;
                    if (!string.IsNullOrEmpty(dev) && Microphone.IsRecording(dev))
                        Microphone.End(dev);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CsoundUnityMicrophone] Microphone.End failed: {e.Message}");
                }
                Debug.Log("[CsoundUnityMicrophone] Native mode activated — Unity mic released for native capture.");
            }
        }
        else
        {
            if (m_NativeModeRefs > 0 && --m_NativeModeRefs == 0)
            {
                // Re-acquire the OS mic for the Unity pipeline (last native user left).
                InitializeMicrophone();
                Debug.Log("[CsoundUnityMicrophone] Native mode deactivated — Unity mic resumed.");
            }
        }
    }

    void Awake()
    {
        m_MicrophoneSource = GetComponent<AudioSource>();
        m_MicrophoneDeviceIndex = PlayerPrefs.GetInt("MicSelectionIndex", 0);
    }
    void Start()
    {
        if (m_MicrophoneSource.clip == null)
        {
            InitializeMicrophone();
        }
    }
    private void InitializeMicrophone()
    {
        try
        {
            // List all available microphones
            string[] availableMicrophones = Microphone.devices;
            if (availableMicrophones.Length > 0)
            {
                Debug.Log("Available Microphones:");
                for (int i = 0; i < availableMicrophones.Length; i++)
                {
                    Debug.Log($"{i}: {availableMicrophones[i]}");
                }
            }
            else
            {
                Debug.LogWarning("No microphones found.");
            }
            if (Microphone.devices.Length > 0)
            {
                m_MicrophoneSource.clip = Microphone.Start(MicrophoneDevice, true, m_RecordLength, m_SampleRate);
                m_MicrophoneSource.Play();
                m_MicrophoneSource.loop = true;
            }
            else
            {
                Debug.LogError("No microphone device found");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"InitializeMicrophone: {e.Message}\n{e.StackTrace}");
        }
    }


    void OnAudioFilterRead(float[] data, int channels)
    {
        // When native mode is active (any caller holds a ref), the low-latency plugin
        // handles audio I/O. Do NOT write to CsoundUnitySharedBuffer — the
        // LiveFxRecordingTap writes to a separate LiveFxRecordingBuffer instead.
        // Just zero the output so Unity's audio pipeline is cleanly bypassed without
        // producing double audio.
        if (m_NativeModeRefs > 0)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = 0f;
            }
            return;
        }

        if (m_LogProcessing)
        {
            Debug.Log($"{m_SourceName} writing {data.Length} samples {channels} channels");
            // Calculate RMS (Root Mean Square)
            float rms = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                rms += data[i] * data[i];
            }
            rms = Mathf.Sqrt(rms / data.Length);
            Debug.Log($"RMS: {rms:F6}");
        }

        // Write to shared buffer

        CsoundUnitySharedBuffer.WriteBuffer(data, data.Length);
        if (m_IsMuted)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = 0f;
            }
        }
    }
    void OnValidate()
    {
        if (Application.isPlaying)
        {
            if (m_IsPlaying)
            {
                m_MicrophoneSource.UnPause();
            }
            else
            {
                m_MicrophoneSource.Pause();
            }
        }
    }
    public void SetMicrophoneDevice(int index)
    {
        if (m_MicrophoneDeviceIndex == index)
        {
            return;
        }
        if (index < 0 || index >= Microphone.devices.Length)
        {
            Debug.LogWarning($"Invalid microphone index: {index}. Total number of microphones: {Microphone.devices.Length}. Using default microphone.");
            m_MicrophoneDeviceIndex = 0;
        }
        else
        {
            m_MicrophoneDeviceIndex = index;
        }
        m_MicrophoneDevice = Microphone.devices[m_MicrophoneDeviceIndex];
        InitializeMicrophone();
    }
}