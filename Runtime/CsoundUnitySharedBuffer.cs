// Static class to hold the shared buffer
// This is used to share microphone audio with Csound instances
// The microphone audio is read from the CsoundUnityMicrophone component
// and written to the shared buffer.
// Csound instances read the audio from the shared buffer.
public static class CsoundUnitySharedBuffer
{
    private static readonly object s_BufferLock = new object();
    private static float[] s_Buffer = new float[65536]; // Large enough for ring buffer
    private static int s_BufferWrite = 0;
    private static int s_BufferRead = 0;
    private static int s_BufferCount = 0;

    // Write new data to the ring buffer
    public static void WriteBuffer(float[] data, int length)
    {
        lock (s_BufferLock)
        {
            if (s_Buffer.Length < length)
            {
                s_Buffer = new float[length * 2];
                s_BufferWrite = 0;
                s_BufferRead = 0;
                s_BufferCount = 0;
            }
            for (int i = 0; i < length; i++)
            {
                s_Buffer[s_BufferWrite] = data[i];
                s_BufferWrite = (s_BufferWrite + 1) % s_Buffer.Length;
                if (s_BufferCount < s_Buffer.Length)
                    s_BufferCount++;
                else
                    s_BufferRead = (s_BufferRead + 1) % s_Buffer.Length; // Overwrite oldest
            }
        }
    }

    // Read data from the ring buffer, fill with zeros if not enough data
    public static bool ReadBuffer(float[] data, int length)
    {
        lock (s_BufferLock)
        {
            int available = s_BufferCount;
            int toRead = System.Math.Min(length, available);
            for (int i = 0; i < toRead; i++)
            {
                data[i] = s_Buffer[s_BufferRead];
                s_BufferRead = (s_BufferRead + 1) % s_Buffer.Length;
            }
            for (int i = toRead; i < length; i++)
            {
                data[i] = 0f; // Fill with silence if not enough data
            }
            s_BufferCount -= toRead;
            return toRead > 0;
        }
    }
}