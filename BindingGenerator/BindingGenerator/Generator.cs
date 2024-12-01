using CodingSeb.ExpressionEvaluator;
using CommandLine;
using CppSharp;
using CppSharp.AST;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace BindingGenerator
{
    public class Generator
    {
        private static Dictionary<string, Declaration> declarations = new Dictionary<string, Declaration>();
        private static Dictionary<string, Class> classes = new Dictionary<string, Class>();
        private static Dictionary<string, Enumeration> enumerations = new Dictionary<string, Enumeration>();
        private static List<MacroDefinition> preprocessedEntities = new List<MacroDefinition>();

        private static List<string> registeredTypes = new List<string>();

        public static void Generate(
            string[] includeDirs,
            LibData[] libs,
            string outputDir,
            string _namespace,
            bool forceClearOutputDirectory = true,
            bool noBuiltinIncludes = false,
            bool noStandardIncludes = false,
            List<string>? forceTypesToGeneration = default,
            Dictionary<string, string>? notFoundTypesOverrides = default,
            List<EnumSearchParameter>? preprocessedConstantSearchParameters = default,
            Dictionary<PrimitiveType, string>? primitiveTypesToCsTypesMap = default,
            Dictionary<string, TypedefStrategy>? typedefStrategies = default,
            Dictionary<string, string>? fieldParametersTypeOverrides = default,
            List<string>? anonymousEnumPrefixes = default,
            ILogger<Generator>? logger = default)
        {
            var primitiveTypesMap = primitiveTypesToCsTypesMap == null ? Utils.PrimitiveTypesToCsTypesMap.ToDictionary() : primitiveTypesToCsTypesMap;

            var parserOptions = new CppSharp.Parser.ParserOptions();

            foreach (var includeDir in includeDirs)
                parserOptions.AddIncludeDirs(includeDir);
            parserOptions.Setup(TargetPlatform.Windows);
            parserOptions.LanguageVersion = CppSharp.Parser.LanguageVersion.CPP17_GNU;
            parserOptions.NoBuiltinIncludes = noBuiltinIncludes;
            parserOptions.NoStandardIncludes = noStandardIncludes;
            var parseResult = ClangParser.ParseSourceFiles(
                libs.Select(t => t.FuncsHeaderPath), parserOptions);

            for (uint i = 0; i != parseResult.DiagnosticsCount; i++)
            {
                var diagnostic = parseResult.GetDiagnostics(i);
                logger?.LogWarning("fileName: {fileName}, line: {line}, column: {column}, message: {message}", diagnostic.FileName, diagnostic.LineNumber, diagnostic.ColumnNumber, diagnostic.Message);
            }

            if (parseResult.Kind != CppSharp.Parser.ParserResultKind.Success)
                throw new Exception(parseResult.Kind.ToString());

            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            var outputDirFiles = Directory.GetFiles(outputDir);

            if (outputDirFiles.Count() > 0 && !forceClearOutputDirectory)
                throw new Exception("В outputDir есть файлы");

            foreach (var file in outputDirFiles.Where(t => t.EndsWith(".cs")))
                File.Delete(file);

            var context = ClangParser.ConvertASTContext(parserOptions.ASTContext);


            var namespaceNameSyntax = SyntaxFactory.IdentifierName(_namespace);

            var namespaceDeclarationSyntax = SyntaxFactory.NamespaceDeclaration(
                namespaceNameSyntax,
                default,
                default,
                default);


            var usings = SyntaxFactory.List(
                new[] {
                    SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Runtime.InteropServices"))
                }
            );

            InitTypes(context, preprocessedConstantSearchParameters, anonymousEnumPrefixes, logger: logger);

            foreach (var lib in libs)
            {
                var translationUnit = context.TranslationUnits.FirstOrDefault(t => lib.FuncsHeaderPath.Contains(t.FileName));
                if (translationUnit == null)
                {
                    throw new Exception($"Не найден translation unit для {lib.FuncsHeaderPath}");
                }

                var evaluator = new ExpressionEvaluator(context);
                var properties = preprocessedEntities.Select(defenition =>
                {
                    var expressionResult = evaluator.Evaluate(defenition.Expression);
                    var type = expressionResult.GetType();
                    var typeName = type.FullName;
                    if (typeName == typeof(long).FullName)
                        typeName = "CLong";
                    if (typeName == typeof(ulong).FullName)
                        typeName = "CULong";

                    return (typeNameSyntax: SyntaxFactory.ParseTypeName(typeName), typeName: typeName, propertyName: defenition.Name, value: expressionResult);

                }).ToArray();

                var methodsDeclarations = translationUnit.Functions.Select(_func =>
                {
                    var parameterList = SyntaxFactory.ParameterList(
                        SyntaxFactory.SeparatedList(
                            _func.Parameters.Select(parameter =>
                            {
                                TypeSyntax paramType;
                                if (fieldParametersTypeOverrides != null && fieldParametersTypeOverrides.ContainsKey(parameter.Name))
                                {
                                    if (declarations.ContainsKey(fieldParametersTypeOverrides[parameter.Name]))
                                    {
                                        paramType = GetTypeSyntax(context, new TagType()
                                        {
                                            Declaration = declarations[fieldParametersTypeOverrides[parameter.Name]]
                                        }, outputDir, namespaceDeclarationSyntax, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                                    }
                                    else
                                        throw new Exception($"Нет типа для переопределения {fieldParametersTypeOverrides[parameter.Name]}");
                                }
                                else
                                {
                                    paramType = GetTypeSyntax(context, parameter.Type, outputDir, namespaceDeclarationSyntax, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                                }

                                var name = parameter.Name;
                                if (name == "internal" || name == "base" || name == "params" || name == "event")
                                    name = "_" + name;
                                return SyntaxFactory.Parameter(default, default, paramType, SyntaxFactory.Identifier(name), default);
                            }
                        )));

                    var returnTypeSyntax = GetTypeSyntax(context, _func.ReturnType.Type, outputDir, namespaceDeclarationSyntax, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);

                    var methodDeclaration = SyntaxFactory.MethodDeclaration(returnTypeSyntax, _func.Name)
                        .WithParameterList(parameterList);

                    return methodDeclaration;
                });

                foreach (var runtimePair in lib.RuntimeData.PerPlatformPathes)
                {
                    var nativeMethodDeclarations = methodsDeclarations.Select(method =>
                    {
                        return method.WithModifiers(
                                SyntaxFactory.TokenList(
                                    SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                                    SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                                    SyntaxFactory.Token(SyntaxKind.ExternKeyword)
                                    )
                        )
                        .WithAttributeLists(
                            SyntaxFactory.List(
                                new[]
                                {
                                    SyntaxFactory.AttributeList(
                                    SyntaxFactory.SeparatedList(
                                        new[]
                                        {
                                            SyntaxFactory.Attribute(
                                                SyntaxFactory.IdentifierName("DllImport"),
                                                SyntaxFactory.AttributeArgumentList(
                                                    SyntaxFactory.SeparatedList(
                                                        new[]
                                                            {
                                                                SyntaxFactory.AttributeArgument(
                                                                     SyntaxFactory.LiteralExpression(
                                                                        SyntaxKind.StringLiteralExpression,
                                                                        SyntaxFactory.Literal(runtimePair.Value))
                                                                     ),
                                                                SyntaxFactory.AttributeArgument(
                                                                         SyntaxFactory.ParseExpression("CallingConvention = CallingConvention.Cdecl")
                                                                    )
                                                            }
                                                        )
                                                    )
                                            )
                                        }
                                        )

                                    )
                                }
                            )
                        )
                        .WithSemicolonToken(
                            SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                            );
                    });

                    var nativeClassSyntax = SyntaxFactory.ClassDeclaration($"{lib.LibName}{runtimePair.Key}Native")
                        .WithModifiers(
                            SyntaxFactory.TokenList(
                                SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                                SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                                SyntaxFactory.Token(SyntaxKind.UnsafeKeyword)
                                )
                        )
                        .AddMembers(nativeMethodDeclarations.ToArray());

                    using (var fileWriter = new StreamWriter(File.OpenWrite($"{outputDir}/{lib.LibName}{runtimePair.Key}Native.cs")))
                    {
                        fileWriter.Write(SyntaxFactory.CompilationUnit().AddMembers(
                            namespaceDeclarationSyntax.AddMembers(
                                nativeClassSyntax
                                )
                            )
                            .WithUsings(usings)
                            .NormalizeWhitespace().ToFullString());
                    }


                    var implementationClassSyntax = SyntaxFactory.ClassDeclaration($"{lib.LibName}{runtimePair.Key}")
                        .WithModifiers(
                            SyntaxFactory.TokenList(
                                SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                                SyntaxFactory.Token(SyntaxKind.UnsafeKeyword)
                                )
                        )
                        .AddMembers(methodsDeclarations.Select(t =>
                        {
                            var parameterNames = t.ParameterList.Parameters.Select(t => t.Identifier.Text);

                            var parametersStr = "";
                            if (parameterNames.Count() > 0)
                                parametersStr = parameterNames.Aggregate((a, b) => a + ',' + b);

                            var predefinedReturnType = t.ReturnType as PredefinedTypeSyntax;

                            if (predefinedReturnType != null && predefinedReturnType.Keyword.Text == "void")
                            {
                                return t.WithBody(
                                    SyntaxFactory.Block(
                                            SyntaxFactory.ParseStatement($"{lib.LibName}{runtimePair.Key}Native.{t.Identifier}({parametersStr});")
                                        )
                                    )
                                .WithModifiers(
                                    SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                                    );
                            }
                            else
                            {
                                return t.WithBody(
                                    SyntaxFactory.Block(
                                            SyntaxFactory.ParseStatement($"return {lib.LibName}{runtimePair.Key}Native.{t.Identifier}({parametersStr});")
                                        )
                                    )
                                .WithModifiers(
                                    SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                                    );
                            }
                        }).ToArray())
                        .WithBaseList(
                            SyntaxFactory.BaseList(
                                    SyntaxFactory.SeparatedList<BaseTypeSyntax>(new[] { SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName($"I{lib.LibName}")) })
                                )
                        );

                    using (var fileWriter = new StreamWriter(File.OpenWrite($"{outputDir}/{lib.LibName}{runtimePair.Key}.cs")))
                    {
                        fileWriter.Write(SyntaxFactory.CompilationUnit()
                            .WithUsings(usings)
                            .AddMembers(
                                namespaceDeclarationSyntax.AddMembers(
                                    implementationClassSyntax
                                       .AddMembers(properties.Select(t =>
                                             {
                                                 var value = $"{t.value}";
                                                 if (t.typeName == "CLong")
                                                     value = $"new CLong({t.value})";
                                                 if (t.typeName == "CULong")
                                                     value = $"new CULong({t.value})";

                                                 return SyntaxFactory.PropertyDeclaration(t.typeNameSyntax, t.propertyName)
                                                    .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.ParseExpression(value)))
                                                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                                                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)));
                                             }
                                            ).ToArray()
                                       )
                                    )
                            ).NormalizeWhitespace().ToFullString());
                    }
                }

                using (var fileWriter = new StreamWriter(File.OpenWrite($"{outputDir}/I{lib.LibName}.cs")))
                {
                    fileWriter.Write(SyntaxFactory.CompilationUnit()
                        .WithUsings(usings)
                        .AddMembers(
                            namespaceDeclarationSyntax.AddMembers(
                                SyntaxFactory.InterfaceDeclaration($"I{lib.LibName}")
                                    .WithModifiers(
                                        SyntaxFactory.TokenList(
                                            SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                                            SyntaxFactory.Token(SyntaxKind.UnsafeKeyword)
                                    ))
                                    .AddMembers(methodsDeclarations.Select(t => t.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))).ToArray())
                                    .AddMembers(properties.Select(t =>
                                         SyntaxFactory.PropertyDeclaration(t.typeNameSyntax, t.propertyName)
                                        .WithAccessorList(
                                            SyntaxFactory.AccessorList(
                                                    SyntaxFactory.List(new[] { SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)) })
                                                )
                                            )
                                        ).ToArray()
                                    )
                                )
                        ).NormalizeWhitespace().ToFullString());
                }

                var isFirst = true;
                var constructorBody = "";
                foreach (var platform in lib.RuntimeData.PerPlatformPathes.Keys)
                {
                    if (isFirst)
                    {
                        constructorBody += $"if (platform == Platform.{platform})";
                        isFirst = false;
                    }
                    else
                        constructorBody += $"else if (platform == Platform.{platform})";
                    constructorBody += $" lib = new {lib.LibName}{platform}();";
                }

                constructorBody += "else";
                constructorBody += " throw new System.NotSupportedException(\"not supported\");";

                using (var fileWriter = new StreamWriter(File.OpenWrite($"{outputDir}/{lib.LibName}.cs")))
                {
                    fileWriter.Write(SyntaxFactory.CompilationUnit()
                        .WithUsings(usings)
                        .AddMembers(
                        namespaceDeclarationSyntax.AddMembers(
                            SyntaxFactory.ClassDeclaration($"{lib.LibName}")
                                .WithModifiers(
                                    SyntaxFactory.TokenList(
                                        SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                                        SyntaxFactory.Token(SyntaxKind.UnsafeKeyword)
                                    )
                                )
                                .AddMembers(
                                    SyntaxFactory.ConstructorDeclaration(lib.LibName)
                                        .WithBody(SyntaxFactory.Block(
                                                SyntaxFactory.ParseStatement(constructorBody)
                                            ))
                                        .WithModifiers(
                                            SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                                        )
                                        .WithParameterList(
                                            SyntaxFactory.ParameterList(
                                                SyntaxFactory.SeparatedList(
                                                    new[]
                                                    {
                                                        SyntaxFactory.Parameter(
                                                            default,
                                                            default,
                                                            SyntaxFactory.ParseTypeName("Platform"),
                                                            SyntaxFactory.Identifier("platform"),
                                                            default)
                                                    }
                                                    )
                                                )
                                        ),
                                    SyntaxFactory.FieldDeclaration(
                                            SyntaxFactory.VariableDeclaration(
                                                    SyntaxFactory.ParseTypeName($"I{lib.LibName}"),
                                                    SyntaxFactory.SeparatedList(
                                                            new[]
                                                            {
                                                                SyntaxFactory.VariableDeclarator("lib")
                                                            }
                                                        )
                                                )
                                        )
                                )
                                .AddMembers(
                                methodsDeclarations.Select(t =>
                                {
                                    var parameterNames = t.ParameterList.Parameters.Select(t => t.Identifier.Text);

                                    var parametersStr = "";
                                    if (parameterNames.Count() > 0)
                                        parametersStr = parameterNames.Aggregate((a, b) => a + ',' + b);

                                    var predefinedReturnType = t.ReturnType as PredefinedTypeSyntax;

                                    if (predefinedReturnType != null && predefinedReturnType.Keyword.Text == "void")
                                    {
                                        return t.WithBody(
                                            SyntaxFactory.Block(
                                                    SyntaxFactory.ParseStatement($"lib.{t.Identifier}({parametersStr});")
                                                )
                                            )
                                        .WithModifiers(
                                            SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                                            );
                                    }
                                    else
                                    {
                                        return t.WithBody(
                                            SyntaxFactory.Block(
                                                    SyntaxFactory.ParseStatement($"return lib.{t.Identifier}({parametersStr});")
                                                )
                                            )
                                        .WithModifiers(
                                            SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                                            );
                                    }
                                }).ToArray())
                                .AddMembers(properties.Select(t =>
                                {
                                    return SyntaxFactory.PropertyDeclaration(t.typeNameSyntax, t.propertyName)
                                       .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.ParseExpression($"lib.{t.propertyName}")))
                                       .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                                       .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)));
                                }).ToArray()
                                )
                            )
                        ).NormalizeWhitespace().ToFullString());
                }
            }


            using (var fileWriter = new StreamWriter(File.OpenWrite($"{outputDir}/Platform.cs")))
            {
                fileWriter.Write(SyntaxFactory.CompilationUnit().AddMembers(
                    namespaceDeclarationSyntax.AddMembers(
                        SyntaxFactory.EnumDeclaration($"Platform")
                            .WithModifiers(
                                SyntaxFactory.TokenList(
                                    SyntaxFactory.Token(SyntaxKind.PublicKeyword)
                            ))
                            .AddMembers(
                                SyntaxFactory.EnumMemberDeclaration("Android"),
                                SyntaxFactory.EnumMemberDeclaration("Windows"),
                                SyntaxFactory.EnumMemberDeclaration("Linux")
                            )
                        )
                    ).NormalizeWhitespace().ToFullString());
            }


            if (forceTypesToGeneration != null)
            {
                foreach (var type in forceTypesToGeneration)
                {
                    RegisterType(context, type, outputDir, namespaceDeclarationSyntax, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                }
            }

            foreach (var declaration in declarations.Keys)
            {
                if (!registeredTypes.Contains(declaration))
                    logger?.LogWarning("Не обработанный тип: {type}", declaration);
            }
        }

        private static TypeSyntax GetTypeSyntax(
            ASTContext context,
            CppSharp.AST.Type type,
            string outputDir,
            NamespaceDeclarationSyntax namespaceDeclaration,
            SyntaxList<UsingDirectiveSyntax> usings,
            Dictionary<string, string>? notFoundTypesOverrides,
            Dictionary<PrimitiveType, string> primitiveTypesMap,
            Dictionary<string, TypedefStrategy>? typedefStrategies,
            Dictionary<string, string>? fieldParametersTypeOverrides,
            ILogger<Generator>? logger)
        {
            var typedefType = type as TypedefType;
            var pointerType = type as PointerType;
            var builtInType = type as BuiltinType;
            var tagType = type as TagType;
            var functionType = type as FunctionType;
            var arrayType = type as ArrayType;

            if (typedefType != null)
            {
                if (typedefStrategies != null && typedefStrategies.ContainsKey(typedefType.Declaration.Name))
                {
                    if (typedefStrategies[typedefType.Declaration.Name] == TypedefStrategy.InferType)
                    {
                        logger?.LogInformation("typedef {declaration} выведен", typedefType.Declaration.DebugText);
                        return GetTypeSyntax(context, typedefType.Declaration.Type, outputDir, namespaceDeclaration, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                    }
                    else
                    {
                        return GetTypeSyntax(context,
                            new TagType()
                            {
                                Declaration = new Enumeration()
                                {
                                    Name = typedefType.Declaration.Name
                                }
                            }, outputDir, namespaceDeclaration, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                    }
                }
                else
                {
                    logger?.LogInformation("typedef {declaration} выведен", typedefType.Declaration.DebugText);
                    return GetTypeSyntax(context, typedefType.Declaration.Type, outputDir, namespaceDeclaration, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                }
            }

            if (pointerType != null)
                return SyntaxFactory.PointerType(GetTypeSyntax(context, pointerType.Pointee, outputDir, namespaceDeclaration, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger));

            if (builtInType != null)
                return SyntaxFactory.ParseTypeName(primitiveTypesMap[builtInType.Type]);

            if (tagType != null)
            {
                try
                {
                    RegisterType(context, tagType.Declaration.Name, outputDir, namespaceDeclaration, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);

                    return SyntaxFactory.ParseTypeName(tagType.Declaration.Name);
                }
                catch (OverridedException ex)
                {
                    return SyntaxFactory.ParseTypeName(ex.NewName);
                }
            }

            if (functionType != null)
            {
                var parameters = new SeparatedSyntaxList<FunctionPointerParameterSyntax>();

                foreach (var parameter in functionType.Parameters)
                    parameters = parameters.Add(SyntaxFactory.FunctionPointerParameter(GetTypeSyntax(context, parameter.Type, outputDir, namespaceDeclaration, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger)));

                parameters = parameters.Add(SyntaxFactory.FunctionPointerParameter(GetTypeSyntax(context, functionType.ReturnType.Type, outputDir, namespaceDeclaration, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger)));

                return SyntaxFactory.FunctionPointerType(
                    SyntaxFactory.FunctionPointerCallingConvention(SyntaxFactory.Token(SyntaxKind.UnmanagedKeyword)),
                    SyntaxFactory.FunctionPointerParameterList(parameters)
                    );
            }

            if (arrayType != null)
                return SyntaxFactory.ArrayType(GetTypeSyntax(context, arrayType.Type, outputDir, namespaceDeclaration, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger));

            throw new System.Exception("type not handled");
        }

        private static void InitTypes(ASTContext context, List<EnumSearchParameter>? preprocessedConstantSearchParameters, List<string>? anonymousEnumPrefixes = default, ILogger<Generator>? logger = default)
        {
            foreach (var translationUnit in context.TranslationUnits)
            {
                foreach (var _class in translationUnit.Classes)
                {
                    classes.Add(_class.Name, _class);
                    declarations.Add(_class.Name, _class);
                }
            }

            var anonymousEnumerations = new List<Enumeration>();

            foreach (var translationUnit in context.TranslationUnits)
            {
                foreach (var _enum in translationUnit.Enums)
                {
                    if (string.IsNullOrEmpty(_enum.Name))
                    {
                        anonymousEnumerations.Add(_enum);
                    }
                    else
                    {
                        enumerations.Add(_enum.Name, _enum);
                        declarations.Add(_enum.Name, _enum);
                    }
                }
            }

            if (anonymousEnumPrefixes != null)
            {
                foreach (var anonymousEnum in anonymousEnumerations)
                {
                    var prefixes = anonymousEnumPrefixes.Where(t => anonymousEnum.Items[0].Name.StartsWith(t));
                    if (prefixes.Any())
                    {
                        var maxPrefix = prefixes.MaxBy(t => t.Length);
                        anonymousEnum.Name = maxPrefix;
                        enumerations.Add(anonymousEnum.Name!, anonymousEnum);
                        declarations.Add(anonymousEnum.Name!, anonymousEnum);
                    }
                }
            }

            if (preprocessedConstantSearchParameters != null)
            {
                foreach (var preprocessedEnumSearchParameter in preprocessedConstantSearchParameters)
                {
                    var definations = new List<MacroDefinition>();
                    foreach (var translationUnit in context.TranslationUnits)
                    {
                        foreach (var entity in translationUnit.PreprocessedEntities)
                        {
                            var macroDefination = entity as MacroDefinition;
                            if (macroDefination != default)
                            {
                                if (preprocessedEnumSearchParameter.ExcludePrefix != default)
                                {
                                    if (macroDefination.Name.StartsWith(preprocessedEnumSearchParameter.Prefix) && !macroDefination.Name.StartsWith(preprocessedEnumSearchParameter.ExcludePrefix))
                                        definations.Add(macroDefination);
                                }
                                else
                                {
                                    if (macroDefination.Name.StartsWith(preprocessedEnumSearchParameter.Prefix))
                                        definations.Add(macroDefination);
                                }
                            }
                        }
                    }

                    preprocessedEntities.AddRange(definations);

                    //var _enum = new Enumeration()
                    //{
                    //    Name = preprocessedEnumSearchParameter.Prefix,
                    //    Type = new BuiltinType() { Type = PrimitiveType.Long },
                    //    //Items = definations.Select(t => new Enumeration.Item() { Name = t.Key, Expression = t.Value }).ToList()
                    //};
                    //_enum.Items = definations.Select(t => _enum.GenerateEnumItemFromMacro(t)).ToList();

                    //enumerations.Add(preprocessedEnumSearchParameter.Prefix, _enum);
                    //declarations.Add(preprocessedEnumSearchParameter.Prefix, _enum);
                }
            }
        }

        private class OverridedException : Exception
        {
            public OverridedException(string newName)
            {
                NewName = newName;
            }

            public string NewName { get; set; }
        }
        private static void RegisterType(
            ASTContext context,
            string typeName,
            string outputDir,
            NamespaceDeclarationSyntax namespaceDeclaration,
            SyntaxList<UsingDirectiveSyntax> usings,
            Dictionary<string, string>? notFoundTypesOverrides,
            Dictionary<PrimitiveType, string> primitiveTypesMap,
            Dictionary<string, TypedefStrategy>? typedefStrategies,
            Dictionary<string, string>? fieldParametersTypeOverrides,
            ILogger<Generator>? logger)
        {
            if (classes.ContainsKey(typeName))
            {
                if (!registeredTypes.Contains(typeName))
                {
                    registeredTypes.Add(typeName);
                    var _class = classes[typeName];
                    using (var fileWriter = new StreamWriter(File.OpenWrite($"{outputDir}/{_class.Name}.cs")))
                    {
                        var attribute = SyntaxFactory.Attribute(
                                      SyntaxFactory.IdentifierName("StructLayout"),
                                      SyntaxFactory.AttributeArgumentList(
                                          new SeparatedSyntaxList<AttributeArgumentSyntax>().Add(
                                              SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression("LayoutKind.Sequential")))));

                        var _struct = SyntaxFactory.StructDeclaration(_class.Name);
                        _struct = _struct.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                        _struct = _struct.AddModifiers(SyntaxFactory.Token(SyntaxKind.UnsafeKeyword));
                        _struct = _struct.AddAttributeLists(SyntaxFactory.AttributeList(new SeparatedSyntaxList<AttributeSyntax>().Add(attribute)));
                        foreach (var field in _class.Fields)
                        {
                            TypeSyntax typeSyntax;
                            if (fieldParametersTypeOverrides != null && fieldParametersTypeOverrides.ContainsKey(field.Name))
                            {
                                if (declarations.ContainsKey(fieldParametersTypeOverrides[field.Name]))
                                {
                                    typeSyntax = GetTypeSyntax(context, new TagType()
                                    {
                                        Declaration = declarations[fieldParametersTypeOverrides[field.Name]]
                                    }, outputDir, namespaceDeclaration, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                                }
                                else
                                    throw new Exception($"Нет типа для переопределения {fieldParametersTypeOverrides[field.Name]}");
                            }
                            else
                            {
                                typeSyntax = GetTypeSyntax(context, field.Type, outputDir, namespaceDeclaration, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                            }

                            var variablesList = new SeparatedSyntaxList<VariableDeclaratorSyntax>();
                            var name = field.Name;
                            if (name == "internal" || name == "base" || name == "params" || name == "event")
                                name = "_" + name;
                            variablesList = variablesList.Add(SyntaxFactory.VariableDeclarator(name));
                            var fieldDeclaration = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(typeSyntax, variablesList));

                            fieldDeclaration = fieldDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                            _struct = _struct.AddMembers(fieldDeclaration);
                        }

                        var root = SyntaxFactory.CompilationUnit();

                        fileWriter.Write(
                            root.WithUsings(usings)
                            .AddMembers(namespaceDeclaration.AddMembers(_struct))
                            .NormalizeWhitespace().ToFullString());
                    }
                }
            }
            else if (enumerations.ContainsKey(typeName))
            {
                if (!registeredTypes.Contains(typeName))
                {
                    registeredTypes.Add(typeName);
                    var _enum = enumerations[typeName];
                    using (var fileWriter = new StreamWriter(File.OpenWrite($"{outputDir}/{_enum.Name}.cs")))
                    {
                        var type = _enum.Type as BuiltinType;

                        var _enumDeclaration = SyntaxFactory.EnumDeclaration(_enum.Name);
                        _enumDeclaration = _enumDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                        _enumDeclaration = _enumDeclaration.WithBaseList(
                            SyntaxFactory.BaseList(
                                new SeparatedSyntaxList<BaseTypeSyntax>().Add(SyntaxFactory.SimpleBaseType(GetTypeSyntax(context, type, outputDir, namespaceDeclaration, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger)))));

                        foreach (var item in _enum.Items)
                        {
                            var _enumMemberDeclaration = SyntaxFactory.EnumMemberDeclaration(item.Name);
                            _enumMemberDeclaration = _enumMemberDeclaration.WithEqualsValue(
                                SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression(item.Value.ToString())));
                            _enumDeclaration = _enumDeclaration.AddMembers(_enumMemberDeclaration);
                        }

                        fileWriter.Write(SyntaxFactory.CompilationUnit().AddMembers(_enumDeclaration).NormalizeWhitespace().ToFullString());
                    }
                }
            }
            else if (notFoundTypesOverrides != null)
            {
                if (notFoundTypesOverrides.ContainsKey(typeName))
                {
                    RegisterType(context, notFoundTypesOverrides[typeName], outputDir, namespaceDeclaration, usings, notFoundTypesOverrides, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                    throw new OverridedException(notFoundTypesOverrides[typeName]);
                }
            }
            //else if (typeName == anonymousEnumName)
            //{
            //    using (var fileWriter = new StreamWriter(File.OpenWrite($"{outputDir}/{anonymousEnumName}.cs")))
            //    {
            //        var namespaceNameSyntax = SyntaxFactory.IdentifierName(_namespace);
            //        var root = SyntaxFactory.NamespaceDeclaration(namespaceNameSyntax);
            //        var _enumDeclaration = SyntaxFactory.EnumDeclaration(anonymousEnumName);
            //        _enumDeclaration = _enumDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
            //        _enumDeclaration = _enumDeclaration.WithBaseList(
            //        SyntaxFactory.BaseList(
            //            new SeparatedSyntaxList<BaseTypeSyntax>().Add(
            //                SyntaxFactory.SimpleBaseType(
            //                    GetTypeSyntax(context, anonymousEnumerations[0].Type, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger)))));

            //        foreach (var _enum in anonymousEnumerations)
            //        {
            //            foreach (var item in _enum.Items)
            //            {
            //                var _enumMemberDeclaration = SyntaxFactory.EnumMemberDeclaration(item.Name);
            //                _enumMemberDeclaration = _enumMemberDeclaration.WithEqualsValue(
            //                    SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression(item.Value.ToString())));
            //                _enumDeclaration = _enumDeclaration.AddMembers(_enumMemberDeclaration);
            //            }
            //        }

            //        root = root.AddMembers(_enumDeclaration);
            //        fileWriter.Write(root.NormalizeWhitespace().ToFullString());
            //    }
            //}
            else
            {
                throw new System.Exception($"not supported type name {typeName}");
            }
        }
    }
}
