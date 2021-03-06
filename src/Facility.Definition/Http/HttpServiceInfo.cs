﻿using System.Collections.Generic;
using System.Linq;

namespace Facility.Definition.Http
{
	/// <summary>
	/// The HTTP mapping for a service.
	/// </summary>
	public sealed class HttpServiceInfo
	{
		/// <summary>
		/// Creates an HTTP mapping for a service.
		/// </summary>
		public HttpServiceInfo(ServiceInfo serviceInfo)
		{
			Service = serviceInfo;

			foreach (var parameter in serviceInfo.GetHttpParameters())
			{
				if (parameter.Name == "url")
					Url = parameter.Value;
				else
					throw parameter.CreateInvalidHttpParameterException();
			}

			Methods = serviceInfo.Methods.Select(x => new HttpMethodInfo(x, serviceInfo)).ToList();
			ErrorSets = serviceInfo.ErrorSets.Select(x => new HttpErrorSetInfo(x)).ToList();

			var httpAttribute = serviceInfo.Dtos.AsEnumerable<IServiceElementInfo>()
				.Concat(serviceInfo.Dtos.SelectMany(x => x.Fields))
				.Concat(serviceInfo.Enums)
				.Concat(serviceInfo.Enums.SelectMany(x => x.Values))
				.Select(x => x.TryGetHttpAttribute())
				.FirstOrDefault(x => x != null);
			if (httpAttribute != null)
				throw new ServiceDefinitionException("'http' attribute not supported on this element.", httpAttribute.Position);

			var methodsByRoute = Methods.OrderBy(x => x, HttpMethodInfo.ByRouteComparer).ToList();
			for (int index = 1; index < methodsByRoute.Count; index++)
			{
				var left = methodsByRoute[index - 1];
				var right = methodsByRoute[index];
				if (HttpMethodInfo.ByRouteComparer.Compare(left, right) == 0)
					throw new ServiceDefinitionException($"Methods '{left.ServiceMethod.Name}' and '{right.ServiceMethod.Name}' have the same route: {right.Method} {right.Path}", right.ServiceMethod.Position);
			}
		}

		/// <summary>
		/// The service.
		/// </summary>
		public ServiceInfo Service { get; }

		/// <summary>
		/// The URL of the HTTP service.
		/// </summary>
		public string Url { get; }

		/// <summary>
		/// The HTTP mapping for the methods.
		/// </summary>
		public IReadOnlyList<HttpMethodInfo> Methods { get; }

		/// <summary>
		/// The HTTP mapping for the error sets.
		/// </summary>
		public IReadOnlyList<HttpErrorSetInfo> ErrorSets { get; }
	}
}
