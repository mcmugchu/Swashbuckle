using Swashbuckle.Swagger;
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Web;
using System.Web.Http.Description;
using System.ComponentModel.DataAnnotations;
using System.Collections;
using Newtonsoft.Json.Serialization;
using System.Reflection.Emit;
using Microsoft.CSharp;
using System.CodeDom;
using Newtonsoft.Json;
using System.ComponentModel;

namespace Swashbuckle.Swagger.FromUriParams
{
    public class HandleUniversalModelParams : IOperationFilter
    {
        public static bool IsEnumerableType(Type type)
        {
            return type.IsArray || type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type);
        }
        private static bool IsBulitinType(Type type)
        {
            return (type == typeof(object) || Type.GetTypeCode(type) != TypeCode.Object);
        }
        public void Apply(Operation operation, SchemaRegistry schemaRegistry, ApiDescription apiDescription)
        {
            if (operation.parameters == null) return;
            if (apiDescription.HttpMethod == HttpMethod.Get
                || apiDescription.HttpMethod == HttpMethod.Delete
                || apiDescription.HttpMethod == HttpMethod.Head
                )
            {
                HandleQueryString(operation, schemaRegistry, apiDescription);
            }
            else
            {
                HandleJsonParameter(operation, schemaRegistry, apiDescription);
            }

        }

        private static ModuleBuilder createModuleBuilder()
        {
            var assemblyNameStr = Guid.NewGuid().ToString();

            AssemblyName assemblyName = new AssemblyName(assemblyNameStr);
            AssemblyBuilder assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
                    assemblyName,
                    AssemblyBuilderAccess.Run);
            ModuleBuilder moduleBuilder
                = assemblyBuilder.DefineDynamicModule(assemblyNameStr);
            return moduleBuilder;
        }

        private static readonly ModuleBuilder moduleBuilder = createModuleBuilder();

        public void HandleJsonParameter(Operation operation, SchemaRegistry schemaRegistry, ApiDescription apiDescription)
        {
            var pathParams = operation.parameters.Where(param => param.@in == "path");
            var parameterDescriptions = apiDescription.ParameterDescriptions.Where(p => !pathParams.Any(pathP => string.Equals(pathP.name, p.Name, StringComparison.InvariantCultureIgnoreCase)));

            if (parameterDescriptions.Count() > 1)
            {
                var objectParams = Enumerable.Empty<Parameter>();

                var typeName = "TempTypes." + apiDescription.ActionDescriptor.ControllerDescriptor.ControllerType + "." + apiDescription.ActionDescriptor.ActionName + "Request_" + apiDescription.GetHashCode();

                var type = moduleBuilder.GetType(typeName);
                if (type == null)
                {
                    lock (moduleBuilder)
                    {
                        TypeBuilder typeBuilder
                            = moduleBuilder.DefineType(
                               typeName,
                                TypeAttributes.Public
                                | TypeAttributes.Class);
                        var properties = parameterDescriptions.SelectMany(
                            param =>
                            {
                                if (param.ParameterDescriptor == null)
                                    return new Tuple<string, Type>[0];
                                var paramType = param.ParameterDescriptor.ParameterType;
                                if (IsBulitinType(paramType) || IsEnumerableType(paramType))
                                {
                                    return new Tuple<string, Type>[]{
                                     Tuple.Create(param.Name, paramType)
                                  };
                                }
                                else
                                {
                                    return paramType.GetProperties().Select(p =>
                                    {
                                        var pName = p.Name;
                                        var jsonProperty = p.GetCustomAttribute<JsonPropertyAttribute>();
                                        if (jsonProperty != null && !string.IsNullOrWhiteSpace(jsonProperty.PropertyName))
                                        {
                                            pName = jsonProperty.PropertyName;
                                        }
                                        return Tuple.Create(pName, p.PropertyType);
                                    });
                                }

                            }
                            ).GroupBy(p => p.Item1.ToLowerInvariant()).Select(g => g.FirstOrDefault());

                        foreach (var p in properties)
                        {
                            var propertyBuilder = typeBuilder.DefineProperty(
                                    p.Item1,
                                    PropertyAttributes.HasDefault,
                                    p.Item2,
                                    null
                                );


                            // Define field
                            FieldBuilder fieldBuilder = typeBuilder.DefineField("_" + p.Item1, p.Item2, FieldAttributes.Private);
                            // Define "getter" for MyChild property
                            MethodBuilder getterBuilder = typeBuilder.DefineMethod("get_" + p.Item1,
                                                                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                                                                p.Item2,
                                                                Type.EmptyTypes);
                            ILGenerator getterIL = getterBuilder.GetILGenerator();
                            getterIL.Emit(OpCodes.Ldarg_0);
                            getterIL.Emit(OpCodes.Ldfld, fieldBuilder);
                            getterIL.Emit(OpCodes.Ret);

                            // Define "setter" for MyChild property
                            MethodBuilder setterBuilder = typeBuilder.DefineMethod("set_" + p.Item1,
                                                                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                                                                null,
                                                                new Type[] { p.Item2 });
                            ILGenerator setterIL = setterBuilder.GetILGenerator();
                            setterIL.Emit(OpCodes.Ldarg_0);
                            setterIL.Emit(OpCodes.Ldarg_1);
                            setterIL.Emit(OpCodes.Stfld, fieldBuilder);
                            setterIL.Emit(OpCodes.Ret);

                            propertyBuilder.SetGetMethod(getterBuilder);
                            propertyBuilder.SetSetMethod(setterBuilder);
                        }

                        type = typeBuilder.CreateType();

                    }
                }

                var refSchema = schemaRegistry.GetOrRegister(type);
                //var schema = schemaRegistry.Definitions[refSchema.@ref.Replace("#/definitions/", "")];
                objectParams = new Parameter[]{new Parameter(){
                            name="requestBody",
                            @in="body",
                            schema=refSchema,
                            required = true
                            }};

                operation.parameters = pathParams.Concat(objectParams).GroupBy(k => k.name.ToLowerInvariant()).Select(s => s.FirstOrDefault()).ToList();

            }
        }

        public void HandleQueryString(Operation operation, SchemaRegistry schemaRegistry, ApiDescription apiDescription)
        {
            var pathParams = operation.parameters.Where(param => param.@in == "path");
            var objectParams = Enumerable.Empty<Parameter>();
            objectParams = apiDescription.ParameterDescriptions.SelectMany(
                      param =>
                      {

                          if (param.ParameterDescriptor == null)
                          {
                              return new Parameter[]{new Parameter(){
                            name=param.Name,
                            @in="query",
                            type = "string",
                            description=param.Documentation,
                            required = true
                            }};
                          }
                          else
                          {

                              var paramType = param.ParameterDescriptor.ParameterType;

                              if (paramType.IsEnum)
                              {
                                  var schemaType = schemaRegistry.MapType(paramType);
                                  return new Parameter[]{new Parameter(){
                                    name=param.Name,
                                    @in="query",
                                    type = schemaType.type,
                                    format=schemaType.format,
                                    description=param.Documentation,
                                    required=param.ParameterDescriptor.IsOptional,
                                    @enum=Enum.GetNames(paramType).Cast<object>().ToList()
                                }};
                              }
                              else if (IsBulitinType(paramType))
                              {
                                  var schemaType = schemaRegistry.MapType(paramType);
                                  return new Parameter[]{new Parameter(){
                                    name=param.Name,
                                    @in="query",
                                    type = schemaType.type,
                                    format=schemaType.format,
                                    description=param.Documentation,
                                    required=param.ParameterDescriptor.IsOptional
                                }};
                              }
                              else if (IsEnumerableType(paramType))
                              {
                                  if (paramType.IsArray)
                                  {
                                      paramType = paramType.GetElementType();
                                  }
                                  else
                                  {
                                      paramType = paramType.GetGenericArguments()[0];
                                  }

                                  if (IsBulitinType(paramType))
                                  {
                                      var schemaType = schemaRegistry.MapType(paramType);
                                      return new Parameter[]{new Parameter(){
                                    name=param.Name,
                                    @in="query",
                                    type = "array",
                                    items= new PartialSchema(){
                                        type=schemaType.type,
                                        format=schemaType.format
                                    },
                                    collectionFormat="csv",
                                    description=param.Documentation,
                                    required=param.ParameterDescriptor.IsOptional
                                    }};
                                  }
                              }
                              else
                              {
                                  return paramType
                                                              .GetProperties().Select(p =>
                                                              {

                                                                  var attr = p.GetCustomAttributes().FirstOrDefault(t => t.GetType().Name == "ParameterAttribute");
                                                                  var parameterName = p.Name;
                                                                  if (attr != null)
                                                                  {
                                                                      var attrNameProperty = attr.GetType().GetProperty("Name");
                                                                      if (attrNameProperty != null)
                                                                      {
                                                                          var pName = Convert.ToString(attrNameProperty.GetValue(attr));
                                                                          if (!string.IsNullOrWhiteSpace(pName))
                                                                          {
                                                                              parameterName = pName;
                                                                          }
                                                                      }
                                                                  }
                                                                  var descAttr = p.GetCustomAttribute<DescriptionAttribute>();

                                                                  var schemaType = schemaRegistry.MapType(p.PropertyType);

                                                                  if (p.PropertyType.IsEnum)
                                                                  {
                                                                      return new Parameter()
                                                                      {
                                                                          name = parameterName,
                                                                          @in = "query",
                                                                          type = schemaType.type,
                                                                          format = schemaType.format,
                                                                          description = descAttr != null ? descAttr.Description : null,
                                                                          required = p.IsDefined(typeof(RequiredAttribute)),
                                                                          @enum = Enum.GetNames(p.PropertyType).Cast<object>().ToList()
                                                                      };
                                                                  }
                                                                  else if (IsEnumerableType(p.PropertyType))
                                                                  {
                                                                      return new Parameter()
                                                                      {
                                                                          name = parameterName,
                                                                          @in = "query",
                                                                          type = "array",
                                                                          items = new PartialSchema()
                                                                          {
                                                                              type = schemaType.type,
                                                                              format = schemaType.format
                                                                          },
                                                                          collectionFormat = "csv",
                                                                          description = descAttr != null ? descAttr.Description : null,
                                                                          required = p.IsDefined(typeof(RequiredAttribute))
                                                                      };
                                                                  }
                                                                  else
                                                                  {
                                                                      return new Parameter()
                                                                      {
                                                                          name = parameterName,
                                                                          @in = "query",
                                                                          type = schemaType.type,
                                                                          format = schemaType.format,
                                                                          description = descAttr != null ? descAttr.Description : null,
                                                                          required = p.IsDefined(typeof(RequiredAttribute))
                                                                      };
                                                                  }
                                                              });
                              }
                          }
                          return Enumerable.Empty<Parameter>();
                      }
                      );

            operation.parameters = pathParams.Concat(objectParams).GroupBy(k => k.name.ToLowerInvariant()).Select(s => s.FirstOrDefault()).ToList();

        }
    }
}