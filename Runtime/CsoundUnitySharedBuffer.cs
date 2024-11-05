// Static class to hold the shared buffer
// This is used to share microphone audio with Csound instances
// The microphone audio is read from the CsoundUnityMicrophone component
// and written to the shared buffer.
// Csound instances read the audio from the shared buffer.
public static class CsoundUnitySharedBuffer
{
    private static readonly object s_BufferLock = new object();
    private static float[] s_Buffer = new float[8192];  // Initial size
    private static int s_BufferSize = 0;
    private static bool s_HasNewData = false;

    public static void WriteBuffer(float[] data, int length)
    {
        lock (s_BufferLock)
        {
            if (s_Buffer.Length < length)
            {
                s_Buffer = new float[length];
            }
            System.Array.Copy(data, s_Buffer, length);
            s_BufferSize = length;
            s_HasNewData = true;
        }
    }

    public static bool ReadBuffer(float[] data, int length)
    {
        lock (s_BufferLock)
        {
            if (s_HasNewData && s_BufferSize == length)
            {
                System.Array.Copy(s_Buffer, data, length);
                return true;
            }
            return false;
        }
    }
} 