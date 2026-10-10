/*
Copyright (C) 2015 Rory Walsh. 

This file is part of CsoundUnity: https://github.com/rorywalsh/CsoundUnity

This interface would not have been possible without Richard Henninger's .NET interface to the Csound API.

Contributors:

Bernt Isak Wærstad
Charles Berman
Giovanni Bedetti
Hector Centeno
NPatch

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), 
to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF 
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR 
ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH 
THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using UnityEngine;
using System;
using System.IO;
using System.Runtime.InteropServices;
using csoundcsharp;
using System.Collections.Generic;
using System.Text;
#if UNITY_EDITOR || UNITY_STANDALONE
using MYFLT = System.Double;
#elif UNITY_ANDROID || UNITY_IOS
using MYFLT = System.Single;
#endif

/*
 * CsoundUnityBridge class
 */
public class CsoundUnityBridge
{
    public IntPtr csound;
    bool compiledOk = false;
    Action onCsoundCreated;

    // Csound's process-wide state (csoundInitialize, the instance list, the global environment and the
    // opcode directory) is shared by every instance. Bridges may be built on a worker thread, so every
    // build and destroy is serialized, and globals are written only when their value changes: after the
    // first instrument, no compile can race a write to the global environment table.
    private static readonly object s_buildLock = new object();
    private static readonly Dictionary<string, string> s_globalEnvironment = new Dictionary<string, string>();
    private static string s_opcodeDirectory;

    private static void SetGlobalEnvironment(CsoundBridgeRequest request)
    {
        if (request.OpcodeDirectory != null && request.OpcodeDirectory != s_opcodeDirectory)
        {
            Csound6.NativeMethods.csoundSetOpcodedir(request.OpcodeDirectory);
            s_opcodeDirectory = request.OpcodeDirectory;
        }

        foreach (var env in request.GlobalEnvironment)
        {
            if (s_globalEnvironment.TryGetValue(env.Key, out var current) && current == env.Value) continue;
            Debug.Log($"Setting {env.Key} for {request.Platform} to: {env.Value}");
            Csound6.NativeMethods.csoundSetGlobalEnv(env.Key, env.Value);
            s_globalEnvironment[env.Key] = env.Value;
        }
    }

    internal static string GetBundledWindowsOpcodeDirectory(string streamingAssetsPath)
    {
        if (string.IsNullOrWhiteSpace(streamingAssetsPath)) return null;
        try
        {
            return Path.GetFullPath(Path.Combine(streamingAssetsPath, "CsoundOpcodes"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds per-Csound-instance search paths for a CSD loaded from disk. This keeps
    /// relative package references such as ./sounds/kick.wav rooted beside the CSD
    /// without changing the process-wide working directory.
    /// </summary>
    internal static string[] BuildRuntimeSearchPathOptions(string csdFile)
    {
        if (string.IsNullOrWhiteSpace(csdFile)) return Array.Empty<string>();

        string directory;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(csdFile));
        }
        catch
        {
            return Array.Empty<string>();
        }

        if (string.IsNullOrWhiteSpace(directory)) return Array.Empty<string>();
        // csoundSetOption receives one complete option, not a shell command line.
        // Quoting a path with spaces therefore makes the quote characters part of
        // INCDIR/SFDIR and prevents Csound from finding packaged includes or samples.
        string value = directory;
        return new[]
        {
            "--default-paths",
            $"--env:SSDIR+={value}",
            $"--env:SFDIR+={value}",
            $"--env:SADIR+={value}",
            $"--env:INCDIR+={value}",
        };
    }

    /// <summary>
    /// Csound treats paths beginning with a dot as explicit paths and therefore does
    /// not consult SSDIR/SFDIR for them. Convert only those quoted path literals to
    /// absolute paths rooted at the runtime CSD; ordinary strings remain untouched.
    /// </summary>
    internal static string ResolveExplicitRelativePaths(string csdText, string csdFile)
    {
        if (string.IsNullOrEmpty(csdText) || string.IsNullOrWhiteSpace(csdFile)) return csdText;
        string directory = Path.GetDirectoryName(Path.GetFullPath(csdFile));
        if (string.IsNullOrWhiteSpace(directory)) return csdText;

        var builder = new StringBuilder(csdText.Length + 256);
        int cursor = 0;
        while (cursor < csdText.Length)
        {
            int quoteStart = csdText.IndexOf('"', cursor);
            if (quoteStart < 0)
            {
                builder.Append(csdText, cursor, csdText.Length - cursor);
                break;
            }

            builder.Append(csdText, cursor, quoteStart - cursor + 1);
            int quoteEnd = quoteStart + 1;
            while (quoteEnd < csdText.Length)
            {
                if (csdText[quoteEnd] == '"')
                {
                    int backslashes = 0;
                    for (int i = quoteEnd - 1; i > quoteStart && csdText[i] == '\\'; i--) backslashes++;
                    if ((backslashes & 1) == 0) break;
                }
                quoteEnd++;
            }

            if (quoteEnd >= csdText.Length)
            {
                builder.Append(csdText, quoteStart + 1, csdText.Length - quoteStart - 1);
                break;
            }

            string value = csdText.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
            if (IsExplicitRelativePath(value))
            {
                string platformPath = value.Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                value = Path.GetFullPath(Path.Combine(directory, platformPath)).Replace('\\', '/');
            }
            builder.Append(value).Append('"');
            cursor = quoteEnd + 1;
        }
        return builder.ToString();
    }

    private static bool IsExplicitRelativePath(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        int prefixLength = value.StartsWith("../", StringComparison.Ordinal) ||
            value.StartsWith("..\\", StringComparison.Ordinal) ? 3 :
            value.StartsWith("./", StringComparison.Ordinal) ||
            value.StartsWith(".\\", StringComparison.Ordinal) ? 2 : 0;
        return prefixLength > 0 && value.Length > prefixLength && !char.IsWhiteSpace(value[prefixLength]);
    }

    /// <summary>
    /// The CsoundUnityBridge constructor sets up the Csound Global Environment Variables set by the user.
    /// Then it creates an instance of Csound and compiles the full csdFile passed as a string.
    /// Then it starts Csound.
    /// </summary>
    /// <param name="csdFile">The Csound (.csd) file content as a string</param>
    /// <param name="environmentSettings">A list of the Csound Environments settings defined by the user</param>
    public CsoundUnityBridge(string csdFile, List<EnvironmentSettings> environmentSettings, bool compileFromFile = false)
        : this(CsoundBridgeRequest.Capture(csdFile, environmentSettings, compileFromFile))
    {
    }

    /// <summary>
    /// Creates, compiles and starts a Csound instance from a request captured on the main thread.
    /// Safe on any thread: apart from Debug.Log it touches no Unity API.
    /// </summary>
    public CsoundUnityBridge(CsoundBridgeRequest request)
    {
        lock (s_buildLock)
        {
            Build(request);
        }
    }

    private void Build(CsoundBridgeRequest request)
    {
        string csdFile = request.CsdSource;
        bool compileFromFile = request.CompileFromFile;

        // On editor and desktop platforms the request names an opcode directory, so Csound avoids
        // the machine-wide plugin directory (see CsoundBridgeRequest.Capture).
        SetGlobalEnvironment(request);

        Csound6.NativeMethods.csoundInitialize(1);
        csound = Csound6.NativeMethods.csoundCreate(System.IntPtr.Zero);
        if (csound == IntPtr.Zero)
        {
            Debug.LogError("Couldn't create Csound!");
            return;
        }

        Csound6.NativeMethods.csoundSetHostImplementedAudioIO(csound, 1, 0);
        Csound6.NativeMethods.csoundCreateMessageBuffer(csound, 0);

        Csound6.NativeMethods.csoundSetOption(csound, "-n");
        Csound6.NativeMethods.csoundSetOption(csound, "-d");
        Csound6.NativeMethods.csoundSetOption(csound, $"--sample-rate={request.SampleRate}");
        // Csound6.NativeMethods.csoundSetOption(csound, $"--control-rate={AudioSettings.outputSampleRate}");
        Csound6.NativeMethods.csoundSetOption(csound, $"--ksmps=32");

        if (compileFromFile)
        {
            foreach (string option in BuildRuntimeSearchPathOptions(csdFile))
                Csound6.NativeMethods.csoundSetOption(csound, option);
        }

#if UNITY_IOS
        Debug.Log($"Initialising sample rate and control rate using Audio Project Settings value: {request.SampleRate}Hz, some values maybe incompatible with older hardware.");
#endif

        // This causes a crash in Unity >= 2021.3.28
        //var parms = GetParams();
        //parms.control_rate_override = AudioSettings.outputSampleRate;
        //parms.sample_rate_override = AudioSettings.outputSampleRate;
        //SetParams(parms);

#if !UNITY_IOS // this is needed to avoid references to this method on iOS, where it's not supported
        if (request.PluginDirectory != null)
        {
            Debug.Log("Csound Force Loading Plugins!");
            var loaded = Csound6.NativeMethods.csoundLoadPlugins(csound, request.PluginDirectory);
            Debug.Log($"PLUGINS LOADED? {loaded}");
        }
#endif
        onCsoundCreated?.Invoke();
        onCsoundCreated = null;

        int ret;
        if (compileFromFile)
        {
            try
            {
                string runtimeCsd = ResolveExplicitRelativePaths(File.ReadAllText(csdFile), csdFile);
                ret = Csound6.NativeMethods.csoundCompileCsdText(csound, runtimeCsd);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Could not prepare runtime CSD paths for {csdFile}: {ex.Message}");
                ret = Csound6.NativeMethods.csoundCompileCsd(csound, csdFile);
            }
        }
        else
        {
            ret = Csound6.NativeMethods.csoundCompileCsdText(csound, csdFile);
        }

        SetMessageOutputEnabled(false);

        // double set to make sure -d -n are set
        Csound6.NativeMethods.csoundSetOption(csound, "-n");
        Csound6.NativeMethods.csoundSetOption(csound, "-d");

        Csound6.NativeMethods.csoundStart(csound);

        Debug.Log($"Csound created and started.\n" +
            $"AudioSettings.outputSampleRate: {request.SampleRate}\n" +
            $"GetSr: {GetSr()}\n" +
            $"GetKr: {GetKr()}\n" +
            $"Get0dbfs: {Get0dbfs()}\n" +
            $"GetKsmps: {GetKsmps()}\n" +
            $"GetMessageLevel: {Csound6.NativeMethods.csoundGetMessageLevel(csound)}"
            );

        //var res = PerformKsmps();
        //Debug.Log($"PerformKsmps: {res}");
        compiledOk = ret == 0 ? true : false;
        //Debug.Log($"CsoundCompile: {compiledOk}");
    }

    /// <summary>
    /// Frees an instance that no CsoundUnity adopted. Only for bridges the audio thread never saw:
    /// an instance in use by OnAudioFilterRead must not be destroyed here.
    /// </summary>
    public void Destroy()
    {
        lock (s_buildLock)
        {
            if (csound == IntPtr.Zero) return;
            Csound6.NativeMethods.csoundDestroyMessageBuffer(csound);
            Csound6.NativeMethods.csoundDestroy(csound);
            csound = IntPtr.Zero;
            compiledOk = false;
        }
    }

    #region Instantiation

#if !UNITY_IOS
    public int LoadPlugins(string dir)
    {
        return Csound6.NativeMethods.csoundLoadPlugins(csound, dir);
    }
#endif

    #endregion
    public int GetVersion()
    {
        return Csound6.NativeMethods.csoundGetVersion();
    }

    public int GetAPIVersion()
    {
        return Csound6.NativeMethods.csoundGetAPIVersion();
    }

    public void StopCsound()
    {
        Csound6.NativeMethods.csoundStop(csound);
    }

    public void OnApplicationQuit()
    {
        StopCsound();
        //Csound6.NativeMethods.csoundCleanup(csound);
        Csound6.NativeMethods.csoundDestroyMessageBuffer(csound);
        Csound6.NativeMethods.csoundDestroy(csound);
    }

    public void Cleanup()
    {
        Csound6.NativeMethods.csoundCleanup(csound);
    }

    public void Reset()
    {
        Csound6.NativeMethods.csoundReset(csound);
    }

    public bool CompiledWithoutError()
    {
        return compiledOk;
    }

    public void SetMessageOutputEnabled(bool enabled)
    {
        const int LOG_ALL = 135;
        const int LOG_NOTHING = 16;
        if (csound != IntPtr.Zero)
            Csound6.NativeMethods.csoundSetMessageLevel(csound, enabled ? LOG_ALL : LOG_NOTHING);
    }

    public int CompileOrc(string orchStr)
    {
        return Csound6.NativeMethods.csoundCompileOrc(csound, orchStr);
    }

    public int PerformKsmps()
    {
        if (csound == IntPtr.Zero) return -1;
        return Csound6.NativeMethods.csoundPerformKsmps(csound);
    }

    public MYFLT Get0dbfs()
    {
        return Csound6.NativeMethods.csoundGet0dBFS(csound);
    }

    public long GetCurrentTimeSamples()
    {
        return Csound6.NativeMethods.csoundGetCurrentTimeSamples(csound);
    }

    public void SendScoreEvent(string scoreEvent)
    {
        Csound6.NativeMethods.csoundInputMessage(csound, scoreEvent);
    }

    public void RewindScore()
    {
        Csound6.NativeMethods.csoundRewindScore(csound);
    }

    public void CsoundSetScoreOffsetSeconds(MYFLT value)
    {
        Csound6.NativeMethods.csoundSetScoreOffsetSeconds(csound, value);
    }

    public void SetChannel(string channel, MYFLT value)
    {
        Csound6.NativeMethods.csoundSetControlChannel(csound, channel, value);
    }

    public void SetStringChannel(string channel, string value)
    {
        Csound6.NativeMethods.csoundSetStringChannel(csound, channel, value);
    }

    public void SetAudioChannel(string name, MYFLT[] audio)
    {
        var bufsiz = GetKsmps();
        var buffer = Marshal.AllocHGlobal(sizeof(MYFLT) * (int)bufsiz);
        Marshal.Copy(audio, 0, buffer, (int)Math.Min(audio.Length, bufsiz));
        Csound6.NativeMethods.csoundSetAudioChannel(csound, name, buffer);
        Marshal.FreeHGlobal(buffer);
    }

    public MYFLT[] GetAudioChannel(string name)
    {
        var bufsiz = GetKsmps();
        var buffer = Marshal.AllocHGlobal(sizeof(MYFLT) * (int)bufsiz);
        MYFLT[] dest = new MYFLT[bufsiz];//include nchnls/nchnlss_i? no, not an output channel: just a single ksmps-sized buffer
        Csound6.NativeMethods.csoundGetAudioChannel(csound, name, buffer);
        Marshal.Copy(buffer, dest, 0, dest.Length);
        Marshal.FreeHGlobal(buffer);
        return dest;
    }

    /// <summary>
    /// Returns the length of a function table (not including the guard point), or -1 if the table does not exist.
    /// </summary>
    public int TableLength(int table)
    {
        return Csound6.NativeMethods.csoundTableLength(csound, table);
    }

    /// <summary>
    /// Returns the value of a slot in a function table. The table number and index are assumed to be valid.
    /// </summary>
	public MYFLT GetTable(int table, int index)
    {
        return Csound6.NativeMethods.csoundTableGet(csound, table, index);
    }

    /// <summary>
    /// Sets the value of a slot in a function table. The table number and index are assumed to be valid.
    /// </summary>
    public void SetTable(int table, int index, MYFLT value)
    {
        Csound6.NativeMethods.csoundTableSet(csound, table, index, value);
    }

    /// <summary>
    /// Copy the contents of a function table into a supplied array dest 
    /// The table number is assumed to be valid, and the destination needs to have sufficient space to receive all the function table contents.
    public void TableCopyOut(int table, out MYFLT[] dest)
    {
        int len = Csound6.NativeMethods.csoundTableLength(csound, table);
        if (len < 1)
        {
            dest = null;
            return;
        }

        dest = new MYFLT[len];
        IntPtr des = Marshal.AllocHGlobal(sizeof(MYFLT) * dest.Length);
        Csound6.NativeMethods.csoundTableCopyOut(csound, table, des);
        Marshal.Copy(des, dest, 0, len);
        Marshal.FreeHGlobal(des);
    }

    /// <summary>
    /// Asynchronous version of tableCopyOut()
    /// </summary>
    public void TableCopyOutAsync(int table, out MYFLT[] dest)
    {
        int len = Csound6.NativeMethods.csoundTableLength(csound, table);
        if (len < 1)
        {
            dest = null;
            return;
        }

        dest = new MYFLT[len];
        IntPtr des = Marshal.AllocHGlobal(sizeof(MYFLT) * dest.Length);
        Csound6.NativeMethods.csoundTableCopyOutAsync(csound, table, des);
        Marshal.Copy(des, dest, 0, len);
        Marshal.FreeHGlobal(des);
    }

    /// <summary>
    /// Copy the contents of an array source into a given function table 
    /// The table number is assumed to be valid, and the table needs to have sufficient space to receive all the array contents.
    /// </summary>
    public void TableCopyIn(int table, MYFLT[] source)
    {
        var len = Csound6.NativeMethods.csoundTableLength(csound, table);
        if (len < 1 || len < source.Length) return;
        IntPtr src = Marshal.AllocHGlobal(sizeof(MYFLT) * source.Length);
        Marshal.Copy(source, 0, src, source.Length);
        Csound6.NativeMethods.csoundTableCopyIn(csound, table, src);
        Marshal.FreeHGlobal(src);
    }

    /// <summary>
    /// Asynchronous version of csoundTableCopyIn()
    /// </summary>
    public void TableCopyInAsync(int table, MYFLT[] source)
    {
        var len = Csound6.NativeMethods.csoundTableLength(csound, table);
        if (len < 1 || len < source.Length) return;
        IntPtr src = Marshal.AllocHGlobal(sizeof(MYFLT) * source.Length);
        Marshal.Copy(source, 0, src, source.Length);
        Csound6.NativeMethods.csoundTableCopyInAsync(csound, table, src);
        Marshal.FreeHGlobal(src);
    }

    /// <summary>
    /// Stores values to function table 'tableNum' in tableValues, and returns the table length (not including the guard point). 
    /// If the table does not exist, tableValues is set to NULL and -1 is returned.
    /// </summary>
    public int GetTable(out MYFLT[] tableValues, int numTable)
    {
        int len = Csound6.NativeMethods.csoundTableLength(csound, numTable);
        if (len < 1)
        {
            tableValues = null;
            return -1;
        }

        IntPtr tablePtr = new IntPtr();
        tableValues = new MYFLT[len];
        int res = Csound6.NativeMethods.csoundGetTable(csound, out tablePtr, numTable);
        if (res != -1)
            Marshal.Copy(tablePtr, tableValues, 0, len);
        else tableValues = null;
        GCHandle gc = GCHandle.FromIntPtr(tablePtr);
        gc.Free();
        return res;
    }

    /// <summary>
    /// Stores the arguments used to generate function table 'tableNum' in args, and returns the number of arguments used. 
    /// If the table does not exist, args is set to NULL and -1 is returned. 
    /// NB: the argument list starts with the GEN number and is followed by its parameters. eg. f 1 0 1024 10 1 0.5 yields the list {10.0,1.0,0.5}
    /// </summary>
    public int GetTableArgs(out MYFLT[] args, int index)
    {
        IntPtr addr = new IntPtr();
        int len = Csound6.NativeMethods.csoundGetTableArgs(csound, out addr, index);
        args = new MYFLT[len];
        if (len != -1)
            Marshal.Copy(addr, args, 0, len);
        else args = null;
        Marshal.FreeHGlobal(addr);
        return len;
    }

    /// <summary>
    /// Checks if a given GEN number num is a named GEN if so, it returns the string length (excluding terminating NULL char) 
    /// Otherwise it returns 0.
    /// </summary>
    public int IsNamedGEN(int num)
    {
        return Csound6.NativeMethods.csoundIsNamedGEN(csound, num);
    }

    /// <summary>
    /// Gets the GEN name from a number num, if this is a named GEN 
    /// The final parameter is the max len of the string (excluding termination)
    /// </summary>
    public void GetNamedGEN(int num, out string name, int len)
    {
        Csound6.NativeMethods.csoundGetNamedGEN(csound, num, out name, len);
    }

    /// <summary>
    /// Named Gen Proxy 
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private class NamedGenProxy
    {
        public IntPtr name;
        public int genum;
        public IntPtr next; //NAMEDGEN pointer used by csound as linked list, but not sure if we care
    }

    /// <summary>
    /// Returns a Dictionary keyed by the names of all named table generators.
    /// Each name is paired with its internal function number.
    /// </summary>
    /// <returns></returns>
    public IDictionary<string, int> GetNamedGens()
    {
        IDictionary<string, int> gens = new Dictionary<string, int>();
        IntPtr pNAMEDGEN = Csound6.NativeMethods.csoundGetNamedGens(csound);
        while (pNAMEDGEN != IntPtr.Zero)
        {
            NamedGenProxy namedGen = (NamedGenProxy)Marshal.PtrToStructure(pNAMEDGEN, typeof(NamedGenProxy));
            gens.Add(Marshal.PtrToStringAnsi(namedGen.name), namedGen.genum);
            pNAMEDGEN = namedGen.next;
        }
        return gens;
    }

    public MYFLT GetSr()
    {
        return Csound6.NativeMethods.csoundGetSr(csound);
    }

    public MYFLT GetKr()
    {
        return Csound6.NativeMethods.csoundGetKr(csound);
    }

    public uint GetKsmps()
    {
        return Csound6.NativeMethods.csoundGetKsmps(csound);
    }

    /// <summary>
    /// Get a sample from Csound's audio output buffer
    /// <summary>
    public MYFLT GetSpoutSample(int frame, int channel)
    {
        return Csound6.NativeMethods.csoundGetSpoutSample(csound, frame, channel);
    }

    public void AddSpinSample(int frame, int channel, MYFLT sample)
    {
        Csound6.NativeMethods.csoundAddSpinSample(csound, frame, channel, sample);
    }

    /// <summary>
    /// Set a sample from Csound's audio output buffer
    /// <summary>
    public void SetSpinSample(int frame, int channel, MYFLT sample)
    {
        Csound6.NativeMethods.csoundSetSpinSample(csound, frame, channel, sample);
    }

    /// <summary>
    /// Clears the input buffer (spin).
    /// </summary>
    public void ClearSpin()
    {
        Csound6.NativeMethods.csoundClearSpin(csound);
    }

    /// <summary>
    /// Returns the Csound audio input working buffer (spin) as a MYFLT array.
    /// Enables external software to write audio into Csound before calling csoundPerformKsmps.
    /// </summary>
    /// <returns>a MYFLT array representing the Csound audio input buffer</returns>
    public MYFLT[] GetSpin()
    {
        var size = (Int32)Csound6.NativeMethods.csoundGetKsmps(csound) * (int)GetNchnlsInput();
        var spin = new MYFLT[size];
        var addr = Csound6.NativeMethods.csoundGetSpin(csound);
        Marshal.Copy(addr, spin, 0, size);
        return spin;
    }

    /// <summary>
    /// Returns the Csound audio output working buffer (spout) as a MYFLT array.
    /// Enables external software to read audio from Csound after calling csoundPerformKsmps.
    /// </summary>
    /// <returns>a MYFLT array representing the Csound audio output buffer</returns>
    public MYFLT[] GetSpout()
    {
        var size = (Int32)Csound6.NativeMethods.csoundGetKsmps(csound) * (int)GetNchnls();
        var spout = new MYFLT[size];
        var addr = Csound6.NativeMethods.csoundGetSpout(csound);
        Marshal.Copy(addr, spout, 0, size);
        return spout;
    }

    /// <summary>
    /// Get a a control channel
    /// <summary>
    public MYFLT GetChannel(string channel)
    {
        return Csound6.NativeMethods.csoundGetControlChannel(csound, channel, IntPtr.Zero);
    }

    /// <summary>
    /// Get number of input channels
    /// <summary>
    public uint GetNchnlsInput()
    {
        return Csound6.NativeMethods.csoundGetNchnlsInput(csound);
    }

    /// <summary>
    /// Get number of input channels
    /// <summary>
    public uint GetNchnls()
    {
        return Csound6.NativeMethods.csoundGetNchnlsInput(csound);
    }

    public int GetCsoundMessageCount()
    {
        return Csound6.NativeMethods.csoundGetMessageCnt(csound);
    }

    public string GetCsoundMessage()
    {
        string message = GetMessageText(Csound6.NativeMethods.csoundGetFirstMessage(csound));
        Csound6.NativeMethods.csoundPopFirstMessage(csound);
        return message;
    }

    public static string GetMessageText(IntPtr message)
    {
        return CharPtr2String(message);
    }

    /// <summary>
    /// Returns a sorted Dictionary keyed by all opcodes which are active in the current instance of csound.
    /// The values contain argument strings representing signatures for an opcode's
    /// output and input parameters.
    /// The argument strings pairs are stored in a list to accomodate opcodes with multiple signatures.
    /// </summary>
    /// <returns>A sorted Dictionary keyed by all opcodes which are active in the current instance of csound.</returns>
    public IDictionary<string, IList<OpcodeArgumentTypes>> GetOpcodeList()
    {
        var opcodes = new SortedDictionary<string, IList<OpcodeArgumentTypes>>();
        IntPtr ppOpcodeList = IntPtr.Zero;
        int size = Csound6.NativeMethods.csoundNewOpcodeList(csound, out ppOpcodeList);
        if ((ppOpcodeList != IntPtr.Zero) && (size >= 0))
        {
            int proxySize = Marshal.SizeOf(typeof(OpcodeListProxy));
            for (int i = 0; i < size; i++)
            {
                OpcodeListProxy proxy = Marshal.PtrToStructure(ppOpcodeList + (i * proxySize), typeof(OpcodeListProxy)) as OpcodeListProxy;
                string opname = Marshal.PtrToStringAnsi(proxy.opname);
                OpcodeArgumentTypes opcode = new OpcodeArgumentTypes
                {
                    outypes = Marshal.PtrToStringAnsi(proxy.outtypes),
                    intypes = Marshal.PtrToStringAnsi(proxy.intypes),
                    flags = proxy.flags
                };
                if (!opcodes.ContainsKey(opname))
                {
                    IList<OpcodeArgumentTypes> types = new List<OpcodeArgumentTypes>();
                    types.Add(opcode);
                    opcodes.Add(opname, types);
                }
                else
                {
                    opcodes[opname].Add(opcode);
                }
            }
            Csound6.NativeMethods.csoundDisposeOpcodeList(csound, ppOpcodeList);
        }
        return opcodes;
    }

    /// <summary>
    /// Provides a dictionary of all currently defined channels resulting from compilation of an orchestra
    /// containing channel definitions.
    /// Entries, keyed by name, are polymorphically assigned to their correct data type: control, audio, string, pvc.
    /// Used by the Csound6SoftwareBus class to initialize its contents.
    /// </summary>
    /// <returns>a dictionary of all currently defined channels keyed by their name to its ChannelInfo</returns>
    public IDictionary<string, ChannelInfo> GetChannelList()
    {
        IDictionary<string, ChannelInfo> channels = new SortedDictionary<string, ChannelInfo>();

        IntPtr ppChannels = IntPtr.Zero;
        int size = Csound6.NativeMethods.csoundListChannels(csound, out ppChannels);
        if ((size > 0) && (ppChannels != IntPtr.Zero))
        {
            int proxySize = Marshal.SizeOf(typeof(ChannelInfoProxy));
            for (int i = 0; i < size; i++)
            {
                var proxy = Marshal.PtrToStructure(ppChannels + (i * proxySize), typeof(ChannelInfoProxy)) as ChannelInfoProxy;
                string chanName = CharPtr2String(proxy.name);

                ChannelInfo info = new ChannelInfo(chanName, (ChannelType)(proxy.type & 15), (ChannelDirection)(proxy.type >> 4));
                var hintProxy = proxy.hints;
                var hints = new ChannelHints((ChannelBehavior)hintProxy.behav, hintProxy.dflt, hintProxy.min, hintProxy.max)
                {
                    x = hintProxy.x,
                    y = hintProxy.y,
                    height = hintProxy.height,
                    width = hintProxy.width,
                    attributes = CharPtr2String(proxy.name)
                };
                info.Hints = hints;
                channels.Add(chanName, info);
            }
            Csound6.NativeMethods.csoundDeleteChannelList(csound, ppChannels);
        }
        return channels;
    }

    /// <summary>
    /// Fills in a provided raw CSOUND_PARAMS object with csounds current parameter settings.
    /// This method is used internally to manage this class and is not expected to be used directly by a host program.
    /// </summary>
    /// <param name="oparms">a CSOUND_PARAMS structure to be filled in by csound</param>
    /// <returns>The same parameter structure that was provided but filled in with csounds current internal contents</returns>
    public CSOUND_PARAMS GetParams()
    {
        CSOUND_PARAMS oparms = new CSOUND_PARAMS();
        Csound6.NativeMethods.csoundGetParams(csound, oparms);
        return oparms;
    }

    /// <summary>
    /// Transfers the contents of the provided raw CSOUND_PARAMS object into csound's 
    /// internal data structues (chiefly its OPARMS structure).
    /// This method is used internally to manage this class and is not expected to be used directly by a host program.
    /// Most values are used and reflected in CSOUND_PARAMS.
    /// Internally to csound, as of release 6.0.0, Heartbeat and IsComputingOpcodeWeights are ignored
    /// and IsUsingCsdLineCounts can only be set and never reset once set.
    /// </summary>
    /// <param name="parms">a </param>
    public void SetParams(CSOUND_PARAMS parms)
    {
        Csound6.NativeMethods.csoundSetParams(csound, parms);
    }

    /// <summary>
    /// Defines a class to hold out and in types, and flags
    /// </summary>
    public class OpcodeArgumentTypes
    {
        public string outypes;
        public string intypes;
        public int flags;
    }

    /// <summary>
    /// Defines an OpcodeList to be Marshaled
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private class OpcodeListProxy
    {
        public IntPtr opname;
        public IntPtr outtypes;
        public IntPtr intypes;
        public int flags;
    }

    /// <summary>
    /// Private proxy class used during marshalling of actual ChannelInfo 
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private class ChannelInfoProxy
    {
        [MarshalAs(UnmanagedType.AnsiBStr)]
        public IntPtr name;
        public int type;
        [MarshalAs(UnmanagedType.Struct)]
        public ChannelHintsProxy hints;
    }

    /// <summary>
    /// Private proxy class used during marshalling of Channel Hints for Krate channels.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct ChannelHintsProxy
    {
        public ChannelHintsProxy(ChannelHints hints)
        {
            behav = (int)hints.behav;
            dflt = hints.dflt; min = hints.min; max = hints.max;
            x = hints.x; y = hints.y; height = hints.height; width = hints.width;
            attributes = IntPtr.Zero;
        }

        public int behav;
        public MYFLT dflt;
        public MYFLT min;
        public MYFLT max;
        public int x;
        public int y;
        public int width;
        public int height;
        [MarshalAs(UnmanagedType.AnsiBStr)]
        public IntPtr attributes;
    }

    public class ChannelInfo
    {
        public ChannelInfo(string _name, ChannelType _type, ChannelDirection _direction)
        {
            Name = _name;
            Type = _type;
            Direction = _direction;
        }
        public string Name;
        public ChannelType Type;
        public ChannelDirection Direction;
        public ChannelHints Hints;
    };

    /// <summary>
    /// This structure holds the parameter hints for control channels.
    /// </summary>
    public class ChannelHints
    {
        /// <summary>
        /// Creates an empty hint by calling main constructor with all zeros
        /// </summary>
        public ChannelHints() : this(ChannelBehavior.None, 0, 0, 0)
        {
        }

        /// <summary>
        /// Creates a channel hint initialized with the most common Control Channel values as provided.
        /// </summary>
        /// <param name="ibehav">Linear, Exponential or </param>
        /// <param name="idflt"></param>
        /// <param name="imin"></param>
        /// <param name="imax"></param>
        public ChannelHints(ChannelBehavior ibehav, MYFLT idflt, MYFLT imin, MYFLT imax)
        {
            behav = ibehav;
            dflt = idflt;
            min = imin;
            max = imax;
            x = 0;
            y = 0;
            width = 0;
            height = 0;
            attributes = null;
        }

        public ChannelBehavior behav;
        public MYFLT dflt;
        public MYFLT min;
        public MYFLT max;
        public int x;
        public int y;
        public int width;
        public int height;
        public string attributes;
    }

    /// <summary>
    /// 
    /// </summary>
    public enum ChannelBehavior
    {
        None = 0,
        Integer = 1,
        Linear = 2,
        Exponential = 3
    }

    /// <summary>
    /// 
    /// </summary>
    public enum ChannelType
    {
        None = 0, //error return type only, meaningless in input
        Control = 1,
        Audio = 2,
        String = 3,
        Pvs = 4,
        Var = 5,
    }

    [Flags]
    public enum ChannelDirection
    {
        Input = 1,
        Output = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    public class CSOUND_PARAMS
    {
        public int debug_mode;     /* debug mode, 0 or 1 */
        public int buffer_frames;  /* number of frames in in/out buffers */
        public int hardware_buffer_frames; /* ibid. hardware */
        public int displays;       /* graph displays, 0 or 1 */
        public int ascii_graphs;   /* use ASCII graphs, 0 or 1 */
        public int postscript_graphs; /* use postscript graphs, 0 or 1 */
        public int message_level;     /* message printout control */
        public int tempo;             /* tempo (sets Beatmode)  */
        public int ring_bell;         /* bell, 0 or 1 */
        public int use_cscore;        /* use cscore for processing */
        public int terminate_on_midi; /* terminate performance at the end
                                        of midifile, 0 or 1 */
        public int heartbeat;         /* print heart beat, 0 or 1 */
        public int defer_gen01_load;  /* defer GEN01 load, 0 or 1 */
        public int midi_key;           /* pfield to map midi key no */
        public int midi_key_cps;       /* pfield to map midi key no as cps */
        public int midi_key_oct;       /* pfield to map midi key no as oct */
        public int midi_key_pch;       /* pfield to map midi key no as pch */
        public int midi_velocity;      /* pfield to map midi velocity */
        public int midi_velocity_amp;   /* pfield to map midi velocity as amplitude */
        public int no_default_paths;     /* disable relative paths from files, 0 or 1 */
        public int number_of_threads;   /* number of threads for multicore performance */
        public int syntax_check_only;   /* do not compile, only check syntax */
        public int csd_line_counts;     /* csd line error reporting */
        public int compute_weights;     /* use calculated opcode weights for
                                          multicore, 0 or 1  */
        public int realtime_mode;       /* use realtime priority mode, 0 or 1 */
        public int sample_accurate;     /* use sample-level score event accuracy */
        public MYFLT sample_rate_override; /* overriding sample rate */
        public MYFLT control_rate_override; /* overriding control rate */
        public int nchnls_override;     /* overriding number of out channels */
        public int nchnls_i_override;   /* overriding number of in channels */
        public MYFLT e0dbfs_override;  /* overriding 0dbfs */
        public int daemon;              /* daemon mode*/
        public int ksmps_override;      /* ksmps override */
        public int FFT_library;         /* fft_lib */
    }


    /// <summary>
    /// Gets a string value from csound's environment values.
    /// Meaningful values include the contents of Windows' OS environment values 
    /// such as SFDIR or SADIR for path name defaults.
    /// </summary>
    /// <param name="key">the name of the Environment Variable to get</param>
    /// <returns>the corresponding value or an empty string if no such key exists</returns>
    public string GetEnv(string key)
    {
        return CharPtr2String(Csound6.NativeMethods.csoundGetEnv(csound, key));
    }

    /// <summary>
    /// Set the global value of environment variable 'name' to 'value', or delete variable if 'value' is NULL. 
    /// It is not safe to call this function while any Csound instances are active. 
    /// See https://csound.com/docs/manual/CommandEnvironment.html for a list of the variables that can be used.
    /// </summary>
    /// <param name="name">The name of the environment variable to set</param>
    /// <param name="value">The value to set, ie a path</param>
    /// <returns>Returns zero on success.</returns>
    public int SetGlobalEnv(string name, string value)
    {
        return Csound6.NativeMethods.csoundSetGlobalEnv(name, value);
    }

    /// <summary>
    /// Converts the char* for an ascii "c" string represented by the provided IntPtr
    /// into a managed string.  Usually used for values returning a const char * from
    /// a csound routine.
    /// Using this method avoids pInvoke's default automatic attpempted deletion
    /// of the returned char[] when string is expressly given as a marshalling type.
    /// </summary>
    /// <param name="pString"></param>
    /// <returns></returns>
    internal static String CharPtr2String(IntPtr pString)
    {
        return ((pString != null) && (pString != IntPtr.Zero)) ? Marshal.PtrToStringAnsi(pString) : string.Empty;
    }
}

/// <summary>
/// Everything a <see cref="CsoundUnityBridge"/> needs from Unity, read on the main thread so the native
/// create/compile/start can run on a worker thread.
/// </summary>
public sealed class CsoundBridgeRequest
{
    public string CsdSource { get; private set; }
    public bool CompileFromFile { get; private set; }
    internal int SampleRate;
    internal RuntimePlatform Platform;
    /// <summary>Null on device: Csound keeps its default opcode directory.</summary>
    internal string OpcodeDirectory;
    internal KeyValuePair<string, string>[] GlobalEnvironment = Array.Empty<KeyValuePair<string, string>>();
    /// <summary>Android plugin folder to force-load after csoundCreate, or null.</summary>
    internal string PluginDirectory;

    /// <summary>Main thread only: reads AudioSettings, Application paths and EnvironmentSettings paths.</summary>
    public static CsoundBridgeRequest Capture(string csdSource, List<EnvironmentSettings> environmentSettings,
        bool compileFromFile)
    {
        var request = new CsoundBridgeRequest
        {
            CsdSource = csdSource,
            CompileFromFile = compileFromFile,
            SampleRate = AudioSettings.outputSampleRate,
            Platform = Application.platform,
        };

        // On editor and desktop platforms, avoid the machine-wide plugin directory: a
        // system Csound installation can contain ABI-incompatible modules that crash
        // csoundCreate. Windows builds may opt into the small, version-matched module set
        // shipped with this app (currently signalflowgraph for ftgenonce).
        if (Application.isEditor || !Application.isMobilePlatform)
        {
            string opcodeDirectory = ".";
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            string bundledOpcodeDirectory =
                CsoundUnityBridge.GetBundledWindowsOpcodeDirectory(Application.streamingAssetsPath);
            if (!string.IsNullOrWhiteSpace(bundledOpcodeDirectory) &&
                Directory.Exists(bundledOpcodeDirectory))
            {
                opcodeDirectory = bundledOpcodeDirectory;
            }
#endif
            request.OpcodeDirectory = opcodeDirectory;
        }

        if (environmentSettings == null || environmentSettings.Count == 0) return request;
        SupportedPlatform? current = CurrentPlatform(request.Platform);
        if (current == null) return request;

        var environment = new List<KeyValuePair<string, string>>();
        foreach (var env in environmentSettings)
        {
            if (env == null || !env.platform.Equals(current.Value)) continue;
            var path = env.GetPath();
            if (string.IsNullOrWhiteSpace(path)) continue;
            environment.Add(new KeyValuePair<string, string>(env.GetTypeString(), path));
            if (current.Value == SupportedPlatform.Android && request.PluginDirectory == null &&
                env.baseFolder.Equals(EnvironmentPathOrigin.Plugins))
            {
                request.PluginDirectory = path;
            }
        }
        request.GlobalEnvironment = environment.ToArray();
        return request;
    }

    private static SupportedPlatform? CurrentPlatform(RuntimePlatform platform)
    {
        switch (platform)
        {
            case RuntimePlatform.OSXEditor:
            case RuntimePlatform.OSXPlayer:
                return SupportedPlatform.MacOS;
            case RuntimePlatform.WindowsPlayer:
            case RuntimePlatform.WindowsEditor:
                return SupportedPlatform.Windows;
            case RuntimePlatform.Android:
                return SupportedPlatform.Android;
            case RuntimePlatform.IPhonePlayer:
                return SupportedPlatform.iOS;
            default:
                return null;
        }
    }
}

/// <summary>
/// Main-thread hand-over of a pre-built bridge to the next <see cref="CsoundUnity"/> Awake with the
/// same CSD source, so instruments compiled on a worker thread skip the synchronous compile.
/// </summary>
internal static class CsoundBridgeAdoption
{
    private static object s_bridge;
    private static string s_source;

    internal static void Begin(object bridge, string csdSource)
    {
        s_bridge = bridge;
        s_source = csdSource;
    }

    /// <summary>The waiting bridge if it was built from this source; it is handed over once.</summary>
    internal static object TryTake(string csdSource)
    {
        if (s_bridge == null || !string.Equals(s_source, csdSource, StringComparison.Ordinal)) return null;
        object bridge = s_bridge;
        s_bridge = null;
        s_source = null;
        return bridge;
    }

    /// <summary>Clears the slot; returns a bridge nobody took so the caller can destroy it.</summary>
    internal static object End()
    {
        object untaken = s_bridge;
        s_bridge = null;
        s_source = null;
        return untaken;
    }
}
