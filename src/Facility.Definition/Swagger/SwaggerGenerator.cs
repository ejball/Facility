﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Facility.Definition.CodeGen;
using Facility.Definition.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace Facility.Definition.Swagger
{
	/// <summary>
	/// Generates a Swagger (Open API 2.0) file for a service definition.
	/// </summary>
	public sealed class SwaggerGenerator : CodeGenerator
	{
		/// <summary>
		/// True to generate YAML.
		/// </summary>
		public bool Yaml { get; set; }

		/// <summary>
		/// Generates a Swagger (Open API 2.0) file for a service definition.
		/// </summary>
		protected override CodeGenOutput GenerateOutputCore(ServiceInfo service)
		{
			var swaggerService = GenerateSwaggerService(service);

			if (Yaml)
			{
				return new CodeGenOutput(CreateNamedText("swagger.yaml", code =>
				{
					var yamlObject = ConvertJTokenToObject(JToken.FromObject(swaggerService, JsonSerializer.Create(SwaggerUtility.JsonSerializerSettings)));
					new SerializerBuilder().DisableAliases().EmitDefaults().WithEventEmitter(x => new OurEventEmitter(x)).Build().Serialize(code.TextWriter, yamlObject);
				}));
			}
			else
			{
				return new CodeGenOutput(CreateNamedText("swagger.json", code =>
				{
					using (var jsonTextWriter = new JsonTextWriter(code.TextWriter) { Formatting = Formatting.Indented, CloseOutput = false })
						JsonSerializer.Create(SwaggerUtility.JsonSerializerSettings).Serialize(jsonTextWriter, swaggerService);
				}));
			}
		}

		private SwaggerService GenerateSwaggerService(ServiceInfo service)
		{
			var httpServiceInfo = new HttpServiceInfo(service);

			var swaggerService = new SwaggerService
			{
				Info = new SwaggerInfo
				{
					Identifier = service.Name,
					Title = GetSummaryOrNull(service) ?? service.Name,
					Description = GetRemarksOrNull(service),
					Version = service.TryGetAttribute("info")?.TryGetParameterValue("version") ?? "0.0.0",
					CodeGen = CodeGenUtility.GetCodeGenComment(GeneratorName),
				}
			};

			string defaultBaseUri = httpServiceInfo.Url;
			if (defaultBaseUri != null)
			{
				var baseUri = new Uri(defaultBaseUri);
				swaggerService.Host = baseUri.Host;
				swaggerService.Schemes = new[] { baseUri.Scheme };
				swaggerService.BasePath = baseUri.PathAndQuery;
			}

			var paths = new SortedDictionary<string, SwaggerOperations>();
			foreach (var httpMethodInfo in httpServiceInfo.Methods)
				AddMethodToPaths(paths, service, httpMethodInfo);
			swaggerService.Paths = new ReadOnlyDictionary<string, SwaggerOperations>(paths);

			var dtoInfos = new Dictionary<string, ServiceDtoInfo>();
			foreach (var httpMethodInfo in httpServiceInfo.Methods)
			{
				if (httpMethodInfo.RequestBodyField != null)
					AddDtos(dtoInfos, GetDtosForType(service.GetFieldType(httpMethodInfo.RequestBodyField.ServiceField)));

				AddDto(dtoInfos, TryCreateMethodRequestBodyDto(httpMethodInfo));

				foreach (var httpResponseInfo in httpMethodInfo.ValidResponses)
				{
					if (httpResponseInfo.BodyField != null)
						AddDtos(dtoInfos, GetDtosForType(service.GetFieldType(httpResponseInfo.BodyField.ServiceField)));

					AddDto(dtoInfos, TryCreateMethodResponseBodyDto(httpMethodInfo, httpResponseInfo));
				}
			}

			while (true)
			{
				int dtoCount = dtoInfos.Count;
				foreach (var field in dtoInfos.Values.SelectMany(x => x.Fields).ToList())
					AddDtos(dtoInfos, GetDtosForType(service.GetFieldType(field)));
				if (dtoCount == dtoInfos.Count)
					break;
			}

			var definitions = new Dictionary<string, SwaggerSchema>();
			foreach (var dtoInfo in dtoInfos.Values)
				definitions[dtoInfo.Name] = GetDtoSchema(service, dtoInfo);
			swaggerService.Definitions = definitions.Count == 0 ? null : definitions;

			return swaggerService;
		}

		private void AddDtos(IDictionary<string, ServiceDtoInfo> dictionary, IEnumerable<ServiceDtoInfo> dtos)
		{
			foreach (var dto in dtos)
				AddDto(dictionary, dto);
		}

		private void AddDto(IDictionary<string, ServiceDtoInfo> dictionary, ServiceDtoInfo dto)
		{
			if (dto != null && !dictionary.ContainsKey(dto.Name))
				dictionary[dto.Name] = dto;
		}

		private static ServiceDtoInfo TryCreateMethodRequestBodyDto(HttpMethodInfo httpMethodInfo)
		{
			if (httpMethodInfo.RequestNormalFields == null || httpMethodInfo.RequestNormalFields.Count == 0)
				return null;

			return new ServiceDtoInfo(name: $"{CodeGenUtility.Capitalize(httpMethodInfo.ServiceMethod.Name)}Request",
				fields: httpMethodInfo.RequestNormalFields.Select(x => x.ServiceField),
				summary: $"The request body for {httpMethodInfo.ServiceMethod.Name}.");
		}

		private static ServiceDtoInfo TryCreateMethodResponseBodyDto(HttpMethodInfo httpMethodInfo, HttpResponseInfo httpResponseInfo)
		{
			if (httpResponseInfo.NormalFields == null || httpResponseInfo.NormalFields.Count == 0)
				return null;

			return new ServiceDtoInfo(name: $"{CodeGenUtility.Capitalize(httpMethodInfo.ServiceMethod.Name)}Response",
				fields: httpResponseInfo.NormalFields.Select(x => x.ServiceField),
				summary: $"The response body for {httpMethodInfo.ServiceMethod.Name}.");
		}

		private IEnumerable<ServiceDtoInfo> GetDtosForType(ServiceTypeInfo type)
		{
			switch (type.Kind)
			{
			case ServiceTypeKind.Error:
				yield return GetErrorDto();
				break;
			case ServiceTypeKind.Dto:
				yield return type.Dto;
				break;
			case ServiceTypeKind.Result:
				yield return GetResultOfDto(type.ValueType);
				break;
			}

			if (type.ValueType != null)
			{
				foreach (var dto in GetDtosForType(type.ValueType))
					yield return dto;
			}
		}

		private static string GetSummaryOrNull(IServiceElementInfo info)
		{
			return info.Summary.Length == 0 ? null : info.Summary;
		}

		private static string GetRemarksOrNull(IServiceMemberInfo info)
		{
			return info.Remarks.Count == 0 ? null : string.Join("\n", info.Remarks);
		}

		private static bool? GetObsoleteOrNull(IServiceElementInfo info)
		{
			return info.IsObsolete() ? true : default(bool?);
		}

		private static ServiceDtoInfo GetErrorDto()
		{
			return new ServiceDtoInfo(name: "Error",
				fields: new[]
				{
					new ServiceFieldInfo(name: "code", typeName: "string", summary: "The error code."),
					new ServiceFieldInfo(name: "message", typeName: "string", summary: "The error message."),
					new ServiceFieldInfo(name: "details", typeName: "object", summary: "Advanced error details."),
					new ServiceFieldInfo(name: "innerError", typeName: "error", summary: "The inner error."),
				},
				summary: "An error.");
		}

		private static ServiceDtoInfo GetResultOfDto(ServiceTypeInfo valueType)
		{
			if (valueType.Kind != ServiceTypeKind.Dto)
				throw new InvalidOperationException("Non-DTO result not supported.");

			return new ServiceDtoInfo(name: $"{valueType.Dto.Name}Result",
				fields: new[]
				{
					new ServiceFieldInfo(name: "value", typeName: valueType.ToString(), summary: "The value."),
					new ServiceFieldInfo(name: "error", typeName: "error", summary: "The error."),
				},
				summary: "A result value or error.");
		}

		private static void AddMethodToPaths(IDictionary<string, SwaggerOperations> paths, ServiceInfo service, HttpMethodInfo httpMethodInfo)
		{
			var methodInfo = httpMethodInfo.ServiceMethod;

			SwaggerOperations operations;
			if (!paths.TryGetValue(httpMethodInfo.Path, out operations))
				paths[httpMethodInfo.Path] = operations = new SwaggerOperations();

			var operation = new SwaggerOperation
			{
				Summary = GetSummaryOrNull(methodInfo),
				Description = GetRemarksOrNull(methodInfo),
				OperationId = methodInfo.Name,
			};

			if (httpMethodInfo.RequestNormalFields.Count != 0 || httpMethodInfo.RequestBodyField != null)
				operation.Consumes = new[] { "application/json" };
			if (httpMethodInfo.ValidResponses.Any(x => (x.NormalFields != null && x.NormalFields.Count != 0) || (x.BodyField != null && service.GetFieldType(x.BodyField.ServiceField).Kind == ServiceTypeKind.Dto)))
				operation.Produces = new[] { "application/json" };

			var parameters = new List<SwaggerParameter>();

			foreach (var httpPathInfo in httpMethodInfo.PathFields)
				parameters.Add(CreateSwaggerParameter(service, httpPathInfo.ServiceField, SwaggerParameterKind.Path, httpPathInfo.ServiceField.Name));

			foreach (var httpQueryInfo in httpMethodInfo.QueryFields)
				parameters.Add(CreateSwaggerParameter(service, httpQueryInfo.ServiceField, SwaggerParameterKind.Query, httpQueryInfo.Name));

			foreach (var httpHeaderInfo in httpMethodInfo.RequestHeaderFields)
				parameters.Add(CreateSwaggerParameter(service, httpHeaderInfo.ServiceField, SwaggerParameterKind.Header, httpHeaderInfo.Name));

			var requestBodyFieldType = httpMethodInfo.RequestBodyField == null ? null : service.GetFieldType(httpMethodInfo.RequestBodyField.ServiceField);
			if (requestBodyFieldType != null && requestBodyFieldType.Kind == ServiceTypeKind.Dto)
				parameters.Add(CreateSwaggerRequestBodyParameter(requestBodyFieldType.Dto, "request", httpMethodInfo.RequestBodyField.ServiceField.Summary));
			else if (httpMethodInfo.RequestNormalFields.Count != 0)
				parameters.Add(CreateSwaggerRequestBodyParameter(TryCreateMethodRequestBodyDto(httpMethodInfo), "request"));

			operation.Parameters = parameters;

			var responses = new Dictionary<string, SwaggerResponse>();

			foreach (var validResponse in httpMethodInfo.ValidResponses)
			{
				string statusCodeString = ((int) validResponse.StatusCode).ToString(CultureInfo.InvariantCulture);

				var bodyField = validResponse.BodyField;
				var bodyFieldType = bodyField == null ? null : service.GetFieldType(bodyField.ServiceField);
				if (bodyField != null)
					responses[statusCodeString] = CreateSwaggerResponse(bodyFieldType.Dto, bodyField.ServiceField.Name, bodyField.ServiceField.Summary);
				else if (validResponse.NormalFields != null && validResponse.NormalFields.Count != 0)
					responses[statusCodeString] = CreateSwaggerResponse(TryCreateMethodResponseBodyDto(httpMethodInfo, validResponse));
				else
					responses[statusCodeString] = CreateSwaggerResponse();
			}

			operation.Responses = responses;

			string httpMethod = httpMethodInfo.Method.ToString().ToLowerInvariant();
			switch (httpMethod)
			{
			case "get":
				operations.Get = operation;
				break;
			case "post":
				operations.Post = operation;
				break;
			case "put":
				operations.Put = operation;
				break;
			case "delete":
				operations.Delete = operation;
				break;
			case "options":
				operations.Options = operation;
				break;
			case "head":
				operations.Head = operation;
				break;
			case "patch":
				operations.Patch = operation;
				break;
			default:
				throw new InvalidOperationException("Unexpected HTTP method: " + httpMethod);
			}
		}

		private static SwaggerParameter CreateSwaggerParameter(ServiceInfo service, ServiceFieldInfo fieldInfo, string inKind, string name)
		{
			var parameterObject = GetTypeSchema<SwaggerParameter>(service.GetFieldType(fieldInfo));
			parameterObject.In = inKind;
			parameterObject.Name = name ?? fieldInfo.Name;
			if (parameterObject.Name != fieldInfo.Name)
				parameterObject.Identifier = fieldInfo.Name;
			parameterObject.Description = GetSummaryOrNull(fieldInfo);
			parameterObject.Required = inKind == SwaggerParameterKind.Path;
			parameterObject.Obsolete = GetObsoleteOrNull(fieldInfo);
			return parameterObject;
		}

		private static SwaggerParameter CreateSwaggerRequestBodyParameter(ServiceDtoInfo dto, string name, string description = null)
		{
			return new SwaggerParameter
			{
				In = SwaggerParameterKind.Body,
				Name = name,
				Description = description,
				Required = true,
				Schema = GetDtoSchemaRef<SwaggerSchema>(dto),
			};
		}

		private static SwaggerResponse CreateSwaggerResponse(ServiceDtoInfo dto = null, string identifier = null, string description = null)
		{
			return new SwaggerResponse
			{
				Description = description,
				Schema = dto != null ? GetDtoSchemaRef<SwaggerSchema>(dto) : null,
				Identifier = identifier,
			};
		}

		private static T GetTypeSchema<T>(ServiceTypeInfo type) where T : ISwaggerSchema, new()
		{
			switch (type.Kind)
			{
			case ServiceTypeKind.String:
				return new T { Type = SwaggerSchemaType.String };
			case ServiceTypeKind.Boolean:
				return new T { Type = SwaggerSchemaType.Boolean };
			case ServiceTypeKind.Double:
				return new T { Type = SwaggerSchemaType.Number, Format = SwaggerSchemaTypeFormat.Double };
			case ServiceTypeKind.Int32:
				return new T { Type = SwaggerSchemaType.Integer, Format = SwaggerSchemaTypeFormat.Int32 };
			case ServiceTypeKind.Int64:
				return new T { Type = SwaggerSchemaType.Integer, Format = SwaggerSchemaTypeFormat.Int64 };
			case ServiceTypeKind.Bytes:
				return new T { Type = SwaggerSchemaType.String, Format = SwaggerSchemaTypeFormat.Byte };
			case ServiceTypeKind.Object:
				return new T { Type = SwaggerSchemaType.Object };
			case ServiceTypeKind.Error:
				return GetErrorSchemaRef<T>();
			case ServiceTypeKind.Dto:
				return GetDtoSchemaRef<T>(type.Dto);
			case ServiceTypeKind.Enum:
				return GetEnumSchema<T>(type.Enum);
			case ServiceTypeKind.Result:
				return GetResultOfSchemaRef<T>(type.ValueType);
			case ServiceTypeKind.Array:
				return GetArrayOfSchema<T>(type.ValueType);
			case ServiceTypeKind.Map:
				return (T) (object) GetMapOfSchema(type.ValueType);
			default:
				throw new InvalidOperationException("Unexpected field type kind: " + type.Kind);
			}
		}

		private static SwaggerSchema GetDtoSchema(ServiceInfo serviceInfo, ServiceDtoInfo dtoInfo)
		{
			var propertiesObject = new Dictionary<string, SwaggerSchema>();

			foreach (var fieldInfo in dtoInfo.Fields)
			{
				SwaggerSchema propertyObject = GetTypeSchema<SwaggerSchema>(serviceInfo.GetFieldType(fieldInfo));
				propertyObject.Description = GetSummaryOrNull(fieldInfo);
				propertyObject.Obsolete = GetObsoleteOrNull(fieldInfo);
				propertiesObject[fieldInfo.Name] = propertyObject;
			}

			return new SwaggerSchema
			{
				Type = SwaggerSchemaType.Object,
				Description = GetSummaryOrNull(dtoInfo),
				Properties = propertiesObject,
				Obsolete = GetObsoleteOrNull(dtoInfo),
				Remarks = GetRemarksOrNull(dtoInfo),
			};
		}

		private static T GetDtoSchemaRef<T>(ServiceDtoInfo dtoInfo) where T : ISwaggerSchema, new()
		{
			return new T
			{
				Ref = "#/definitions/" + dtoInfo.Name,
			};
		}

		private static T GetEnumSchema<T>(ServiceEnumInfo enumInfo) where T : ISwaggerSchema, new()
		{
			return new T
			{
				Type = SwaggerSchemaType.String,
				Enum = enumInfo.Values.Select(x => (JToken) x.Name).ToList(),
			};
		}

		private static T GetErrorSchemaRef<T>() where T : ISwaggerSchema, new()
		{
			return new T
			{
				Ref = "#/definitions/Error",
			};
		}

		private static T GetResultOfSchemaRef<T>(ServiceTypeInfo type) where T : ISwaggerSchema, new()
		{
			return new T
			{
				Ref = "#/definitions/" + type.Dto.Name + "Result",
			};
		}

		private static T GetArrayOfSchema<T>(ServiceTypeInfo type) where T : ISwaggerSchema, new()
		{
			return new T
			{
				Type = SwaggerSchemaType.Array,
				Items = GetTypeSchema<SwaggerSchema>(type),
			};
		}

		private static SwaggerSchema GetMapOfSchema(ServiceTypeInfo type)
		{
			return new SwaggerSchema
			{
				Type = SwaggerSchemaType.Object,
				AdditionalProperties = GetTypeSchema<SwaggerSchema>(type),
			};
		}

		private static object ConvertJTokenToObject(JToken token)
		{
			if (token is JValue)
				return ((JValue) token).Value;
			if (token is JArray)
				return token.AsEnumerable().Select(ConvertJTokenToObject).ToList();
			if (token is JObject)
				return token.AsEnumerable().Cast<JProperty>().ToDictionary(x => x.Name, x => ConvertJTokenToObject(x.Value));
			throw new InvalidOperationException("Unexpected token: " + token);
		}

		private class OurEventEmitter : ChainedEventEmitter
		{
			public OurEventEmitter(IEventEmitter nextEmitter)
				: base(nextEmitter)
			{
			}

			public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
			{
				// ensure strings that look like numbers remain strings
				double unused;
				if (eventInfo.Source.Type == typeof(string) && eventInfo.Style == ScalarStyle.Any && double.TryParse((string) eventInfo.Source.Value, out unused))
					eventInfo.Style = ScalarStyle.SingleQuoted;

				base.Emit(eventInfo, emitter);
			}
		}
	}
}
