// Audio bus for passing audio data between components.
// Uses a lock-free double-buffer pattern instead of locks.
//
// Writer (OnAudioFilterRead) writes to the back buffer, then atomically swaps.
// Reader (OnAudioFilterRead) reads from the front buffer.
// No locks — avoids priority inversion on the audio thread.

using System.Threading;

public class CsoundUnityAudioBus
{
    private float[] s_FrontBuffer;  // Reader reads from this
    private float[] s_BackBuffer;    // Writer writes to this
    private int s_BufferSize = 0;
    private volatile bool s_HasNewData = false;
    private int s_WriteVersion = 0;  // bumped on every write; lets extra readers Peek without consuming

    public CsoundUnityAudioBus()
    {
        s_FrontBuffer = new float[8192];
        s_BackBuffer = new float[8192];
    }

    /// <summary>
    /// Write data to the bus. Called from the audio thread (single producer).
    /// Writes to the back buffer, then atomically swaps front and back.
    /// </summary>
    public void WriteBuffer(float[] data, int length)
    {
        // Grow back buffer if needed (no allocations on subsequent calls with same size)
        if (s_BackBuffer.Length < length)
        {
            s_BackBuffer = new float[length];
        }

        // Copy data into back buffer
        System.Array.Copy(data, s_BackBuffer, length);

        // Swap: back becomes front, front becomes back
        // Thread-safe because reader only accesses front buffer, and we only
        // swap after the copy is complete. The volatile write of s_HasNewData
        // ensures the swap is visible before the flag.
        var temp = s_FrontBuffer;
        s_FrontBuffer = s_BackBuffer;
        s_BackBuffer = temp;

        s_BufferSize = length;
        Volatile.Write(ref s_HasNewData, true);
        Interlocked.Increment(ref s_WriteVersion);
    }

    /// <summary>
    /// Non-consuming read for ADDITIONAL observers (e.g. the sampler recording this
    /// bus while it also feeds a connected instrument). Unlike <see cref="ReadBuffer"/>
    /// it does NOT clear the new-data flag, so the primary consumer is unaffected.
    /// Each observer keeps its own <paramref name="lastVersion"/> cursor; a block is
    /// delivered at most once per observer.
    /// Returns the number of samples copied (0 when nothing new or size mismatch).
    /// </summary>
    public int PeekBuffer(float[] data, int length, ref int lastVersion)
    {
        int v = Volatile.Read(ref s_WriteVersion);
        if (v == lastVersion) return 0;
        lastVersion = v;
        if (s_BufferSize != length) return 0;
        System.Array.Copy(s_FrontBuffer, data, length);
        return length;
    }

    /// <summary>
    /// Read data from the bus. Called from the audio thread (single consumer).
    /// Only returns data if new data is available and the buffer size matches.
    /// </summary>
    public bool ReadBuffer(float[] data, int length)
    {
        if (Volatile.Read(ref s_HasNewData) && s_BufferSize == length)
        {
            System.Array.Copy(s_FrontBuffer, data, length);
            s_HasNewData = false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clear the buffer, marking it as having no new data.
    /// </summary>
    public void ClearBuffer()
    {
        s_HasNewData = false;
        s_BufferSize = 0;
    }
}