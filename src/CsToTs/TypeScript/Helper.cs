﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using HandlebarsDotNet;

namespace CsToTs.TypeScript {

    public static class Helper {
        private static readonly BindingFlags BindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        private static readonly Lazy<string> _lazyTemplate = new Lazy<string>(GetDefaultTemplate);
        private static string Template => _lazyTemplate.Value;

        private static bool SkipCheck(string s, TypeScriptOptions o) =>
            s != null && o.SkipTypePatterns.Any(p => Regex.Match(s, p).Success);

        internal static string GenerateTypeScript(IEnumerable<Type> types, TypeScriptOptions options) {
            var context = new TypeScriptContext(options);
            GetTypeScriptDefinitions(types, context);
 
            Handlebars.Configuration.TextEncoder = SimpleEncoder.Instance;

            var generator = Handlebars.Compile(Template);
            return generator(context);
        }

        private static void GetTypeScriptDefinitions(IEnumerable<Type> types, TypeScriptContext context) {
            foreach (var type in types) {
                if (!type.IsEnum) {
                    PopulateTypeDefinition(type, context);
                }
                else {
                    PopulateEnumDefinition(type, context);
                }
            }
        }

        private static TypeDefinition PopulateTypeDefinition(Type type, TypeScriptContext context) {
            if (type.IsGenericParameter) return null;
            var typeCode = Type.GetTypeCode(type);
            if (typeCode != TypeCode.Object) return null;
            if (SkipCheck(type.ToString(), context.Options)) return null;

            if (type.IsConstructedGenericType) {
                type.GetGenericArguments().ToList().ForEach(t => PopulateTypeDefinition(t, context));
                type = type.GetGenericTypeDefinition();
            }

            var existing = context.Types.FirstOrDefault(t => t.ClrType == type);
            if (existing != null) return existing;

            var interfaceRefs = GetInterfaces(type, context);

            var useInterface = context.Options.UseInterfaceForClasses; 
            var isInterface = type.IsInterface || (useInterface != null && useInterface(type));
            var baseTypeRef = string.Empty;
            if (type.IsClass) {
                if (type.BaseType != typeof(object) && PopulateTypeDefinition(type.BaseType, context) != null) {
                    baseTypeRef = GetTypeRef(type.BaseType, context);
                }
                else if (context.Options.DefaultBaseType != null) {
                    baseTypeRef = context.Options.DefaultBaseType(type);
                }
            }

            string declaration, typeName;
            if (!type.IsGenericType) {
                typeName = declaration = ApplyRename(type.Name, context);
            }
            else {
                var genericPrms = type.GetGenericArguments().Select(g => {
                    var constraints = g.GetGenericParameterConstraints()
                        .Where(c => PopulateTypeDefinition(c, context) != null)
                        .Select(c => GetTypeRef(c, context))
                        .ToList();

                    if (g.IsClass
                        && (useInterface == null || !useInterface(type))
                        && g.GenericParameterAttributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint)) {
                        constraints.Add($"{{ new(): {g.Name} }}");
                    }

                    if (constraints.Any()) {
                        return $"{g.Name} extends {string.Join(" & ", constraints)}";
                    }

                    return g.Name;
                });

                typeName = ApplyRename(StripGenericFromName(type.Name), context);
                var genericPrmStr = string.Join(", ", genericPrms);
                declaration = $"{typeName}<{genericPrmStr}>";
            }

            CtorDefinition ctor = null;
            if (isInterface) {
                declaration = $"export interface {declaration}";

                if (!string.IsNullOrEmpty(baseTypeRef)) {
                    interfaceRefs.Insert(0, baseTypeRef);
                }
            }
            else {
                var abs = type.IsAbstract ? " abstract" : string.Empty;
                declaration = $"export{abs} class {declaration}";

                if (!string.IsNullOrEmpty(baseTypeRef)) {
                    declaration = $"{declaration} extends {baseTypeRef}";
                }

                var ctorGenerator = context.Options.CtorGenerator;
                if (ctorGenerator != null) {
                    ctor = ctorGenerator(type);
                }
            }
            
            if (interfaceRefs.Any()) {
                var imp = isInterface ? "extends" : "implements";
                var interfaceRefStr = string.Join(", ", interfaceRefs);
                declaration = $"{declaration} {imp} {interfaceRefStr}";
            }

            var typeDef = new TypeDefinition(type, typeName, declaration, ctor);
            context.Types.Add(typeDef);
            typeDef.Members.AddRange(GetMembers(type, context));
            typeDef.Methods.AddRange(GetMethods(type, context));

            return typeDef;
        }

        private static List<string> GetInterfaces(Type type, TypeScriptContext context) {
            var interfaces = type.GetInterfaces().ToList();
            return interfaces
                .Except(type.BaseType?.GetInterfaces() ?? Enumerable.Empty<Type>())
                .Except(interfaces.SelectMany(i => i.GetInterfaces())) // get only implemented by this type
                .Where(i => PopulateTypeDefinition(i, context) != null)
                .Select(i => GetTypeRef(i, context))
                .ToList();
        }

        private static EnumDefinition PopulateEnumDefinition(Type type, TypeScriptContext context) {
            var existing = context.Enums.FirstOrDefault(t => t.ClrType == type);
            if (existing != null) return existing;

            var members = Enum.GetNames(type)
                .Select(n => new EnumField(n, Convert.ToInt32(Enum.Parse(type, n)).ToString()));

            var def = new EnumDefinition(type, ApplyRename(type.Name, context), members);
            context.Enums.Add(def);
            
            return def;
        }
        
        private static IEnumerable<MemberDefinition> GetMembers(Type type, TypeScriptContext context) {
            var shouldGenerateMember = context.Options.ShouldGenerateMember;
            if (shouldGenerateMember == null) return Enumerable.Empty<MemberDefinition>();
            var memberRenamer = context.Options.MemberRenamer ?? new Func<MemberInfo,string>(x => x.Name);
            var useDecorators = context.Options.UseDecorators ?? new Func<MemberInfo, IEnumerable<string>>(_=> (new List<string>()));


            var memberDefs = type.GetFields(BindingFlags)
                .Select(f => {
                    var fieldType = f.FieldType;
                    var nullable = false;
                    if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(Nullable<>))
                    {
                        // choose the generic parameter, rather than the nullable
                        fieldType = fieldType.GetGenericArguments()[0];
                        nullable = true;
                    }

                    var memberDefinition = new MemberDefinition(memberRenamer(f), GetTypeRef(fieldType, context), nullable, useDecorators(f).ToList());
                    if (shouldGenerateMember(f, memberDefinition))
                    {
                        return memberDefinition;
                    }

                    return null;
                })
                .ToList();

            memberDefs.AddRange(
                type.GetProperties(BindingFlags)
                    .Select(p =>
                    {
                        var propertyType = p.PropertyType;
                        var nullable = false;
                        if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
                        {
                            // choose the generic parameter, rather than the nullable
                            propertyType = propertyType.GetGenericArguments()[0];
                            nullable = true;
                        }
                        
                        var memberDefinition =  new MemberDefinition(memberRenamer(p), GetTypeRef(propertyType, context), nullable,
                            useDecorators(p).ToList());
                        if (shouldGenerateMember(p, memberDefinition))
                        {
                            return memberDefinition;
                        }

                        return null;
                    }).ToList()
            );

            return memberDefs.Where(md => md != null).ToList();
        }

        private static IEnumerable<MethodDefinition> GetMethods(Type type, TypeScriptContext context) {
            var shouldGenerateMethod = context.Options.ShouldGenerateMethod;
            if (shouldGenerateMethod == null) return Enumerable.Empty<MethodDefinition>();

            var useDecorators = context.Options.UseDecorators ?? (_ => new List<string>());
            var memberRenamer = context.Options.MemberRenamer ?? (x => x.Name);

            var retVal = new List<MethodDefinition>();
            var methods = type.GetMethods(BindingFlags).Where(m => !m.IsSpecialName);
            foreach (var method in methods) {
                string declaration;
                if (method.IsGenericMethod) {
                    var methodName = memberRenamer(method);
                    
                    var genericPrms = method.GetGenericArguments().Select(t => GetTypeRef(t, context));
                    declaration = $"{methodName}<{string.Join(", ", genericPrms)}>";
                }
                else {
                    declaration = memberRenamer(method);
                }

                var parameters = method.GetParameters()
                    .Select(p => new MemberDefinition(p.Name, GetTypeRef(p.ParameterType, context)));
                
                var decorators = useDecorators(method);
                    
                var methodDefinition = new MethodDefinition(declaration, parameters, decorators);

                if (shouldGenerateMethod(method, methodDefinition)) {
                    retVal.Add(methodDefinition);
                }
            }

            return retVal;
        }

        private static TypeCode GetTypeCode(Type type)
        {
            if (type == typeof(Guid))
            {
                return TypeCode.String;
            }

            return Type.GetTypeCode(type);
        }

        private static string GetTypeRef(Type type, TypeScriptContext context) {
            if (type.IsGenericParameter)
                return type.Name;

            if (type.IsEnum) {
                var enumDef = PopulateEnumDefinition(type, context);
                return enumDef != null ? enumDef.Name : "any";
            }

            var typeCode = GetTypeCode(type);
            if (typeCode != TypeCode.Object) 
                return GetPrimitiveMemberType(typeCode, context.Options);

            var dictionaryType = ExtractDictionaryType(type, context);
            if (dictionaryType != null) return dictionaryType;
            
            var enumerableType = ExtractEnumerableType(type, context);
            if (enumerableType != null) return enumerableType;

            var typeDef = PopulateTypeDefinition(type, context);
            if (typeDef == null) 
                return "any";

            var typeName = typeDef.Name;
            if (type.IsGenericType) {
                var genericPrms = type.GetGenericArguments().Select(t => GetTypeRef(t, context));
                return $"{typeName}<{string.Join(", ", genericPrms)}>";
            }

            return typeName;
        }

        private static string ExtractDictionaryType(Type type, TypeScriptContext context)
        {
            var dictionaryInterface = type.GetInterfaces().Concat(new[] { type }).FirstOrDefault(t =>
                t.IsConstructedGenericType && t.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            if (dictionaryInterface == null)
            {
                return null;
            }

            var genericArguments = dictionaryInterface.GetGenericArguments();
            var keyType = GetTypeRef(genericArguments[0], context);
            var valueType = GetTypeRef(genericArguments[1], context);
            return $"{{ [key: {keyType}]: {valueType} }}";
        }

        private static string ExtractEnumerableType(Type type, TypeScriptContext context)
        {
            Type enumerableGenericArgument = null;
            if (type.IsInterface && type.IsConstructedGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                enumerableGenericArgument = type.GetGenericArguments().Single();
            }
            else if (type.IsInterface && type == typeof(IEnumerable))
            {
                enumerableGenericArgument = typeof(object);
            }
            else
            {
                enumerableGenericArgument = type.GetInterfaces()
                    .FirstOrDefault(i => i.IsConstructedGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    ?.GetGenericArguments().Single();
            }

            if (enumerableGenericArgument != null)
            {
                return $"Array<{GetTypeRef(enumerableGenericArgument, context)}>";
            }

            return null;
        }

        private static string GetPrimitiveMemberType(TypeCode typeCode, TypeScriptOptions options) {
            switch (typeCode) {
                case TypeCode.Boolean:
                    return "boolean";
                case TypeCode.Byte:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.SByte:
                case TypeCode.Single:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                    return "number";
                case TypeCode.Char:
                case TypeCode.String:
                    return "string";
                case TypeCode.DateTime:
                    return options.UseDateForDateTime ? "Date" : "string";
                default:
                    return "any";
            }
        }
        
        private static string StripGenericFromName(string name) => name.Substring(0, name.IndexOf('`'));

        private static string ApplyRename(string typeName, TypeScriptContext context) {
            var options = context.Options;
            typeName = options.TypeRenamer != null ? options.TypeRenamer(typeName) : typeName;

            var checkName = typeName;
            var i = 1;
            while (context.Types.Any(td => td.Name == checkName)) {
                checkName = $"{typeName}${i++}";
            }
            
            return checkName;
        }
 
        private static string GetDefaultTemplate() {
            var ass = typeof(Generator).Assembly;
            var resourceName = ass.GetManifestResourceNames().First(r => r.Contains("template.handlebars"));
            using (var reader = new StreamReader(ass.GetManifestResourceStream(resourceName), Encoding.UTF8)) {
                return reader.ReadToEnd();
            }
        }
    }
}