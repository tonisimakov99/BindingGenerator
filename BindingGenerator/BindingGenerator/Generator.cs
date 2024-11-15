using CommandLine;
using CppSharp;
using CppSharp.AST;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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
        private static List<Enumeration> anonymousEnumerations = new List<Enumeration>();
        private static List<MacroDefinition> preprocessedEntities = new List<MacroDefinition>();

        private static List<string> registeredTypes = new List<string>();

        public static void Generate(
            string[] includeDirs,
            string headerPath,
            string outputDir,
            string libFileImportPath,
            string _namespace,
            string apiClassName,
            bool forceClearOutputDirectory = true,
            bool noBuiltinIncludes = false,
            List<EnumSearchParameter>? preprocessedEnumSearchParameters = default,
            Dictionary<PrimitiveType, string>? primitiveTypesToCsTypesMap = default,
            Dictionary<string, TypedefStrategy>? typedefStrategies = default,
            Dictionary<string, string>? fieldParametersTypeOverrides = default,
            string anonymousEnumName = "anonymous",
            ILogger<Generator>? logger = default)
        {
            var primitiveTypesMap = primitiveTypesToCsTypesMap == null ? Utils.PrimitiveTypesToCsTypesMap.ToDictionary() : primitiveTypesToCsTypesMap;

            var parserOptions = new CppSharp.Parser.ParserOptions();

            foreach (var includeDir in includeDirs)
                parserOptions.AddIncludeDirs(includeDir);
            parserOptions.Setup(TargetPlatform.Windows);
            parserOptions.LanguageVersion = CppSharp.Parser.LanguageVersion.CPP17_GNU;
            parserOptions.NoBuiltinIncludes = noBuiltinIncludes;
            var parseResult = ClangParser.ParseSourceFiles(
                new[] {
                        headerPath
                }, parserOptions);

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

            InitTypes(context, anonymousEnumName, preprocessedEnumSearchParameters, logger: logger);

            var rootSyntax = SyntaxFactory.CompilationUnit();
            rootSyntax = rootSyntax.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName("System.Runtime.InteropServices")));

            var classSyntax = SyntaxFactory.ClassDeclaration(apiClassName);
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.UnsafeKeyword));
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword));


            var attributesArgsList = new SeparatedSyntaxList<AttributeArgumentSyntax>();
            attributesArgsList = attributesArgsList.Add(
                SyntaxFactory.AttributeArgument(
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(libFileImportPath))));
            attributesArgsList = attributesArgsList.Add(
                SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression("CallingConvention = CallingConvention.Cdecl")));


            var dllImportAttribute = SyntaxFactory.Attribute(
                SyntaxFactory.IdentifierName("DllImport"),
                SyntaxFactory.AttributeArgumentList(attributesArgsList)
                );

            var funcSyntaxTypes = new List<MethodDeclarationSyntax>();

            using (var fileWriter = new StreamWriter(File.OpenWrite($"{outputDir}/{apiClassName}.cs")))
            {
                foreach (var translationUnit in context.TranslationUnits)
                {
                    foreach (var _func in translationUnit.Functions)
                    {
                        var modifiers = new SyntaxTokenList();
                        modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                        modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
                        modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.ExternKeyword));

                        var attributes = new SyntaxList<AttributeListSyntax>();
                        attributes = attributes.Add(SyntaxFactory.AttributeList(new SeparatedSyntaxList<AttributeSyntax>().Add(dllImportAttribute)));
                        var parameters = new SeparatedSyntaxList<ParameterSyntax>();

                        foreach (var parameter in _func.Parameters)
                        {
                            TypeSyntax paramType;
                            if (fieldParametersTypeOverrides != null && fieldParametersTypeOverrides.ContainsKey(parameter.Name))
                            {
                                if (declarations.ContainsKey(fieldParametersTypeOverrides[parameter.Name]))
                                {
                                    paramType = GetTypeSyntax(context, new TagType()
                                    {
                                        Declaration = declarations[fieldParametersTypeOverrides[parameter.Name]]
                                    }, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                                }
                                else
                                    throw new Exception($"Нет типа для переопределения {fieldParametersTypeOverrides[parameter.Name]}");
                            }
                            else
                            {
                                paramType = GetTypeSyntax(context, parameter.Type, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                            }

                            parameters = parameters.Add(SyntaxFactory.Parameter(default, default, paramType, SyntaxFactory.Identifier(parameter.Name), default));
                        }

                        var returnTypeSyntax = GetTypeSyntax(context, _func.ReturnType.Type, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                        var funcSyntax = SyntaxFactory.MethodDeclaration(
                            attributes,
                            modifiers,
                            returnTypeSyntax,
                            null,
                            SyntaxFactory.Identifier(_func.Name),
                            null,
                            SyntaxFactory.ParameterList(parameters),
                            default,
                            null,
                            SyntaxFactory.Token(SyntaxKind.SemicolonToken));


                        funcSyntaxTypes.Add(funcSyntax);
                    }
                }

                classSyntax = classSyntax.AddMembers(funcSyntaxTypes.ToArray());

                var namespaceNameSyntax = SyntaxFactory.IdentifierName(_namespace);

                var namespaceDeclarationSyntax = SyntaxFactory.NamespaceDeclaration(
                    namespaceNameSyntax,
                    default,
                    default,
                    default);

                namespaceDeclarationSyntax = namespaceDeclarationSyntax.AddMembers(classSyntax);

                rootSyntax = rootSyntax.AddMembers(namespaceDeclarationSyntax);
                fileWriter.Write(rootSyntax.NormalizeWhitespace().ToFullString());
            }

            foreach (var declaration in declarations.Keys)
            {
                if (!registeredTypes.Contains(declaration))
                    logger?.LogWarning("Не обработанный тип: {type}", declaration);
            }

            if (!registeredTypes.Contains(anonymousEnumName))
                logger?.LogWarning("Не обработанный анонимный enum: {type}", anonymousEnumName);

        }

        private static TypeSyntax GetTypeSyntax(
            ASTContext context,
            CppSharp.AST.Type type,
            string outputDir,
            string _namespace,
            string anonymousEnumName,
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

            if (typedefType != null)
            {
                if (typedefStrategies != null && typedefStrategies.ContainsKey(typedefType.Declaration.Name))
                {
                    if (typedefStrategies[typedefType.Declaration.Name] == TypedefStrategy.InferType)
                    {
                        logger?.LogWarning("typedef {declaration} выведен", typedefType.Declaration.DebugText);
                        return GetTypeSyntax(context, typedefType.Declaration.Type, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
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
                            }, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                    }
                }
                else
                {
                    logger?.LogWarning("typedef {declaration} выведен", typedefType.Declaration.DebugText);
                    return GetTypeSyntax(context, typedefType.Declaration.Type, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                }
            }

            if (pointerType != null)
                return SyntaxFactory.PointerType(GetTypeSyntax(context, pointerType.Pointee, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger));

            if (builtInType != null)
                return SyntaxFactory.ParseTypeName(primitiveTypesMap[builtInType.Type]);

            if (tagType != null)
            {
                if (!registeredTypes.Contains(tagType.Declaration.Name))
                    RegisterType(context, tagType.Declaration.Name, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);

                return SyntaxFactory.ParseTypeName(tagType.Declaration.Name);
            }

            if (functionType != null)
            {
                var parameters = new SeparatedSyntaxList<FunctionPointerParameterSyntax>();

                foreach (var parameter in functionType.Parameters)
                    parameters = parameters.Add(SyntaxFactory.FunctionPointerParameter(GetTypeSyntax(context, parameter.Type, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger)));

                return SyntaxFactory.FunctionPointerType(
                    SyntaxFactory.FunctionPointerCallingConvention(SyntaxFactory.Token(SyntaxKind.UnmanagedKeyword)),
                    SyntaxFactory.FunctionPointerParameterList(parameters)
                    );
            }

            throw new System.Exception("type not handled");
        }

        private static void InitTypes(ASTContext context, string anonymousEnumName, List<EnumSearchParameter>? preprocessedEnumSearchParameters, ILogger<Generator>? logger)
        {
            foreach (var translationUnit in context.TranslationUnits)
            {
                foreach (var _class in translationUnit.Classes)
                {
                    classes.Add(_class.Name, _class);
                    declarations.Add(_class.Name, _class);
                }
            }

            foreach (var translationUnit in context.TranslationUnits)
            {
                foreach (var _enum in translationUnit.Enums)
                {
                    if (string.IsNullOrEmpty(_enum.Name))
                    {
                        _enum.Name = anonymousEnumName;
                        anonymousEnumerations.Add(_enum);
                    }
                    else
                    {
                        enumerations.Add(_enum.Name, _enum);
                        declarations.Add(_enum.Name, _enum);
                    }
                }
            }

            if (preprocessedEnumSearchParameters != null)
            {
                foreach (var preprocessedEnumSearchParameter in preprocessedEnumSearchParameters)
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

                    var _enum = new Enumeration()
                    {
                        Name = preprocessedEnumSearchParameter.Prefix,
                        Type = new BuiltinType() { Type = PrimitiveType.Long },
                        //Items = definations.Select(t => new Enumeration.Item() { Name = t.Key, Expression = t.Value }).ToList()
                    };
                    _enum.Items = definations.Select(t => _enum.GenerateEnumItemFromMacro(t)).ToList();

                    enumerations.Add(preprocessedEnumSearchParameter.Prefix, _enum);
                    declarations.Add(preprocessedEnumSearchParameter.Prefix, _enum);
                }
            }
        }


        private static void RegisterType(
            ASTContext context,
            string typeName,
            string outputDir,
            string _namespace,
            string anonymousEnumName,
            Dictionary<PrimitiveType, string> primitiveTypesMap,
            Dictionary<string, TypedefStrategy>? typedefStrategies,
            Dictionary<string, string>? fieldParametersTypeOverrides,
            ILogger<Generator>? logger)
        {
            registeredTypes.Add(typeName);

            if (classes.ContainsKey(typeName))
            {
                var _class = classes[typeName];
                using (var fileWriter = new StreamWriter(File.OpenWrite($"{outputDir}/{_class.Name}.cs")))
                {
                    var namespaceNameSyntax = SyntaxFactory.IdentifierName(_namespace);

                    var rootSyntax = SyntaxFactory.NamespaceDeclaration(
                        namespaceNameSyntax,
                        default,
                        new SyntaxList<UsingDirectiveSyntax>().Add(SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName("System.Runtime.InteropServices"))),
                        default);
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
                                }, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                            }
                            else
                                throw new Exception($"Нет типа для переопределения {fieldParametersTypeOverrides[field.Name]}");
                        }
                        else
                        {
                            typeSyntax = GetTypeSyntax(context, field.Type, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger);
                        }

                        var variablesList = new SeparatedSyntaxList<VariableDeclaratorSyntax>();
                        var name = field.Name;
                        if (name == "internal" || name == "base" || name == "params")
                            name = "_" + name;
                        variablesList = variablesList.Add(SyntaxFactory.VariableDeclarator(name));
                        var fieldDeclaration = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(typeSyntax, variablesList));

                        fieldDeclaration = fieldDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                        _struct = _struct.AddMembers(fieldDeclaration);
                    }

                    rootSyntax = rootSyntax.AddMembers(_struct);

                    fileWriter.Write(rootSyntax.NormalizeWhitespace().ToFullString());
                }
            }
            else if (enumerations.ContainsKey(typeName))
            {
                var _enum = enumerations[typeName];
                using (var fileWriter = new StreamWriter(File.OpenWrite($"{outputDir}/{_enum.Name}.cs")))
                {
                    var namespaceNameSyntax = SyntaxFactory.IdentifierName(_namespace);
                    var root = SyntaxFactory.NamespaceDeclaration(namespaceNameSyntax);
                    var type = _enum.Type as BuiltinType;

                    var _enumDeclaration = SyntaxFactory.EnumDeclaration(_enum.Name);
                    _enumDeclaration = _enumDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                    _enumDeclaration = _enumDeclaration.WithBaseList(
                        SyntaxFactory.BaseList(
                            new SeparatedSyntaxList<BaseTypeSyntax>().Add(SyntaxFactory.SimpleBaseType(GetTypeSyntax(context, type, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger)))));

                    foreach (var item in _enum.Items)
                    {
                        var _enumMemberDeclaration = SyntaxFactory.EnumMemberDeclaration(item.Name);
                        _enumMemberDeclaration = _enumMemberDeclaration.WithEqualsValue(
                            SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression(item.Value.ToString())));
                        _enumDeclaration = _enumDeclaration.AddMembers(_enumMemberDeclaration);
                    }

                    root = root.AddMembers(_enumDeclaration);

                    fileWriter.Write(root.NormalizeWhitespace().ToFullString());
                }
            }
            else if (typeName == anonymousEnumName)
            {
                using (var fileWriter = new StreamWriter(File.OpenWrite($"{outputDir}/{anonymousEnumName}.cs")))
                {
                    var namespaceNameSyntax = SyntaxFactory.IdentifierName(_namespace);
                    var root = SyntaxFactory.NamespaceDeclaration(namespaceNameSyntax);
                    var _enumDeclaration = SyntaxFactory.EnumDeclaration(anonymousEnumName);
                    _enumDeclaration = _enumDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                    _enumDeclaration = _enumDeclaration.WithBaseList(
                    SyntaxFactory.BaseList(
                        new SeparatedSyntaxList<BaseTypeSyntax>().Add(
                            SyntaxFactory.SimpleBaseType(
                                GetTypeSyntax(context, anonymousEnumerations[0].Type, outputDir, _namespace, anonymousEnumName, primitiveTypesMap, typedefStrategies, fieldParametersTypeOverrides, logger)))));

                    foreach (var _enum in anonymousEnumerations)
                    {
                        foreach (var item in _enum.Items)
                        {
                            var _enumMemberDeclaration = SyntaxFactory.EnumMemberDeclaration(item.Name);
                            _enumMemberDeclaration = _enumMemberDeclaration.WithEqualsValue(
                                SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression(item.Value.ToString())));
                            _enumDeclaration = _enumDeclaration.AddMembers(_enumMemberDeclaration);
                        }
                    }

                    root = root.AddMembers(_enumDeclaration);
                    fileWriter.Write(root.NormalizeWhitespace().ToFullString());
                }
            }
            else
            {
                throw new System.Exception("not supported type");
            }
        }
    }
}
