using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using SignalRSwaggerGen.Attributes;
using SignalRSwaggerGen.Enums;
using SignalRSwaggerGen.Utils;
using SignalRSwaggerGen.Utils.Comparison;
using SignalRSwaggerGen.Utils.XmlComments;

namespace SignalRSwaggerGen
{
    internal sealed class SignalRSwaggerGen : IOpenApiDocumentTransformer
    {
        private static readonly SignalRReturnAttributeComparer _returnAttributeComparer = new SignalRReturnAttributeComparer();
        private readonly SignalRSwaggerGenOptions _options;
        private readonly List<XmlComments> _xmlComments;
        private readonly Dictionary<Type, OpenApiSchema> _schemaCache = new();

        public SignalRSwaggerGen(SignalRSwaggerGenOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (_options.Assemblies.Count == 0) _options.ScanAssembly(Assembly.GetEntryAssembly());
            _xmlComments = new List<XmlComments>();
            LoadXmlComments();
        }

        public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
        {
            var hubs = GetHubs();
            foreach (var hub in hubs)
            {
                var xmlComments = GetXmlComments(hub);
                ProcessHub(document, context, hub, xmlComments);
            }
            return Task.CompletedTask;
        }

        private void ProcessHub(OpenApiDocument swaggerDoc, OpenApiDocumentTransformerContext context, Type hub, XmlComments xmlComments)
        {
            var hubAttribute = hub.GetCustomAttribute<SignalRHubAttribute>();
            if (!HubShouldBeDisplayedOnDocument(context, hubAttribute)) return;
            var hubXml = GetHubXml(hub, xmlComments);
            var hubPath = GetHubPath(hub, hubAttribute);
            var tag = GetTag(hub, hubAttribute, hubXml);
            var methods = GetHubMethods(hub, hubAttribute);
            foreach (var method in methods)
            {
                var methodXml = GetMethodXml(method, xmlComments);
                ProcessMethod(swaggerDoc, context, hub, hubAttribute, hubPath, tag, method, methodXml);
            }
        }

        private void ProcessMethod(
            OpenApiDocument swaggerDoc,
            OpenApiDocumentTransformerContext context,
            Type hub,
            SignalRHubAttribute hubAttribute,
            string hubPath,
            string tag,
            MethodInfo method,
            MemberElement methodXml)
        {
            var methodAttribute = method.GetCustomAttribute<SignalRMethodAttribute>();
            var methodPath = GetMethodPath(hubPath, method, hubAttribute, methodAttribute);
            var methodParams = GetMethodParams(method, hubAttribute, methodAttribute);
            var methodReturnParam = method.ReturnParameter;
            var operation = GetOperation(methodAttribute);
            var summary = GetMethodSummary(hubAttribute, methodAttribute, methodXml);
            var description = methodAttribute?.Description;
            AddOpenApiPath(swaggerDoc, context, hub, hubAttribute, tag, methodPath, operation, summary, description, methodParams, methodReturnParam, method, methodXml);
        }

        private void AddOpenApiPath(
            OpenApiDocument swaggerDoc,
            OpenApiDocumentTransformerContext context,
            Type hub,
            SignalRHubAttribute hubAttribute,
            string tag,
            string methodPath,
            Operation operation,
            string summary,
            string description,
            IEnumerable<ParameterInfo> methodParams,
            ParameterInfo methodReturnParam,
            MethodInfo method,
            MemberElement methodXml)
        {
            swaggerDoc.Paths.Add(
                methodPath,
                new OpenApiPathItem
                {
                    Operations = new Dictionary<OperationType, OpenApiOperation>
                    {
                        {
                            (OperationType)operation,
                            new OpenApiOperation
                            {
                                Summary = summary,
                                Description = description,
                                Tags = new List<OpenApiTag> { new OpenApiTag { Name = tag } },
                                Parameters = ToOpenApiParameters(context, hubAttribute, methodParams, methodXml).ToList(),
                                Responses = ToOpenApiResponses(context, methodReturnParam),
                                RequestBody =  GetOpenApiRequestBody(context, method),
                                Security = GetSecurity(hub, method),
                            }
                        }
                    }
                });
        }

        private static IList<OpenApiSecurityRequirement> GetSecurity(Type hub, MethodInfo method)
        {
            var securityEnabled =
                hub.GetCustomAttribute<AuthorizeAttribute>() != null
                && method.GetCustomAttribute<AllowAnonymousAttribute>() == null
                || method.GetCustomAttribute<AuthorizeAttribute>() != null;

            return securityEnabled
                ? new List<OpenApiSecurityRequirement>
                {
                    new OpenApiSecurityRequirement
                    {
                        {
                            new OpenApiSecurityScheme
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.SecurityScheme,
                                    Id = "basic",
                                }
                            },
                            Array.Empty<string>()
                        }
                    }
                }
                : null;
        }

        private IEnumerable<OpenApiParameter> ToOpenApiParameters(
            OpenApiDocumentTransformerContext context,
            SignalRHubAttribute hubAttribute,
            IEnumerable<ParameterInfo> parameters,
            MemberElement methodXml)
        {
            return parameters.Select(param =>
            {
                var paramXml = methodXml?.Params?.FirstOrDefault(x => x.Name == param.Name);
                var paramAttribute = param.GetCustomAttribute<SignalRParamAttribute>();
                var description = GetParamDescription(hubAttribute, paramAttribute, paramXml);
                var type = paramAttribute?.ParamType ?? param.ParameterType;
                return new OpenApiParameter
                {
                    Name = param.Name,
                    In = ParameterLocation.Query,
                    Description = description,
                    Schema = GetOpenApiSchema(context, type),
                };
            });
        }

        private OpenApiResponses ToOpenApiResponses(OpenApiDocumentTransformerContext context, ParameterInfo returnParam)
        {
            if (returnParam.GetCustomAttribute<SignalRHiddenAttribute>() != null) return null;
            var responses = new OpenApiResponses();
            var returnAttributes = returnParam.GetCustomAttributes<SignalRReturnAttribute>().Distinct(_returnAttributeComparer).ToList();
            if (!returnAttributes.Any()) returnAttributes.Add(new SignalRReturnAttribute());
            foreach (var returnAttribute in returnAttributes)
            {
                var type = returnAttribute.ReturnType ?? returnParam.ParameterType;
                if (!TryGetReturnType(type, out type)) continue;
                var mediaType = new OpenApiMediaType
                {
                    Schema = GetOpenApiSchema(context, type)
                };
                responses.Add(returnAttribute.StatusCode.ToString(), new OpenApiResponse
                {
                    Description = returnAttribute.Description,
                    Content = GetContentByMediaType(mediaType)
                });
            }
            return responses;
        }

        private OpenApiRequestBody GetOpenApiRequestBody(OpenApiDocumentTransformerContext context, MethodInfo method)
        {
            var requestBodyAttribute = method.GetCustomAttribute<SignalRRequestBodyAttribute>();
            if (requestBodyAttribute == null) return null;
            var mediaType = new OpenApiMediaType
            {
                Schema = GetOpenApiSchema(context, requestBodyAttribute.BodyType)
            };
            return new OpenApiRequestBody
            {
                Required = requestBodyAttribute.IsRequired,
                Description = requestBodyAttribute.Description,
                Content = GetContentByMediaType(mediaType)
            };
        }

        static Type jsonContextType = typeof(OpenApiOptions).Assembly.GetType("Microsoft.AspNetCore.OpenApi.OpenApiJsonSchemaContext");
        static PropertyInfo defaultProp = jsonContextType.GetProperty("Default", BindingFlags.Static | BindingFlags.Public);
        static Type openApiJsonSchemaType = typeof(OpenApiOptions).Assembly.GetType("OpenApiJsonSchema");
        static PropertyInfo schemaProp = openApiJsonSchemaType.GetProperty("Schema", BindingFlags.Public | BindingFlags.Instance);

        private OpenApiSchema GetOpenApiSchema(OpenApiDocumentTransformerContext context, Type type)
        {
            if (!_schemaCache.TryGetValue(type, out var schema))
            {
                var jsonOptions = context.ApplicationServices.GetService<IOptions<JsonOptions>>();
                var jsonSerializerOptions = jsonOptions?.Value?.SerializerOptions ?? JsonSerializerOptions.Web;

                try
                {
                    var schemaObject = JsonSchemaExporter.GetJsonSchemaAsNode(jsonSerializerOptions, type, JsonSchemaExporterOptions.Default);
                    var jsonContext = (JsonSerializerContext)defaultProp.GetValue(null);
                    var deserializedSchema = JsonSerializer.Deserialize(schemaObject, openApiJsonSchemaType, jsonContext.Options);
                    schema = (OpenApiSchema)schemaProp.GetValue(deserializedSchema);
                }
                catch (Exception ex)
                {
                    schema = new OpenApiSchema();
                }

                _schemaCache.Add(type, schema);
            }
            return schema;
        }

        private static Dictionary<string, OpenApiMediaType> GetContentByMediaType(OpenApiMediaType mediaType)
        {
            return new Dictionary<string, OpenApiMediaType>
            {
                { "application/json", mediaType },
                { "text/json", mediaType },
                { "text/plain", mediaType },
            };
        }

        private bool HubShouldBeDisplayedOnDocument(OpenApiDocumentTransformerContext context, SignalRHubAttribute hubAttribute)
        {
            var documentNames = hubAttribute.DocumentNames ?? _options.DocumentNames;
            return !documentNames.Any()
                || documentNames.Contains(context.DocumentName);
        }

        private string GetHubPath(Type hub, SignalRHubAttribute hubAttribute)
        {
            var hubName = GetHubName(hub);
            var nameTransformer = hubAttribute.NameTransformer ?? _options.NameTransformer;
            if (nameTransformer != null) hubName = nameTransformer.Transform(hubName);
            if (hubAttribute.Path != null) return hubAttribute.Path.Replace(Constants.HubNamePlaceholder, hubName);
            return _options.HubPathFunc(hubName);
        }

        private static string GetHubName(Type hub)
        {
            var hubName = hub.IsInterface && hub.Name[0] == 'I'
                ? hub.Name.Substring(1)
                : hub.Name;
            return hubName.Split('`')[0];
        }

        private string GetTag(Type hub, SignalRHubAttribute hubAttribute, MemberElement hubXml)
        {
            if (hubAttribute.Tag != null) return hubAttribute.Tag;
            if (!hubAttribute.XmlCommentsDisabled
                && _options.UseHubXmlCommentsSummaryAsTag
                && hubXml?.Summary?.Text != null) return hubXml.Summary.Text;
            return GetHubName(hub);
        }

        private string GetMethodPath(string hubPath, MethodInfo method, SignalRHubAttribute hubAttribute, SignalRMethodAttribute methodAttribute)
        {
            var methodPathSuffix = new string(' ', method.GetParameters().Length);
            var methodName = methodAttribute == null
                ? method.Name
                : methodAttribute.Name.Replace(Constants.MethodNamePlaceholder, method.Name);
            var nameTransformer = hubAttribute.NameTransformer ?? _options.NameTransformer;
            if (nameTransformer != null) methodName = nameTransformer.Transform(methodName);
            return $"{hubPath}/{methodName}{methodPathSuffix}";
        }

        private Operation GetOperation(SignalRMethodAttribute methodAttribute)
        {
            var operation = methodAttribute?.Operation ?? Operation.Inherit;
            if (operation == Operation.Inherit) operation = _options.Operation;
            return operation;
        }

        private IEnumerable<Type> GetHubs()
        {
            return _options.Assemblies
                .SelectMany(a =>
                    a.GetTypes()
                    .Where(t =>
                        t.GetCustomAttribute<SignalRHubAttribute>() != null
                        && t.GetCustomAttribute<SignalRHiddenAttribute>() == null));
        }

        private IEnumerable<MethodInfo> GetHubMethods(Type hub, SignalRHubAttribute hubAttribute)
        {
            var autoDiscover = GetAutoDiscover(hubAttribute);
            IEnumerable<MethodInfo> methods;
            switch (autoDiscover)
            {
                case AutoDiscover.None:
                case AutoDiscover.Params:
                    methods = hub.GetMethods(ReflectionUtils.DeclaredPublicInstance).Where(x => x.GetCustomAttribute<SignalRMethodAttribute>() != null);
                    break;
                case AutoDiscover.Methods:
                case AutoDiscover.MethodsAndParams:
                    methods = hub.GetMethods(ReflectionUtils.DeclaredPublicInstance);
                    break;
                default:
                    throw new NotSupportedException($"Auto-discover option '{autoDiscover}' not supported");
            }
            return methods.Where(x => x.GetCustomAttribute<SignalRHiddenAttribute>() == null);
        }

        private IEnumerable<ParameterInfo> GetMethodParams(MethodInfo method, SignalRHubAttribute hubAttribute, SignalRMethodAttribute methodAttribute)
        {
            var autoDiscover = GetAutoDiscover(hubAttribute, methodAttribute);
            IEnumerable<ParameterInfo> methodParams;
            switch (autoDiscover)
            {
                case AutoDiscover.None:
                case AutoDiscover.Methods:
                    methodParams = method.GetParameters().Where(x => x.GetCustomAttribute<SignalRParamAttribute>() != null);
                    break;
                case AutoDiscover.Params:
                case AutoDiscover.MethodsAndParams:
                    methodParams = method.GetParameters();
                    break;
                default:
                    throw new NotSupportedException($"Auto-discover option '{autoDiscover}' not supported");
            }
            return methodParams.Where(x => x.GetCustomAttribute<SignalRHiddenAttribute>() == null);
        }

        private AutoDiscover GetAutoDiscover(SignalRHubAttribute hubAttribute)
        {
            var autoDiscover = hubAttribute.AutoDiscover;
            if (autoDiscover == AutoDiscover.Inherit) autoDiscover = _options.AutoDiscover;
            return autoDiscover;
        }

        private AutoDiscover GetAutoDiscover(SignalRHubAttribute hubAttribute, SignalRMethodAttribute methodAttribute)
        {
            var autoDiscover = methodAttribute?.AutoDiscover ?? AutoDiscover.Inherit;
            if (autoDiscover == AutoDiscover.Inherit) autoDiscover = hubAttribute.AutoDiscover;
            if (autoDiscover == AutoDiscover.Inherit) autoDiscover = _options.AutoDiscover;
            return autoDiscover;
        }

        private static string GetMethodSummary(SignalRHubAttribute hubAttribute, SignalRMethodAttribute methodAttribute, MemberElement methodXml)
        {
            var summary = methodAttribute?.Summary;
            if (summary != null) return summary;
            if (!hubAttribute.XmlCommentsDisabled) summary = methodXml?.Summary?.Text;
            return summary;
        }

        private static string GetParamDescription(SignalRHubAttribute hubAttribute, SignalRParamAttribute paramAttribute, ParamElement paramXml)
        {
            var description = paramAttribute?.Description;
            if (description != null) return description;
            if (!hubAttribute.XmlCommentsDisabled) description = paramXml?.Text;
            return description;
        }

        private XmlComments GetXmlComments(Type hub)
        {
            return _xmlComments.FirstOrDefault(x => x.Assembly?.Name?.Text == hub.Assembly.GetName().Name);
        }

        private static MemberElement GetHubXml(Type hub, XmlComments xmlComments)
        {
            return xmlComments?.Members?.FirstOrDefault(x => x.Name == hub.GetXmlCommentsName());
        }

        private static MemberElement GetMethodXml(MethodInfo method, XmlComments xmlComments)
        {
            return xmlComments?.Members?.FirstOrDefault(x => x.Name == method.GetXmlCommentsName());
        }

        private static bool TryGetReturnType(Type inType, out Type outType)
        {
            outType = inType;
            if (inType.IsGenericType)
            {
                if (inType.IsGenericTypeDefinition) return false;
                var genericTypeDef = inType.GetGenericTypeDefinition();
                if (genericTypeDef == typeof(Task<>)
                    || genericTypeDef == typeof(ValueTask<>))
                {
                    outType = inType.GetGenericArguments()[0];
                    return true;
                }
            }
            else
            {
                if (inType == typeof(void)
                    || inType == typeof(Task)
                    || inType == typeof(ValueTask)) return false;
            }
            return true;
        }

        private void LoadXmlComments()
        {
            var xmlSerializer = new XmlSerializer(typeof(XmlComments));
            foreach (var path in _options.PathsToXmlCommentsFiles)
            {
                using (var streamReader = new StreamReader(path))
                {
                    var xmlComments = (XmlComments)xmlSerializer.Deserialize(streamReader);
                    _xmlComments.Add(xmlComments);
                }
            }
        }
    }
}