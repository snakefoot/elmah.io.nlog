﻿using System;
using System.Collections.Generic;
using Elmah.Io.Client;
using NLog;
using NLog.Common;
using NLog.Config;
using NLog.Targets;
using NLog.Layouts;
using System.Text;
using NLog.MessageTemplates;
using System.Net.Http.Headers;
#if NETSTANDARD
using System.Reflection;
#endif
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Elmah.Io.NLog
{
    /// <summary>
    /// NLog target for storing log messages in elmah.io.
    /// </summary>
    [Target("elmah.io")]
    [Target("elmah-io")]
    [Target("ElmahIo")]
    public class ElmahIoTarget : AsyncTaskTarget
    {
#if NETSTANDARD
        private static readonly string _assemblyVersion = typeof(ElmahIoTarget).GetTypeInfo().Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>().Version;
        private static readonly string _nlogAssemblyVersion = typeof(AsyncTaskTarget).GetTypeInfo().Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>().Version;
#else
        private static readonly string _assemblyVersion = typeof(ElmahIoTarget).Assembly.GetName().Version.ToString();
        private static readonly string _nlogAssemblyVersion = typeof(AsyncTaskTarget).Assembly.GetName().Version.ToString();
#endif

        private IElmahioAPI _client;
        private readonly string DefaultLayout;
        private bool _usingDefaultLayout;
        private Guid _logId;
        private string _apiKey;

        /// <summary>
        /// The API key from the elmah.io UI.
        /// </summary>
        [RequiredParameter]
        public string ApiKey
        {
            get
            {
                return _apiKey;
            }
            set
            {
                var apiKey = RenderLogEvent(value, LogEventInfo.CreateNullEvent());
                _apiKey = apiKey;
            }
        }

        // The following LogId property is declared as a string and not a guid for .NET core to work

        /// <summary>
        /// The id of the log to send messages to.
        /// </summary>
        [RequiredParameter]
        public string LogId
        {
            get
            {
                return _logId != Guid.Empty ? _logId.ToString() : null;
            }
            set
            {
                var logId = RenderLogEvent(value, LogEventInfo.CreateNullEvent());
                _logId = Guid.Parse(logId);
            }
        }

        /// <summary>
        /// Register an action to be called before logging an error. Use the OnMessage action to
        /// decorate error messages with additional information.
        /// </summary>
        public Action<CreateMessage> OnMessage { get; set; }

        /// <summary>
        /// Register an action to be called if communicating with the elmah.io API fails.
        /// You can use this callback to log the error somewhere else in case an error happens.
        /// </summary>
        public Action<CreateMessage, Exception> OnError { get; set; }

        /// <summary>
        /// Register an action to filter log messages. Use this to add client-side ignore
        /// of some error messages. If the filter action returns true, the error is ignored.
        /// </summary>
        public Func<CreateMessage, bool> OnFilter { get; set; }

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

        public IWebProxy WebProxy { get; set; }

        public Layout HostnameLayout { get; set; }

        public Layout CookieLayout { get; set; }

        public Layout FormLayout { get; set; }

        public Layout QueryStringLayout { get; set; }

        public Layout HeadersLayout { get; set; }

        public Layout SourceLayout { get; set; }

        public Layout CategoryLayout { get; set; }

        public Layout ApplicationLayout { get; set; }

        public Layout UserLayout { get; set; }

        public Layout MethodLayout { get; set; }

        public Layout VersionLayout { get; set; }

        public Layout UrlLayout { get; set; }

        public Layout TypeLayout { get; set; }

        public Layout StatusCodeLayout { get; set; }

        public Layout CorrelationIdLayout { get; set; }

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member

        /// <summary>
        /// Create a new target with default values.
        /// </summary>
        public ElmahIoTarget()
        {
            DefaultLayout = Layout?.ToString();
            IncludeEventProperties = true;
            TaskDelayMilliseconds = 250;// Delay to optimize for bulk send (reduce http requests)
            TaskTimeoutSeconds = 150;   // Long timeout to allow http request to complete before starting next task
            RetryCount = 0;             // Skip retry on error / timeout
            BatchSize = 50;             // Avoid too many messages in a single batch (reduce request size)
        }

        /// <summary>
        /// Create a new target with a preconfigured IElmahioAPI client. You mostly want to create
        /// the elmah.io target through either NLog.config or using the default constructor.
        /// </summary>
        public ElmahIoTarget(IElmahioAPI client) : this()
        {
            _client = client;
        }

        /// <inheritdoc/>
        protected override void InitializeTarget()
        {
            _usingDefaultLayout = Layout == null || Layout.ToString() == DefaultLayout;

            TrySetLayout(
                v => HostnameLayout = v,
                ToLayout("event-properties:hostname", "scopeproperty:hostname", "gdc:hostname", "aspnet-request-host", "machinename"),
                ToLayout("event-properties:hostname", "scopeproperty:hostname", "gdc:hostname", "machinename"));
            TrySetLayout(
                v => CookieLayout = v,
                ToLayout("event-properties:cookies", "aspnet-request-cookie:outputFormat=Json"),
                ToLayout("event-properties:cookies"));
            TrySetLayout(
                v => FormLayout = v,
                ToLayout("event-properties:form", "aspnet-request-form:outputFormat=Json"),
                ToLayout("event-properties:form"));
            TrySetLayout(
                v => QueryStringLayout = v,
                ToLayout("event-properties:querystring", "aspnet-request-querystring:outputFormat=Json"),
                ToLayout("event-properties:querystring"));
            TrySetLayout(
                v => HeadersLayout = v,
                ToLayout("event-properties:servervariables", "aspnet-request-headers:outputFormat=Json"),
                ToLayout("event-properties:servervariables"));
            SourceLayout = ToLayout("event-properties:source", "scopeproperty:source", "gdc:source");
            CategoryLayout = ToLayout("event-properties:category", "scopeproperty:category", "gdc:category", "logger");
            ApplicationLayout = ToLayout("event-properties:application", "scopeproperty:application", "gdc:application");
#if NET45
            TrySetLayout(
                v => UserLayout = v,
                ToLayout("event-properties:user", "scopeproperty:user", "gdc:user", "aspnet-user-identity", "identity:authType=false:isAuthenticated=false"),
                ToLayout("event-properties:user", "scopeproperty:user", "gdc:user", "identity:authType=false:isAuthenticated=false"));
#else
            TrySetLayout(
                v => UserLayout = v,
                ToLayout("event-properties:user", "scopeproperty:user", "gdc:user", "aspnet-user-identity", "environment-user"),
                ToLayout("event-properties:user", "scopeproperty:user", "gdc:user", "environment-user"));
#endif
            TrySetLayout(
                v => MethodLayout = v,
                ToLayout("event-properties:method", "scopeproperty:method", "gdc:method", "aspnet-request-method"),
                ToLayout("event-properties:method", "scopeproperty:method", "gdc:method"));
            VersionLayout = ToLayout("event-properties:version", "scopeproperty:version", "gdc:version");
            CorrelationIdLayout = ToLayout("event-properties:correlationid", "scopeproperty:correlationid", "gdc:correlationid");
            TrySetLayout(
                v => UrlLayout = v,
                ToLayout("event-properties:url", "scopeproperty:url", "gdc:url", "aspnet-request-url"),
                ToLayout("event-properties:url", "scopeproperty:url", "gdc:url"));
            TypeLayout = ToLayout("event-properties:type", "scopeproperty:type", "gdc:type");
            TrySetLayout(
                v => StatusCodeLayout = v,
                ToLayout("event-properties:statuscode", "scopeproperty:statuscode", "gdc:statuscode", "aspnet-response-statuscode"),
                ToLayout("event-properties:statuscode", "scopeproperty:statuscode", "gdc:statuscode"));

            base.InitializeTarget();
        }

        private static string ToLayout(params string[] names)
        {
            string layout = "";
            for (var i = names.Length - 1; i >= 0; i--)
            {
                if (i == names.Length - 1)
                {
                    layout = $"${{{names[i]}}}";
                }
                else
                {
                    layout = $"${{{names[i]}:whenEmpty={layout}}}";
                }
            }
            return layout;
        }

        private void TrySetLayout(Action<Layout> layout, string value, string fallback)
        {
            try
            {
                layout(Layout.FromString(value, throwConfigExceptions:true));
            }
            catch (NLogConfigurationException)
            {
                layout(fallback);
            }
        }

        /// <inheritdoc/>
        protected override Task WriteAsyncTask(LogEventInfo logEvent, CancellationToken cancellationToken)
        {
            throw new NotImplementedException(); // Never reached, because of override of IList-handler
        }

        /// <inheritdoc/>
        protected override Task WriteAsyncTask(IList<LogEventInfo> logEvents, CancellationToken cancellationToken)
        {
            if (_client == null)
            {
                var api = ElmahioAPI.Create(ApiKey, new ElmahIoOptions
                {
                    WebProxy = WebProxy,
                    Timeout = TimeSpan.FromSeconds(Math.Min(TaskTimeoutSeconds, 30)),
                    UserAgent = UserAgent(),
                });
                api.Messages.OnMessage += (sender, args) =>
                {
                    OnMessage?.Invoke(args.Message);
                };
                api.Messages.OnMessageFail += (sender, args) =>
                {
                    InternalLogger.Error(args.Error, "ElmahIoTarget(Name={0}): Error - {1}", Name, args.Message);
                    OnError?.Invoke(args.Message, args.Error);
                };
                _client = api;
            }

            IList<CreateMessage> messages = null;
            for (int i = 0; i < logEvents.Count; ++i)
            {
                var logEvent = logEvents[i];
                var title = _usingDefaultLayout ? logEvent.FormattedMessage : Layout.Render(logEvent);

                var message = new CreateMessage
                {
                    Title = title,
                    TitleTemplate = logEvent.Message ?? title,
                    Severity = LevelToSeverity(logEvent.Level),
                    DateTime = logEvent.TimeStamp.ToUniversalTime(),
                    Detail = logEvent.Exception?.ToString(),
                    Data = PropertiesToData(logEvent),
                    Source = Source(logEvent),
                    Hostname = RenderLogEvent(HostnameLayout, logEvent),
                    Application = RenderLogEvent(ApplicationLayout, logEvent),
                    User = RenderLogEvent(UserLayout, logEvent),
                    Method = RenderLogEvent(MethodLayout, logEvent),
                    Version = RenderLogEvent(VersionLayout, logEvent),
                    Url = Url(logEvent),
                    Type = Type(logEvent),
                    StatusCode = StatusCode(logEvent),
                    CorrelationId = RenderLogEvent(CorrelationIdLayout, logEvent),
                    Category = RenderLogEvent(CategoryLayout, logEvent),
                    ServerVariables = RenderItems(logEvent, HeadersLayout),
                    Cookies = RenderItems(logEvent, CookieLayout),
                    Form = RenderItems(logEvent, FormLayout),
                    QueryString = RenderItems(logEvent, QueryStringLayout),
                };

                if (OnFilter != null && OnFilter(message))
                {
                    continue;
                }

                if (logEvents.Count == 1)
                {
                    return _client.Messages.CreateAndNotifyAsync(_logId, message, cancellationToken);
                }

                messages = messages ?? new List<CreateMessage>(logEvents.Count);
                messages.Add(message);
            }

            if (messages?.Count > 0)
            {
                return _client.Messages.CreateBulkAndNotifyAsync(_logId, messages, cancellationToken);
            }

            return Task.FromResult<Message>(null);
        }

        private string Url(LogEventInfo logEvent)
        {
            var url = RenderLogEvent(UrlLayout, logEvent);
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out Uri result)) return null;
            if (result.IsAbsoluteUri) return result.AbsolutePath;
            return result.OriginalString;
        }

        private string Type(LogEventInfo logEvent)
        {
            var type = RenderLogEvent(TypeLayout, logEvent);
            if (!string.IsNullOrWhiteSpace(type)) return type;
            if (logEvent.Exception != null) return logEvent.Exception.GetBaseException().GetType().FullName;
            return null;
        }

        private string Source(LogEventInfo logEvent)
        {
            var source = RenderLogEvent(SourceLayout, logEvent);
            if (!string.IsNullOrWhiteSpace(source)) return source;
            return logEvent.Exception?.GetBaseException().Source;
        }

        private IList<Item> RenderItems(LogEventInfo logEvent, Layout layout)
        {
            var rendered = RenderLogEvent(layout, logEvent);
            if (string.IsNullOrWhiteSpace(rendered)) return new List<Item>();
            var items = new List<Item>();
            if (rendered.StartsWith("[{") && rendered.EndsWith("}]")) // JSON rendered using a NLog ASP.NET layout renderer
            {
                var renderedJson = JsonConvert.DeserializeObject<JArray>(rendered);
                foreach (JObject item in renderedJson)
                {
                    foreach (var property in item)
                    {
                        items.Add(new Item(property.Key, property.Value?.ToString()));
                    }
                }
            }
            else // User sended something with the right name as part of structured logging or custom properties
            {
                foreach (var keyAndValue in rendered.Split(new[] { "\", \"" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var keyAndValueSplit = keyAndValue.Split(new[] { "\"=\"" }, StringSplitOptions.None);
                    if (keyAndValueSplit.Length <= 0) continue;
                    var key = keyAndValueSplit[0]?.TrimStart('\"').TrimEnd('\"');
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    string value = null;
                    if (keyAndValueSplit.Length > 1) value = keyAndValueSplit[1].TrimStart('\"').TrimEnd('\"');
                    items.Add(new Item(key, value));
                }
            }

            return items;
        }

        private int? StatusCode(LogEventInfo logEvent)
        {
            var statusCode = RenderLogEvent(StatusCodeLayout, logEvent);
            if (string.IsNullOrWhiteSpace(statusCode)) return null;
            if (!int.TryParse(statusCode, out int result)) return null;
            return result;
        }

        private List<Item> PropertiesToData(LogEventInfo logEvent)
        {
            var items = new List<Item>();
            if (logEvent.Exception != null)
            {
                items.AddRange(logEvent.Exception.ToDataList());
            }

            if (!ShouldIncludeProperties(logEvent) && ContextProperties.Count == 0) return items;

            var properties = GetAllProperties(logEvent);

            StringBuilder sb = new StringBuilder();
            var valueFormatter = ResolveService<IValueFormatter>();
            foreach (var obj in properties)
            {
                if (obj.Value != null)
                {
                    string text;
                    if (obj.Value is string value)
                    {
                        text = value;
                    }
                    else
                    {
                        sb.Length = 0;  // Reuse StringBuilder
                        valueFormatter.FormatValue(obj.Value, null, CaptureType.Normal, null, sb);
                        text = sb.ToString();
                    }
                    items.Add(new Item { Key = obj.Key, Value = text });
                }
            }

            return items;
        }

        private string LevelToSeverity(LogLevel level)
        {
            if (level == LogLevel.Debug) return nameof(Severity.Debug);
            if (level == LogLevel.Error) return nameof(Severity.Error);
            if (level == LogLevel.Fatal) return nameof(Severity.Fatal);
            if (level == LogLevel.Trace) return nameof(Severity.Verbose);
            if (level == LogLevel.Warn) return nameof(Severity.Warning);
            return nameof(Severity.Information);
        }

        private static string UserAgent()
        {
            return new StringBuilder()
                .Append(new ProductInfoHeaderValue(new ProductHeaderValue("Elmah.Io.NLog", _assemblyVersion)).ToString())
                .Append(" ")
                .Append(new ProductInfoHeaderValue(new ProductHeaderValue("NLog", _nlogAssemblyVersion)).ToString())
                .ToString();
        }
    }
}
