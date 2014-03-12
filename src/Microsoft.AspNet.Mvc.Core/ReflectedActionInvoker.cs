﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNet.DependencyInjection;
using Microsoft.AspNet.Mvc.Filters;
using Microsoft.AspNet.Mvc.Internal;
using Microsoft.AspNet.Mvc.ModelBinding;

namespace Microsoft.AspNet.Mvc
{
    public class ReflectedActionInvoker : IActionInvoker
    {
        private readonly ActionContext _actionContext;
        private readonly ReflectedActionDescriptor _descriptor;
        private readonly IActionResultFactory _actionResultFactory;
        private readonly IControllerFactory _controllerFactory;
        private readonly IActionBindingContextProvider _bindingProvider;
        private readonly INestedProviderManager<FilterProviderContext> _filterProvider;

        private readonly List<IAuthorizationFilter> _authorizationFilters = new List<IAuthorizationFilter>();
        private readonly List<IActionFilter> _actionFilters = new List<IActionFilter>();
        private readonly List<IActionResultFilter> _actionResultFilters = new List<IActionResultFilter>();

        public ReflectedActionInvoker(ActionContext actionContext,
                                      ReflectedActionDescriptor descriptor,
                                      IActionResultFactory actionResultFactory,
                                      IControllerFactory controllerFactory,
                                      IActionBindingContextProvider bindingContextProvider,
                                      INestedProviderManager<FilterProviderContext> filterProvider)
        {
            _actionContext = actionContext;
            _descriptor = descriptor;
            _actionResultFactory = actionResultFactory;
            _controllerFactory = controllerFactory;
            _bindingProvider = bindingContextProvider;
            _filterProvider = filterProvider;
        }

        public async Task InvokeActionAsync()
        {
            IActionResult actionResult;
            var filterProviderContext = new FilterProviderContext(_descriptor);
            _filterProvider.Invoke(filterProviderContext);

            PreArrangeFiltersInPipeline(filterProviderContext);

            var modelState = new ModelStateDictionary();
            object controller = _controllerFactory.CreateController(_actionContext, modelState);

            if (controller == null)
            {
                actionResult = new HttpStatusCodeResult(404);
            }
            else
            {
                var method = _descriptor.MethodInfo;

                if (method == null)
                {
                    actionResult = new HttpStatusCodeResult(404);
                }
                else
                {
                    var parameterValues = await GetParameterValues(modelState);

                    if (_authorizationFilters.Count > 0)
                    {
                        var authZEndPoint = new AuthorizationFilterEndPoint();
                        _authorizationFilters.Add(authZEndPoint);

                        var authZContext = new AuthorizationFilterContext(_actionContext);
                        var authZPipeline = new FilterPipelineBuilder<AuthorizationFilterContext>(_authorizationFilters, authZContext);

                        await authZPipeline.InvokeAsync();

                        if (authZContext.ActionResult == null &&
                            !authZContext.HasFailed &&
                            authZEndPoint.EndPointCalled)
                        {
                            actionResult = null;
                        }
                        else
                        {
                            // User cleaned out the result but we failed or short circuited the end point.
                            actionResult = authZContext.ActionResult ?? new HttpStatusCodeResult(401);
                        }
                    }
                    else
                    {
                        actionResult = null;
                    }

                    if (actionResult == null)
                    {
                        var actionFilterContext = new ActionFilterContext(_actionContext,
                                                                          parameterValues,
                                                                          method.ReturnType);

                        // TODO: This is extremely temporary and is going to get soon replaced with the action executer
                        var actionEndPoint = new ReflectedActionFilterEndPoint(async (inArray) => method.Invoke(controller, inArray),
                                                                                _actionResultFactory);

                        _actionFilters.Add(actionEndPoint);

                        var actionFilterPipeline = new FilterPipelineBuilder<ActionFilterContext>(_actionFilters,
                            actionFilterContext);

                        await actionFilterPipeline.InvokeAsync();

                        actionResult = actionFilterContext.Result;
                    }
                }
            }

            var actionResultFilterContext = new ActionResultFilterContext(_actionContext, actionResult);
            var actionResultFilterEndPoint = new ActionResultFilterEndPoint();
            _actionResultFilters.Add(actionResultFilterEndPoint);

            var actionResultPipeline = new FilterPipelineBuilder<ActionResultFilterContext>(_actionResultFilters, actionResultFilterContext);

            await actionResultPipeline.InvokeAsync();
        }

        private async Task<IDictionary<string, object>> GetParameterValues(ModelStateDictionary modelState)
        {
            var actionBindingContext = await _bindingProvider.GetActionBindingContextAsync(_actionContext);
            var parameters = _descriptor.Parameters;

            var parameterValues = new Dictionary<string, object>(parameters.Count, StringComparer.Ordinal);
            for (int i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i];
                if (parameter.BodyParameterInfo != null)
                {
                    var inputFormatterContext = actionBindingContext.CreateInputFormatterContext(
                                                        modelState,
                                                        parameter);
                    await actionBindingContext.InputFormatter.ReadAsync(inputFormatterContext);
                    parameterValues[parameter.Name] = inputFormatterContext.Model;
                }
                else
                {
                    var modelBindingContext = actionBindingContext.CreateModelBindingContext(
                                                        modelState,
                                                        parameter);
                    actionBindingContext.ModelBinder.BindModel(modelBindingContext);
                    parameterValues[parameter.Name] = modelBindingContext.Model;
                }
            }

            return parameterValues;
        }

        private void PreArrangeFiltersInPipeline(FilterProviderContext context)
        {
            if (context.OrderedFilterList == null || context.OrderedFilterList.Count == 0)
            {
                return;
            }

            foreach (var filter in context.OrderedFilterList)
            {
                PlaceFilter(filter);
            }
        }

        private void PlaceFilter(object filter)
        {
            var authFilter = filter as IAuthorizationFilter;
            var actionFilter = filter as IActionFilter;
            var actionResultFilter = filter as IActionResultFilter;

            // TODO: Exception filters

            if (authFilter != null)
            {
                _authorizationFilters.Add(authFilter);
            }

            if (actionFilter != null)
            {
                _actionFilters.Add(actionFilter);
            }

            if (actionResultFilter != null)
            {
                _actionResultFilters.Add(actionResultFilter);
            }
        }
    }
}