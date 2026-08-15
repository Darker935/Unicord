using Microsoft.AppCenter.Crashes;
using Microsoft.Extensions.Logging;
#if DEBUG
using Microsoft.Extensions.Logging.Debug;
#endif
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation.Diagnostics;
using Windows.Storage;
using Windows.Storage.Search;
using Windows.System.Diagnostics;

namespace Unicord.Universal
{
    internal static class Logger
    {
        // ETW buffers events in memory; a readable .etl only appears when the session is closed and
        // saved. There is no per-event flush on FileLoggingSession, so SaveAsync/SaveNowAsync are the
        // only durability points and anything since the last one is lost if the process is killed.
        private class WinRTLoggerProvider : ILoggerProvider
        {
            // Sized for a caller that logs in a loop, not for the app's current call sites. At the
            // point this overflows the trace is already unreadable, so entries are dropped and
            // counted rather than blocking whoever is logging.
            private const int QueueCapacity = 16384;

            // ETW silently discards events past its own size limit, which would turn "someone logged
            // a payload" into "the event vanished with no explanation". Truncate instead.
            private const int MaxFieldLength = 8192;

            private class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();
                public void Dispose() { }
            }

            // A snapshot of one logging call. TState is only valid for the duration of the Log call,
            // so the message and properties are copied here before the entry leaves the caller.
            private readonly struct LogEntry
            {
                public LogEntry(LoggingChannel channel, string name, LoggingLevel level, string message, KeyValuePair<string, object>[] properties, Exception exception)
                {
                    Channel = channel;
                    Name = name;
                    Level = level;
                    Message = message;
                    Properties = properties;
                    Exception = exception;
                    ThreadId = Environment.CurrentManagedThreadId;
                    Timestamp = DateTimeOffset.Now;
                    Barrier = null;
                }

                public LogEntry(TaskCompletionSource<bool> barrier)
                {
                    Channel = null;
                    Name = null;
                    Level = LoggingLevel.Verbose;
                    Message = null;
                    Properties = null;
                    Exception = null;
                    ThreadId = 0;
                    Timestamp = default;
                    Barrier = barrier;
                }

                public readonly LoggingChannel Channel;
                public readonly string Name;
                public readonly LoggingLevel Level;
                public readonly string Message;
                public readonly KeyValuePair<string, object>[] Properties;
                public readonly Exception Exception;
                public readonly int ThreadId;
                public readonly DateTimeOffset Timestamp;
                public readonly TaskCompletionSource<bool> Barrier;
            }

            private class WinRTLogger : ILogger
            {
                private readonly string category;
                private readonly LoggingChannel channel;
                private readonly WinRTLoggerProvider provider;

                public WinRTLogger(string categoryName, LoggingChannel channel, WinRTLoggerProvider provider)
                {
                    this.category = categoryName;
                    this.channel = channel;
                    this.provider = provider;
                }

                public IDisposable BeginScope<TState>(TState state) where TState : notnull
                    => NullScope.Instance;

                // Rendering a message is the dominant cost of a log call, and nothing can make that
                // free on this runtime. What this makes free is *not* logging: a hot path can guard
                // with if (logger.IsEnabled(...)) and pay nothing. Returning true unconditionally,
                // as this used to, made that guard useless.
                public bool IsEnabled(LogLevel logLevel)
                    => logLevel != LogLevel.None && logLevel >= MinimumLevel;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                {
                    if (!IsEnabled(logLevel))
                        return;

                    // Snapshot on the calling thread, build the LoggingFields and cross the WinRT
                    // boundary on the writer thread. Voice logs from paths that must not allocate or
                    // block more than necessary.
                    var message = formatter != null ? formatter(state, exception) : state?.ToString();
                    var properties = Capture(state);
                    var name = !string.IsNullOrEmpty(eventId.Name) ? eventId.Name : category;

                    provider.Enqueue(new LogEntry(channel, name, MapLevel(logLevel), message, properties, exception));
                }

                // Microsoft.Extensions.Logging passes the message template's named holes as a
                // key/value list. Keeping them lets ETW record real typed fields instead of one
                // pre-rendered string.
                private static KeyValuePair<string, object>[] Capture<TState>(TState state)
                {
                    if (state is IReadOnlyList<KeyValuePair<string, object>> values)
                    {
                        var count = values.Count;
                        if (count == 0)
                            return null;

                        var captured = new KeyValuePair<string, object>[count];
                        for (var i = 0; i < count; i++)
                            captured[i] = values[i];

                        return captured;
                    }

                    return null;
                }

                private static LoggingLevel MapLevel(LogLevel level) => level switch
                {
                    LogLevel.Trace or LogLevel.Debug => LoggingLevel.Verbose,
                    LogLevel.Information => LoggingLevel.Information,
                    LogLevel.Warning => LoggingLevel.Warning,
                    LogLevel.Error => LoggingLevel.Error,
                    LogLevel.Critical => LoggingLevel.Critical,
                    _ => LoggingLevel.Verbose
                };
            }

            private readonly ConcurrentDictionary<string, LoggingChannel> channels
                = new ConcurrentDictionary<string, LoggingChannel>();
            private readonly BlockingCollection<LogEntry> queue
                = new BlockingCollection<LogEntry>(QueueCapacity);
            private readonly object sessionLock = new object();
            private readonly SemaphoreSlim saveLock = new SemaphoreSlim(1, 1);
            private readonly Thread writer;

            private FileLoggingSession session;
            private string sessionName;
            private int sessionIndex;
            private long dropped;

            /// <summary>
            /// Name of this process's trace folder. Only the name is held, never a StorageFolder:
            /// the folder can be deleted at any time - by Clear, or by hand in Explorer - and a
            /// cached folder object would silently break every later save for the rest of the run.
            /// </summary>
            public string RunFolderName { get; }

            public WinRTLoggerProvider()
            {
                this.RunFolderName = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyyMMdd-HHmmss}-{1}",
                    DateTime.Now,
                    ProcessDiagnosticInfo.GetForCurrentProcess().ProcessId);

                this.session = CreateSession();

                this.writer = new Thread(WriterLoop)
                {
                    Name = "Unicord Log Writer",
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal
                };

                this.writer.Start();
            }

            /// <summary>
            /// One folder per process, so a run's traces stay together instead of being interleaved
            /// with every other run in a single flat directory. Resolved on every use and created if
            /// missing, so deleting it cannot break saving.
            /// </summary>
            private async Task<StorageFolder> GetRunFolderAsync()
            {
                var logs = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync("Logs", CreationCollisionOption.OpenIfExists);

                return await logs.CreateFolderAsync(RunFolderName, CreationCollisionOption.OpenIfExists);
            }

            /// <summary>
            /// Grouping traces into a per-run folder is tidying, not the save itself. The file is
            /// already written and readable in Logs\ before this runs, so a failure here is reported
            /// and swallowed rather than being allowed to fail the save and strand the file.
            /// </summary>
            private async Task MoveIntoRunFolderAsync(StorageFile file)
            {
                try
                {
                    var folder = await GetRunFolderAsync().ConfigureAwait(false);
                    await file.MoveAsync(folder, file.Name, NameCollisionOption.GenerateUniqueName);
                }
                catch (Exception)
                {
                    // Leave it in Logs\ - still a valid trace, just not filed under the run.
                }
            }

            public ILogger CreateLogger(string categoryName)
            {
                var channel = channels.GetOrAdd(categoryName, name =>
                {
                    var created = new LoggingChannel(name, new LoggingChannelOptions());

                    lock (sessionLock)
                        session.AddLoggingChannel(created);

                    return created;
                });

                return new WinRTLogger(categoryName, channel, this);
            }

            private FileLoggingSession CreateSession()
            {
                // Sortable, human-readable session names. The previous GUID names made it impossible
                // to tell which of the files in LocalState\Logs was the newest without stat'ing them.
                var index = Interlocked.Increment(ref sessionIndex);
                var name = string.Format(CultureInfo.InvariantCulture, "Unicord-{0:yyyyMMdd-HHmmss}-{1:D2}", DateTime.Now, index);

                sessionName = name;

                var created = new FileLoggingSession(name);
                created.LogFileGenerated += OnLogFileGenerated;
                return created;
            }

            /// <summary>
            /// Filename prefix of the files the live session is writing right now. FileLoggingSession
            /// keeps those in the Logs root alongside finished traces, and ETW opens them so they can
            /// be deleted while still in use - the delete succeeds silently and the next save then has
            /// nothing left to finalise. Anything touching that folder has to skip these.
            /// </summary>
            private string LiveFilePrefix
            {
                get
                {
                    lock (sessionLock)
                        return "Log-" + sessionName + "-";
                }
            }

            private void OnLogFileGenerated(IFileLoggingSession sender, LogFileGeneratedEventArgs args)
            {
                // Raised only when the session rolls over to a new file; CloseAndSaveToFileAsync
                // returns its final file directly instead of raising this.
                //
                // Subscribing takes ownership: the session may delete or overwrite this file the
                // moment the handler returns, so it has to be moved now and synchronously. Handling
                // the event without moving the file is worse than never subscribing - it is what made
                // saves fail with "MoveFile failed" and left every trace at ~1KB, because the file
                // holding the actual events had already been reclaimed.
                MoveIntoRunFolderAsync(args.File).GetAwaiter().GetResult();
            }

            private void Enqueue(in LogEntry entry)
            {
                // Never block a caller on logging. If the writer cannot keep up the entry is dropped
                // and counted, and the count is reported into the next saved session.
                if (!queue.TryAdd(entry))
                    Interlocked.Increment(ref dropped);
            }

            private void WriterLoop()
            {
                foreach (var entry in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (entry.Barrier != null)
                        {
                            entry.Barrier.TrySetResult(true);
                            continue;
                        }

                        Write(entry);
                        ReportDrops(entry.Channel);
                    }
                    catch (Exception)
                    {
                        // Logging must never take the app down.
                    }
                }
            }

            private void ReportDrops(LoggingChannel channel)
            {
                // Reported from the writer thread as soon as the backlog clears, so the gap is marked
                // in the trace where it happened rather than only being totalled at save time.
                if (Volatile.Read(ref dropped) == 0 || queue.Count > QueueCapacity / 2)
                    return;

                var lost = Interlocked.Exchange(ref dropped, 0);
                if (lost == 0)
                    return;

                var fields = new LoggingFields();
                fields.AddDateTime("Timestamp", DateTimeOffset.Now);
                fields.AddInt64("Dropped", lost);

                channel.LogEvent("LogEntriesDropped", fields, LoggingLevel.Warning);
            }

            private void Write(in LogEntry entry)
            {
                var fields = new LoggingFields();

                // The ETW timestamp is when the writer thread ran, not when the call was made. Voice
                // diagnostics are read as a timeline, so the caller's clock has to be recorded.
                fields.AddDateTime("Timestamp", entry.Timestamp);
                fields.AddInt32("ThreadId", entry.ThreadId);

                if (entry.Message != null)
                    fields.AddString("Message", Truncate(entry.Message));

                if (entry.Properties != null)
                {
                    foreach (var property in entry.Properties)
                    {
                        // The template itself is not useful as a field, and Message already holds the
                        // rendered text.
                        if (property.Key == "{OriginalFormat}" || property.Key == "Message")
                            continue;

                        AddField(fields, property.Key, property.Value);
                    }
                }

                if (entry.Exception != null)
                {
                    fields.AddString("ExceptionType", entry.Exception.GetType().FullName);
                    fields.AddString("ExceptionMessage", Truncate(entry.Exception.Message));

                    if (entry.Exception.StackTrace != null)
                        fields.AddString("StackTrace", Truncate(entry.Exception.StackTrace));
                }

                entry.Channel.LogEvent(entry.Name, fields, entry.Level);
            }

            private static void AddField(LoggingFields fields, string name, object value)
            {
                switch (value)
                {
                    case null:
                        fields.AddEmpty(name);
                        break;
                    case string s:
                        fields.AddString(name, Truncate(s));
                        break;
                    case bool b:
                        fields.AddBoolean(name, b);
                        break;
                    case int i:
                        fields.AddInt32(name, i);
                        break;
                    case long l:
                        fields.AddInt64(name, l);
                        break;
                    case uint ui:
                        fields.AddUInt32(name, ui);
                        break;
                    case ulong ul:
                        fields.AddUInt64(name, ul);
                        break;
                    case double d:
                        fields.AddDouble(name, d);
                        break;
                    case float f:
                        fields.AddSingle(name, f);
                        break;
                    case Guid g:
                        fields.AddGuid(name, g);
                        break;
                    case DateTimeOffset dto:
                        fields.AddDateTime(name, dto);
                        break;
                    default:
                        fields.AddString(name, Truncate(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
                        break;
                }
            }

            private static string Truncate(string value)
            {
                if (value == null || value.Length <= MaxFieldLength)
                    return value;

                return value.Substring(0, MaxFieldLength) + "... [truncated " + (value.Length - MaxFieldLength).ToString(CultureInfo.InvariantCulture) + " chars]";
            }

            private async Task DrainAsync()
            {
                var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!queue.TryAdd(new LogEntry(barrier)))
                    return;

                // Bounded so a wedged writer cannot hold up suspension past its deferral budget.
                var completed = await Task.WhenAny(barrier.Task, Task.Delay(2000)).ConfigureAwait(false);
                if (completed != barrier.Task)
                    barrier.TrySetResult(false);
            }

            public async Task<StorageFile> SaveAsync()
            {
                // The button, EnteredBackground and Suspending can all land at once. Two sessions
                // closing concurrently race over the same on-disk files.
                await saveLock.WaitAsync().ConfigureAwait(false);

                try
                {
                    var lost = Interlocked.Exchange(ref dropped, 0);
                    if (lost > 0)
                    {
                        var fields = new LoggingFields();
                        fields.AddInt64("Dropped", lost);

                        lock (sessionLock)
                        {
                            if (channels.TryGetValue("Unicord", out var channel))
                                channel.LogEvent("LogEntriesDropped", fields, LoggingLevel.Warning);
                        }
                    }

                    await DrainAsync().ConfigureAwait(false);

                    FileLoggingSession closing;
                    lock (sessionLock)
                    {
                        closing = session;
                        var replacement = CreateSession();

                        // Every live channel has to be attached to the replacement before the old
                        // session closes. Without this the new session has no channels at all and
                        // every log call after the first save is silently discarded for the rest of
                        // the process.
                        foreach (var channel in channels.Values)
                            replacement.AddLoggingChannel(channel);

                        session = replacement;
                    }

                    var saved = await closing.CloseAndSaveToFileAsync();
                    closing.LogFileGenerated -= OnLogFileGenerated;

                    if (saved != null)
                        await MoveIntoRunFolderAsync(saved).ConfigureAwait(false);

                    return saved;
                }
                finally
                {
                    saveLock.Release();
                }
            }

            public async Task<int> ClearSavedLogsAsync()
            {
                // Held so a save cannot rotate the session mid-clear, which would change which files
                // are live underneath us.
                await saveLock.WaitAsync().ConfigureAwait(false);

                try
                {
                    var live = LiveFilePrefix;
                    var logs = await ApplicationData.Current.LocalFolder
                        .CreateFolderAsync("Logs", CreationCollisionOption.OpenIfExists);

                    var deleted = 0;

                    foreach (var file in (await logs.GetFilesAsync()).ToList())
                    {
                        // Skipping these is the whole point: deleting the running session's own
                        // in-progress file is what made clear-then-save fail.
                        if (file.Name.StartsWith(live, StringComparison.OrdinalIgnoreCase))
                            continue;

                        deleted += await TryDeleteAsync(file);
                    }

                    foreach (var run in (await logs.GetFoldersAsync()).ToList())
                    {
                        var files = (await run.GetFilesAsync()).ToList();

                        var locked = 0;
                        foreach (var file in files)
                        {
                            var removed = await TryDeleteAsync(file);
                            deleted += removed;
                            locked += removed == 0 ? 1 : 0;
                        }

                        // This run keeps writing here, so its traces go but the folder stays.
                        if (locked != 0 || string.Equals(run.Name, RunFolderName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            await run.DeleteAsync(StorageDeleteOption.PermanentDelete);
                        }
                        catch (Exception)
                        {
                        }
                    }

                    return deleted;
                }
                finally
                {
                    saveLock.Release();
                }
            }

            private static async Task<int> TryDeleteAsync(StorageFile file)
            {
                try
                {
                    await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    return 1;
                }
                catch (Exception)
                {
                    return 0;
                }
            }

            public void Dispose()
            {
                queue.CompleteAdding();

                lock (sessionLock)
                    session.Dispose();
            }
        }

        /// <summary>
        /// The level below which log calls are discarded before anything is rendered. Callers on hot
        /// paths should guard with <see cref="ILogger.IsEnabled(LogLevel)"/>, which honours this, so
        /// that logging they do not want costs nothing.
        /// </summary>
        public static LogLevel MinimumLevel { get; set; }
#if DEBUG
            = LogLevel.Debug;
#else
            = LogLevel.Information;
#endif

        private static WinRTLoggerProvider WinRTProvider
             = new WinRTLoggerProvider();

        // The factory filter is left wide open so MinimumLevel is the single authority and can be
        // changed at runtime in either direction.
        public static ILoggerFactory LoggerFactory = new LoggerFactory(new ILoggerProvider[] {
#if DEBUG
            new DebugLoggerProvider(),
#endif
            WinRTProvider
        }, new LoggerFilterOptions()
        {
            MinLevel = LogLevel.Trace
        });

        private static readonly ILogger InternalLogger
            = LoggerFactory.CreateLogger("Unicord");

        public static ILogger<T> GetLogger<T>() where T : class
            => LoggerFactory.CreateLogger<T>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Log(object message, [CallerMemberName] string source = "General")
        {
            InternalLogger.Log(LogLevel.Information, new EventId(100, source), "[{Source}] {Message}", source, message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void LogError(Exception ex, [CallerMemberName] string memberName = "UnknownMember", [CallerFilePath] string filePath = "Unknown.cs", [CallerLineNumber] int lineNum = 0)
        {
            InternalLogger.Log(LogLevel.Error, ex, "An error occured: {MemberName} @ {FilePath}:{LineNumber}", memberName, filePath, lineNum);
            Crashes.TrackError(ex, new Dictionary<string, string> { ["MemberName"] = memberName, ["FilePath"] = filePath, ["LineNumber"] = lineNum.ToString() });
        }

        /// <summary>
        /// Closes the current trace session, writes it to LocalState\Logs and starts a new one.
        /// This is the only point at which buffered ETW events reach the disk.
        /// </summary>
        public static Task<StorageFile> SaveNowAsync()
            => WinRTProvider.SaveAsync();

        public static async Task OnSuspendingAsync()
        {
            await WinRTProvider.SaveAsync();
        }

        /// <summary>
        /// The folder <see cref="FileLoggingSession"/> saves completed traces into.
        /// </summary>
        public static Task<StorageFolder> GetLogsFolderAsync()
            => ApplicationData.Current.LocalFolder.CreateFolderAsync("Logs", CreationCollisionOption.OpenIfExists).AsTask();

        // Traces live in a folder per run, so both of these have to walk subfolders rather than just
        // the Logs root.
        public static async Task<(int Count, ulong Size)> GetLogsInfoAsync()
        {
            var folder = await GetLogsFolderAsync();
            var files = await folder.CreateFileQueryWithOptions(
                new QueryOptions(CommonFileQuery.OrderByName, null) { FolderDepth = FolderDepth.Deep })
                .GetFilesAsync();

            ulong size = 0;
            foreach (var file in files)
            {
                var properties = await file.GetBasicPropertiesAsync();
                size += properties.Size;
            }

            return (files.Count, size);
        }

        /// <summary>
        /// Deletes saved traces, including the per-run folders holding them. The files the running
        /// session is still writing are left alone, so this is safe to call while the app is running.
        /// </summary>
        public static Task<int> ClearLogsAsync()
            => WinRTProvider.ClearSavedLogsAsync();
    }
}
