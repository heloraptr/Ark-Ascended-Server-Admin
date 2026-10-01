using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ArkAscendedServerAdmin.Install;
using ArkAscendedServerAdmin.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace ArkAscendedServerAdmin.Infrastructure.Install;

/// <summary>
/// Production <see cref="ISteamCmdProcessLauncher"/>: runs the child under a Windows pseudo console
/// (ConPTY) so its output streams line by line. SteamCMD block-buffers stdout when it is a pipe, so under
/// <see cref="ProcessSteamCmdLauncher"/> every line arrives in one burst when it exits; behind a console
/// it writes each line as it happens. The pseudo console needs no desktop or window, so it works for the
/// service in session 0. stdout and stderr share the one console stream, so every line goes to
/// <c>onOutput</c>. If the pseudo console cannot be created or the child cannot be started in it, the
/// run falls back to the pipe launcher and says so in one warning line.
/// </summary>
/// <remarks>
/// Handle ownership and teardown, in order:
/// <list type="number">
/// <item>Two anonymous pipes are created. The console host duplicates the child ends (input read,
/// output write) inside <c>CreatePseudoConsole</c>, and our copies are closed straight away, so the only
/// output write end left belongs to the host and the reader sees end of stream when the host exits.</item>
/// <item>The input write end stays open until the pseudo console is disposed, after the reader is
/// done. Closing it early (to give SteamCMD end of input, as the pipe launcher does) makes the console
/// host shut down and end the child at once with STATUS_CONTROL_C_EXIT before it prints anything.
/// SteamCMD logs in anonymously and never prompts; a prompt would wait until the run is canceled.</item>
/// <item>The output read end belongs to the reader thread, which closes it at end of stream. The reader
/// never stops early: a throwing <c>onOutput</c> is caught, the first exception is kept, and draining
/// continues, so the host can never block on a full pipe. An unexpected read failure is logged and
/// reported once, and the reader keeps reading and discarding; SteamCMD is never killed for it.</item>
/// <item>The process handle is held until the end of the run, so its id cannot be reused while a
/// cancellation kills the tree by id. The thread handle is closed at once.</item>
/// <item>After the child exits (or is killed), the pseudo console is closed while the reader is still
/// draining, then the reader is awaited. Only then is a callback exception rethrown.</item>
/// <item>If the child never starts, no reader exists: the output read end is closed before the pseudo
/// console, so closing the console cannot wait on output that nobody drains.</item>
/// </list>
/// </remarks>
public sealed class PseudoConsoleSteamCmdLauncher : ISteamCmdProcessLauncher
{
    /// <summary>The one line written (as a warning) when a run falls back to the pipe launcher.</summary>
    public const string FallbackNotice = "Live SteamCMD output is unavailable on this system; lines will appear when SteamCMD exits.";

    /// <summary>The one line written (as a warning) when reading the pseudo console fails mid-run.</summary>
    public const string OutputLostNotice = "Live SteamCMD output was lost; SteamCMD keeps running and its result will be reported when it exits.";

    /// <summary>Wide enough that SteamCMD's longest lines (install paths, progress) are never wrapped.</summary>
    private const short ConsoleColumns = 512;

    private const short ConsoleRows = 50;

    private const int ErrorBrokenPipe = 109;
    private const int ErrorPipeNotConnected = 233;

    /// <summary>How long to wait for the last output after the pseudo console is closed.</summary>
    private static readonly TimeSpan _drainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Pause between reads after an unexpected read failure, so a dead handle cannot spin a core.</summary>
    private static readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(100);

    private readonly ILogger<PseudoConsoleSteamCmdLauncher> _logger;
    private readonly Func<Stream, Stream>? _outputFilter;
    private readonly ProcessSteamCmdLauncher _fallback = new();

    public PseudoConsoleSteamCmdLauncher(ILogger<PseudoConsoleSteamCmdLauncher> logger)
        : this(logger, outputFilter: null)
    {
    }

    /// <summary>Test seam: <paramref name="outputFilter"/> wraps the stream the reader reads from, to inject read failures.</summary>
    public PseudoConsoleSteamCmdLauncher(ILogger<PseudoConsoleSteamCmdLauncher> logger, Func<Stream, Stream>? outputFilter)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _outputFilter = outputFilter;
    }

    public async Task<int> RunAsync(SteamCmdLaunch launch, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(onOutput);
        ArgumentNullException.ThrowIfNull(onError);
        cancellationToken.ThrowIfCancellationRequested();

        var console = PseudoConsole.TryCreate(out var failure);
        if (console is null)
        {
            _logger.LogWarning(failure, "Could not create a pseudo console for SteamCMD; falling back to redirected output.");
            return await FallBackAsync(launch, onOutput, onError, cancellationToken);
        }

        ChildProcess process;
        try
        {
            process = console.Start(launch);
        }
        catch (Exception ex)
        {
            // No reader exists yet; Dispose closes our output read end before the pseudo console, so the
            // close cannot wait on output nobody will drain.
            console.Dispose();
            // A missing executable is not a pseudo console problem: the pipe launcher would only fail the
            // same way after a misleading notice, so it is reported as is.
            if (ex is not Win32Exception || (Path.IsPathFullyQualified(launch.FileName) && !File.Exists(launch.FileName)))
            {
                throw;
            }

            // Anything else (an access check that only fails in session 0, an odd Windows build) degrades
            // to the old output; a failure that is not about the pseudo console recurs there unchanged.
            _logger.LogWarning(ex, "Could not start SteamCMD in a pseudo console; falling back to redirected output.");
            return await FallBackAsync(launch, onOutput, onError, cancellationToken);
        }

        using (console)
        using (process)
        {
            var sink = new LineSink(onOutput);
            var state = new ReaderState();
            var reader = Task.Factory.StartNew(
                () => Drain(console.Output, new TerminalLineAssembler(sink.Deliver, ConsoleRows), state, onError),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            try
            {
                await WaitForExitAsync(process.Handle, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                state.ChildExited = true;
                KillTree(process);
                await WaitForExitAsync(process.Handle, CancellationToken.None);
                await CloseAndDrainAsync(console, reader, state);
                throw;
            }

            state.ChildExited = true;
            if (!NativeMethods.GetExitCodeProcess(process.Handle, out var exitCode))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetExitCodeProcess failed for SteamCMD.");
            }

            await CloseAndDrainAsync(console, reader, state);
            sink.ThrowIfFailed();
            return exitCode;
        }
    }

    private Task<int> FallBackAsync(SteamCmdLaunch launch, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
    {
        onError(FallbackNotice);
        return _fallback.RunAsync(launch, onOutput, onError, cancellationToken);
    }

    /// <summary>
    /// Reads until end of stream. End of stream, a broken pipe, and a handle closed under us at teardown
    /// are the expected ends. Any other read failure is logged and, while the child still runs, reported
    /// once with <see cref="OutputLostNotice"/>; the reader then keeps reading and discarding, so the
    /// console host never blocks on a full pipe and SteamCMD runs on to its real exit code.
    /// </summary>
    private void Drain(SafeFileHandle output, TerminalLineAssembler assembler, ReaderState state, Action<string> onError)
    {
        using var file = new FileStream(output, FileAccess.Read, bufferSize: 0);
        using var stream = _outputFilter?.Invoke(file) ?? file;
        var buffer = new byte[8192];
        var lost = false;
        while (!state.Stopped)
        {
            int read;
            try
            {
                read = stream.Read(buffer, 0, buffer.Length);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is ErrorBrokenPipe or ErrorPipeNotConnected)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (IOException ex)
            {
                if (!lost)
                {
                    lost = true;
                    _logger.LogWarning(ex, "Reading SteamCMD output from the pseudo console failed; the rest of this run's output is discarded.");
                    if (!state.ChildExited)
                    {
                        onError(OutputLostNotice);
                    }
                }

                Thread.Sleep(_retryDelay);
                continue;
            }

            if (read == 0)
            {
                break;
            }

            if (!lost)
            {
                assembler.Append(buffer.AsSpan(0, read));
            }
        }

        if (!lost)
        {
            assembler.Complete();
        }
    }

    /// <summary>
    /// Closes the pseudo console while the reader keeps draining: on some Windows builds
    /// <c>ClosePseudoConsole</c> blocks until its final output has been read, and the reader only sees end
    /// of stream once the console host has exited.
    /// </summary>
    private async Task CloseAndDrainAsync(PseudoConsole console, Task reader, ReaderState state)
    {
        await Task.Run(console.Close);
        try
        {
            await reader.WaitAsync(_drainTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("SteamCMD output did not reach end of stream within {Timeout}; the last lines may be missing.", _drainTimeout);
        }

        state.Stopped = true;
    }

    /// <summary>Flags shared between <see cref="RunAsync"/> and the reader thread.</summary>
    private sealed class ReaderState
    {
        private volatile bool _childExited;
        private volatile bool _stopped;

        /// <summary>Set once the child has exited or is being killed; a lost-output notice is pointless after that.</summary>
        public bool ChildExited
        {
            get => _childExited;
            set => _childExited = value;
        }

        /// <summary>Set when the run is over, so a reader stuck retrying a failing handle gives up.</summary>
        public bool Stopped
        {
            get => _stopped;
            set => _stopped = value;
        }
    }

    /// <summary>
    /// Hands lines to the caller's callback on the reader thread. The first exception it throws is kept
    /// and later lines are dropped, but the reader keeps draining; <see cref="RunAsync"/> rethrows the
    /// exception once the child has been reaped. (Under the pipe launcher the same exception would escape
    /// on a thread-pool thread and take the process down.)
    /// </summary>
    private sealed class LineSink(Action<string> onOutput)
    {
        private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _failure;

        public void Deliver(string line)
        {
            if (_failure is not null)
            {
                return;
            }

            try
            {
                onOutput(line);
            }
            catch (Exception ex)
            {
                _failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        }

        public void ThrowIfFailed() => _failure?.Throw();
    }

    private static Task WaitForExitAsync(SafeProcessHandle handle, CancellationToken cancellationToken)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitHandle = new ProcessWaitHandle(handle);
        var registration = ThreadPool.RegisterWaitForSingleObject(waitHandle, (_, _) => exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        var cancellation = cancellationToken.Register(() => exited.TrySetCanceled(cancellationToken));
        return Wait();

        async Task Wait()
        {
            try
            {
                await exited.Task;
            }
            finally
            {
                await cancellation.DisposeAsync();
                registration.Unregister(null);
                waitHandle.Dispose();
            }
        }
    }

    /// <summary>
    /// Kills the child and its descendants. The tree kill runs first, while the root is still alive, because
    /// descendants are found through it; it is best effort. TerminateProcess on our own handle then
    /// guarantees the root is gone, so the wait for its exit that follows always ends.
    /// </summary>
    private void KillTree(ChildProcess process)
    {
        // The process handle stays open until we dispose it, so the pid cannot have been reused.
        try
        {
            using var child = Process.GetProcessById(process.Id);
            child.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or AggregateException)
        {
            // Already gone, or part of the tree could not be killed; the root is handled below.
            _logger.LogDebug(ex, "Killing the SteamCMD process tree was incomplete.");
        }

        // Fails harmlessly (access denied) when the process has already exited.
        NativeMethods.TerminateProcess(process.Handle, 1);
    }

    /// <summary>A started child: its process handle and id. The thread handle is closed at once.</summary>
    private sealed class ChildProcess(SafeProcessHandle handle, int id) : IDisposable
    {
        public SafeProcessHandle Handle { get; } = handle;

        public int Id { get; } = id;

        public void Dispose() => Handle.Dispose();
    }

    /// <summary>Lets <see cref="ThreadPool.RegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object?, int, bool)"/> wait on a process handle it does not own.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    /// <summary>
    /// One pseudo console with the parent ends of its two pipes. The console host holds its own
    /// duplicates of the child ends, which are closed here right after creation so the output pipe reaches
    /// end of stream when the host exits.
    /// </summary>
    private sealed class PseudoConsole : IDisposable
    {
        private readonly SafePseudoConsoleHandle _handle;
        private readonly SafeFileHandle _input;

        private PseudoConsole(SafePseudoConsoleHandle handle, SafeFileHandle input, SafeFileHandle output)
        {
            _handle = handle;
            _input = input;
            Output = output;
        }

        /// <summary>The read end of the console's output; owned by the reader once it starts.</summary>
        public SafeFileHandle Output { get; }

        public static PseudoConsole? TryCreate(out Exception? failure)
        {
            failure = null;
            SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
            try
            {
                if (!NativeMethods.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0)
                    || !NativeMethods.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed.");
                }

                var size = new NativeMethods.Coord { X = ConsoleColumns, Y = ConsoleRows };
                var result = NativeMethods.CreatePseudoConsole(size, inputRead, outputWrite, 0, out var handle);
                if (result != 0)
                {
                    handle.Dispose();
                    throw new Win32Exception(result, $"CreatePseudoConsole failed (0x{result:X8}).");
                }

                inputRead.Dispose();
                outputWrite.Dispose();
                return new PseudoConsole(handle, inputWrite, outputRead);
            }
            catch (Exception ex) when (ex is Win32Exception or EntryPointNotFoundException or DllNotFoundException)
            {
                // EntryPointNotFoundException: Windows older than 10 1809 has no ConPTY.
                failure = ex;
                inputRead?.Dispose();
                inputWrite?.Dispose();
                outputRead?.Dispose();
                outputWrite?.Dispose();
                return null;
            }
        }

        /// <summary>Starts the child attached to this console; throws <see cref="Win32Exception"/> like <c>Process.Start</c> when it cannot.</summary>
        public ChildProcess Start(SteamCmdLaunch launch)
        {
            var commandLine = WindowsCommandLine.Build(launch.FileName, launch.Arguments).ToCharArray();
            Array.Resize(ref commandLine, commandLine.Length + 1); // CreateProcessW wants a writable, terminated buffer.

            var attributeListSize = IntPtr.Zero;
            NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            var attributeList = Marshal.AllocHGlobal(attributeListSize);
            var initialized = false;
            var addedReference = false;
            try
            {
                if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed.");
                }

                initialized = true;
                _handle.DangerousAddRef(ref addedReference);
                if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList, 0, NativeMethods.ProcThreadAttributePseudoConsole, _handle.DangerousGetHandle(), IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute failed.");
                }

                var startupInfo = new NativeMethods.StartupInfoEx
                {
                    StartupInfo = new NativeMethods.StartupInfo
                    {
                        Cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>(),

                        // Null std handles on purpose: without STARTF_USESTDHANDLES the child inherits this
                        // process's own redirected stdout (a service, a test runner) and writes there
                        // instead of to the pseudo console.
                        Flags = NativeMethods.StartfUseStdHandles,
                    },
                    AttributeList = attributeList,
                };

                if (!NativeMethods.CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    NativeMethods.ExtendedStartupInfoPresent,
                    IntPtr.Zero,
                    launch.WorkingDirectory,
                    ref startupInfo,
                    out var processInfo))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not start {launch.FileName}.");
                }

                NativeMethods.CloseHandle(processInfo.Thread);
                return new ChildProcess(new SafeProcessHandle(processInfo.Process, ownsHandle: true), processInfo.ProcessId);
            }
            finally
            {
                if (addedReference)
                {
                    _handle.DangerousRelease();
                }

                if (initialized)
                {
                    NativeMethods.DeleteProcThreadAttributeList(attributeList);
                }

                Marshal.FreeHGlobal(attributeList);
            }
        }

        /// <summary>Closes the console, which ends any client still attached; may block until output is drained.</summary>
        public void Close() => _handle.Dispose();

        public void Dispose()
        {
            // Output first: when no reader ever ran (the child failed to start), nobody drains the pipe,
            // and ClosePseudoConsole can wait on undrained output on some Windows builds. With a reader,
            // the read end is normally closed already, and a read still in flight keeps the handle alive
            // until it returns.
            Output.Dispose();
            _handle.Dispose();
            _input.Dispose();
        }
    }

    private sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePseudoConsoleHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            NativeMethods.ClosePseudoConsole(handle);
            return true;
        }
    }

    private static class NativeMethods
    {
        internal const int StartfUseStdHandles = 0x00000100;
        internal const uint ExtendedStartupInfoPresent = 0x00080000;
        internal static readonly IntPtr ProcThreadAttributePseudoConsole = 0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Coord
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            public int Cb;
            public IntPtr Reserved;
            public IntPtr Desktop;
            public IntPtr Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2Size;
            public IntPtr Reserved2;
            public IntPtr StdInput;
            public IntPtr StdOutput;
            public IntPtr StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public int ProcessId;
            public int ThreadId;
        }

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int CreatePseudoConsole(Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out SafePseudoConsoleHandle phPC);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(
            string? lpApplicationName,
            char[] lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref StartupInfoEx lpStartupInfo,
            out ProcessInformation lpProcessInformation);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out int lpExitCode);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);
    }
}
