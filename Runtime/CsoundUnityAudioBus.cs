public class CsoundUnityAudioBus
{
    private readonly object s_BufferLock = new object();
    private float[] s_Buffer = new float[8192];  // Initial size
    private int s_BufferSize = 0;
    private bool s_HasNewData = false;

    public void WriteBuffer(float[] data, int length)
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

    public bool ReadBuffer(float[] data, int length)
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