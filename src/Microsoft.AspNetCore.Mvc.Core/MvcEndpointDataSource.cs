﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matchers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public class EndpointInfo
    {
        public string Template { get; set; }
        public string Name { get; set; }
        public object Defaults { get; set; }
    }

    public class MvcEndpointDataSourceOptions
    {
        public List<EndpointInfo> Endpoints { get; set; } = new List<EndpointInfo>();
    }

    internal class MvcEndpointDataSource : EndpointDataSource
    {
        private readonly IOptions<MvcEndpointDataSourceOptions> _options;
        private readonly IActionSelector _actionSelector;
        private readonly IActionDescriptorCollectionProvider _actions;
        private readonly IActionInvokerFactory _invokerFactory;
        private readonly IActionDescriptorChangeProvider[] _actionDescriptorChangeProviders;
        private readonly IActionContextAccessor _actionContextAccessor;
        private readonly List<Endpoint> _endpoints;

        private IChangeToken _changeToken;

        public MvcEndpointDataSource(
            IOptions<MvcEndpointDataSourceOptions> options,
            IActionSelector actionSelector,
            IActionDescriptorCollectionProvider actions,
            IActionInvokerFactory invokerFactory,
            IEnumerable<IActionDescriptorChangeProvider> actionDescriptorChangeProviders)
            : this(options, actionSelector, actions, invokerFactory, actionDescriptorChangeProviders, actionContextAccessor: null)
        {
        }

        public MvcEndpointDataSource(
            IOptions<MvcEndpointDataSourceOptions> options,
            IActionSelector actionSelector,
            IActionDescriptorCollectionProvider actions,
            IActionInvokerFactory invokerFactory,
            IEnumerable<IActionDescriptorChangeProvider> actionDescriptorChangeProviders,
            IActionContextAccessor actionContextAccessor)
        {
            if (actions == null)
            {
                throw new ArgumentNullException(nameof(actions));
            }

            if (invokerFactory == null)
            {
                throw new ArgumentNullException(nameof(invokerFactory));
            }

            if (actionDescriptorChangeProviders == null)
            {
                throw new ArgumentNullException(nameof(actionDescriptorChangeProviders));
            }

            _options = options;
            _actionSelector = actionSelector;
            _actions = actions;
            _invokerFactory = invokerFactory;
            _actionDescriptorChangeProviders = actionDescriptorChangeProviders.ToArray();

            // The IActionContextAccessor is optional. We want to avoid the overhead of using CallContext
            // if possible.
            _actionContextAccessor = actionContextAccessor;

            _endpoints = new List<Endpoint>();

            InitializeEndpoints();
        }

        private void InitializeEndpoints()
        {
            // note: this code has haxxx. This will only work in some constrained scenarios
            foreach (var action in _actions.ActionDescriptors.Items)
            {
                if (action.AttributeRouteInfo == null)
                {
                    // Action does not have an attribute route
                    continue;
                }

                RequestDelegate invokerDelegate = (context) =>
                {
                    var values = context.Features.Get<IEndpointFeature>().Values;
                    var routeData = new RouteData();
                    foreach (var kvp in values)
                    {
                        routeData.Values.Add(kvp.Key, kvp.Value);
                    }

                    var actionContext = new ActionContext(context, routeData, action);
                    if (_actionContextAccessor != null)
                    {
                        _actionContextAccessor.ActionContext = actionContext;
                    }

                    var invoker = _invokerFactory.CreateInvoker(actionContext);
                    return invoker.InvokeAsync();
                };

                var descriptors = action.FilterDescriptors.OrderBy(f => f, FilterDescriptorOrderComparer.Comparer).Select(f => f.Filter).ToArray();
                var metadataCollection = new EndpointMetadataCollection(descriptors);

                Address address = null;
                if (action.AttributeRouteInfo != null
                    && !string.IsNullOrEmpty(action.AttributeRouteInfo.Name))
                {
                    address = new Address(action.AttributeRouteInfo.Name);
                }

                _endpoints.Add(new MatcherEndpoint(
                    next => invokerDelegate,
                    action.AttributeRouteInfo.Template,
                    action.RouteValues,
                    action.AttributeRouteInfo.Order,
                    metadataCollection,
                    action.DisplayName,
                    address));
            }

            foreach(var info in _options.Value.Endpoints)
            {
                RequestDelegate invokerDelegate = (context) =>
                {
                    var values = context.Features.Get<IEndpointFeature>().Values;
                    var routeData = new RouteData();
                    foreach (var kvp in values)
                    {
                        routeData.Values.Add(kvp.Key, kvp.Value);
                    }

                    var routeContext = new RouteContext(context);
                    routeContext.RouteData = routeData;
                    var candidates = _actionSelector.SelectCandidates(routeContext);
                    if (candidates == null || candidates.Count == 0)
                    {
                        return Task.CompletedTask;
                    }

                    var actionDescriptor = _actionSelector.SelectBestCandidate(routeContext, candidates);
                    if (actionDescriptor == null)
                    {
                        return Task.CompletedTask;
                    }

                    var actionContext = new ActionContext(context, routeData, actionDescriptor);
                    if (_actionContextAccessor != null)
                    {
                        _actionContextAccessor.ActionContext = actionContext;
                    }

                    var invoker = _invokerFactory.CreateInvoker(actionContext);
                    return invoker.InvokeAsync();
                };

                _endpoints.Add(new MatcherEndpoint(
                    next => invokerDelegate,
                    info.Template,
                    info.Defaults,
                    0,
                    EndpointMetadataCollection.Empty,
                    null,
                    new Address() { Name = info.Name }));
            }
        }

        private IChangeToken GetCompositeChangeToken()
        {
            if (_actionDescriptorChangeProviders.Length == 1)
            {
                return _actionDescriptorChangeProviders[0].GetChangeToken();
            }

            var changeTokens = new IChangeToken[_actionDescriptorChangeProviders.Length];
            for (var i = 0; i < _actionDescriptorChangeProviders.Length; i++)
            {
                changeTokens[i] = _actionDescriptorChangeProviders[i].GetChangeToken();
            }

            return new CompositeChangeToken(changeTokens);
        }

        public override IChangeToken ChangeToken
        {
            get
            {
                if (_changeToken == null)
                {
                    _changeToken = GetCompositeChangeToken();
                }

                return _changeToken;
            }
        }

        public override IReadOnlyList<Endpoint> Endpoints => _endpoints;
    }
}