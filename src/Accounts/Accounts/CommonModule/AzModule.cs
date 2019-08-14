// ----------------------------------------------------------------------------------
//
// Copyright Microsoft Corporation
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// ----------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Management.Automation;
using System.Net.Http;
using System.Collections.Generic;
using Microsoft.WindowsAzure.Commands.Utilities.Common;
using System.Linq;
using System.Collections.Concurrent;
using Microsoft.Azure.Commands.Common.Authentication;
using Microsoft.Azure.Commands.Profile.CommonModule;

namespace Microsoft.Azure.Commands.Common
{

    using GetEventData = Func<EventArgs>;
    using SignalDelegate = Func<string, CancellationToken, Func<EventArgs>, Task>;
    using PipelineChangeDelegate = Action<Func<HttpRequestMessage, CancellationToken, Action, Func<string, CancellationToken, Func<EventArgs>, Task>, Func<HttpRequestMessage, CancellationToken, Action, Func<string, CancellationToken, Func<EventArgs>, Task>, Task<HttpResponseMessage>>, Task<HttpResponseMessage>>>;

    /// <summary>
    /// Cheap and dirty implementation of module functions (does not have to look like this!)
    /// </summary>
    public class AzModule : IDisposable
    {
        IEventStore _deferredEvents;
        ICommandRuntime _runtime;
        TelemetryProvider _telemetry;
        AdalLogger _logger;
        internal static readonly string[] ClientHeaders = {"x-ms-client-request-id", "client-request-id", "x-ms-request-id", "request-id" };
        public AzModule(ICommandRuntime runtime, IEventStore eventHandler)
        {
            _runtime = runtime;
            _deferredEvents = eventHandler;
            _logger = new AdalLogger(_deferredEvents.GetDebugLogger());
            _telemetry = TelemetryProvider.Create(
                _deferredEvents.GetWarningLogger(), _deferredEvents.GetDebugLogger()); 
        }

        public AzModule(ICommandRuntime runtime) : this(runtime, new EventStore())
        {
        }

        public AzModule(ICommandRuntime runtime, IEventStore store, TelemetryProvider provider)
        {
            _deferredEvents = store;
            _runtime = runtime;
            _logger = new AdalLogger(_deferredEvents.GetDebugLogger()); ;
            _telemetry = provider;
        }


        /// <summary>
        /// Called when the module is loading. Allows adding HTTP pipeline steps that will always be present.
        /// </summary>
        /// <param name="resourceId"><c>string</c>containing the expected resource id (ie, ARM).</param>
        /// <param name="moduleName"><c>string</c>containing the name of the module being loaded.</param>
        /// <param name="prependStep">a delegate which allows the module to prepend a step in the HTTP Pipeline</param>
        /// <param name="appendStep">a delegate which allows the module to append a step in the HTTP Pipeline</param>
        public void OnModuleLoad(string resourceId, string moduleName, PipelineChangeDelegate prependStep, PipelineChangeDelegate appendStep)
        {
            // this will be called once when the module starts up 
            // the common module can prepend or append steps to the pipeline at this point.
            prependStep(UniqueId.Instance.SendAsync);
        }

        /// <summary>
        /// The cmdlet will call this for every event during the pipeline. 
        /// </summary>
        /// <param name="id">a <c>string</c> containing the name of the event being raised (well-known events are in <see cref="Microsoft.Azure.Commands.Common.Events"/></param>
        /// <param name="cancellationToken">a <c>CancellationToken</c> indicating if this request is being cancelled.</param>
        /// <param name="getEventData">a delegate to call to get the event data for this event</param>
        /// <param name="signal">a delegate to signal an event from the handler to the cmdlet.</param>
        /// <param name="invocationInfo">The <see cref="System.Management.Automation.InvocationInfo" /> from the cmdlet</param>
        /// <param name="parameterSetName">The <see cref="string" /> containing the name of the parameter set for this invocation (if available></param>
        /// <param name="correlationId">The <see cref="string" /> containing the correlation id for the cmdlet (if available)</param>
        /// <param name="processRecordId">The <see cref="string" /> containing the correlation id for the individual process record. (if available)</param>
        /// <param name="exception">The <see cref="System.Exception" /> that is being thrown (if available)</param>
        public async Task EventListener(string id, CancellationToken cancellationToken, GetEventData getEventData, SignalDelegate signal, InvocationInfo invocationInfo, string parameterSetName, string correlationId, string processRecordId, System.Exception exception)
        {
            /// Drain the queue of ADAL events whenever an event is fired
            DrainDeferredEvents(signal, cancellationToken);
            switch (id)
            {
                case Events.BeforeCall:
                    await OnBeforeCall(id, cancellationToken, getEventData, signal, processRecordId);
                    break;
                case Events.CmdletProcessRecordAsyncStart:
                    await OnProcessRecordAsyncStart(id, cancellationToken, getEventData, signal, processRecordId, invocationInfo, parameterSetName, correlationId);
                    break;
                case Events.CmdletProcessRecordAsyncEnd:
                    await OnProcessRecordAsyncEnd(id, cancellationToken, getEventData, signal, processRecordId);
                    break;
                case Events.CmdletException:
                    await OnCmdletException(id, cancellationToken, getEventData, signal, processRecordId, exception);
                    break;
                case Events.ResponseCreated:
                    await OnResponseCreated(id, cancellationToken, getEventData, signal, processRecordId);
                    break;
                default:
                    getEventData.Print(signal, cancellationToken, Events.Information, id);
                    break;
            }
        }

        internal async Task OnResponseCreated(string id, CancellationToken cancellationToken, GetEventData getEventData, SignalDelegate signal, string processRecordId)
        {
            var data = EventDataConverter.ConvertFrom(getEventData());
            var response = data?.ResponseMessage as HttpResponseMessage;
            if (response != null)
            {
                AzurePSQoSEvent qos;
                if (_telemetry.TryGetValue(processRecordId, out qos) && null != response?.Headers)
                {
                    IEnumerable<string> headerValues;
                    foreach (var headerName in ClientHeaders)
                    {
                        if (response.Headers.TryGetValues(headerName, out headerValues) && headerValues.Any())
                        {
                            qos.ClientRequestId = headerValues.First();
                            break;
                        }
                    }
                }

                /// Print formatted response message
                await signal(Events.Debug, cancellationToken,
                    () => EventHelper.CreateLogEvent(GeneralUtilities.GetLog(response)));
            }
        }

        internal async Task OnCmdletException(string id, CancellationToken cancellationToken, GetEventData getEventData, SignalDelegate signal, string processRecordId, Exception exception)
        {
            var data = EventDataConverter.ConvertFrom(getEventData());
            await signal(Events.Debug, cancellationToken,
                () => EventHelper.CreateLogEvent($"[{id}]: Received Exception with message '{data?.Message}'"));
            AzurePSQoSEvent qos;
            if (_telemetry.TryGetValue(processRecordId, out qos))
            {
                await signal(Events.Debug, cancellationToken,
                    () => EventHelper.CreateLogEvent($"[{id}]: Sending new QosEvent for command '{qos.CommandName}': {qos.ToString()}"));
                qos.IsSuccess = false;
                qos.Exception = exception;
                _telemetry.LogEvent(processRecordId);
            }
        }

        internal async Task OnProcessRecordAsyncStart(string id, CancellationToken cancellationToken, GetEventData getEventData, SignalDelegate signal, string processRecordId, InvocationInfo invocationInfo, string parameterSetName, string correlationId)
        {
            var qos = _telemetry.CreateQosEvent(invocationInfo, parameterSetName, correlationId, processRecordId);
            await signal(Events.Debug, cancellationToken,
                () => EventHelper.CreateLogEvent($"[{id}]: Created new QosEvent for command '{qos?.CommandName}': {qos?.ToString()}"));
        }

        internal async Task OnProcessRecordAsyncEnd(string id, CancellationToken cancellationToken, GetEventData getEventData, SignalDelegate signal, string processRecordId)
        {
            AzurePSQoSEvent qos;
            if (_telemetry.TryGetValue(processRecordId, out qos))
            {
                qos.IsSuccess = qos.Exception == null;
                await signal(Events.Debug, cancellationToken,
                    () => EventHelper.CreateLogEvent($"[{id}]: Sending new QosEvent for command '{qos.CommandName}': {qos.ToString()}"));
                _telemetry.LogEvent(processRecordId);
            }
        }

        internal async Task OnBeforeCall(string id, CancellationToken cancellationToken, GetEventData getEventData, SignalDelegate signal, string processRecordId)
        {
            var data = EventDataConverter.ConvertFrom(getEventData()); // also, we manually use our TypeConverter to return an appropriate type
            var request = data?.RequestMessage as HttpRequestMessage;
            if (request != null)
            {
                AzurePSQoSEvent qos;
                IEnumerable<string> headerValues;
                if (_telemetry.TryGetValue(processRecordId, out qos))
                {
                    foreach (var headerName in ClientHeaders)
                    {
                        if (request.Headers.TryGetValues(headerName, out headerValues) && headerValues.Any())
                        {
                            qos.ClientRequestId = headerValues.First();
                            break;
                        }
                    }
                }

                /// Print formatted request message
                await signal(Events.Debug, cancellationToken,
                    () => EventHelper.CreateLogEvent(GeneralUtilities.GetLog(request)));
            }
        }


        /// <summary>
        /// Free resources associated with this instance
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Free resources associated with this instance - allows customization in extending types
        /// </summary>
        /// <param name="disposing">True if the data should be disposed - differentiates from IDisposable call</param>
        public virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _telemetry?.Flush();
                _telemetry?.Dispose();
                _telemetry = null;
                _logger?.Dispose();
                _logger = null;
                _deferredEvents?.Dispose();
                _deferredEvents = null;
            }
        }


        private async void DrainDeferredEvents(SignalDelegate signal, CancellationToken token)
        {
            EventData data;
            while (_deferredEvents.TryGetEvent(out data) && !token.IsCancellationRequested)
            {
                await signal(data.Id, token, () => data);
            }
        }
    }

}