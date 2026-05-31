// Static class to hold the shared buffer
// This is used to share microphone audio with Csound instances
// The microphone audio is read from the CsoundUnityMicrophone component
// and written to the shared buffer.
// Csound instances read the audio from the shared buffer.
//
// Lock-free SPSC (single-producer single-consumer) ring buffer.
// CsoundUnityMicrophone.OnAudioFilterRead is the single writer.
// CsoundUnity/MicSampler OnAudioFilterRead is the single reader.
// No locks — uses atomic head/tail with acquire/release semantics
// to avoid priority inversion on the audio thread.

using System.Threading;

public static class CsoundUnitySharedBuffer
{
    // Power-of-2 size enables mask-based wrapping (faster than modulo)
    private const int InitialCapacity = 65536; // Must be power of 2
    private static int s_Capacity = InitialCapacity;
    private static int s_Mask = InitialCapacity - 1;
    private static float[] s_Buffer = new float[InitialCapacity];

    // Volatile indices for lock-free SPSC pattern
    // Written only by their respective side (writer writes head, reader writes tail)
    private static volatile int s_Head; // Write position (writer increments after write)
    private static volatile int s_Tail; // Read position (reader increments after read)

    // Total count for auto-grow detection (only writer updates, reader reads)
    private static volatile int s_Count;

    /// <summary>
    /// Write new data to the ring buffer. Called from the audio thread (single producer).
    /// If the buffer is full, the oldest data is overwritten.
    /// </summary>
    public static void WriteBuffer(float[] data, int length)
    {
        // Check if buffer needs to grow
        if (length > s_Capacity)
        {
            GrowBuffer(length * 2);
        }

        // Check if we'd overflow and need to advance tail (overwrite oldest)
        int available = s_Capacity - Volatile.Read(ref s_Count);
        if (length > available)
        {
            // Advance tail to make room (discard oldest samples)
            int overflow = length - available;
            Interlocked.Add(ref s_Count, -overflow);
            // We don't move tail here — reader will see count < capacity and read normally
            // Instead we just let head wrap and overwrite; reader handles it via count
        }

        int head = s_Head;
        for (int i = 0; i < length; i++)
        {
            s_Buffer[head & s_Mask] = data[i];
            head++;
        }

        // Publish write with release semantics (ensures data is visible before index update)
        Interlocked.Exchange(ref s_Head, head);
        Interlocked.Add(ref s_Count, length);
    }

    /// <summary>
    /// Read data from the ring buffer. Fill with zeros if not enough data.
    /// Called from the audio thread (single consumer).
    /// Returns true if any data was available.
    /// </summary>
    public static bool ReadBuffer(float[] data, int length)
    {
        int count = Volatile.Read(ref s_Count);
        int toRead = length < count ? length : count;

        if (toRead > 0)
        {
            int tail = s_Tail;
            for (int i = 0; i < toRead; i++)
            {
                data[i] = s_Buffer[tail & s_Mask];
                tail++;
            }

            // Publish read with release semantics
            Interlocked.Exchange(ref s_Tail, tail);
            Interlocked.Add(ref s_Count, -toRead);
        }

        // Fill remainder with silence
        for (int i = toRead; i < length; i++)
        {
            data[i] = 0f;
        }

        return toRead > 0;
    }

    private static void GrowBuffer(int newCapacity)
    {
        // Round up to next power of 2
        int cap = 1;
        while (cap < newCapacity) cap <<= 1;

        var newBuffer = new float[cap];
        // Copy any existing data to beginning of new buffer
        int count = Volatile.Read(ref s_Count);
        int tail = s_Tail;
        for (int i = 0; i < count; i++)
        {
            newBuffer[i] = s_Buffer[(tail + i) & s_Mask];
        }

        s_Buffer = newBuffer;
        s_Capacity = cap;
        s_Mask = cap - 1;
        s_Head = count;
        s_Tail = 0;
    }
}