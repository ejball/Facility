using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Facility.Definition.CodeGen;
using Newtonsoft.Json;
using YamlDotNet.Serialization;

namespace Facility.Definition.Swagger
{
	/// <summary>
	/// Parses Swagger (Open API) 2.0.
	/// </summary>
	public sealed class SwaggerParser
	{
		/// <summary>
		/// The service name (defaults to '/info/x-identifier' or '/info/title').
		/// </summary>
		public string ServiceName { get; set; }

		/// <summary>
		/// Parses Swagger (Open API) 2.0 into a service definition.
		/// </summary>
		public ServiceInfo ParseDefinition(NamedText source)
		{
			var position = new NamedTextPosition(source.Name);

			string json = source.Text;
			if (!s_detectJsonRegex.IsMatch(json))
			{
				// convert YAML to JSON
				using (var stringReader = new StringReader(source.Text))
					json = JsonConvert.SerializeObject(new DeserializerBuilder().Build().Deserialize(stringReader), SwaggerUtility.JsonSerializerSettings);
			}

			SwaggerService swaggerService;
			using (var stringReader = new StringReader(json))
			using (var jsonTextReader = new JsonTextReader(stringReader))
				swaggerService = JsonSerializer.Create(SwaggerUtility.JsonSerializerSettings).Deserialize<SwaggerService>(jsonTextReader);

			string name = ServiceName ?? swaggerService.Info?.Identifier ??
				CleanUpName(string.Concat((swaggerService.Info?.Title ?? "").Split().Select(CleanUpName).Select(CodeGenUtility.Capitalize)));
			if (name == null)
				throw new ServiceDefinitionException("Missing service info title.", position);

			var attributes = new List<ServiceAttributeInfo>();

			string version = swaggerService.Info?.Version;
			if (!string.IsNullOrWhiteSpace(version))
				attributes.Add(new ServiceAttributeInfo("info", new[] { new ServiceAttributeParameterInfo("version", version, position) }, position));

			string scheme = GetBestScheme(swaggerService.Schemes);
			string host = swaggerService.Host;
			string basePath = swaggerService.BasePath ?? "";
			if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(scheme))
			{
				string url = new UriBuilder(scheme, host) { Path = basePath }.Uri.AbsoluteUri;
				attributes.Add(new ServiceAttributeInfo("http", new[] { new ServiceAttributeParameterInfo("url", url) }));
			}

			var members = new List<IServiceMemberInfo>();

			foreach (var swaggerPath in swaggerService.Paths.EmptyIfNull())
			{
				var swaggerOperations = swaggerService.ResolveOperations(swaggerPath.Value, position);
				AddServiceMethod(members, "GET", swaggerPath.Key, swaggerOperations.Get, swaggerOperations.Parameters, swaggerService, position);
				AddServiceMethod(members, "POST", swaggerPath.Key, swaggerOperations.Post, swaggerOperations.Parameters, swaggerService, position);
				AddServiceMethod(members, "PUT", swaggerPath.Key, swaggerOperations.Put, swaggerOperations.Parameters, swaggerService, position);
				AddServiceMethod(members, "DELETE", swaggerPath.Key, swaggerOperations.Delete, swaggerOperations.Parameters, swaggerService, position);
				AddServiceMethod(members, "OPTIONS", swaggerPath.Key, swaggerOperations.Options, swaggerOperations.Parameters, swaggerService, position);
				AddServiceMethod(members, "HEAD", swaggerPath.Key, swaggerOperations.Head, swaggerOperations.Parameters, swaggerService, position);
				AddServiceMethod(members, "PATCH", swaggerPath.Key, swaggerOperations.Patch, swaggerOperations.Parameters, swaggerService, position);
			}

			foreach (var swaggerDefinition in swaggerService.Definitions.EmptyIfNull())
			{
				if (swaggerDefinition.Value.Type == SwaggerSchemaType.Object &&
					!members.OfType<ServiceMethodInfo>().Any(x => swaggerDefinition.Key.Equals(x.Name + "Request", StringComparison.OrdinalIgnoreCase)) &&
					!members.OfType<ServiceMethodInfo>().Any(x => swaggerDefinition.Key.Equals(x.Name + "Response", StringComparison.OrdinalIgnoreCase)))
				{
					AddServiceDto(members, swaggerDefinition.Key, swaggerDefinition.Value, swaggerService, position);
				}
			}

			return new ServiceInfo(name, members: members, attributes: attributes,
				summary: swaggerService.Info?.Title,
				remarks: SplitRemarks(swaggerService.Info?.Description),
				position: position);
		}

		private static string GetBestScheme(IReadOnlyList<string> schemes)
		{
			return schemes?.FirstOrDefault(x => x == "https") ?? schemes?.FirstOrDefault(x => x == "http") ?? schemes?.FirstOrDefault();
		}

		private void AddServiceDto(List<IServiceMemberInfo> members, string name, SwaggerSchema schema, SwaggerService swaggerService, NamedTextPosition position)
		{
			var attributes = new List<ServiceAttributeInfo>();

			if (schema.Obsolete.GetValueOrDefault())
				attributes.Add(new ServiceAttributeInfo("obsolete"));

			var fields = new List<ServiceFieldInfo>();

			foreach (var property in schema.Properties.EmptyIfNull())
			{
				var fieldAttributes = new List<ServiceAttributeInfo>();

				if (property.Value.Obsolete.GetValueOrDefault())
					fieldAttributes.Add(new ServiceAttributeInfo("obsolete"));

				string typeName = swaggerService.TryGetFacilityTypeName(property.Value, position);
				if (typeName != null)
				{
					fields.Add(new ServiceFieldInfo(
						CleanUpName(property.Key),
						typeName: typeName,
						attributes: fieldAttributes,
						summary: property.Value.Description,
						position: position));
				}
			}

			members.Add(new ServiceDtoInfo(
				name: name,
				fields: fields,
				attributes: attributes,
				summary: schema.Description,
				remarks: SplitRemarks(schema.Remarks),
				position: position));
		}

		private void AddServiceMethod(IList<IServiceMemberInfo> members, string method, string path, SwaggerOperation swaggerOperation, IReadOnlyList<SwaggerParameter> swaggerOperationsParameters, SwaggerService swaggerService, NamedTextPosition position)
		{
			if (swaggerOperation == null)
				return;

			string name = CleanUpName(swaggerOperation.OperationId);
			if (!ServiceDefinitionUtility.IsValidName(name))
				name = method.ToLowerInvariant() + CleanUpName(string.Concat(path.Split('/').Select(CleanUpName).Select(CodeGenUtility.Capitalize)));

			var httpAttributeValues = new List<ServiceAttributeParameterInfo>
			{
				new ServiceAttributeParameterInfo("method", method),
				new ServiceAttributeParameterInfo("path", path),
			};

			var requestFields = new List<ServiceFieldInfo>();
			foreach (var swaggerParameter in swaggerOperationsParameters.EmptyIfNull().Concat(swaggerOperation.Parameters.EmptyIfNull()))
				AddRequestFields(requestFields, swaggerService.ResolveParameter(swaggerParameter, position), name, method, swaggerService, position);

			var responseFields = new List<ServiceFieldInfo>();
			var swaggerResponsePairs = swaggerOperation.Responses.EmptyIfNull().Where(x => x.Key[0] == '2').ToList();
			foreach (var swaggerResponsePair in swaggerResponsePairs)
			{
				AddResponseFields(responseFields, swaggerResponsePair.Key, swaggerService.ResolveResponse(swaggerResponsePair.Value, position),
					name, httpAttributeValues, swaggerOperation.Responses.Count == 1, swaggerService, position);
			}

			members.Add(new ServiceMethodInfo(
				name: name,
				requestFields: requestFields,
				responseFields: responseFields,
				attributes: new[] { new ServiceAttributeInfo("http", httpAttributeValues) },
				summary: swaggerOperation.Summary,
				remarks: SplitRemarks(swaggerOperation.Description),
				position: position));
		}

		private void AddRequestFields(IList<ServiceFieldInfo> requestFields, SwaggerParameter swaggerParameter, string serviceMethodName, string httpMethod, SwaggerService swaggerService, NamedTextPosition position)
		{
			string kind = swaggerParameter.In;
			if (kind == SwaggerParameterKind.Path || kind == SwaggerParameterKind.Query || kind == SwaggerParameterKind.Header)
			{
				string typeName = swaggerService.TryGetFacilityTypeName(swaggerParameter, position);
				if (typeName != null)
				{
					var attributes = new List<ServiceAttributeInfo>();

					if (swaggerParameter.Obsolete.GetValueOrDefault())
						attributes.Add(new ServiceAttributeInfo("obsolete"));

					if (kind == SwaggerParameterKind.Query)
					{
						var parameters = new List<ServiceAttributeParameterInfo>();
						if (httpMethod != "GET")
							parameters.Add(new ServiceAttributeParameterInfo("from", "query"));
						if (swaggerParameter.Identifier != null && swaggerParameter.Identifier != swaggerParameter.Name)
							parameters.Add(new ServiceAttributeParameterInfo("name", swaggerParameter.Name));
						if (parameters.Count != 0)
							attributes.Add(new ServiceAttributeInfo("http", parameters));
					}
					else if (kind == SwaggerParameterKind.Header)
					{
						attributes.Add(new ServiceAttributeInfo("http",
							new[]
							{
								new ServiceAttributeParameterInfo("from", "header"),
								new ServiceAttributeParameterInfo("name", swaggerParameter.Name),
							}));
					}

					requestFields.Add(new ServiceFieldInfo(
						swaggerParameter.Identifier ?? CleanUpName(swaggerParameter.Name),
						typeName: typeName,
						attributes: attributes,
						summary: swaggerParameter.Description,
						position: position));
				}
			}
			else if (kind == SwaggerParameterKind.Body)
			{
				var bodySchema = swaggerService.ResolveDefinition(swaggerParameter.Schema, position);
				if (bodySchema.Value.Type == SwaggerSchemaType.Object)
				{
					if (bodySchema.Key == null || bodySchema.Key.Equals(serviceMethodName + "Request", StringComparison.OrdinalIgnoreCase))
					{
						foreach (var property in bodySchema.Value.Properties.EmptyIfNull())
						{
							var attributes = new List<ServiceAttributeInfo>();

							if (property.Value.Obsolete.GetValueOrDefault())
								attributes.Add(new ServiceAttributeInfo("obsolete"));

							string typeName = swaggerService.TryGetFacilityTypeName(property.Value, position);
							if (typeName != null)
							{
								requestFields.Add(new ServiceFieldInfo(
									CleanUpName(property.Key),
									typeName: typeName,
									attributes: attributes,
									summary: property.Value.Description,
									position: position));
							}
						}
					}
					else
					{
						requestFields.Add(new ServiceFieldInfo(
							bodySchema.Value.Identifier ?? "body",
							typeName: bodySchema.Key,
							attributes: new[] { new ServiceAttributeInfo("http", new[] { new ServiceAttributeParameterInfo("from", "body", position) }) },
							summary: swaggerParameter.Description,
							position: position));
					}
				}
			}
		}

		private void AddResponseFields(IList<ServiceFieldInfo> responseFields, string statusCode, SwaggerResponse swaggerResponse, string serviceMethodName, IList<ServiceAttributeParameterInfo> httpAttributeValues, bool isOnlyResponse, SwaggerService swaggerService, NamedTextPosition position)
		{
			var bodySchema = default(KeyValuePair<string, SwaggerSchema>);

			if (swaggerResponse.Schema != null)
				bodySchema = swaggerService.ResolveDefinition(swaggerResponse.Schema, position);

			if (bodySchema.Value != null && (bodySchema.Key == null || bodySchema.Key.Equals(serviceMethodName + "Response", StringComparison.OrdinalIgnoreCase)))
			{
				if (!isOnlyResponse || (statusCode != "200" && statusCode != "204"))
					httpAttributeValues.Add(new ServiceAttributeParameterInfo("code", statusCode, position));

				foreach (var property in bodySchema.Value.Properties.EmptyIfNull())
				{
					var attributes = new List<ServiceAttributeInfo>();

					if (property.Value.Obsolete.GetValueOrDefault())
						attributes.Add(new ServiceAttributeInfo("obsolete"));

					string typeName = swaggerService.TryGetFacilityTypeName(property.Value, position);
					if (typeName != null)
					{
						responseFields.Add(new ServiceFieldInfo(
							CleanUpName(property.Key),
							typeName: typeName,
							attributes: attributes,
							summary: property.Value.Description,
							position: position));
					}
				}
			}
			else if (swaggerResponse.Identifier == null && isOnlyResponse && swaggerResponse.Schema == null)
			{
				if (statusCode != "200" && statusCode != "204")
					httpAttributeValues.Add(new ServiceAttributeParameterInfo("code", statusCode, position));
			}
			else
			{
				responseFields.Add(new ServiceFieldInfo(
					swaggerResponse.Identifier ?? CodeGenUtility.Uncapitalize(bodySchema.Key) ?? $"status{statusCode}",
					typeName: bodySchema.Key ?? "boolean",
					attributes: new[]
					{
						new ServiceAttributeInfo("http",
						new[]
						{
							new ServiceAttributeParameterInfo("from", "body", position),
							new ServiceAttributeParameterInfo("code", statusCode, position),
						})
					},
					summary: swaggerResponse.Description,
					position: position));
			}
		}

		private static IReadOnlyList<string> SplitRemarks(string remarks)
		{
			return remarks == null ? null : Regex.Split(remarks, @"\r?\n");
		}

		private static string CleanUpName(string name)
		{
			return name == null ? null : Regex.Replace(name, @"[^a-zA-Z0-9_]", "");
		}

		static readonly Regex s_detectJsonRegex = new Regex(@"^\s*[{/]", RegexOptions.Singleline);
	}
}
