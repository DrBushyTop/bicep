// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Bicep.Core;
using Bicep.Core.CodeAction;
using Bicep.Core.Extensions;
using Bicep.Core.Parsing;
using Bicep.Core.Semantics;
using Bicep.Core.Syntax;
using Bicep.Core.TypeSystem;
using Bicep.Core.TypeSystem.Types;
using Bicep.LanguageServer.Completions;
using Bicep.Core.Text;
using Bicep.Core.PrettyPrintV2;

namespace Bicep.LanguageServer.Refactor;

/// <summary>
/// Offers quick fixes to declare missing symbols reported as BCP057.
/// </summary>
public class UndefinedSymbolCodeFixProvider : ICodeFixProvider
{
    private const string DiagnosticCode = "BCP057";

    private readonly SemanticModel semanticModel;

    public UndefinedSymbolCodeFixProvider(SemanticModel semanticModel)
    {
        this.semanticModel = semanticModel;
    }

    public IEnumerable<CodeFix> GetFixes(SemanticModel semanticModel, IReadOnlyList<SyntaxBase> matchingNodes)
    {
        try
        {
            return GetFixesInternal(semanticModel, matchingNodes);
        }
        catch
        {
            return [];
        }
    }

    private IEnumerable<CodeFix> GetFixesInternal(SemanticModel semanticModel, IReadOnlyList<SyntaxBase> matchingNodes)
    {
        var results = new List<CodeFix>();

        var allDiagnostics = semanticModel.GetAllDiagnostics();
        var diagnostics = allDiagnostics.Where(diag => diag.Code == DiagnosticCode).ToArray();

        if (diagnostics.Length == 0 || matchingNodes.Count == 0)
        {
            return results;
        }

        var variableAccessesInRange = matchingNodes.OfType<VariableAccessSyntax>();
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (var variableAccess in variableAccessesInRange)
        {
            var diagnostic = diagnostics.FirstOrDefault(diag => SpansOverlap(variableAccess.Span.Position, variableAccess.GetEndPosition(), diag.Span));
            if (diagnostic is null)
            {
                continue;
            }

            var name = variableAccess.Name.IdentifierName;
            if (!seen.Add(name) || !Lexer.IsValidIdentifier(name))
            {
                continue;
            }

            if (semanticModel.Binder.GetNearestAncestor<StatementSyntax>(variableAccess) is not { } parentStatement)
            {
                continue;
            }

            var contextualType = NullIfErrorOrAny(semanticModel.GetDeclaredType(variableAccess));
            var declaredAssignmentType = NullIfErrorOrAny(semanticModel.GetDeclaredTypeAssignment(variableAccess)?.Reference.Type);
            var inferredType = NullIfErrorOrAny(semanticModel.GetTypeInfo(variableAccess));
            var effectiveType = declaredAssignmentType ?? contextualType ?? inferredType ?? InferByContext(semanticModel, variableAccess);
            var typeString = GetTypeString(effectiveType);

            var newline = semanticModel.Configuration.Formatting.Data.NewlineKind.ToEscapeSequence();

            foreach (var fix in CreateQuickFixes(parentStatement, name, typeString, newline))
            {
                results.Add(fix);
            }
        }

        return results;
    }

    private IEnumerable<CodeFix> CreateQuickFixes(StatementSyntax parentStatement, string name, string typeString, string newline)
    {
        var parameterInsertionOffset = FindInsertionOffset(parentStatement, typeof(ParameterDeclarationSyntax));
        yield return new CodeFix(
            $"Create parameter '{name}'",
            isPreferred: false,
            CodeFixKind.Refactor,
            new CodeReplacement(new TextSpan(parameterInsertionOffset, 0), $"param {name} {typeString}{newline}{newline}"));

        var variableInsertionOffset = FindInsertionOffset(parentStatement, typeof(VariableDeclarationSyntax));
        yield return new CodeFix(
            $"Create variable '{name}'",
            isPreferred: false,
            CodeFixKind.Refactor,
            new CodeReplacement(new TextSpan(variableInsertionOffset, 0), $"var {name} = ''{newline}{newline}"));
    }

    private int FindInsertionOffset(StatementSyntax anchorStatement, Type declarationType)
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

        var anchorStartLine = ExpressionAndTypeExtractor.GetFirstLineOfStatementIncludingComments(
            lineStarts,
            sourceFile.ProgramSyntax,
            anchorStatement);
        return TextCoordinateConverter.GetOffset(lineStarts, anchorStartLine, 0);
    }

    private static string GetTypeString(TypeSymbol? type) => type switch
    {
        StringType => "string",
        BooleanType => "bool",
        IntegerType => "int",
        ArrayType => "array",
        ObjectType => "object",
        _ => "string",
    };

    private static TypeSymbol? NullIfErrorOrAny(TypeSymbol? type) => type is ErrorType or AnyType ? null : type;

    private static bool SpansOverlap(int requestStart, int requestEnd, TextSpan span) =>
        requestStart <= span.GetEndPosition() && requestEnd >= span.Position;

    private static TypeSymbol? InferByContext(SemanticModel semanticModel, VariableAccessSyntax variableAccess)
    {
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

        // Try to derive from enclosing property type (e.g., resource property).
        if (semanticModel.Binder.GetParent(variableAccess) is SyntaxBase parent)
        {
            var declaredType = semanticModel.GetDeclaredType(parent);
            if (declaredType is not null && declaredType is not ErrorType && declaredType is not AnyType)
            {
                return declaredType;
            }
        }

        return null;
    }

    private static bool IsBooleanContext(SemanticModel model, VariableAccessSyntax access)
    {
        SyntaxBase? current = access;
        while (current is not null)
        {
            if (current is TernaryOperationSyntax ternary && ReferenceEquals(ternary.ConditionExpression, access))
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
}

