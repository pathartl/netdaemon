﻿using JoySoftware.HomeAssistant.Client;
using JoySoftware.HomeAssistant.NetDaemon.Common;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JoySoftware.HomeAssistant.NetDaemon.Daemon
{
    internal interface INetDaemonHost : INetDaemon
    {
        Task Run(string host, short port, bool ssl, string token, CancellationToken cancellationToken);

        Task Stop();
    }

    public class NetDaemonHost : INetDaemonHost
    {
        private readonly List<Task> _eventHandlerTasks = new List<Task>();
        private readonly IHassClient _hassClient;

        private readonly IList<(string pattern, Func<string, EntityState?, EntityState?, Task> action)> _stateActions =
            new List<(string pattern, Func<string, EntityState?, EntityState?, Task> action)>();

        private readonly List<string> _supportedDomainsForTurnOnOff = new List<string>
        {
            "light",
            "switch"
        };

        private Scheduler _scheduler;
        private bool _stopped = false;

        public NetDaemonHost(IHassClient hassClient, ILoggerFactory? loggerFactory = null)
        {
            loggerFactory ??= DefaultLoggerFactory;
            Logger = loggerFactory.CreateLogger<NetDaemonHost>();
            _hassClient = hassClient;
            _scheduler = new Scheduler();
            //Action = new FluentAction(this);
        }

        private static ILoggerFactory DefaultLoggerFactory => LoggerFactory.Create(builder =>
        {
            builder
                .ClearProviders()
                //                .AddFilter("HassClient.HassClient", LogLevel.Debug)
                .AddConsole();
        });

        /// <summary>
        /// </summary>
        /// <remarks>
        ///     Valid patterns are:
        ///     light.thelight   - En entity id
        ///     light           - No dot means a domain
        ///     empty           - All events
        /// </remarks>
        /// <param name="pattern">Event pattern</param>
        /// <param name="action">The action to call when event is missing</param>
        public void ListenState(string pattern,
            Func<string, EntityState?, EntityState?, Task> action)
        {
            _stateActions.Add((pattern, action));
        }

        public async Task TurnOnAsync(string entityId, params (string name, object val)[] attributeNameValuePair)
        {
            // Use default domain "homeassistant" if supported is missing
            var domain = GetDomainFromEntity(entityId);
            // Use it if it is supported else use default "homeassistant" domain
            domain = _supportedDomainsForTurnOnOff.Contains(domain) ? domain : "homeassistant";

            // Convert the value pairs to dynamic type
            var attributes = attributeNameValuePair.ToDynamic();
            // and add the entity id dynamically
            attributes.entity_id = entityId;

            await _hassClient.CallService(domain, "turn_on", attributes);
        }

        public async Task TurnOffAsync(string entityId, params (string name, object val)[] attributeNameValuePair)
        {
            // Get the domain if supported, else domain is homeassistant
            var domain = GetDomainFromEntity(entityId);
            // Use it if it is supported else use default "homeassistant" domain
            domain = _supportedDomainsForTurnOnOff.Contains(domain) ? domain : "homeassistant";

            // Use expando object as all other methods
            var attributes = attributeNameValuePair.ToDynamic();
            // and add the entity id dynamically
            attributes.entity_id = entityId;

            await _hassClient.CallService(domain, "turn_off", attributes);
        }

        public async Task ToggleAsync(string entityId, params (string name, object val)[] attributeNameValuePair)
        {
            // Get the domain if supported, else domain is homeassistant
            var domain = GetDomainFromEntity(entityId);
            // Use it if it is supported else use default "homeassistant" domain
            domain = _supportedDomainsForTurnOnOff.Contains(domain) ? domain : "homeassistant";

            // Use expando object as all other methods
            var attributes = attributeNameValuePair.ToDynamic();
            // and add the entity id dynamically
            attributes.entity_id = entityId;

            await _hassClient.CallService(domain, "toggle", attributes);
        }

        public EntityState? GetState(string entity)
        {
            return _hassClient.States.TryGetValue(entity, out var returnValue)
                ? returnValue.ToDaemonEntityState()
                : null;
        }

        public IEntity Entity(params string[] entityId)
        {
            return new EntityManager(entityId, this);
        }

        public IEntity Entities(Func<IEntityProperties, bool> func)
        {
            var x = State.Where(func);

            return new EntityManager(x.Select(n => n.EntityId).ToArray(), this);
        }

        public ILight Light(params string[] entity)
        {
            var entityList = new List<string>(entity.Length);
            foreach (var e in entity)
                // Add the domain light if missing domain in id
                entityList.Add(!e.Contains('.') ? string.Concat("light.", e) : e);
            return new EntityManager(entityList.ToArray(), this);
        }

        //TODO: Optimize
        public IEnumerable<EntityState> State => _hassClient.States.Values.Select(n => n.ToDaemonEntityState());

        public IScheduler Scheduler => _scheduler;

        //public IAction Action { get; }

        public ILogger Logger { get; }

        /// <summary>
        ///     Runs the Daemon
        /// </summary>
        /// <remarks>
        ///     Connects to Home Assistant and the task completes if canceled or if Home Assistant
        ///     can´t be connected or disconnects.
        /// </remarks>
        /// <param name="host"></param>
        /// <param name="port"></param>
        /// <param name="ssl"></param>
        /// <param name="token"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task Run(string host, short port, bool ssl, string token, CancellationToken cancellationToken)
        {
            if (_hassClient == null)
                throw new NullReferenceException("HassClient cant be null when running daemon, check constructor!");

            // We don´t need to provide cancellation to hass client it has it´s own connect timeout
            var connectResult = await _hassClient.ConnectAsync(host, port, ssl, token, true);

            if (!connectResult)
                return;

            await _hassClient.SubscribeToEvents();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var changedEvent = await _hassClient.ReadEventAsync();
                    if (changedEvent != null)
                    {
                        // Remove all completed Tasks
                        _eventHandlerTasks.RemoveAll(x => x.IsCompleted);

                        _eventHandlerTasks.Add(HandleNewEvent(changedEvent, cancellationToken));
                    }
                    else
                    {
                        // Will only happen when doing unit tests
                        await Task.Delay(1000, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal behaviour do nothing
                await _scheduler.Stop();
                throw;
            }
            catch
            {
                throw;
            }
            await _scheduler.Stop();
            
            
        }

        public async Task Stop()
        {
            if (_hassClient == null)
                throw new NullReferenceException("HassClient cant be null when running daemon, check constructor!");

            if (_stopped)
                return;
            await _scheduler.Stop();
            await _hassClient.CloseAsync();

            _stopped = true;
        }

        public ITime Timer()
        {
            return new Common.TimeManager(this);
        }

        public IEntity Lights(Func<IEntityProperties, bool> func)
        {
            var x = State.Where(func).Where(n => n.EntityId.Contains("light."));

            return new EntityManager(x.Select(n => n.EntityId).ToArray(), this);
        }

        private async Task HandleNewEvent(HassEvent hassEvent, CancellationToken token)
        {
            if (hassEvent.EventType == "state_changed")
            {
                var stateData = (HassStateChangedEventData?)hassEvent.Data;

                if (stateData == null)
                    throw new NullReferenceException("StateData is null!");

                try
                {
                    _hassClient.States[stateData.EntityId] = stateData.NewState!;
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
               
                var tasks = new List<Task>();
                foreach (var (pattern, func) in _stateActions)
                    if (string.IsNullOrEmpty(pattern))
                        tasks.Add(func(stateData.EntityId,
                            stateData.NewState?.ToDaemonEntityState(),
                            stateData.OldState?.ToDaemonEntityState()
                        ));
                    else if (stateData.EntityId.StartsWith(pattern))
                        tasks.Add(func(stateData.EntityId,
                            stateData.NewState?.ToDaemonEntityState(),
                            stateData.OldState?.ToDaemonEntityState()
                        ));
                // No hit

                if (tasks.Count > 0)
                    await tasks.WhenAll(token);
            }
        }

        private string GetDomainFromEntity(string entity)
        {
            var entityParts = entity.Split('.');
            if (entityParts.Length != 2)
                throw new ApplicationException($"entity_id is mal formatted {entity}");

            return entityParts[0];
        }
    }
}