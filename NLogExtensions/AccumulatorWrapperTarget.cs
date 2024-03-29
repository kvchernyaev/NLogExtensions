﻿
#if NET4_5 || NET45 || NET4_0 || NET4_6 || NET4_7
#define USECONCURRENT
#endif


#region usings
using System;
#if USECONCURRENT
using System.Collections.Concurrent;
#endif
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using NLog.Common;
using NLog.Config;
using NLog.Internal;
using NLog.Conditions;
using NLog.Filters;
using NLog.Targets.Wrappers;
using NLog.Targets;
using NLog;
#endregion



namespace NLogExtensions
{
    /// <summary>
    /// The first record is to log immediately, then accumulate for a time and flush by timer. Equivalence is taken into account.
    /// </summary>
    [Target("AccumulatorWrapperTarget")]
    public class AccumulatorWrapperTarget : WrapperTargetBase
    {
#if USECONCURRENT
        ConcurrentDictionary<AsyncLogEventInfo, Tuple<int, StringBuilder, AsyncLogEventInfo>>
            _entriesCounts;
#else
        class Tuple<T1, T2, T3>
        {
            public T1 Item1;
            public T2 Item2;
            public T3 Item3;
            public Tuple(T1 item1, T2 item2, T3 item3)
            {
                Item2 = item2;
                Item1 = item1;
                Item3 = item3;
            }
        }
        Dictionary<AsyncLogEventInfo, Tuple<int, StringBuilder, AsyncLogEventInfo>> _entriesCounts;
#endif



        class AsyncLogEventInfoEqualityComparer : IEqualityComparer<AsyncLogEventInfo>
        {
            public AsyncLogEventInfoEqualityComparer(bool useFormattedMessage)
            {
                _useFormattedMessage = useFormattedMessage;
            }


            readonly bool _useFormattedMessage = false;


            public bool Equals(AsyncLogEventInfo x, AsyncLogEventInfo y)
            {
                LogEventInfo a = x.LogEvent;
                LogEventInfo b = y.LogEvent;

                return a.LoggerName == b.LoggerName &&
                       a.Level == b.Level &&
                       (_useFormattedMessage
                           ? a.FormattedMessage == b.FormattedMessage
                           : a.Message == b.Message) &&
                       // exception.ToString is very expensive so do it last
                       a.Exception?.ToString() == b.Exception?.ToString();
            }


            public int GetHashCode(AsyncLogEventInfo x)
            {
                LogEventInfo a = x.LogEvent;
                if (a == null) return 0;
                int withoutExc = (a.LoggerName?.GetHashCode() ?? 0) ^
                                 ((_useFormattedMessage ? a.FormattedMessage : a.Message)?.GetHashCode() ?? 0) ^
                                 (a.Level?.GetHashCode() ?? 0);
                return a.Exception == null
                    ? withoutExc
                    : withoutExc ^ (a.Exception.Message?.GetHashCode() ?? 0) ^
                      a.Exception.GetType().GetHashCode()
#if !NETSTANDARD1_3 && !NETSTANDARD1_5 && !SILVERLIGHT && !__ANDROID__ && !__IOS__
                      ^ a.Exception.TargetSite.GetHashCode()
#endif
                      ;
                // do not use a.Exception.StackTrace - i think it is performance impact
            }
        }



        Timer _flushTimer;
        readonly object _lockObject = new object();
        volatile bool _isTimerOnNow;


        #region ctors
        const int FlushTimeoutDefault = 5000;


        /// <summary>
        /// Initializes a new instance of the <see cref="AccumulatorWrapperTarget" /> class with default values for properties.
        /// </summary>
        public AccumulatorWrapperTarget()
            : this(null, null, FlushTimeoutDefault) { }


        /// <summary>
        /// Initializes a new instance of the <see cref="AccumulatorWrapperTarget" /> class with default values for properties.
        /// </summary>
        /// <param name="name">Name of the target.</param>
        /// <param name="wrappedTarget">The wrapped target.</param>
        public AccumulatorWrapperTarget(string name, Target wrappedTarget)
            : this(name, wrappedTarget, FlushTimeoutDefault) { }


        /// <summary>
        /// Initializes a new instance of the <see cref="AccumulatorWrapperTarget" /> class with default values for properties.
        /// </summary>
        /// <param name="wrappedTarget">The wrapped target.</param>
        public AccumulatorWrapperTarget(Target wrappedTarget)
            : this(null, wrappedTarget, FlushTimeoutDefault) { }


        /// <summary>
        /// Initializes a new instance of the <see cref="AccumulatorWrapperTarget" /> class
        /// </summary>
        /// <param name="name"></param>
        /// <param name="wrappedTarget"></param>
        /// <param name="flushTimeout"></param>
        public AccumulatorWrapperTarget(string name, Target wrappedTarget, int flushTimeout)
        {
            Name = name;
            WrappedTarget = wrappedTarget;
            FlushTimeout = flushTimeout;
            GroupByTemplate = true;
        }
        #endregion


        #region settings
        /// <summary>
        /// Gets or sets the timeout (in milliseconds) after which the contents of buffer will be flushed 
        /// </summary>
        [RequiredParameter]
        [DefaultValue(5000)]
        public int FlushTimeout { get; set; }


        /// <summary>
        /// just backing field for GroupByTemplate
        /// </summary>
        bool _GroupByTemplate;


        /// <summary>
        /// Group log messages by <see cref="LogEventInfo.Message"/> (GroupByTemplate==true) or <see cref="LogEventInfo.FormattedMessage"/> (GroupByTemplate==false). By default is true.
        /// </summary>
        [DefaultValue(true)]
        public bool GroupByTemplate
        {
            get { return _GroupByTemplate; }
            set
            {
                _GroupByTemplate = value;
#if USECONCURRENT
                _entriesCounts =
                    new ConcurrentDictionary<AsyncLogEventInfo,
                        Tuple<int, StringBuilder, AsyncLogEventInfo>>(
                        new AsyncLogEventInfoEqualityComparer(!_GroupByTemplate));
#else
                _entriesCounts =
                    new Dictionary<AsyncLogEventInfo, Tuple<int, StringBuilder, AsyncLogEventInfo>>(
                        new AsyncLogEventInfoEqualityComparer(!_GroupByTemplate));
#endif
            }
        }


        /// <summary>
        /// Separator for messages in one group. By default is NewLine.
        /// </summary>
        [DefaultValue("\\n")]
        public string GroupByTemplateSeparator { get; set; } = Environment.NewLine;


        /// <summary>
        /// Append count of waiting accumulated messages to the <see cref="LogEventInfo.Message"/> when this wrapper is flushed. Pattern {0} means the place for count for string.Format.
        /// For example, " (Hits: {0})"
        /// </summary>
        [DefaultValue(" - {0} times:")]
        public string CountAppendFormat { get; set; } = " - {0} times";


        /// <summary>
        /// If true so grouped accumulated message is corrected and contains (is appended by) count of accumulated messages and the messages themselves. If false so you can use Properties (IsFirst, AccumulatedCount, AccumulatedMessages) in layout.
        /// </summary>
        [DefaultValue(true)]
        public bool CorrectMessageForGroup { get; set; } = true;
        #endregion


        /// <inheritdoc />
        protected override void InitializeTarget()
        {
            base.InitializeTarget();
            InternalLogger.Trace("BufferingWrapper(Name={0}): Create Timer", Name);
            _flushTimer = new Timer(FlushCallback, null, Timeout.Infinite, Timeout.Infinite);
        }


        /// <summary>
        /// Flushes pending events in the buffer (if any), followed by flushing the WrappedTarget.
        /// </summary>
        /// <param name="asyncContinuation">The asynchronous continuation.</param>
        protected override void FlushAsync(AsyncContinuation asyncContinuation)
        {
            Flush();
            base.FlushAsync(asyncContinuation);
        }


        /// <summary>
        /// Closes the target by flushing pending events in the buffer (if any).
        /// </summary>
        protected override void CloseTarget()
        {
            Timer currentTimer = _flushTimer;
            if (currentTimer != null)
            {
                _flushTimer = null;
                if (WaitForDispose(currentTimer, TimeSpan.FromSeconds(1)))
                    Flush();
            }

            base.CloseTarget();
        }


        /// <summary>
        /// Disposes the Timer, and waits for it to leave the Timer-callback-method
        /// </summary>
        /// <param name="timer">The Timer object to dispose</param>
        /// <param name="timeout">Timeout to wait (TimeSpan.Zero means dispose without wating)</param>
        /// <returns>Timer disposed within timeout (true/false)</returns>
        private static bool WaitForDispose(Timer timer, TimeSpan timeout)
        {
            timer.Change(Timeout.Infinite, Timeout.Infinite);

            if (timeout != TimeSpan.Zero)
            {
                ManualResetEvent waitHandle = new ManualResetEvent(false);
                if (timer.Dispose(waitHandle) && !waitHandle.WaitOne((int)timeout.TotalMilliseconds))
                    return false;   // Return without waiting for timer, and without closing waitHandle - Dispose is still working

                waitHandle.Close();
            }
            else
                timer.Dispose();

            return true;
        }


        /// <inheritdoc />
        /// <summary>
        /// The first record is to log immediately, then accumulate for a time and flush by timer. Equivalence is taken into account.
        /// </summary>
        protected override void Write(AsyncLogEventInfo e)
        {
            Tuple<int, StringBuilder, AsyncLogEventInfo> count;
#if USECONCURRENT
            count = _entriesCounts.AddOrUpdate(e,
                /*do not store first - it is logged out immediately*/
                new Tuple<int, StringBuilder, AsyncLogEventInfo>(0, NeedsStringBuilder(e.LogEvent)
                    ? new StringBuilder()
                    : null, default(AsyncLogEventInfo)),
                (k, v) =>
                {
                    // but store all the others
                    if (NeedsStringBuilder(e.LogEvent))
                    {
                        v.Item2.Append(Escape(e.LogEvent.FormattedMessage));
                        v.Item2.Append(this.GroupByTemplateSeparator);
                    }
                    return new Tuple<int, StringBuilder, AsyncLogEventInfo>(v.Item1 + 1, v.Item2,
                        e /*in flush it will be the last*/);
                });
#else
            lock (_lockObject)
            {
                if (_entriesCounts.TryGetValue(e, out count))
                {
                    if (NeedsStringBuilder(e.LogEvent))
                    {
                        count.Item2.Append(Escape(e.LogEvent.FormattedMessage));
                        count.Item2.Append(this.GroupByTemplateSeparator);
                    }
                    count = new Tuple<int, StringBuilder, AsyncLogEventInfo>(count.Item1 + 1, count.Item2,
                        e/*in flush it will be the last*/);
                }
                else
                    count = new Tuple<int, StringBuilder, AsyncLogEventInfo>(0,
                        NeedsStringBuilder(e.LogEvent)
                            ? new StringBuilder()
                            : null, default(AsyncLogEventInfo));
                _entriesCounts[e] = count;
            }
#endif

            if (count.Item1 == 0)
            {
                e.LogEvent.Properties["IsFirst"] = "true";
                WrappedTarget.WriteAsyncLogEvents(e);
                TurnOnTimerIfOffline();
            }
        }


        /// <summary>
        /// When all messages are the same (no parameters or structural logging used) so StringBuilder is not needed
        /// </summary>
        bool NeedsStringBuilder(LogEventInfo e) =>
            GroupByTemplate && e.Message.Contains("{") &&
            e.Message != "{0}" /*message=="{0}" when logger.Error(exception)*/;


        void TurnOnTimerIfOffline()
        {
            if (!_isTimerOnNow)
            {
                _isTimerOnNow = true;
                _flushTimer.Change(FlushTimeout, Timeout.Infinite);
            }
        }


        void FlushCallback(object _)
        {
            try
            {
                if (_flushTimer == null)
                    return;

                Flush();
            }
            catch (Exception exception)
            {
                InternalLogger.Error(exception, "BufferingWrapper(Name={0}): Error in flush procedure.", Name);
            }
            finally
            {
                _isTimerOnNow = false;
            }
        }


        void Flush()
        {
            if (WrappedTarget == null)
            {
                InternalLogger.Error("BufferingWrapper(Name={0}): WrappedTarget is NULL", Name);
                return;
            }

            lock (_lockObject)
            {
#if USECONCURRENT
                ICollection<AsyncLogEventInfo> keys = _entriesCounts.Keys;
#else
                ICollection<AsyncLogEventInfo> keys = _entriesCounts.Keys.ToList();
#endif
                foreach (AsyncLogEventInfo initialLog in keys)
                {
                    Tuple<int, StringBuilder, AsyncLogEventInfo> count;
#if USECONCURRENT
                    if (_entriesCounts.TryRemove(initialLog, out count) && count.Item1 > 0)
#else
                    count = _entriesCounts[initialLog];
                    if (_entriesCounts.Remove(initialLog))
#endif
                    {
                        AsyncLogEventInfo lastLog = count.Item3;

                        // do not remove if count > 0 (insert it back) - on aggressive logs we should not send an extra log
#if USECONCURRENT
                        _entriesCounts.AddOrUpdate(initialLog,
                            new Tuple<int, StringBuilder, AsyncLogEventInfo>(0, NeedsStringBuilder(initialLog.LogEvent) ? new StringBuilder() : null, default(AsyncLogEventInfo)),
                            (k, v) => v/*do not change it if it is already there - situation is aggressive and first log is already sent*/);
#else
                        lock (_lockObject)
                        {
                            if (!_entriesCounts.ContainsKey(initialLog))
                                _entriesCounts[initialLog] =
                                    new Tuple<int, StringBuilder, AsyncLogEventInfo>(0,
                                        NeedsStringBuilder(initialLog.LogEvent)
                                            ? new StringBuilder()
                                            : null, default(AsyncLogEventInfo));
                        }
#endif


                        if (count.Item1 > 1)
                        {
                            string sbString = null;

                            if (NeedsStringBuilder(lastLog.LogEvent))
                                lastLog.LogEvent.Properties["AccumulatedMessages"] =
                                    sbString = count.Item2.ToString();

                            if (CorrectMessageForGroup && !string.IsNullOrEmpty(CountAppendFormat))
                                if (sbString != null)
                                    lastLog.LogEvent.Message
                                        = /*messages differ so log all of them*/
                                        Escape(lastLog.LogEvent.Message) +
                                        string.Format(CountAppendFormat, count.Item1) +
                                        (this.GroupByTemplateSeparator == Environment.NewLine
                                            ? Environment.NewLine
                                            : "") +
                                        sbString;
                                else
                                    lastLog.LogEvent.Message
                                        += /*all messages are the same, so just append count*/
                                        string.Format(CountAppendFormat, count.Item1);
                        }

                        lastLog.LogEvent.Properties["AccumulatedCount"] = count.Item1;
                        lastLog.LogEvent.Properties["IsFirst"] = "false";
                        WrappedTarget.WriteAsyncLogEvents(lastLog);
                    }
                }
            }
        }


        static string Escape(string s) => s.Replace("{", "{{").Replace("}", "}}");
    }
}
