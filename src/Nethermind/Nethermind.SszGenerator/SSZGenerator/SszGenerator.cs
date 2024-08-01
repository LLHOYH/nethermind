using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using Nethermind.Generated.Ssz;
using Microsoft.CodeAnalysis.Operations;

[Generator]
public partial class SszGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (syntaxNode, _) => IsClassWithAttribute(syntaxNode),
                transform: (context, _) => GetClassWithAttribute(context))
            .Where(classNode => classNode is not null);

        //var methodDeclarations = context.SyntaxProvider
        //    .CreateSyntaxProvider(
        //        predicate: (syntaxNode, _) => IsMethodWithAttribute(syntaxNode),
        //        transform: (context, _) => GetMethodWithAttribute(context))
        //    .Where(methodNode => methodNode is not null);

        //var fieldDeclarations = context.SyntaxProvider
        //    .CreateSyntaxProvider(
        //        predicate: (syntaxNode, _) => IsFieldWithAttribute(syntaxNode),
        //        transform: (context, _) => GetFieldWithAttribute(context))
        //    .Where(fieldNode => fieldNode is not null);

        context.RegisterSourceOutput(classDeclarations, (spc, decl) =>
        {
            if (decl is null)
            {
                return;
            }
            var generatedCode = GenerateClassCode(decl);
            spc.AddSource($"{decl.TypeName}SszSerializer.cs", SourceText.From(generatedCode, Encoding.UTF8));
        });

        //context.RegisterSourceOutput(methodDeclarations, (spc, methodNode) =>
        //{
        //    if (methodNode is MethodDeclarationSyntax methodDeclaration)
        //    {
        //        var methodName = methodDeclaration.Identifier.Text;
        //        var classDeclaration = methodDeclaration.Parent as ClassDeclarationSyntax;
        //        var className = classDeclaration?.Identifier.Text ?? "default";
        //        var namespaceName = GetNamespace(methodDeclaration);
        //        var generatedCode = GenerateMethodCode(namespaceName, className, methodName);
        //        spc.AddSource($"{className}_{methodName}_method_generated.cs", SourceText.From(generatedCode, Encoding.UTF8));
        //    }
        //});

        //context.RegisterSourceOutput(fieldDeclarations, (spc, fieldNode) =>
        //{
        //    if (fieldNode is FieldDeclarationSyntax fieldDeclaration)
        //    {
        //        var variable = fieldDeclaration.Declaration.Variables.FirstOrDefault();
        //        if (variable != null)
        //        {
        //            var fieldName = variable.Identifier.Text;
        //            var classDeclaration = fieldDeclaration.Parent as ClassDeclarationSyntax;
        //            var className = classDeclaration?.Identifier.Text ?? "default";
        //            var namespaceName = GetNamespace(fieldDeclaration);
        //            var generatedCode = GenerateFieldCode(namespaceName, className, fieldName);
        //            spc.AddSource($"{className}_{fieldName}_field_generated.cs", SourceText.From(generatedCode, Encoding.UTF8));
        //        }
        //    }
        //});

    }

    private static bool IsClassWithAttribute(SyntaxNode syntaxNode)
    {
        return syntaxNode is TypeDeclarationSyntax classDeclaration &&
               classDeclaration.AttributeLists.Any(x => x.Attributes.Any());
    }

    private static TypeDeclaration? GetClassWithAttribute(GeneratorSyntaxContext context)
    {
        var classDeclaration = (TypeDeclarationSyntax)context.Node;
        foreach (var attributeList in classDeclaration.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var symbolInfo = context.SemanticModel.GetSymbolInfo(attribute);
                if (symbolInfo.Symbol is IMethodSymbol methodSymbol &&
                    methodSymbol.ContainingType.ToString() == "Nethermind.Generated.Ssz.SszSerializableAttribute")
                {
                    var props = context.SemanticModel.GetDeclaredSymbol(classDeclaration)?.GetMembers().OfType<IPropertySymbol>();
                    if (props?.Any() != true)
                    {
                        continue;
                    }

                    return new(
                        GetNamespace(classDeclaration),
                        classDeclaration.Identifier.Text,
                        classDeclaration is StructDeclarationSyntax,
                        props.Select(x => new PropertyDeclaration(SszType.From(x), x.Name)).ToArray());
                }
            }
        }
        return null;
    }

    //private static MethodDeclarationSyntax? GetMethodWithAttribute(GeneratorSyntaxContext context)
    //{
    //    var methodDeclaration = (MethodDeclarationSyntax)context.Node;
    //    foreach (var attributeList in methodDeclaration.AttributeLists)
    //    {
    //        foreach (var attribute in attributeList.Attributes)
    //        {
    //            var symbolInfo = context.SemanticModel.GetSymbolInfo(attribute);
    //            if (symbolInfo.Symbol is IMethodSymbol methodSymbol &&
    //                methodSymbol.ContainingType.Name == "FunctionAttribute")
    //            {
    //                return methodDeclaration;
    //            }
    //        }
    //    }
    //    return null;
    //}

    //private static FieldDeclarationSyntax? GetFieldWithAttribute(GeneratorSyntaxContext context)
    //{
    //    var fieldDeclaration = (FieldDeclarationSyntax)context.Node;
    //    foreach (var attributeList in fieldDeclaration.AttributeLists)
    //    {
    //        foreach (var attribute in attributeList.Attributes)
    //        {
    //            var symbolInfo = context.SemanticModel.GetSymbolInfo(attribute);
    //            if (symbolInfo.Symbol is IMethodSymbol methodSymbol &&
    //                methodSymbol.ContainingType.Name == "FieldAttribute")
    //            {
    //                return fieldDeclaration;
    //            }
    //        }
    //    }
    //    return null;
    //}

    private static string GetNamespace(SyntaxNode syntaxNode)
    {
        var namespaceDeclaration = syntaxNode.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        return namespaceDeclaration?.Name.ToString() ?? "GlobalNamespace";
    }

    struct Dyn
    {
        public string OffsetDeclaration { get; init; }
        public string DynamicEncode { get; init; }
        public string DynamicLength { get; init; }
    }

    private static string GenerateClassCode(TypeDeclaration decl)
    {
        int staticLength = decl.Members.Sum(prop => prop.Type.StaticLength);
        List<Dyn> dynOffsets = new();
        PropertyDeclaration? prevM = null;

        foreach (var prop in decl.Members)
        {
            if (prop.Type.IsDynamic)
            {
                dynOffsets.Add(new Dyn
                {
                    OffsetDeclaration = prevM is null ? $"int dynOffset{dynOffsets.Count + 1} = {staticLength}" : ($"int dynOffset{dynOffsets.Count + 1} = dynOffset{dynOffsets.Count} + {prevM!.Type.DynamicLength}"),
                    DynamicEncode = prop.Type.DynamicEncode ?? "",
                    DynamicLength = prop.Type.DynamicLength!,
                });
                prevM = prop;
            }
        }

        var offset = 0;
        var dynOffset = 0;

        var result = $@"using Nethermind.Serialization.Ssz;

namespace {decl.TypeNamespaceName}.Generated;

public partial class {decl.TypeName}SszSerializer
{{
    public int GetLength({(decl.IsStruct ? "ref " : "")}{decl.TypeName} container)
    {{
        return {staticLength}{(dynOffsets.Any() ? $" + \n               {string.Join(" +\n               ", dynOffsets.Select(m => m.DynamicLength))}" : "")};
    }}

    public ReadOnlySpan<byte> Serialize({(decl.IsStruct ? "ref " : "")}{decl.TypeName} container)
    {{
        Span<byte> buf = new byte[GetLength({(decl.IsStruct ? "ref " : "")}container)];

        {string.Join(";\n        ", dynOffsets.Select(m => m.OffsetDeclaration))};

        {string.Join(";\n        ", decl.Members.Select(m =>
        {
            if (m.Type.IsDynamic) dynOffset++;
            string result = m.Type.StaticEncode.Replace("{offset}", $"{offset}").Replace("{dynOffset}", $"dynOffset{dynOffset}");
            offset += m.Type.StaticLength;
            return result;
        }))};

        {string.Join(";\n        ", dynOffsets.Select((m, i) => m.DynamicEncode.Replace("{dynOffset}", $"dynOffset{i + 1}").Replace("{length}", m.DynamicLength)))};

        return buf;
    }}
}}
";
        return result;
    }

    //private static string GenerateMethodCode(string namespaceName, string className, string methodName)
    //{
    //    return $@"
    //        namespace {namespaceName}
    //        {{
    //            public partial class {className}
    //            {{
    //                public string GetMethodName()
    //                {{
    //                    return ""{methodName}"";
    //                }}
    //            }}
    //        }}
    //        ";
    //}

    //private static string GenerateFieldCode(string namespaceName, string className, string fieldName)
    //{
    //    return $@"
    //        namespace {namespaceName}
    //        {{
    //            public partial class {className}
    //            {{
    //                public string GetFieldName()
    //                {{
    //                    return ""{fieldName}"";
    //                }}
    //            }}
    //        }}
    //        ";
    //}

    //private static bool IsMethodWithAttribute(SyntaxNode syntaxNode)
    //{
    //    return syntaxNode is MethodDeclarationSyntax methodDeclaration &&
    //           methodDeclaration.AttributeLists.Any();
    //}

    //private static bool IsFieldWithAttribute(SyntaxNode syntaxNode)
    //{
    //    return syntaxNode is FieldDeclarationSyntax fieldDeclaration &&
    //           fieldDeclaration.AttributeLists.Any();
    //}
}


