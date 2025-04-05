using System;
using Microsoft.AspNetCore.OpenApi;
using SignalRSwaggerGen;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class SwaggerGenOptionsExtensions
    {
        /// <summary>
        /// Add SignalRSwaggerGen to generate documentation for SignalR hubs
        /// </summary>
        /// <param name="swaggerGenOptions">...</param>
        public static void AddSignalRSwaggerGen(this OpenApiOptions swaggerGenOptions)
        {
            var signalRSwaggerGenOptions = new SignalRSwaggerGenOptions();
            swaggerGenOptions.AddDocumentTransformer(new SignalRSwaggerGen.SignalRSwaggerGen(signalRSwaggerGenOptions));
        }

        /// <summary>
        /// Add SignalRSwaggerGen to generate documentation for SignalR hubs
        /// </summary>
        /// <param name="swaggerGenOptions">...</param>
        /// <param name="action">Action for setting up options for SignalRSwaggerGen</param>
        public static void AddSignalRSwaggerGen(this OpenApiOptions swaggerGenOptions, Action<SignalRSwaggerGenOptions> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var signalRSwaggerGenOptions = new SignalRSwaggerGenOptions();
            action(signalRSwaggerGenOptions);
            swaggerGenOptions.AddDocumentTransformer(new SignalRSwaggerGen.SignalRSwaggerGen(signalRSwaggerGenOptions));
        }
    }
}
