// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Bicep.Core.Extensions;
using Bicep.Core.Navigation;
using Bicep.Core.Parsing;
using Bicep.Core.PrettyPrintV2;
using Bicep.Core.Semantics;
using Bicep.Core.SourceGraph;
using Bicep.Core.Syntax;
using Bicep.Core.Syntax.Visitors;
using Bicep.Core.Text;
using Bicep.Core.TypeSystem;
using Bicep.Core.TypeSystem.Types;

namespace Bicep.Core.CodeAction.Fixes;

/// <summary>
/// Generates quick fixes to declare missing symbols reported as BCP057.
/// </summary>
public static class UndefinedSymbolCodeFixGenerator
{
    /// <summary>
    /// Generates code fixes for an undefined symbol (BCP057 diagnostic).
    /// </summary>
    /// <param name="semanticModel">The semantic model.</param>
    /// <param name="variableAccess">The variable access syntax node that references an undefined symbol.</param>
    /// <returns>Code fixes to create a parameter or variable declaration, or empty if fixes cannot be generated.</returns>
    public static IEnumerable<CodeFix> GetFixes(SemanticModel semanticModel, VariableAccessSyntax variableAccess)
    {
        try
        {
            return GetFixesInternal(semanticModel, variableAccess);
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<CodeFix> GetFixesInternal(SemanticModel semanticModel, VariableAccessSyntax variableAccess)
    {
        var name = variableAccess.Name.IdentifierName;
        if (!Lexer.IsValidIdentifier(name))
        {
            return [];
        }

        // New declarations must be inserted before a statement; if the access isn't
        // inside one (e.g., top-level expression), we can't determine insertion point
        if (semanticModel.Binder.GetNearestAncestor<StatementSyntax>(variableAccess) is not { } parentStatement)
        {
            return [];
        }

        var moduleParameterTypeString = TryGetModuleParameterTypeString(semanticModel, variableAccess);
        var contextualType = TypeHelper.NullIfErrorOrAny(semanticModel.GetDeclaredType(variableAccess));
        var declaredAssignment = semanticModel.GetDeclaredTypeAssignment(variableAccess);
        var declaredAssignmentType = TypeHelper.NullIfErrorOrAny(declaredAssignment?.Reference.Type);
        var inferredType = TypeHelper.NullIfErrorOrAny(semanticModel.GetTypeInfo(variableAccess));
        var contextInferredType = InferByContext(semanticModel, variableAccess);
        var effectiveType = declaredAssignmentType ?? contextualType ?? inferredType ?? contextInferredType;

        // Type resolution priority for generated parameters:
        // 1. Module parameter types (preserve exact type from referenced module)
        // 2. Clear usage context (bool in conditions, int in arithmetic)
        // 3. Resource-derived types (e.g., resourceInput<'Microsoft.Storage/storageAccounts@2023-01-01'>.sku)
        // 4. User-defined type aliases (preserve named types when possible)
        // 5. Fallback to TypeStringifier with medium strictness
        string parameterTypeString;
        if (moduleParameterTypeString is not null)
        {
            parameterTypeString = moduleParameterTypeString;
        }
        else if (contextInferredType is BooleanType or IntegerType)
        {
            // Clear usage context - use the inferred primitive type
            parameterTypeString = GetTypeString(contextInferredType);
        }
        else
        {
            // No clear usage context - try resource-derived or complex types
            parameterTypeString = TryGetResourceInputTypeString(semanticModel, variableAccess)
                ?? TryGetUserDefinedTypeName(semanticModel, variableAccess, declaredAssignment)
                ?? GetTypeString(effectiveType);
        }

        var prettyPrintContext = PrettyPrinterV2Context.From(semanticModel);

        return CreateQuickFixes(semanticModel, prettyPrintContext, parentStatement, name, parameterTypeString, effectiveType);
    }

    private static IEnumerable<CodeFix> CreateQuickFixes(
        SemanticModel semanticModel,
        PrettyPrinterV2Context prettyPrintContext,
        StatementSyntax parentStatement,
        string name,
        string parameterTypeString,
        TypeSymbol? effectiveType)
    {
        var parameterInsertionOffset = FindInsertionOffset(semanticModel, parentStatement, typeof(ParameterDeclarationSyntax));
        var parameterText = BuildDeclarationText(
            semanticModel,
            prettyPrintContext,
            parameterInsertionOffset,
            $"{SyntaxFactory.ParameterKeywordToken.Text} {name} {parameterTypeString}");
        yield return new CodeFix(
            $"Create parameter '{name}'",
            isPreferred: false,
            CodeFixKind.QuickFix,
            new CodeReplacement(new TextSpan(parameterInsertionOffset, 0), parameterText));

        var variableInsertionOffset = FindInsertionOffset(semanticModel, parentStatement, typeof(VariableDeclarationSyntax));
        var defaultInitializer = GetDefaultInitializer(effectiveType);
        var variableDeclaration = SyntaxFactory.CreateVariableDeclaration(name, defaultInitializer);
        var variableText = BuildDeclarationText(
            semanticModel,
            prettyPrintContext,
            variableInsertionOffset,
            PrettyPrinterV2.Print(variableDeclaration, prettyPrintContext).TrimEnd());
        yield return new CodeFix(
            $"Create variable '{name}'",
            isPreferred: false,
            CodeFixKind.QuickFix,
            new CodeReplacement(new TextSpan(variableInsertionOffset, 0), variableText));
    }

    /// <summary>
    /// Builds the text for a new declaration with appropriate spacing.
    /// Ensures consistent formatting: adds newlines to create one blank line
    /// between the new declaration and existing code.
    /// </summary>
    private static string BuildDeclarationText(SemanticModel semanticModel, PrettyPrinterV2Context prettyPrintContext, int insertionOffset, string declaration)
    {
        var existingNewlines = CountConsecutiveNewlinesAtOffset(semanticModel, insertionOffset, prettyPrintContext.Newline);

        // Target spacing: one blank line after the declaration (2 newlines total).
        // If we're already on a blank line, add just enough to separate cleanly.
        var totalDesiredNewlines = 2;
        var additionalNewlines = Math.Max(totalDesiredNewlines - existingNewlines, 1);
        var spacing = string.Concat(Enumerable.Repeat(prettyPrintContext.Newline, additionalNewlines));

        return $"{declaration}{spacing}";
    }

    private static int CountConsecutiveNewlinesAtOffset(SemanticModel semanticModel, int insertionOffset, string newline)
    {
        var lineStarts = semanticModel.SourceFile.LineStarts;
        if (lineStarts.Length == 0 || insertionOffset < 0 || insertionOffset > semanticModel.SourceFile.Text.Length)
        {
            return 0;
        }

        var (line, _) = TextCoordinateConverter.GetPosition(lineStarts, insertionOffset);
        if (line < 0 || line >= lineStarts.Length)
        {
            return 0;
        }

        var lineStart = lineStarts[line];
        var lineEnd = line + 1 < lineStarts.Length ? lineStarts[line + 1] : semanticModel.SourceFile.Text.Length;
        var lineLength = lineEnd - lineStart;

        // Count consecutive newline sequences starting at the offset.
        if (lineLength > newline.Length || lineEnd + newline.Length > semanticModel.SourceFile.Text.Length)
        {
            // Not a blank line or at end of file.
            return 0;
        }

        var text = semanticModel.SourceFile.Text;
        var count = 0;
        var cursor = insertionOffset;
        while (cursor + newline.Length <= text.Length && text.AsSpan(cursor).StartsWith(newline, StringComparison.Ordinal))
        {
            count++;
            cursor += newline.Length;
        }

        return count;
    }

    /// <summary>
    /// Finds optimal insertion point for a new declaration.
    /// Inserts after existing declarations of the same type, or before the anchor
    /// statement if no prior declarations of that type exist.
    /// </summary>
    private static int FindInsertionOffset(SemanticModel semanticModel, StatementSyntax anchorStatement, Type declarationType)
    {
        var sourceFile = semanticModel.SourceFile;
        var lineStarts = sourceFile.LineStarts;

        var existing = sourceFile.ProgramSyntax.Children.OfType<StatementSyntax>()
            .Where(s => s.GetType() == declarationType && s.Span.Position < anchorStatement.Span.Position)
            .OrderByDescending(s => s.Span.Position)
            .FirstOrDefault();

        if (existing is { })
        {
            var insertLine = TextCoordinateConverter.GetPosition(lineStarts, existing.GetEndPosition()).line + 1;
            return TextCoordinateConverter.GetOffset(lineStarts, insertLine, 0);
        }

        var anchorStartLine = StatementLineHelper.GetFirstLineOfStatementIncludingComments(
            lineStarts,
            sourceFile.ProgramSyntax,
            anchorStatement);
        return TextCoordinateConverter.GetOffset(lineStarts, anchorStartLine, 0);
    }

    private static string GetTypeString(TypeSymbol? type)
    {
        // When we cannot infer a meaningful type at all, fall back to "string"
        // rather than emitting an opaque helper type like "object? /* unknown */".
        // This gives a simple, editable scalar in cases where context didn't
        // provide a better type.
        if (type is null or ErrorType or AnyType)
        {
            return LanguageConstants.String.Name;
        }

        // Use TypeStringifier for consistent type string generation.
        // Medium strictness gives us reasonable types (e.g., 'int' instead of
        // literal '123'), and we remove top-level nullability so generated
        // parameters are non-nullable by default.
        return TypeStringifier.Stringify(type, typeProperty: null, TypeStringifier.Strictness.Medium, removeTopLevelNullability: true);
    }

    private static SyntaxBase GetDefaultInitializer(TypeSymbol? type)
    {
        return GetDefaultInitializerCore(type, []);
    }

    private static SyntaxBase GetDefaultInitializerCore(TypeSymbol? type, HashSet<TypeSymbol> visitedTypes)
    {
        if (type is null)
        {
            return SyntaxFactory.CreateStringLiteral(string.Empty);
        }

        // Prevent infinite recursion for recursive types
        if (visitedTypes.Contains(type))
        {
            return SyntaxFactory.CreateObject([]);
        }

        // Handle nullable types - use the non-null default
        if (TypeHelper.TryRemoveNullability(type) is TypeSymbol nonNullableType)
        {
            return GetDefaultInitializerCore(nonNullableType, visitedTypes);
        }

        return type switch
        {
            BooleanLiteralType boolLit => SyntaxFactory.CreateBooleanLiteral(boolLit.Value),
            BooleanType => SyntaxFactory.CreateBooleanLiteral(false),
            IntegerLiteralType intLit => SyntaxFactory.CreatePositiveOrNegativeInteger(intLit.Value),
            IntegerType => SyntaxFactory.CreateIntegerLiteral(0),
            StringLiteralType strLit => SyntaxFactory.CreateStringLiteral(strLit.RawStringValue),
            StringType => SyntaxFactory.CreateStringLiteral(string.Empty),
            ArrayType or TypedArrayType or TupleType => SyntaxFactory.CreateArray([]),
            ObjectType objectType => GetDefaultInitializerForObject(objectType, visitedTypes),
            UnionType union => GetDefaultInitializerForUnion(union, visitedTypes),
            _ => SyntaxFactory.CreateStringLiteral(string.Empty)
        };
    }

    private static SyntaxBase GetDefaultInitializerForObject(ObjectType objectType, HashSet<TypeSymbol> visitedTypes)
    {
        var writeableProperties = objectType.Properties.Values
            .Where(p => !p.Flags.HasFlag(TypePropertyFlags.ReadOnly))
            .ToArray();

        // For empty objects or objects with only optional properties, just use {}
        if (writeableProperties.Length == 0)
        {
            return SyntaxFactory.CreateObject([]);
        }

        // Limit object expansion to 5 properties to keep generated code readable;
        // larger objects are better left as {} for manual population
        if (writeableProperties.Length > 5)
        {
            return SyntaxFactory.CreateObject([]);
        }

        visitedTypes = [.. visitedTypes, objectType];

        var properties = writeableProperties
            .Select(p => SyntaxFactory.CreateObjectProperty(p.Name, GetDefaultInitializerCore(p.TypeReference.Type, visitedTypes)));

        return SyntaxFactory.CreateObject(properties);
    }

    private static SyntaxBase GetDefaultInitializerForUnion(UnionType union, HashSet<TypeSymbol> visitedTypes)
    {
        // For unions, try to pick a reasonable default from the first non-null member
        var firstNonNullMember = union.Members.FirstOrDefault(m => m.Type is not NullType)?.Type;
        return firstNonNullMember is not null ? GetDefaultInitializerCore(firstNonNullMember, visitedTypes) : SyntaxFactory.CreateNullLiteral();
    }

    /// <summary>
    /// Detects when the undefined symbol is used as a resource property value,
    /// allowing generation of resource-derived types.
    /// </summary>
    private static string? TryGetResourceInputTypeString(SemanticModel semanticModel, VariableAccessSyntax variableAccess)
    {
        // Walk up to see if we're in a resource property assignment
        SyntaxBase? current = variableAccess;
        List<string> propertyPath = new();

        // Build the property path by walking up through ObjectPropertySyntax nodes
        while (current is not null)
        {
            current = semanticModel.Binder.GetParent(current);

            if (current is ObjectPropertySyntax objProp && objProp.TryGetKeyText() is string propName)
            {
                // Add property name to the front of the path (we're walking backwards)
                propertyPath.Insert(0, propName);
            }
            else if (current is ResourceDeclarationSyntax resourceDecl)
            {
                // We've reached the resource declaration
                // Generate resourceInput type for any resource property with a path
                if (propertyPath.Count == 0)
                {
                    // No property path found
                    return null;
                }

                // Try to get the resource type string
                if (resourceDecl.Type is StringSyntax stringSyntax &&
                    stringSyntax.TryGetLiteralValue() is string resourceTypeString)
                {
                    // Build the full type path: resourceInput<'Type@version'>.sku or .properties.encryption
                    var fullPath = string.Join(".", propertyPath);
                    return $"resourceInput<'{resourceTypeString}'>.{fullPath}";
                }
                break;
            }
        }

        return null;
    }

    private static string? TryGetUserDefinedTypeName(SemanticModel semanticModel, VariableAccessSyntax variableAccess, DeclaredTypeAssignment? assignment)
    {
        // If we already have a declared assignment with a type alias, prefer it
        if (assignment?.DeclaringSyntax is TypeVariableAccessSyntax declaredTypeAccess)
        {
            var declaredType = assignment.Reference.Type;
            if (TryGetUserDefinedTypeNameFromTypeSyntax(semanticModel, declaredTypeAccess, declaredType) is { } declaredTypeName)
            {
                return declaredTypeName;
            }
        }

        // Walk up from the variable access to find a parent declaration that carries a type alias
        SyntaxBase? current = variableAccess;
        while (current is not null)
        {
            current = semanticModel.Binder.GetParent(current);

            switch (current)
            {
                case OutputDeclarationSyntax outputDecl:
                    var outputType = semanticModel.GetDeclaredTypeAssignment(outputDecl)?.Reference.Type;
                    if (outputDecl.Type is { } outputTypeSyntax &&
                        TryGetUserDefinedTypeNameFromTypeSyntax(semanticModel, outputTypeSyntax, outputType) is { } outputAlias)
                    {
                        return outputAlias;
                    }
                    break;

                case ParameterDeclarationSyntax paramDecl:
                    var paramType = semanticModel.GetDeclaredTypeAssignment(paramDecl)?.Reference.Type;
                    if (paramDecl.Type is { } paramTypeSyntax &&
                        TryGetUserDefinedTypeNameFromTypeSyntax(semanticModel, paramTypeSyntax, paramType) is { } paramAlias)
                    {
                        return paramAlias;
                    }
                    break;

                case VariableDeclarationSyntax variableDecl:
                    var varType = semanticModel.GetDeclaredTypeAssignment(variableDecl)?.Reference.Type;
                    if (variableDecl.Type is { } varTypeSyntax &&
                        TryGetUserDefinedTypeNameFromTypeSyntax(semanticModel, varTypeSyntax, varType) is { } varAlias)
                    {
                        return varAlias;
                    }
                    break;

                case TypeVariableAccessSyntax typeAccess:
                    return TryGetUserDefinedTypeNameFromTypeSyntax(semanticModel, typeAccess, assignment?.Reference.Type);
            }
        }

        return null;
    }

    private static string? TryGetUserDefinedTypeNameFromTypeSyntax(SemanticModel semanticModel, SyntaxBase typeSyntax, TypeSymbol? typeSymbol)
    {
        if (typeSyntax is null)
        {
            return null;
        }

        return typeSyntax switch
        {
            NullableTypeSyntax nullable => TryGetUserDefinedTypeNameFromTypeSyntax(
                semanticModel,
                nullable.Base,
                typeSymbol is null ? null : TypeHelper.TryRemoveNullability(typeSymbol))
                is { } innerName
                ? $"{innerName}{((typeSymbol is null || TypeHelper.IsNullable(typeSymbol)) ? "?" : string.Empty)}"
                : null,

            TypeVariableAccessSyntax typeAccess => TryGetUserDefinedTypeNameFromTypeAccess(semanticModel, typeAccess, typeSymbol),
            _ => null,
        };
    }

    private static string? TryGetUserDefinedTypeNameFromTypeAccess(SemanticModel semanticModel, TypeVariableAccessSyntax typeAccess, TypeSymbol? typeSymbol)
    {
        var symbol = semanticModel.Binder.GetSymbolInfo(typeAccess);
        if (symbol is not TypeAliasSymbol typeAlias)
        {
            return null;
        }

        var isNullable = typeSymbol is not null && TypeHelper.IsNullable(typeSymbol);
        return isNullable ? $"{typeAlias.Name}?" : typeAlias.Name;
    }

    /// <summary>
    /// Checks if the child syntax node is contained within the parent syntax node's span.
    /// This is used instead of reference equality when walking up the syntax tree,
    /// because the child may be wrapped in intermediate nodes (e.g., parentheses).
    /// Uses <see cref="IPositionableExtensions.IsOverlapping(IPositionable,int)"/> to
    /// avoid duplicating span comparison logic.
    /// </summary>
    private static bool IsContainedIn(SyntaxBase child, SyntaxBase parent)
    {
        var span = child.Span;
        return parent.IsOverlapping(span.Position) &&
               parent.IsOverlapping(span.GetEndPosition());
    }

    private static string? TryGetModuleParameterTypeString(SemanticModel semanticModel, VariableAccessSyntax variableAccess)
    {
        string? moduleParameterName = null;
        bool insideModuleParams = false;
        SyntaxBase? current = variableAccess;

        while (current is not null)
        {
            if (current is ObjectPropertySyntax propertySyntax && propertySyntax.TryGetKeyText() is string propertyName)
            {
                moduleParameterName ??= propertyName;

                if (LanguageConstants.IdentifierComparer.Equals(propertyName, LanguageConstants.ModuleParamsPropertyName))
                {
                    insideModuleParams = true;
                }
            }

            if (current is ModuleDeclarationSyntax moduleDeclaration)
            {
                if (!insideModuleParams || moduleParameterName is null)
                {
                    return null;
                }

                if (semanticModel.Binder.GetSymbolInfo(moduleDeclaration) is not ModuleSymbol moduleSymbol)
                {
                    return null;
                }

                if (!moduleSymbol.TryGetSemanticModel().IsSuccess(out var moduleSemanticModel, out _))
                {
                    return null;
                }

                if (!moduleSemanticModel.Parameters.TryGetValue(moduleParameterName, out var parameterMetadata))
                {
                    return null;
                }

                if (moduleSemanticModel is SemanticModel bicepSemanticModel &&
                    bicepSemanticModel.SourceFile is BicepSourceFile moduleSourceFile)
                {
                    var paramSyntax = moduleSourceFile.ProgramSyntax.Declarations
                        .OfType<ParameterDeclarationSyntax>()
                        .FirstOrDefault(p => LanguageConstants.IdentifierComparer.Equals(p.Name.IdentifierName, moduleParameterName));

                    if (paramSyntax?.Type is { } typeSyntax)
                    {
                        var typeText = moduleSourceFile.Text.Substring(typeSyntax.Span.Position, typeSyntax.Span.Length).Trim();
                        if (!string.IsNullOrWhiteSpace(typeText))
                        {
                            return typeText;
                        }
                    }
                }

                var parameterType = TypeHelper.TryRemoveNullability(parameterMetadata.TypeReference.Type) ?? parameterMetadata.TypeReference.Type;

                if (parameterType is IUnresolvedResourceDerivedType unresolvedResourceDerivedType)
                {
                    return TypeStringifier.FormatResourceDerivedType(unresolvedResourceDerivedType);
                }

                return GetTypeString(parameterType);
            }

            current = semanticModel.Binder.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// Infers type from usage context when the semantic model doesn't provide one.
    /// Checks (in order): comparison with literals, boolean context, arithmetic,
    /// string interpolation, for-loop iteration, and then walks ancestor nodes
    /// looking for a non-error declared type (e.g., enclosing output/variable/parameter
    /// declaration or function argument type).
    /// </summary>
    private static TypeSymbol? InferByContext(SemanticModel semanticModel, VariableAccessSyntax variableAccess)
    {
        if (InferFromComparisonWithLiteral(semanticModel, variableAccess) is { } comparisonType)
        {
            return comparisonType;
        }

        // If used in a conditional/ternary/logical context, assume bool.
        if (IsBooleanContext(semanticModel, variableAccess))
        {
            return LanguageConstants.Bool;
        }

        // If used in arithmetic, assume int.
        if (IsArithmeticContext(semanticModel, variableAccess))
        {
            return LanguageConstants.Int;
        }

        // If used inside string interpolation, assume string.
        if (IsStringInterpolationContext(semanticModel, variableAccess))
        {
            return LanguageConstants.String;
        }

        // If used as the iterable expression in a for-loop, assume array.
        if (IsForExpressionContext(semanticModel, variableAccess))
        {
            return LanguageConstants.Array;
        }

        // Walk ancestors to find a non-error declared type that applies to this
        // expression position, handling cases where the access is wrapped in
        // neutral syntax nodes (e.g., parentheses).
        SyntaxBase? current = semanticModel.Binder.GetParent(variableAccess);
        while (current is not null)
        {
            var declaredType = TypeHelper.NullIfErrorOrAny(semanticModel.GetDeclaredType(current));
            if (declaredType is not null)
            {
                return declaredType;
            }

            current = semanticModel.Binder.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// Returns true for any context where a boolean value is expected or implied,
    /// including: ternary conditions, negation, logical operators, comparisons,
    /// and if-condition expressions.
    /// </summary>
    private static bool IsBooleanContext(SemanticModel model, VariableAccessSyntax access)
    {
        SyntaxBase? current = access;
        while (current is not null)
        {
            if (current is TernaryOperationSyntax ternary && IsContainedIn(access, ternary.ConditionExpression))
            {
                return true;
            }
            if (current is UnaryOperationSyntax unary && unary.Operator == UnaryOperator.Not)
            {
                return true;
            }
            if (current is BinaryOperationSyntax binary)
            {
                if (binary.Operator is BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr or BinaryOperator.Equals or BinaryOperator.NotEquals or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual)
                {
                    return true;
                }
            }
            if (current is IfConditionSyntax ifCondition && IsContainedIn(access, ifCondition.ConditionExpression))
            {
                return true;
            }

            current = model.Binder.GetParent(current);
        }

        return false;
    }

    private static bool IsArithmeticContext(SemanticModel model, VariableAccessSyntax access)
    {
        SyntaxBase? current = access;
        while (current is not null)
        {
            if (current is BinaryOperationSyntax binary &&
                binary.Operator is BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo)
            {
                return true;
            }
            current = model.Binder.GetParent(current);
        }

        return false;
    }

    private static bool IsStringInterpolationContext(SemanticModel model, VariableAccessSyntax access)
    {
        SyntaxBase? current = access;
        while (current is not null)
        {
            // Use span-based containment to check if access is within any interpolation expression
            // This handles cases where access is wrapped (e.g., in parentheses)
            if (current is StringSyntax stringSyntax && stringSyntax.Expressions.Any(expr => IsContainedIn(access, expr)))
            {
                return true;
            }

            current = model.Binder.GetParent(current);
        }

        return false;
    }

    private static bool IsForExpressionContext(SemanticModel model, VariableAccessSyntax access)
    {
        SyntaxBase? current = access;
        while (current is not null)
        {
            if (current is ForSyntax forSyntax)
            {
                var exprSpan = forSyntax.Expression.Span;
                return access.Span.Position >= exprSpan.Position &&
                       access.Span.GetEndPosition() <= exprSpan.GetEndPosition();
            }

            current = model.Binder.GetParent(current);
        }

        return false;
    }

    /// <summary>
    /// When comparing with a literal (e.g., <c>x == 'foo'</c> or <c>x > 5</c>),
    /// infers the variable's type from the literal's type.
    /// </summary>
    private static TypeSymbol? InferFromComparisonWithLiteral(SemanticModel model, VariableAccessSyntax access)
    {
        SyntaxBase? current = access;
        while (current is not null)
        {
            // for ==, !=, <, <=, >, >= pick the other operand's primitive type
            if (current is BinaryOperationSyntax binary &&
                binary.Operator is BinaryOperator.Equals or BinaryOperator.NotEquals or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual)
            {
                // Use span-based containment to determine which operand contains the access
                // This handles cases where access is wrapped (e.g., in parentheses)
        var otherExpression = IsContainedIn(access, binary.LeftExpression) ? binary.RightExpression : binary.LeftExpression;
        var otherType = model.GetTypeInfo(otherExpression);

        if (TypeHelper.TryGetArmPrimitiveType(otherType) is { } primitiveType &&
            primitiveType is StringType or IntegerType or BooleanType)
        {
            return primitiveType;
        }
            }

            current = model.Binder.GetParent(current);
        }

        return null;
    }

}
