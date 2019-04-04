// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Mvc.Rendering
{
    /// <summary>
    /// Extensions for rendering components.
    /// </summary>
    public static class HtmlHelperComponentPrerenderingExtensions
    {
        /// <summary>
        /// Renders the <typeparamref name="TComponent"/> <see cref="IComponent"/>.
        /// </summary>
        /// <param name="htmlHelper">The <see cref="IHtmlHelper"/>.</param>
        /// <returns>The HTML produced by the rendered <typeparamref name="TComponent"/>.</returns>
        public static Task<IHtmlContent> RenderComponentAsync<TComponent>(this IHtmlHelper htmlHelper) where TComponent : IComponent
        {
            if (htmlHelper == null)
            {
                throw new ArgumentNullException(nameof(htmlHelper));
            }

            return htmlHelper.RenderComponentAsync<TComponent>(null);
        }

        /// <summary>
        /// Renders the <typeparamref name="TComponent"/> <see cref="IComponent"/>.
        /// </summary>
        /// <param name="htmlHelper">The <see cref="IHtmlHelper"/>.</param>
        /// <param name="parameters">An <see cref="object"/> containing the parameters to pass
        /// to the component.</param>
        /// <returns>The HTML produced by the rendered <typeparamref name="TComponent"/>.</returns>
        public static async Task<IHtmlContent> RenderComponentAsync<TComponent>(
            this IHtmlHelper htmlHelper,
            object parameters) where TComponent : IComponent
        {
            if (htmlHelper == null)
            {
                throw new ArgumentNullException(nameof(htmlHelper));
            }

            var httpContext = htmlHelper.ViewContext.HttpContext;
            var serviceProvider = httpContext.RequestServices;
            var prerenderer = serviceProvider.GetService<IComponentPrerenderer>();

            if (prerenderer == null)
            {
                throw new InvalidOperationException($"No '{typeof(IComponentPrerenderer).Name}' implementation has been registered in the dependency injection container. " +
                    $"This typically means a call to 'services.AddRazorComponents()' is missing in 'Startup.ConfigureServices'.");
            }

            var parametersCollection = parameters == null ?
                ParameterCollection.Empty :
                ParameterCollection.FromDictionary(HtmlHelper.ObjectToDictionary(parameters));

            var result = await prerenderer.PrerenderComponentAsync(
                new ComponentPrerenderingContext
                {
                    ComponentType = typeof(TComponent),
                    Parameters = parametersCollection,
                    Context = httpContext
                });

            return new HtmlContentPrerenderComponentResultAdapter(result);
        }
    }
}