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

    void Awake()
    {
        m_MicrophoneSource = GetComponent<AudioSource>();
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
            if (Microphone.devices.Length > 0)
            {
                // Get the default microphone if no specific device is provided
                if (m_MicrophoneDeviceIndex >= 0 && m_MicrophoneDeviceIndex < Microphone.devices.Length)
                {
                    m_MicrophoneDevice = Microphone.devices[m_MicrophoneDeviceIndex];
                }

                m_MicrophoneSource.clip = Microphone.Start(m_MicrophoneDevice, true, m_RecordLength, m_SampleRate);
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
        if (m_LogProcessing)
        {
            Debug.Log($"{m_SourceName} writing {data.Length} samples");
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
}