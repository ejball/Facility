﻿using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Facility.Definition.Swagger
{
	/// <summary>
	/// Helpers for Swagger (Open API 2.0).
	/// </summary>
	public static class SwaggerUtility
	{
		/// <summary>
		/// JSON serializer settings for Swagger DTOs.
		/// </summary>
		public static readonly JsonSerializerSettings JsonSerializerSettings = new JsonSerializerSettings
		{
			ContractResolver = new CamelCaseExceptDictionaryKeysContractResolver(),
			DateParseHandling = DateParseHandling.None,
			NullValueHandling = NullValueHandling.Ignore,
			MissingMemberHandling = MissingMemberHandling.Ignore,
			MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
		};

		internal static string GetDefinitionNameFromRef(string refValue, NamedTextPosition position)
		{
			const string refPrefix = "#/definitions/";
			if (!refValue.StartsWith(refPrefix, StringComparison.Ordinal))
				throw new ServiceDefinitionException("Definition $ref must start with '#/definitions/'.", position);
			return UnescapeRefPart(refValue.Substring(refPrefix.Length));
		}

		internal static KeyValuePair<string, SwaggerSchema> ResolveDefinition(this SwaggerService swaggerService, SwaggerSchema swaggerDefinition, NamedTextPosition position)
		{
			string name = null;

			if (swaggerDefinition.Ref != null)
			{
				name = GetDefinitionNameFromRef(swaggerDefinition.Ref, position);
				if (!swaggerService.Definitions.TryGetValue(name, out swaggerDefinition))
					throw new ServiceDefinitionException($"Missing definition named '{name}'.", position);
			}

			return new KeyValuePair<string, SwaggerSchema>(name, swaggerDefinition);
		}

		internal static SwaggerOperations ResolveOperations(this SwaggerService swaggerService, SwaggerOperations swaggerOperations, NamedTextPosition position)
		{
			if (swaggerOperations.Ref != null)
			{
				const string refPrefix = "#/paths/";
				if (!swaggerOperations.Ref.StartsWith(refPrefix, StringComparison.Ordinal))
					throw new ServiceDefinitionException("Operations $ref must start with '#/paths/'.", position);

				string name = UnescapeRefPart(swaggerOperations.Ref.Substring(refPrefix.Length));
				if (!swaggerService.Paths.TryGetValue(name, out swaggerOperations))
					throw new ServiceDefinitionException($"Missing path named '{name}'.", position);
			}

			return swaggerOperations;
		}

		internal static SwaggerParameter ResolveParameter(this SwaggerService swaggerService, SwaggerParameter swaggerParameter, NamedTextPosition position)
		{
			if (swaggerParameter.Ref != null)
			{
				const string refPrefix = "#/parameters/";
				if (!swaggerParameter.Ref.StartsWith(refPrefix, StringComparison.Ordinal))
					throw new ServiceDefinitionException("Parameter $ref must start with '#/parameters/'.", position);

				string name = UnescapeRefPart(swaggerParameter.Ref.Substring(refPrefix.Length));
				if (!swaggerService.Parameters.TryGetValue(name, out swaggerParameter))
					throw new ServiceDefinitionException($"Missing parameter named '{name}'.", position);
			}

			return swaggerParameter;
		}

		internal static SwaggerResponse ResolveResponse(this SwaggerService swaggerService, SwaggerResponse swaggerResponse, NamedTextPosition position)
		{
			if (swaggerResponse.Ref != null)
			{
				const string refPrefix = "#/responses/";
				if (!swaggerResponse.Ref.StartsWith(refPrefix, StringComparison.Ordinal))
					throw new ServiceDefinitionException("Response $ref must start with '#/responses/'.", position);

				string name = UnescapeRefPart(swaggerResponse.Ref.Substring(refPrefix.Length));
				if (!swaggerService.Responses.TryGetValue(name, out swaggerResponse))
					throw new ServiceDefinitionException($"Missing response named '{name}'.", position);
			}

			return swaggerResponse;
		}

		internal static string TryGetFacilityTypeName(this SwaggerService swaggerService, ISwaggerSchema swaggerSchema, NamedTextPosition position)
		{
			switch (swaggerSchema.Type ?? SwaggerSchemaType.Object)
			{
			case SwaggerSchemaType.String:
				return swaggerSchema.Format == SwaggerSchemaTypeFormat.Byte ? "bytes" : "string";

			case SwaggerSchemaType.Number:
				return "double";

			case SwaggerSchemaType.Integer:
				return swaggerSchema.Format == SwaggerSchemaTypeFormat.Int32 ? "int32" : "int64";

			case SwaggerSchemaType.Boolean:
				return "boolean";

			case SwaggerSchemaType.Array:
				return swaggerSchema.Items?.Type == SwaggerSchemaType.Array ? null :
					$"{swaggerService.TryGetFacilityTypeName(swaggerSchema.Items, position)}[]";

			case SwaggerSchemaType.Object:
				var fullSchema = swaggerSchema as SwaggerSchema;
				if (fullSchema != null)
				{
					if (fullSchema.Ref != null)
					{
						var resolvedSchema = swaggerService.ResolveDefinition(fullSchema, position);

						if (swaggerService.IsFacilityError(resolvedSchema))
							return "error";

						string resultOfType = swaggerService.TryGetFacilityResultOfType(resolvedSchema);
						if (resultOfType != null)
							return $"result<{resultOfType}>";

						return resolvedSchema.Key;
					}

					if (fullSchema.AdditionalProperties != null)
						return $"map<{swaggerService.TryGetFacilityTypeName(fullSchema.AdditionalProperties, position)}>";
				}

				return "object";
			}

			return null;
		}

		internal static bool IsFacilityError(this SwaggerService swaggerService, KeyValuePair<string, SwaggerSchema> swaggerSchema)
		{
			return swaggerSchema.Key == "Error" &&
				swaggerSchema.Value.Properties.EmptyIfNull().Any(x => x.Key == "code" && x.Value.Type == SwaggerSchemaType.String) &&
				swaggerSchema.Value.Properties.EmptyIfNull().Any(x => x.Key == "message" && x.Value.Type == SwaggerSchemaType.String);
		}

		internal static string TryGetFacilityResultOfType(this SwaggerService swaggerService, KeyValuePair<string, SwaggerSchema> swaggerSchema)
		{
			const string nameSuffix = "Result";
			if (!swaggerSchema.Key.EndsWith(nameSuffix, StringComparison.Ordinal))
				return null;

			string typeName = swaggerSchema.Key.Substring(0, swaggerSchema.Key.Length - nameSuffix.Length);
			return swaggerSchema.Value.Properties.EmptyIfNull().Any(x => x.Key == "value" && x.Value.Ref == "#/definitions/" + typeName) &&
				swaggerSchema.Value.Properties.EmptyIfNull().Any(x => x.Key == "error" && x.Value.Ref == "#/definitions/Error") ? typeName : null;
		}

		internal static IReadOnlyList<T> EmptyIfNull<T>(this IReadOnlyList<T> list)
		{
			return list ?? new T[0];
		}

		internal static IReadOnlyDictionary<TKey, TValue> EmptyIfNull<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> list)
		{
			return list ?? new Dictionary<TKey, TValue>();
		}

		private static string UnescapeRefPart(string value)
		{
			return value.Replace("~1", "/").Replace("~0", "~");
		}

		private sealed class CamelCaseExceptDictionaryKeysContractResolver : CamelCasePropertyNamesContractResolver
		{
			protected override JsonDictionaryContract CreateDictionaryContract(Type objectType)
			{
				JsonDictionaryContract contract = base.CreateDictionaryContract(objectType);
				contract.PropertyNameResolver = propertyName => propertyName;
				return contract;
			}
		}
	}
}
