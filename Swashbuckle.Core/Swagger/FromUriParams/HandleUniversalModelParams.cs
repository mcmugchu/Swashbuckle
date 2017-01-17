using Swashbuckle.Swagger;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Http.Description;

namespace Swashbuckle.Swagger.FromUriParams
{
    public class HandleUniversalModelParams : HandleFromUriParams
    {
        protected override void HandleFromUriObjectParams(Operation operation, SchemaRegistry schemaRegistry, ApiDescription apiDescription)
        {
            var fromUriObjectParams = operation.parameters
                .Where(param => (param.@in == "query" || param.@in == "body") && param.type == null)
                .ToArray();

            foreach (var objectParam in fromUriObjectParams)
            {
                var type = apiDescription.ParameterDescriptions
                    .Single(paramDesc => paramDesc.Name == objectParam.name)
                    .ParameterDescriptor.ParameterType;

                var refSchema = schemaRegistry.GetOrRegister(type);
                var schema = schemaRegistry.Definitions[refSchema.@ref.Replace("#/definitions/", "")];

                var qualifier = string.IsNullOrEmpty(objectParam.name) ? "" : (objectParam.name + ".");
                ExtractAndAddQueryParams(schema, qualifier, objectParam.required, schemaRegistry, operation.parameters);
                operation.parameters.Remove(objectParam);
            }

            var fromPathParamNames = operation.parameters.Where(param => (param.@in == "path" && param.name != null)).Select(p => p.name.ToLower()).ToArray();

            //var x = operation.parameters.Where(param => (param.@in == "path" && param.name != null));
            //foreach (var p in x)
            //{
            //    if (p.name.ToLower() == "listingtype" || p.name.ToLowerInvariant() == "type")
            //    {

            //    }
            //}

            var duplicatedParamInPath = operation.parameters.Where(param => (param.@in == "path" && fromPathParamNames.Contains(param.name.ToLower())));
            foreach (var p in duplicatedParamInPath.ToList())
            {
                operation.parameters.Remove(p);
            }

            var duplicatedParamInQuery = operation.parameters.Where(param => (param.@in == "query" && fromPathParamNames.Contains(param.name.ToLower())));
            foreach (var p in duplicatedParamInQuery.ToList())
            {
                p.@in = "path";
            }
        }
    }
}